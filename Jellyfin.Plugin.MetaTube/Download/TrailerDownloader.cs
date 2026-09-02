using Jellyfin.Plugin.MetaTube.Extensions;
#if __EMBY__
using MediaBrowser.Model.Logging;
#else
using Microsoft.Extensions.Logging;
#endif

namespace Jellyfin.Plugin.MetaTube.Download;

public enum TrailerDownloadResult
{
    /// 正式文件已完整落盘。
    Success,

    /// 可重试的失败：网络错误、空闲超时、长度校验不通过等。
    Failed,

    /// URL 非法或是 HLS 播放列表，重试没有意义。
    Invalid,

    /// 下载期间本地已出现有效预览片，主动放弃且不重试。
    Aborted
}

public class TrailerDownloader
{
    // 使用静态 HttpClient 以避免套接字耗尽问题。
    // 超时统一由本类的空闲计时器控制，故禁用 HttpClient 自身超时。
    private static readonly HttpClient HttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    private const int BufferSize = 8192;

    // 空闲超时：连续这么久没有读到任何数据才判定超时。
    // 这里刻意不设“下载总时长上限”——那样慢速链路上的大文件必然被中途砍断，且每次重试都会被同样地砍断。
    private const int IdleTimeoutSeconds = 60;

    // 下载中的临时文件后缀。先下载到临时文件，完整且校验通过后再原子重命名为最终文件。
    private const string TempFileSuffix = ".tmp";

    // HLS 播放列表无法用单次 GET 直接落盘成可播放文件。
    // 不拦的话会“完整下载”一个几 KB 的 m3u8 文本并改名成 .mp4，长度校验还会通过，
    // 之后所有“本地已存在”的判断都会命中这个假文件，闭环永久卡死。
    private static readonly string[] HlsExtensions = { ".m3u8", ".m3u" };

    private static readonly string[] HlsMediaTypes =
    {
        "application/vnd.apple.mpegurl", "application/x-mpegurl", "audio/mpegurl", "audio/x-mpegurl"
    };

    private readonly ILogger _logger;

    public TrailerDownloader(ILogger logger)
    {
        _logger = logger;
    }

    /// <param name="stillMissing">
    ///     正式落盘前的最后一次本地检查。返回 false 表示本地已经有预览片，本次下载放弃且不覆盖用户文件。
    /// </param>
    public async Task<TrailerDownloadResult> DownloadTrailerAsync(string trailerUrl, string trailerFilePath,
        IProgress<double> progress, CancellationToken cancellationToken, Func<bool> stillMissing = null)
    {
        // 先下载到临时文件，下载完整并校验通过后才原子重命名为最终文件。
        // 这样任何中断（网络失败 / kill -9 / 断电）都只会留下 .tmp，最终的 mp4 不会出现，
        // “文件存在即跳过”因此始终是可靠的判断，不会把半截文件误认为有效。
        var tempFilePath = trailerFilePath + TempFileSuffix;

        if (!Uri.TryCreate(trailerUrl, UriKind.Absolute, out var uri))
        {
            _logger.Error("Invalid trailer URL: {0}", trailerUrl);
            return TrailerDownloadResult.Invalid;
        }

        if (HlsExtensions.Any(ext => uri.AbsolutePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.Warn("Skip HLS trailer URL, not directly downloadable: {0}", trailerUrl);
            return TrailerDownloadResult.Invalid;
        }

        // 空闲计时器：每读到一批数据就重置，连续 IdleTimeoutSeconds 秒无数据才取消。
        using var idleCts = new CancellationTokenSource(TimeSpan.FromSeconds(IdleTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(idleCts.Token, cancellationToken);
        var token = linkedCts.Token;

        try
        {
            using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrEmpty(mediaType) && HlsMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
            {
                _logger.Warn("Skip HLS trailer response ({0}): {1}", mediaType, trailerUrl);
                return TrailerDownloadResult.Invalid;
            }

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;

            var directory = Path.GetDirectoryName(trailerFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            long downloadedBytes = 0;

            // FileMode.Create 会覆盖此前异常终止遗留的 .tmp。
            using (var contentStream = await response.Content.ReadAsStreamAsync(token))
            using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None,
                       BufferSize, true))
            {
                var buffer = new byte[BufferSize];

                while (true)
                {
                    var bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (bytesRead == 0) break;

                    // 读到数据就重置空闲计时。
                    idleCts.CancelAfter(TimeSpan.FromSeconds(IdleTimeoutSeconds));

                    await fileStream.WriteAsync(buffer, 0, bytesRead, token);
                    downloadedBytes += bytesRead;

                    if (progress != null && totalBytes > 0)
                    {
                        var percentage = (double)downloadedBytes / totalBytes * 100;
                        if (Math.Floor(percentage) > Math.Floor((double)(downloadedBytes - bytesRead) / totalBytes * 100))
                            progress.Report(percentage);
                    }
                }

                // 确保所有数据落盘后再关闭文件流，便于随后的原子重命名。
                await fileStream.FlushAsync(token);
            }

            if (totalBytes > 0 && downloadedBytes != totalBytes)
            {
                _logger.Error("File size mismatch for trailer: {0}. Expected: {1}, Actual: {2}",
                    trailerFilePath, totalBytes, downloadedBytes);
                SafeDelete(tempFilePath);
                return TrailerDownloadResult.Failed;
            }

            if (downloadedBytes == 0)
            {
                _logger.Error("Empty trailer downloaded: {0}", trailerFilePath);
                SafeDelete(tempFilePath);
                return TrailerDownloadResult.Failed;
            }

            // 落盘前最后一次本地检查：用户可能在下载期间自己放了预览片。
            if (stillMissing != null && !stillMissing())
            {
                _logger.Info("Local trailer appeared during download, discarding: {0}", trailerFilePath);
                SafeDelete(tempFilePath);
                return TrailerDownloadResult.Aborted;
            }

            // 目标位置若残留零字节文件先删掉，否则下面禁止覆盖的移动会失败。
            // 清理范围严格限定在本次计算出的目标路径，用户放置的其它文件一律不动。
            DeleteIfEmpty(trailerFilePath);

            try
            {
                // 同一目录（同一卷）内的重命名是原子操作，因此最终文件要么完整出现、要么完全不出现。
                // 禁止覆盖：用户在下载期间放进来的文件优先。
                File.Move(tempFilePath, trailerFilePath, false);
            }
            catch (IOException) when (File.Exists(trailerFilePath))
            {
                _logger.Info("Local trailer already exists, discarding: {0}", trailerFilePath);
                SafeDelete(tempFilePath);
                return TrailerDownloadResult.Aborted;
            }

            _logger.Info("Successfully downloaded trailer to: {0}", trailerFilePath);
            return TrailerDownloadResult.Success;
        }
        catch (OperationCanceledException)
        {
            SafeDelete(tempFilePath);

            // 用户主动取消：向上传播，由调用方停止整个流程且不重试。
            cancellationToken.ThrowIfCancellationRequested();

            // 否则就是空闲超时，属于可重试的失败。
            _logger.Warn("Download stalled for {0}s, aborting trailer: {1}", IdleTimeoutSeconds, trailerFilePath);
            return TrailerDownloadResult.Failed;
        }
        catch (Exception e)
        {
            _logger.Error("Error downloading trailer {0}: {1}", trailerFilePath, e.Message);
            SafeDelete(tempFilePath);
            return TrailerDownloadResult.Failed;
        }
    }

    // 安全删除文件：忽略清理过程中的任何异常，避免清理失败再次抛出。
    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 忽略删除临时文件时的异常。
        }
    }

    private static void DeleteIfEmpty(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length == 0)
                info.Delete();
        }
        catch
        {
            // 忽略删除零字节文件时的异常，随后的移动会给出明确结果。
        }
    }
}
