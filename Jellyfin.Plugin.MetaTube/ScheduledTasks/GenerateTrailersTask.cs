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
            progress?.Report((double)idx / items.Count * 100);

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

                // 将单文件下载进度映射到任务整体进度区间 [idx, idx+1] / 总数，
                // 避免任务级与下载级共用同一个 progress 导致进度条来回跳动。
                var downloadProgress = new SyncProgress(p =>
                    progress?.Report((idx + Math.Clamp(p, 0.0, 100.0) / 100.0) / items.Count * 100));

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
                    
                    // Download trailer file.
                    success = await _trailerDownloader.DownloadTrailerAsync(trailerUrl, trailerFilePath, downloadProgress, cancellationToken);
                    
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

    // 同步进度适配器：在调用线程内直接转发进度（不经过 SynchronizationContext），
    // 确保下载进度被实时、有序地映射到任务整体进度。
    private sealed class SyncProgress : IProgress<double>
    {
        private readonly Action<double> _handler;

        public SyncProgress(Action<double> handler)
        {
            _handler = handler;
        }

        public void Report(double value)
        {
            _handler(value);
        }
    }
}