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

        foreach (var (idx, item) in items.WithIndex())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var trailersFolderPath = Path.Join(item.ContainingFolderPath, TrailersFolder);

                // Skip if contains .ignore file.
                if (File.Exists(Path.Join(trailersFolderPath, ".ignore")))
                    continue;

                // 清理上次中断（断电 / kill -9）遗留的孤儿临时文件，避免永久残留。
                // 放在 .ignore 之后、各 continue 之前，确保即使本片随后被跳过也能清掉残留。
                CleanupOrphanTempFiles(trailersFolderPath);

                var trailerUrl = item.GetTrailerUrl();

                // Skip if no remote trailers.
                if (string.IsNullOrWhiteSpace(trailerUrl))
                    continue;

                var trailerFilePath = Path.Join(trailersFolderPath,
                    $"{item.Name.Split().First()}{TrailerFileSuffix}");

                // 如果预告片文件已存在，跳过
                if (File.Exists(trailerFilePath))
                    continue;

                // Create trailers folder if not exists.
                if (!Directory.Exists(trailersFolderPath))
                    Directory.CreateDirectory(trailersFolderPath);

                _logger.Info("Downloading trailer for video {0} to {1}", item.Name, trailerFilePath);

                // 添加重试逻辑
                const int maxRetries = 2;
                bool success = false;
                
                for (int retryCount = 0; retryCount <= maxRetries; retryCount++)
                {
                    if (retryCount > 0)
                    {
#if __EMBY__
                        _logger.Info("Retry {0}/{1} downloading trailer for video {2}", retryCount, maxRetries, item.Name);
#else
                        _logger.LogInformation("Retry {0}/{1} downloading trailer for video {2}", retryCount, maxRetries, item.Name);
#endif
                    }
                    
                    // Download trailer file.（进度按整部影片计数推进，不传单文件进度）
                    success = await _trailerDownloader.DownloadTrailerAsync(trailerUrl, trailerFilePath, null, cancellationToken);
                    
                    if (success)
                    {
                        File.SetLastWriteTimeUtc(trailerFilePath, DateTime.UtcNow);
                        break;
                    }
                    
                    // 最后一次尝试失败后记录日志
                    if (retryCount == maxRetries)
                    {
#if __EMBY__
                        _logger.Error("Failed to download trailer for video {0} after {1} retries", item.Name, maxRetries);
#else
                        _logger.LogError("Failed to download trailer for video {0} after {1} retries", item.Name, maxRetries);
#endif
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Error("Download trailer for video {0} error: {1}", item.Name, e.Message);
            }

            // 每处理完一部影片（无论下载、跳过或失败），按整体进度前进一格：
            // 进度 = 已处理部数 / 总部数 × 100。完成一部就前进，直观且不依赖 Content-Length。
            progress?.Report((double)(idx + 1) / items.Count * 100);
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