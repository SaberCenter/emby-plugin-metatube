#if __EMBY__

using System.Threading.Channels;
using Jellyfin.Plugin.MetaTube.Download;
using Jellyfin.Plugin.MetaTube.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Jellyfin.Plugin.MetaTube.Trailers;

public enum TrailerProcessStatus
{
    /// 无需下载，或下载期间本地已出现预览片。
    Skipped,

    /// 正式文件已落盘。
    Downloaded,

    /// 重试耗尽仍未成功，或 URL 不可用。
    Failed
}

public sealed class TrailerProcessResult
{
    public TrailerProcessStatus Status { get; init; }

    public string ItemName { get; init; }

    public string Message { get; init; }
}

public sealed class TrailerCheckResult
{
    public bool NeedDownload { get; init; }

    public string SkipReason { get; init; }

    public string ItemName { get; init; }

    public string TrailerUrl { get; init; }

    public string TrailersFolderPath { get; init; }

    public string TargetPath { get; init; }

    public static TrailerCheckResult Skip(string reason, string itemName = null)
    {
        return new TrailerCheckResult { NeedDownload = false, SkipReason = reason, ItemName = itemName };
    }
}

/// <summary>
///     预览片下载的唯一执行者。两个入口（ItemUpdated 自动检查、Generate Trailers 计划任务）
///     都只调用 Enqueue，之后走完全相同的检测流程和同一条串行队列。
/// </summary>
public sealed class TrailerService
{
    private const string TrailersFolderName = "trailers";
    private const string IgnoreFileName = ".ignore";
    private const string TrailerNameSuffix = "-trailer";
    private const string TrailerFileExtension = ".mp4";

    private const int MaxRetries = 2;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    // 有效预览片的扩展名白名单。.tmp 不在其中，因此任何检查都不会把临时文件当成预览片。
    private static readonly string[] VideoExtensions =
    {
        ".mp4", ".mkv", ".avi", ".wmv", ".mov", ".m4v", ".flv",
        ".ts", ".webm", ".mpg", ".mpeg", ".rmvb", ".asf", ".m2ts"
    };

    private static readonly object InstanceLock = new();
    private static TrailerService _instance;

    private static readonly TrailerProcessResult CancelledResult = new()
    {
        Status = TrailerProcessStatus.Skipped, Message = "cancelled"
    };

    private readonly TrailerDownloader _downloader;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

    // 去重队列：同一 InternalId 在队列中只保留一份，重复事件自动合并。
    // 队列本身就是串行器，因此不需要任何全局执行锁。
    private readonly Dictionary<long, QueueEntry> _pending = new();
    private readonly object _pendingLock = new();
    private readonly Channel<QueueEntry> _queue = Channel.CreateUnbounded<QueueEntry>();

    // 以下三个字段一律在 _pendingLock 内读写。
    // _sequence 是入队序号，_cancelledUpTo 是取消水位线：序号不大于水位线的项一律作废。
    // 有了水位线，“已出队但尚未开始”的项也能被取消覆盖，不依赖 _currentCts 是否已发布。
    private long _sequence;
    private long _cancelledUpTo;

    // 当前正在处理的项的取消源，供 CancelPending 中止进行中的下载。
    private CancellationTokenSource _currentCts;

    private TrailerService(ILogManager logManager, ILibraryManager libraryManager)
    {
        _logger = logManager.CreateLogger<TrailerService>();
        _libraryManager = libraryManager;
        _downloader = new TrailerDownloader(logManager.CreateLogger<TrailerDownloader>());

        _ = Task.Run(ConsumeAsync);
    }

    /// 事件入口和计划任务由 Emby 分别构造，这里共用同一个实例，保证只有一条队列。
    public static TrailerService GetInstance(ILogManager logManager, ILibraryManager libraryManager)
    {
        if (_instance != null) return _instance;

        lock (InstanceLock)
        {
            return _instance ??= new TrailerService(logManager, libraryManager);
        }
    }

    #region Queue

    /// 入队。O(1)，不阻塞、不做文件 IO、不下载——事件处理器只能走这个入口。
    public void Enqueue(long internalId)
    {
        Enqueue(internalId, null);
    }

    /// 入队并返回一个在该项处理完成时结束的任务，供计划任务推进进度与统计汇总。
    public Task<TrailerProcessResult> EnqueueAsync(long internalId)
    {
        var completion =
            new TaskCompletionSource<TrailerProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(internalId, completion);
        return completion.Task;
    }

    private void Enqueue(long internalId, TaskCompletionSource<TrailerProcessResult> completion)
    {
        lock (_pendingLock)
        {
            // 已经在队列中：合并等待者，不重复排队。
            if (_pending.TryGetValue(internalId, out var existing))
            {
                if (completion != null) existing.Waiters.Add(completion);
                return;
            }

            var entry = new QueueEntry(internalId, ++_sequence);
            if (completion != null) entry.Waiters.Add(completion);

            // 服务已关闭：立刻结束等待者，避免调用方永远等下去。
            if (!_queue.Writer.TryWrite(entry))
            {
                CompleteWaiters(entry, CancelledResult);
                return;
            }

            _pending[internalId] = entry;
        }
    }

    /// 插件卸载或重载：停止接收新任务，清空队列并取消当前下载，让消费者自然退出，
    /// 同时让出单例位置，后续 GetInstance 会建一个干净的服务。
    public void Shutdown()
    {
        lock (InstanceLock)
        {
            if (ReferenceEquals(_instance, this)) _instance = null;
        }

        _queue.Writer.TryComplete();
        CancelPending();
    }

    /// 用户停止计划任务：清空队列中尚未开始的项，并取消当前正在进行的下载。
    /// 被丢弃的自动入队项不做补偿，下次刮削或下次全量检查会重新覆盖到。
    public void CancelPending()
    {
        CancellationTokenSource current;

        lock (_pendingLock)
        {
            // 先立水位线：此刻之前入队的项一律作废，无论它还在队列里、还是已被消费者取走。
            _cancelledUpTo = _sequence;

            while (_queue.Reader.TryRead(out var entry))
            {
                _pending.Remove(entry.InternalId);
                CompleteWaiters(entry, CancelledResult);
            }

            current = _currentCts;
        }

        try
        {
            // 在锁外取消：取消回调会同步执行（中止连接、关闭文件流），不应占着队列锁。
            current?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 当前项刚好处理完毕，无需取消。
        }
    }

    private async Task ConsumeAsync()
    {
        try
        {
            // 单消费者，顺序处理：插件内任意时刻最多运行一个预览片检查或下载流程。
            await foreach (var entry in _queue.Reader.ReadAllAsync())
            {
                var cts = new CancellationTokenSource();
                bool cancelled;

                lock (_pendingLock)
                {
                    _pending.Remove(entry.InternalId);

                    // 比对水位线与发布 CTS 在同一把锁内完成，CancelPending 也在同一把锁内
                    // 立水位线并取 CTS 快照，因此取消要么被水位线拦下、要么落到已发布的 CTS 上，
                    // “已出队但尚未开始”的窗口不存在。
                    cancelled = entry.Sequence <= _cancelledUpTo;
                    if (!cancelled) _currentCts = cts;
                }

                if (cancelled)
                {
                    cts.Dispose();
                    CompleteWaiters(entry, CancelledResult);
                    continue;
                }

                TrailerProcessResult result;

                try
                {
                    result = await ProcessAsync(entry.InternalId, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    result = CancelledResult;
                }
                catch (Exception e)
                {
                    _logger.Error("Process trailer for item {0} error: {1}", entry.InternalId, e.Message);
                    result = new TrailerProcessResult
                    {
                        Status = TrailerProcessStatus.Failed, Message = e.Message
                    };
                }
                finally
                {
                    lock (_pendingLock)
                    {
                        if (ReferenceEquals(_currentCts, cts)) _currentCts = null;
                    }

                    cts.Dispose();
                }

                CompleteWaiters(entry, result);
            }
        }
        catch (Exception e)
        {
            _logger.Error("Trailer queue consumer stopped unexpectedly: {0}", e.Message);
        }
    }

    private void CompleteWaiters(QueueEntry entry, TrailerProcessResult result)
    {
        lock (_pendingLock)
        {
            foreach (var waiter in entry.Waiters)
                waiter.TrySetResult(result);
        }
    }

    private sealed class QueueEntry
    {
        public QueueEntry(long internalId, long sequence)
        {
            InternalId = internalId;
            Sequence = sequence;
        }

        public long InternalId { get; }

        public long Sequence { get; }

        public List<TaskCompletionSource<TrailerProcessResult>> Waiters { get; } = new();
    }

    #endregion

    #region Check

    /// <summary>
    ///     统一检测流程。无副作用：只读取数据库记录和文件系统，不写文件、不删文件、不下载。
    ///     计划任务预检阶段与队列消费者出队时各调用一次。
    /// </summary>
    public TrailerCheckResult Check(long internalId)
    {
        // 第一步：功能是否启用。
        if (!Plugin.Instance.Configuration.EnableTrailers)
            return TrailerCheckResult.Skip("trailers disabled");

        // 第二步：读取并确认电影记录。
        // 必须重新读取，不能复用事件参数里的对象或元数据提供器返回的中间对象。
        if (_libraryManager.GetItemById(internalId) is not Movie movie)
            return TrailerCheckResult.Skip("not a movie");

        var itemName = movie.Name;

        // 第三步：MetaTube 元数据是否完整。
        if (string.IsNullOrWhiteSpace(movie.GetProviderId(Plugin.ProviderId)))
            return TrailerCheckResult.Skip("no MetaTube provider id", itemName);

        var trailerUrl = movie.GetTrailerUrl();
        if (string.IsNullOrWhiteSpace(trailerUrl))
            return TrailerCheckResult.Skip("no trailer url", itemName);

        var movieFolderPath = movie.ContainingFolderPath;
        if (string.IsNullOrEmpty(movieFolderPath))
            return TrailerCheckResult.Skip("no containing folder", itemName);

        var trailersFolderPath = Path.Join(movieFolderPath, TrailersFolderName);

        // 第四步：是否被用户忽略。.ignore 是目录级开关，作用于该目录下的所有电影。
        if (File.Exists(Path.Join(trailersFolderPath, IgnoreFileName)))
            return TrailerCheckResult.Skip("ignored", itemName);

        // 第五步：本地是否已经存在预览片。只看文件系统，不查 Emby 数据库——
        // 新下载的文件在 Emby 重新扫描之前不会进库，数据库永远滞后，无法作为判断依据。
        if (HasLocalTrailer(movieFolderPath, trailersFolderPath))
            return TrailerCheckResult.Skip("local trailer exists", itemName);

        // 第六步：计算目标路径。
        var targetPath = Path.Join(trailersFolderPath,
            $"{GetTrailerBaseName(movie)}{TrailerNameSuffix}{TrailerFileExtension}");

        return new TrailerCheckResult
        {
            NeedDownload = true,
            ItemName = itemName,
            TrailerUrl = trailerUrl,
            TrailersFolderPath = trailersFolderPath,
            TargetPath = targetPath
        };
    }

    // 一个电影目录只维护一个预览片：同目录多部影片通常是同一部片的不同版本，
    // 共用一个预览片是期望行为，因此规则一不做文件名匹配。
    private static bool HasLocalTrailer(string movieFolderPath, string trailersFolderPath)
    {
        // 规则一：trailers 子目录下存在任意有效预览片文件。
        // 旧版本按“标题第一个词”命名的文件也落在这里，会被识别为已存在，不会重复下载。
        if (HasValidVideo(trailersFolderPath, null))
            return true;

        // 规则二：电影目录下存在 *-trailer.<视频扩展名>，即 Emby 的后缀规则。
        return HasValidVideo(movieFolderPath, path =>
            Path.GetFileNameWithoutExtension(path).EndsWith(TrailerNameSuffix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasValidVideo(string folderPath, Func<string, bool> predicate)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            return false;

        try
        {
            foreach (var path in Directory.EnumerateFiles(folderPath))
            {
                if (!IsValidVideoFile(path)) continue;
                if (predicate == null || predicate(path)) return true;
            }
        }
        catch (Exception)
        {
            // 目录不可读时视为没有本地预览片。
        }

        return false;
    }

    // 有效预览片：扩展名在白名单内、不是隐藏文件（含 macOS 的 ._ 派生文件）、长度大于 0。
    // .tmp 因为不在白名单内自然被排除；零字节文件不算有效，因此不会阻止下载。
    private static bool IsValidVideoFile(string path)
    {
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName) || fileName.StartsWith(".", StringComparison.Ordinal))
            return false;

        if (!VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            return false;

        try
        {
            return new FileInfo(path).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // 目标文件名取原视频文件名：媒体库里的原始文件名固定是番号（如 SONE-123.mp4），
    // 不受标题翻译和用户改名影响。item.Name 形如 “SONE-123 中文标题”，会随二者变化，
    // 是旧实现重复下载的根因。
    private static string GetTrailerBaseName(BaseItem movie)
    {
        if (!string.IsNullOrEmpty(movie.Path) && File.Exists(movie.Path))
        {
            var fileName = Path.GetFileNameWithoutExtension(movie.Path);
            if (!string.IsNullOrWhiteSpace(fileName)) return fileName;
        }

        // Path 是目录或已不存在时退回目录名。
        var folderPath = movie.ContainingFolderPath;
        if (!string.IsNullOrEmpty(folderPath))
        {
            var folderName = Path.GetFileName(
                folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(folderName)) return folderName;
        }

        var firstWord = movie.Name?.Split().FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstWord) ? "movie" : firstWord;
    }

    #endregion

    #region Download

    private async Task<TrailerProcessResult> ProcessAsync(long internalId, CancellationToken cancellationToken)
    {
        var check = Check(internalId);
        if (!check.NeedDownload)
        {
            _logger.Debug("Skip trailer for item {0}: {1}", check.ItemName ?? internalId.ToString(), check.SkipReason);
            return new TrailerProcessResult
            {
                Status = TrailerProcessStatus.Skipped, ItemName = check.ItemName, Message = check.SkipReason
            };
        }

        try
        {
            Directory.CreateDirectory(check.TrailersFolderPath);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // 电影目录对 Emby 进程不可写。这是环境问题，重试没有意义，
            // 给一条能直接指向原因的日志即可，不要让裸异常看起来像插件出错。
            _logger.Error(
                "Cannot create trailers folder {0}: {1}. Check that the movie folder is writable by Emby.",
                check.TrailersFolderPath, e.Message);

            return new TrailerProcessResult
            {
                Status = TrailerProcessStatus.Failed, ItemName = check.ItemName,
                Message = "trailers folder not writable"
            };
        }

        _logger.Info("Downloading trailer for video {0} to {1}", check.ItemName, check.TargetPath);

        var message = "download failed";

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt > 0)
            {
                _logger.Info("Retry {0}/{1} downloading trailer for video {2}", attempt, MaxRetries, check.ItemName);
                await Task.Delay(RetryDelay, cancellationToken);
            }

            // 落盘前的最后一次本地检查交给下载器在正式改名前调用，用的还是同一个 Check。
            var result = await _downloader.DownloadTrailerAsync(check.TrailerUrl, check.TargetPath, null,
                cancellationToken, () => Check(internalId).NeedDownload);

            switch (result)
            {
                case TrailerDownloadResult.Success:
                    return new TrailerProcessResult
                    {
                        Status = TrailerProcessStatus.Downloaded, ItemName = check.ItemName
                    };

                // 本地已出现有效预览片：不是失败，也不重试。
                case TrailerDownloadResult.Aborted:
                    return new TrailerProcessResult
                    {
                        Status = TrailerProcessStatus.Skipped, ItemName = check.ItemName,
                        Message = "local trailer appeared"
                    };

                // URL 非法或是 HLS：重试没有意义。
                case TrailerDownloadResult.Invalid:
                    message = "invalid or unsupported trailer url";
                    _logger.Error("Failed to download trailer for video {0}: {1}", check.ItemName, message);
                    return new TrailerProcessResult
                    {
                        Status = TrailerProcessStatus.Failed, ItemName = check.ItemName, Message = message
                    };
            }
        }

        // 重试耗尽即放弃：正式文件不出现，.tmp 已删除，本地仍判定为“缺失”。
        // 插件不排队重试、不记录失败项，之后由用户刷新元数据或运行全量检查触发。
        _logger.Error("Failed to download trailer for video {0} after {1} retries", check.ItemName, MaxRetries);
        return new TrailerProcessResult
        {
            Status = TrailerProcessStatus.Failed, ItemName = check.ItemName, Message = message
        };
    }

    #endregion
}

#endif
