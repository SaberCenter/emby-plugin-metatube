---
name: emby-plugin-api
description: Emby 4.9.1.80 插件 API 的实测事实与反射探测方法。在挂接 ILibraryManager 事件、注册 IServerEntryPoint 或 IScheduledTask、或需要确认某个 Emby 类型/成员是否存在及其签名时使用。
---

# Emby 插件 API 实测记录

Emby 的程序集没有公开文档，`MediaBrowser.Controller.dll` / `MediaBrowser.Model.dll` 也没法 grep。下面是本仓库在 **4.9.1.80 引用程序集 + 4.9.5.0 运行时**上实际验证过的事实。

列表里没有的，用 [PROBE.md](PROBE.md) 的方法自己探，**探完补回本文件**。

## IServerEntryPoint

`MediaBrowser.Controller.Plugins.IServerEntryPoint`：成员只有 `void Run()`，基接口是 `IDisposable`。

**Emby 会自动发现插件程序集里的实现并用 DI 构造**，构造函数可注入 `ILogManager`、`ILibraryManager` 等；启动时调用 `Run()`，卸载/关闭时调用 `Dispose()`。不需要在 `Plugin.cs` 或别处做任何注册。这条在 4.9.5.0 运行时验证过：`Trailers/TrailerEntryPoint.cs` 未做任何注册就被实例化，`Run()` 里的事件订阅生效。

`Run()` 是同步 `void`。后台循环要在里面自己起 `Task.Run`，并由 `Dispose()` 负责关停——Emby 只调 `Dispose()`，不会替你终止已经跑起来的任务。

## ILibraryManager 事件

四个事件，全部是 `EventHandler<ItemChangeEventArgs>`：`ItemAdded`、`ItemAdding`、`ItemUpdated`、`ItemRemoved`。

`ItemChangeEventArgs`：`BaseItem Item`、`BaseItem Parent`、`ItemUpdateType UpdateReason`、`BaseItem[] CollectionFolders`。

### 时序（运行时观察）

新文件入库时：`ItemAdded`（**此时元数据提供器还没跑，ProviderIds 是空的**）→ 提供器执行 → 保存 → `ItemUpdated(MetadataDownload)`。

要等元数据完整就挂 `ItemUpdated`；挂 `ItemAdded` 只会拿到空壳。

图片保存走单独的 `ItemUpdated(ImageUpdate)`，一次刮削会发多次事件，必须按 `UpdateReason` 过滤才不会重复处理。

事件是同步派发的：处理器里做耗时操作会阻塞 Emby 的库更新管道，只应做过滤和入队。

### ItemUpdateType 的取值

`[Flags]`，但 **`None = 1` 不是 0**：

| 成员 | 值 |
|---|---:|
| None | 1 |
| MetadataImport | 2 |
| ImageUpdate | 4 |
| MetadataDownload | 8 |
| MetadataEdit | 16 |

判断"元数据类更新"：`(e.UpdateReason & (ItemUpdateType.MetadataImport | ItemUpdateType.MetadataDownload | ItemUpdateType.MetadataEdit)) != 0`

## 实体类型

`Movie` 和 `Trailer` 都直接继承 `Video`，是兄弟而非父子——`item is Movie` 已经排除了 `Trailer` 实体。

`Video.ExtraType` 是 `Nullable<MediaBrowser.Model.Entities.ExtraType>`。附加内容（预告片、花絮）进库后本身也是 item，同样会发事件；再加一条 `ExtraType == null` 才能把"被解析成 `Movie` 的附加内容"排掉。

`BaseItem` 常用成员：`long InternalId`、`string Path`、`string ContainingFolderPath`、`bool IsVirtualItem`、`bool IsFileProtocol`、`LocationType`、`Guid[] LocalTrailerIds`、`int? LocalTrailerCount`、`string[] RemoteTrailers`。

`item.Path` 对单文件影片是视频文件路径，对文件夹形式的影片是目录——用之前先 `File.Exists` 判一次。

## ILibraryManager 查询

`GetItemById` 有 `long` 和 `Guid` 两种重载。**Emby 用 `long InternalId` 作主键，Jellyfin 用 `Guid`**，共享代码在这里必须 `#if` 分支。

`GetItemList` 有带 `CancellationToken` 的重载，大库查询用它，否则用户停止任务时不会及时生效。

`InternalItemsQuery.HasAnyProviderId` 在 Emby 是 `string[]`，在 Jellyfin 是 `Dictionary<string, string>`；`IncludeItemTypes` 在 Emby 是 `string[]`（用 `nameof(Movie)`），在 Jellyfin 是 `BaseItemKind[]`。

## IScheduledTask

`MediaBrowser.Model.Tasks.IScheduledTask`：`Name` / `Key` / `Description` / `Category` 四个属性，`Task Execute(CancellationToken, IProgress<double>)`，`IEnumerable<TaskTriggerInfo> GetDefaultTriggers()`。

（Jellyfin 侧方法名是 `ExecuteAsync(IProgress<double>, CancellationToken)`，参数顺序也相反。）

`GetDefaultTriggers()` **可以返回空**（`yield break`），任务就只在用户手动运行时执行，需要定时的用户自行在 Emby UI 里添加触发器。

**但 Emby 把每个任务的触发器持久化在自己的配置里**：老版本装过的用户那份默认触发器已经落盘，改代码删掉默认值不会清除它，得让用户在 UI 里手动删。只有全新安装才是干净的无触发器状态。

`TaskTriggerInfo.Type` 是 string，常量挂在 `TaskTriggerInfo` 上：`TriggerDaily`、`TriggerWeekly`、`TriggerInterval`、`TriggerSystemEvent`、`TriggerStartup`。
