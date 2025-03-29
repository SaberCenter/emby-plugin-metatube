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
    // 使用静态 HttpClient 以避免套接字耗尽问题
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private const int BufferSize = 8192; // 8KB buffer for downloading
    private const int DownloadTimeoutSeconds = 30; // 30秒下载超时

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
        try
        {
            // 创建一个30秒超时的令牌
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

            // 获取文件大小
            var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, combinedToken);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? -1L;

            // 创建目录（如果不存在）
            var directory = Path.GetDirectoryName(trailerFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 下载文件
            using var contentStream = await response.Content.ReadAsStreamAsync(combinedToken);
            using var fileStream = new FileStream(trailerFilePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);
            
            var buffer = new byte[BufferSize];
            long downloadedBytes = 0;
            var startTime = DateTime.UtcNow;

            while (true)
            {
                // 检查是否超时
                if ((DateTime.UtcNow - startTime).TotalSeconds > DownloadTimeoutSeconds)
                {
#if __EMBY__
                    _logger.Warn("Download timeout for trailer: {0}", trailerFilePath);
#else
                    _logger.LogWarning("Download timeout for trailer: {0}", trailerFilePath);
#endif
                    return false;
                }

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
            // 如果文件部分下载，则删除它
            if (File.Exists(trailerFilePath))
            {
                File.Delete(trailerFilePath);
            }
            return false;
        }
        catch (Exception ex)
        {
#if __EMBY__
            _logger.Error("Error downloading trailer: {0}", ex.Message);
#else
            _logger.LogError("Error downloading trailer: {0}", ex.Message);
#endif
            // 如果文件部分下载，则删除它
            if (File.Exists(trailerFilePath))
            {
                File.Delete(trailerFilePath);
            }
            return false;
        }
    }
} 