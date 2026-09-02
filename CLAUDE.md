# CLAUDE.md

本文件为 Claude Code 在本仓库工作时的指引。

## 项目性质

MetaTube —— **Emby 插件**（fork 自 `metatube-community/jellyfin-plugin-metatube`，上游同时支持 Jellyfin 与 Emby）。
本仓库只维护 **Emby** 一侧。

**只需要编写 Emby 编译相关代码：**

- 只关心 `Debug.Emby` / `Release.Emby` 两个构建配置（定义 `__EMBY__`，`TargetFramework=net8.0`，引用 `MediaBrowser.Server.Core 4.9.1.80`）。
- Jellyfin 侧代码（`#else` / `#if !__EMBY__` 分支、`Jellyfin.Controller` / `Jellyfin.Model` 引用、`Configuration/configPage.html`、`Extensions/JellyfinExtensions.cs`）**保留原样，不需要为其新增功能**；仅在为了让文件仍能编译/结构完整时才顺带改动。
- 新增逻辑写在 `#if __EMBY__` 分支内；改动共享代码时确认不会破坏另一侧的 `#if/#else` 结构。
- 验收标准：`Release.Emby` 构建通过即可，不必验证 Jellyfin 配置。

## 构建

```bash
dotnet build --configuration Release.Emby
```

- Debug 构建：`dotnet build --configuration Debug.Emby`
- 版本号默认取 UTC 时间 `yyyy.Mdd.Hmm.0`；可覆盖：`dotnet build -c Release.Emby -p:Version=2026.831.1200.0`
- 产物：
  - `Jellyfin.Plugin.MetaTube/bin/Release.Emby/net8.0/MetaTube.dll`
  - `Jellyfin.Plugin.MetaTube/bin/Emby.MetaTube@v<version>.zip`（csproj 中 `Zip` target 在 Release 配置下自动打包）
- 安装到 Emby：把 `MetaTube.dll` 拷进 Emby 的 `plugins` 目录后重启服务。
- 仓库内**没有测试项目、没有 lint 配置**，改完跑一次构建即可。
- CI（`.github/workflows/dotnetcore.yml`）只支持 `workflow_dispatch` 手动触发，会同时构建 `Release` 与 `Release.Emby` 并发布 Release + manifest。

## 本机环境（macOS，已就绪）

- .NET SDK `10.0.400`，Host `10.0.11`，`osx-arm64`，路径 `/usr/local/share/dotnet/dotnet`
- 本机只装了 10.x SDK；`net8.0` 目标框架通过 NuGet 引用包还原，**`Release.Emby` 已实测编译通过（0 warning / 0 error，约 8s）**
- 首次构建需要联网还原 NuGet（`MediaBrowser.Server.Core`、`System.Memory` 等）
- `dotnet build` 可能提示 "An issue was encountered verifying workloads"，本项目不依赖任何 workload，可忽略

## 代码结构

- `Plugin.cs` —— 插件入口。Emby 下继承 `BasePluginSimpleUI<PluginConfiguration>` + `IHasThumbImage`（配置界面由特性自动生成，`thumb.png` 作为 EmbeddedResource）；Jellyfin 下才走 `configPage.html`。
- `Configuration/PluginConfiguration.cs` —— 全部配置项。**Emby 下新增配置必须在 `#if __EMBY__` 内加 `[DisplayName]` / `[Description]`（必填项加 `[Required]`），否则配置界面上不显示文案。**
- `ApiClient.cs` —— MetaTube 后端 HTTP 客户端（`/v1/movies`、`/v1/actors`、`/v1/images/*`、`/v1/translate`）。
- `Providers/` —— 元数据与图片 Provider。`BaseProvider` 在 Emby 下额外实现 `IHasSupportedExternalIdentifiers`，`GetImageResponse` 返回 `HttpResponseInfo`（Jellyfin 侧是 `HttpResponseMessage`）。
- `ScheduledTasks/` —— 计划任务：`GenerateTrailersTask`、`OrganizeMetadataTask`、`UpdatePluginTask`。
- `Download/TrailerDownloader.cs` —— 预告片下载（进度、超时统一在此处理）。
- `Translation/` —— 翻译引擎，含 DeepSeek 直连（`DeepSeekClient`、`TranslationEngine`、`TranslationMode`）。
- `Extensions/EmbyExtensions.cs` —— 整个文件包在 `#if __EMBY__` 内，Emby API 适配（`ILogManager.CreateLogger<T>()`、字符串排序等）。
- `ExternalIds/`、`Helpers/`（Levenshtein 匹配、替换表）、`Metadata/`（后端 JSON DTO）。
- `docs/` —— 本仓库自建的设计文档（预告片下载设计、DeepSeek API review）。

## 约定

- Emby 与 Jellyfin 的 API 差异一律用 `#if __EMBY__` / `#else` 在**同一文件内**隔离，不要新建平行文件。
- 日志统一用 `Logger.Info/Warn/Error/Debug("... {0}", arg)` 位置参数格式（Emby 用 `MediaBrowser.Model.Logging.ILogger`，Jellyfin 侧由 `JellyfinExtensions` 提供同名扩展方法适配），不要写 Jellyfin 风格的 `{Name}` 结构化模板。
- 提交信息沿用上游前缀：`Fix(Trailer): …`、`Feature(Emby): …`、`Chore: …`，正文可用中文。
- 远端：`origin` = `SaberCenter/emby-plugin-metatube`，`upstream` = `metatube-community/jellyfin-plugin-metatube`。同步上游时注意保留本仓库的 Emby 侧改动。

## PR 只提到本仓库

**所有 PR 的目标仓库都是 `SaberCenter/emby-plugin-metatube`，base 分支是 `main`。**

创建 PR 时必须显式指定仓库：

```bash
gh pr create --repo SaberCenter/emby-plugin-metatube --base main ...
```

原因：本仓库是 `metatube-community/jellyfin-plugin-metatube` 的 fork，`gh` 在未指定 `--repo` 时会把 PR 开到 fork 的父仓库（upstream）去。这已经误发生过一次（upstream#643，已关闭）。PR 一旦开出就无法删除，只能关闭，会在别人的公共仓库留下永久记录。

同理，`gh pr list` / `gh pr view` / `gh issue` 等命令也都带上 `--repo SaberCenter/emby-plugin-metatube`，避免读到 upstream 的数据。

要往 upstream 提交改动时先跟用户确认，不要自行发起。
