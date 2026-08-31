using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using MediaBrowser.Controller.Entities;
#if __EMBY__
using MediaBrowser.Model.Logging;
#else
using Microsoft.Extensions.Logging;
#endif

namespace Jellyfin.Plugin.MetaTube.Download;

public class TrailerDownloader
{
    private readonly ILogger _logger;
    // 使用静态 HttpClient 以避免套接字耗尽问题。
    // 下载超时统一由 DownloadTimeoutSeconds 令牌控制，故禁用 HttpClient 自身超时，
    // 避免实例级 30s 超时与下载级 300s 超时混用造成的混淆。
    private static readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private const int BufferSize = 8192; // 8KB buffer for downloading
    private const int DownloadTimeoutSeconds = 300; // 300秒下载超时

    // 下载中的临时文件后缀。先下载到临时文件，完整且校验通过后再原子重命名为最终文件。
    private const string TempFileSuffix = ".tmp";

#if __EMBY__
    public TrailerDownloader(ILogger logger)
#else
    public TrailerDownloader(ILogger logger)
#endif
    {
        _logger = logger;
        // 移除实例级HttpClient的创建
    }

    public async Task<bool> DownloadTrailerAsync(string trailerUrl, string trailerFilePath, IProgress<double> progress, CancellationToken cancellationToken)
    {
        // 先下载到临时文件，下载完整并校验通过后才原子重命名为最终文件。
        // 这样任何中断（网络失败 / kill -9 / 断电）都只会留下 .tmp，最终的 mp4 不会出现，
        // “文件存在即跳过”因此始终是可靠的判断，不会把半截文件误认为有效。
        var tempFilePath = trailerFilePath + TempFileSuffix;

        try
        {
            // 创建一个300秒超时的令牌
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(DownloadTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            var combinedToken = linkedCts.Token;

            // 检查URL是否有效
            if (!Uri.TryCreate(trailerUrl, UriKind.Absolute, out var uri))
            {
#if __EMBY__
                _logger.Error("Invalid trailer URL: {0}", trailerUrl);
#else
                _logger.LogError("Invalid trailer URL: {0}", trailerUrl);
#endif
                return false;
            }

            // 获取响应头与文件大小
            var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, combinedToken);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? -1L;

            // 创建目录（如果不存在）
            var directory = Path.GetDirectoryName(trailerFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 下载到临时文件（FileMode.Create 会覆盖此前中断遗留的 .tmp）。
            long downloadedBytes = 0;
            using (var contentStream = await response.Content.ReadAsStreamAsync(combinedToken))
            using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true))
            {
                var buffer = new byte[BufferSize];

                while (true)
                {
                    // 超时由 combinedToken（DownloadTimeoutSeconds）统一控制：
                    // 一旦超时，下面的 ReadAsync 会抛出 OperationCanceledException 并被捕获处理。
                    var bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, combinedToken);
                    if (bytesRead == 0) break;

                    await fileStream.WriteAsync(buffer, 0, bytesRead, combinedToken);
                    downloadedBytes += bytesRead;

                    // 更新进度
                    if (totalBytes > 0)
                    {
                        var progressPercentage = (double)downloadedBytes / totalBytes * 100;
                        if (Math.Floor(progressPercentage) > Math.Floor((double)(downloadedBytes - bytesRead) / totalBytes * 100))
                        {
                            progress?.Report(progressPercentage);
                        }
                    }
                }

                // 确保所有数据落盘后再关闭文件流，便于随后的原子重命名。
                await fileStream.FlushAsync(combinedToken);
            }

            // 验证文件大小是否与预期一致
            if (totalBytes > 0 && downloadedBytes != totalBytes)
            {
#if __EMBY__
                _logger.Error("File size mismatch for trailer: {0}. Expected: {1}, Actual: {2}",
                    trailerFilePath, totalBytes, downloadedBytes);
#else
                _logger.LogError("File size mismatch for trailer: {0}. Expected: {1}, Actual: {2}",
                    trailerFilePath, totalBytes, downloadedBytes);
#endif
                // 大小不匹配，删除临时文件
                SafeDelete(tempFilePath);
                return false;
            }

            // 原子重命名：临时文件 → 最终文件。
            // 同一目录（同一卷）内的重命名是原子操作，因此最终文件要么完整出现、要么完全不出现。
            File.Move(tempFilePath, trailerFilePath, true);

#if __EMBY__
            _logger.Info("Successfully downloaded trailer to: {0}", trailerFilePath);
#else
            _logger.LogInformation("Successfully downloaded trailer to: {0}", trailerFilePath);
#endif
            return true;
        }
        catch (OperationCanceledException)
        {
#if __EMBY__
            _logger.Warn("Download canceled or timed out for trailer: {0}", trailerFilePath);
#else
            _logger.LogWarning("Download canceled or timed out for trailer: {0}", trailerFilePath);
#endif
            // 删除部分下载的临时文件
            SafeDelete(tempFilePath);
            return false;
        }
        catch (Exception ex)
        {
#if __EMBY__
            _logger.Error("Error downloading trailer: {0}", ex.Message);
#else
            _logger.LogError("Error downloading trailer: {0}", ex.Message);
#endif
            // 删除部分下载的临时文件
            SafeDelete(tempFilePath);
            return false;
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
}
