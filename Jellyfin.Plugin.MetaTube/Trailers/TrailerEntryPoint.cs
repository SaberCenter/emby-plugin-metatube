#if __EMBY__

using Jellyfin.Plugin.MetaTube.Extensions;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;

namespace Jellyfin.Plugin.MetaTube.Trailers;

/// <summary>
///     订阅 Emby 的库更新事件，把刮削完成的电影送进预览片队列。
///     处理器只做过滤和入队：不阻塞、不做文件 IO、不下载。
/// </summary>
public class TrailerEntryPoint : IServerEntryPoint
{
    // 只关心元数据被写入的更新。图片保存（ImageUpdate）等无关更新不触发检查。
    private const ItemUpdateType MetadataChanged =
        ItemUpdateType.MetadataDownload | ItemUpdateType.MetadataEdit | ItemUpdateType.MetadataImport;

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;
    private readonly TrailerService _trailerService;

    public TrailerEntryPoint(ILogManager logManager, ILibraryManager libraryManager)
    {
        _logger = logManager.CreateLogger<TrailerEntryPoint>();
        _libraryManager = libraryManager;
        _trailerService = TrailerService.GetInstance(logManager, libraryManager);
    }

    public void Run()
    {
        // 用 ItemUpdated 而不是 ItemAdded：新电影入库时先触发 ItemAdded，
        // 那时 MetaTube 元数据和 TrailerUrl 还没写入；刮削完成后的 UpdateItem 才带完整数据。
        _libraryManager.ItemUpdated += OnItemUpdated;
    }

    public void Dispose()
    {
        _libraryManager.ItemUpdated -= OnItemUpdated;

        // 插件卸载 / 重载时必须一并关停队列：否则后台消费者会继续用已失效的 Emby 服务对象工作。
        _trailerService.Shutdown();

        GC.SuppressFinalize(this);
    }

    private void OnItemUpdated(object sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e == null) return;

            // 按更新原因过滤。
            if ((e.UpdateReason & MetadataChanged) == 0) return;

            // 按类型过滤。Trailer 与 Movie 同为 Video 的子类，类型判断已能排除 Trailer 实体；
            // 追加 ExtraType 判断是为了排除被解析成 Movie 的附加内容——
            // 下载完成的预览片被 Emby 扫入库后本身也是一个 item，同样会触发 ItemUpdated。
            if (e.Item is not Movie movie || movie.ExtraType != null) return;

            // 按可用性过滤：虚拟项和非本地文件没有可用路径。
            if (movie.IsVirtualItem || !movie.IsFileProtocol) return;

            _trailerService.Enqueue(movie.InternalId);
        }
        catch (Exception ex)
        {
            _logger.Error("Handle item updated event error: {0}", ex.Message);
        }
    }
}

#endif
