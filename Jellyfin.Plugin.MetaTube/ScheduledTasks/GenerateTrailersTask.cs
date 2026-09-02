using Jellyfin.Plugin.MetaTube.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
#if __EMBY__
using Jellyfin.Plugin.MetaTube.Trailers;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Logging;

#else
using Jellyfin.Plugin.MetaTube.Download;
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Enums;
#endif

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

public class GenerateTrailersTask : IScheduledTask
{
#if !__EMBY__
    // Emby: trailers can be stored in a trailers sub-folder.
    // https://support.emby.media/support/solutions/articles/44001159193-trailers
    private const string TrailersFolder = "trailers";

    // Uniform suffix for all trailer files.
    private const string TrailerFileSuffix = "-trailer.mp4";

    // 下载中的临时文件后缀与匹配模式（例如 SSIS-001-trailer.mp4.tmp）。
    private const string TempFileSuffix = ".tmp";
    private const string TempSearchPattern = $"*{TrailerFileSuffix}{TempFileSuffix}";
#endif

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;
#if __EMBY__
    private readonly ILogManager _logManager;
#else
    private readonly TrailerDownloader _trailerDownloader;
#endif

#if __EMBY__
    public GenerateTrailersTask(ILogManager logManager, ILibraryManager libraryManager)
    {
        _logger = logManager.CreateLogger<GenerateTrailersTask>();
        _libraryManager = libraryManager;
        _logManager = logManager;
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
#if __EMBY__
        // 不设置默认触发器：任务只在用户手动运行时执行。
        // 需要定时的用户自行在 Emby UI 中添加触发器。
        yield break;
#else
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(1).Ticks
        };
#endif
    }

#if __EMBY__
    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        // Stop the task if disabled.
        if (!Plugin.Instance.Configuration.EnableTrailers)
            return;

        await Task.Yield();

        progress?.Report(0);

        // 单例可能因插件重载被换过，每次执行时重新取，避免拿到已关停的服务。
        var trailerService = TrailerService.GetInstance(_logManager, _libraryManager);

        // 用带 token 的重载：大媒体库查询期间用户停止任务能及时生效。
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video },
            HasAnyProviderId = new[] { Plugin.ProviderId },
            IncludeItemTypes = new[] { nameof(Movie) }
        }, cancellationToken);

        // 预检阶段：逐个 Check 得出真正需要下载的候选，进度分母用它而不是全库总数。
        // 五千部片里只有十几部缺预览片时，按总数算的进度条会瞬间冲到 99% 再卡住数小时。
        var candidates = new List<long>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item is not Movie movie || movie.ExtraType != null)
                continue;

            try
            {
                if (trailerService.Check(movie.InternalId).NeedDownload)
                    candidates.Add(movie.InternalId);
            }
            catch (Exception e)
            {
                _logger.Error("Scan trailer for video {0} error: {1}", item.Name, e.Message);
            }
        }

        _logger.Info("Trailers to download: {0}", candidates.Count);

        if (candidates.Count == 0)
        {
            progress?.Report(100);
            return;
        }

        // 下载阶段：入队后与自动检查排在同一条队列里串行执行。
        // 先入队再注册取消回调：注册时 token 若已取消会立即回调，
        // 因此“入队途中被取消”和“入队完成后被取消”都能被 CancelPending 覆盖，不留竞态窗口。
        var pending = new List<Task<TrailerProcessResult>>(candidates.Count);
        foreach (var internalId in candidates)
        {
            if (cancellationToken.IsCancellationRequested) break;
            pending.Add(trailerService.EnqueueAsync(internalId));
        }

        using var registration = cancellationToken.Register(trailerService.CancelPending);

        if (pending.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(100);
            return;
        }

        int downloaded = 0, failed = 0, skipped = 0;
        var failures = new List<string>();

        for (var index = 0; index < pending.Count; index++)
        {
            var result = await pending[index];

            switch (result.Status)
            {
                case TrailerProcessStatus.Downloaded:
                    downloaded++;
                    break;
                case TrailerProcessStatus.Failed:
                    failed++;
                    failures.Add($"{result.ItemName} ({result.Message})");
                    break;
                default:
                    skipped++;
                    break;
            }

            // 成功与失败同样计入，进度不会卡住。
            progress?.Report((double)(index + 1) / pending.Count * 100);

            cancellationToken.ThrowIfCancellationRequested();
        }

        _logger.Info("Trailer scan finished: pending {0}, succeeded {1}, failed {2}, skipped {3}",
            pending.Count, downloaded, failed, skipped);

        foreach (var failure in failures)
            _logger.Error("Trailer download failed: {0}", failure);

        progress?.Report(100);
    }
#else
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Stop the task if disabled.
        if (!Plugin.Instance.Configuration.EnableTrailers)
            return;

        await Task.Yield();

        progress?.Report(0);

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video },
            HasAnyProviderId = new Dictionary<string, string> { { Plugin.ProviderId, string.Empty } },
            IncludeItemTypes = new[] { BaseItemKind.Movie }
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

                for (var retryCount = 0; retryCount <= maxRetries; retryCount++)
                {
                    if (retryCount > 0)
                        _logger.Info("Retry {0}/{1} downloading trailer for video {2}", retryCount, maxRetries,
                            entry.Item.Name);

                    // Download trailer file.（不传单文件进度，进度按待下载影片计数推进）
                    var result = await _trailerDownloader.DownloadTrailerAsync(entry.TrailerUrl,
                        entry.TrailerFilePath, null, cancellationToken);

                    if (result == TrailerDownloadResult.Success)
                    {
                        File.SetLastWriteTimeUtc(entry.TrailerFilePath, DateTime.UtcNow);
                        break;
                    }

                    // 不可重试的失败，直接放弃这一部。
                    if (result != TrailerDownloadResult.Failed)
                        break;

                    // 最后一次尝试失败后记录日志
                    if (retryCount == maxRetries)
                        _logger.Error("Failed to download trailer for video {0} after {1} retries", entry.Item.Name,
                            maxRetries);
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
#endif
}
