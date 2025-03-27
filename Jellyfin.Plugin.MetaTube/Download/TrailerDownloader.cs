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
    private readonly HttpClient _httpClient;
    private const int BufferSize = 8192; // 8KB buffer for downloading

#if __EMBY__
    public TrailerDownloader(ILogger logger)
#else
    public TrailerDownloader(ILogger logger)
#endif
    {
        _logger = logger;
        _httpClient = new HttpClient();
    }

    public async Task<bool> DownloadTrailerAsync(string trailerUrl, string trailerFilePath, IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
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
            var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? -1L;

            // 创建目录（如果不存在）
            var directory = Path.GetDirectoryName(trailerFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 下载文件
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(trailerFilePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);
            
            var buffer = new byte[BufferSize];
            long downloadedBytes = 0;

            while (true)
            {
                var bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                if (bytesRead == 0) break;

                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                downloadedBytes += bytesRead;

                // 更新进度
                if (totalBytes > 0)
                {
                    var progressPercentage = (double)downloadedBytes / totalBytes * 100;
                    progress?.Report(progressPercentage);
                }
            }

#if __EMBY__
            _logger.Info("Successfully downloaded trailer to: {0}", trailerFilePath);
#else
            _logger.LogInformation("Successfully downloaded trailer to: {0}", trailerFilePath);
#endif
            return true;
        }
        catch (Exception ex)
        {
#if __EMBY__
            _logger.Error("Error downloading trailer: {0}", ex.Message);
#else
            _logger.LogError("Error downloading trailer: {0}", ex.Message);
#endif
            return false;
        }
    }
} 