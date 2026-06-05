using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Download;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
#if __EMBY__
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Logging;

#else
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Enums;
#endif

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

public class GenerateTrailersTask : IScheduledTask
{
    // Emby: trailers can be stored in a trailers sub-folder.
    // https://support.emby.media/support/solutions/articles/44001159193-trailers
    private const string TrailersFolder = "trailers";

    // Uniform suffix for all trailer files.
    private const string TrailerFileSuffix = "-trailer.mp4";

    // 下载中的临时文件后缀与匹配模式（例如 SSIS-001-trailer.mp4.tmp）。
    private const string TempFileSuffix = ".tmp";
    private const string TempSearchPattern = $"*{TrailerFileSuffix}{TempFileSuffix}";

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;
    private readonly TrailerDownloader _trailerDownloader;

#if __EMBY__
    public GenerateTrailersTask(ILogManager logManager, ILibraryManager libraryManager)
    {
        _logger = logManager.CreateLogger<GenerateTrailersTask>();
        _libraryManager = libraryManager;
        _trailerDownloader = new TrailerDownloader(logManager.CreateLogger<TrailerDownloader>());
    }
#else
    public GenerateTrailersTask(ILogger<GenerateTrailersTask> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _trailerDownloader = new TrailerDownloader(logger);
    }
#endif

    public string Key => $"{Plugin.ProviderName}GenerateTrailers";

    public string Name => "Generate Trailers";

    public string Description => $"Generates video trailers provided by {Plugin.ProviderName} in library.";

    public string Category => Plugin.ProviderName;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
#if __EMBY__
            Type = TaskTriggerInfo.TriggerDaily,
#else
            Type = TaskTriggerInfoType.DailyTrigger,
#endif
            TimeOfDayTicks = TimeSpan.FromHours(1).Ticks
        };
    }

#if __EMBY__
    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
#else
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
#endif
    {
        // Stop the task if disabled.
        if (!Plugin.Instance.Configuration.EnableTrailers)
            return;

        await Task.Yield();

        progress?.Report(0);

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video },
#if __EMBY__
            HasAnyProviderId = new[] { Plugin.ProviderId },
            IncludeItemTypes = new[] { nameof(Movie) },
#else
            HasAnyProviderId = new Dictionary<string, string> { { Plugin.ProviderId, string.Empty } },
            IncludeItemTypes = new[] { BaseItemKind.Movie }
#endif
        }).ToList();

        // 第一遍：扫描出“待下载”的影片（跳过已下载 / 无预览片 / 被忽略的，且不计入进度），
        // 顺便清理上次中断遗留的孤儿临时文件。进度条只反映待下载的部分。
        var pending = new List<(BaseItem Item, string TrailerUrl, string TrailerFilePath, string TrailersFolderPath)>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var trailersFolderPath = Path.Join(item.ContainingFolderPath, TrailersFolder);

                // Skip if contains .ignore file.
                if (File.Exists(Path.Join(trailersFolderPath, ".ignore")))
                    continue;

                // 清理上次中断（断电 / kill -9）遗留的孤儿临时文件，避免永久残留。
                CleanupOrphanTempFiles(trailersFolderPath);

                var trailerUrl = item.GetTrailerUrl();

                // Skip if no remote trailers.
                if (string.IsNullOrWhiteSpace(trailerUrl))
                    continue;

                var trailerFilePath = Path.Join(trailersFolderPath,
                    $"{item.Name.Split().First()}{TrailerFileSuffix}");

                // 已下载则直接跳过，且不计入进度。
                if (File.Exists(trailerFilePath))
                    continue;

                pending.Add((item, trailerUrl, trailerFilePath, trailersFolderPath));
            }
            catch (Exception e)
            {
                _logger.Error("Scan trailer for video {0} error: {1}", item.Name, e.Message);
            }
        }

        _logger.Info("Trailers to download: {0}", pending.Count);

        // 第二遍：仅下载待下载的影片，进度条按“待下载数”推进，完成一部前进一格。
        foreach (var (idx, entry) in pending.WithIndex())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Create trailers folder if not exists.
                if (!Directory.Exists(entry.TrailersFolderPath))
                    Directory.CreateDirectory(entry.TrailersFolderPath);

                _logger.Info("Downloading trailer for video {0} to {1}", entry.Item.Name, entry.TrailerFilePath);

                // 添加重试逻辑
                const int maxRetries = 2;
                var success = false;

                for (var retryCount = 0; retryCount <= maxRetries; retryCount++)
                {
                    if (retryCount > 0)
                    {
#if __EMBY__
                        _logger.Info("Retry {0}/{1} downloading trailer for video {2}", retryCount, maxRetries, entry.Item.Name);
#else
                        _logger.LogInformation("Retry {0}/{1} downloading trailer for video {2}", retryCount, maxRetries, entry.Item.Name);
#endif
                    }

                    // Download trailer file.（不传单文件进度，进度按待下载影片计数推进）
                    success = await _trailerDownloader.DownloadTrailerAsync(entry.TrailerUrl, entry.TrailerFilePath, null, cancellationToken);

                    if (success)
                    {
                        File.SetLastWriteTimeUtc(entry.TrailerFilePath, DateTime.UtcNow);
                        break;
                    }

                    // 最后一次尝试失败后记录日志
                    if (retryCount == maxRetries)
                    {
#if __EMBY__
                        _logger.Error("Failed to download trailer for video {0} after {1} retries", entry.Item.Name, maxRetries);
#else
                        _logger.LogError("Failed to download trailer for video {0} after {1} retries", entry.Item.Name, maxRetries);
#endif
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Error("Download trailer for video {0} error: {1}", entry.Item.Name, e.Message);
            }

            // 完成一部待下载影片就前进一格：进度 = 已处理待下载数 / 待下载总数 × 100。
            progress?.Report((double)(idx + 1) / pending.Count * 100);
        }

        progress?.Report(100);
    }

    // 清理 trailers 文件夹中遗留的孤儿临时文件（上次下载因断电 / 强杀中断留下的 .tmp）。
    // 静默忽略异常：清理失败不应影响后续下载。
    private static void CleanupOrphanTempFiles(string trailersFolderPath)
    {
        if (!Directory.Exists(trailersFolderPath))
            return;

        try
        {
            foreach (var tempFile in Directory.GetFiles(trailersFolderPath, TempSearchPattern))
                File.Delete(tempFile);
        }
        catch
        {
            // 忽略清理临时文件时的异常。
        }
    }
}
