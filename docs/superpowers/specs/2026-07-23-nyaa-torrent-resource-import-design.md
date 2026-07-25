# Nyaa / BitTorrent 资源包一键导入设计

日期：2026-07-23

## 目标

在 Windows Niratan 内提供一个面向 Nyaa 的受控下载入口，并把常见整包内容自动导入现有功能：

- EPUB 进入小说私有书架。
- EPUB + audiobook + SRT 在高置信度时自动完成 Sasayaki 匹配。
- 视频批量进入视频库。
- 同名或 `video.ja.srt` 形式的 sidecar 字幕自动绑定视频。

该功能不改变 Niratan 作为用户可见阅读、视频和 Sasayaki 行为事实源的地位；Nyaa 只作为用户明确触发的资源获取入口。

## 上游调研

### Taiga

Taiga 将 Nyaa 当作 RSS source，而不是依赖未公开 API。其 `feed_source.cpp` 读取 `nyaa:size`、`nyaa:seeders`、`nyaa:leechers` 和 `nyaa:downloads`，说明 RSS 是稳定、低耦合的搜索结果边界。

参考：https://github.com/erengy/taiga/blob/develop/src/v1/track/feed_source.cpp

可借鉴：RSS 字段解析与 Provider 识别。

不可直接复制：Taiga 为 GPL-3.0；Niratan 只采用公开协议层面的设计模式。

### Miru

Miru 的 Nyaa extension 展示了分类、关键字搜索、结果详情和 torrent 下载链接的最小 Provider 形态。

参考：https://github.com/miru-project/repo/blob/main/repo/nyaa.si.js

可借鉴：Provider 与播放器/阅读器解耦。

限制：该 extension 抓取 HTML，页面结构变化风险高；Niratan 改用 RSS，不采用正则解析 HTML。

### Seanime

Seanime 把 torrent provider、torrent client、文件分析、选择和媒体库扫描拆开；多文件 torrent 在拿到 metadata 后先分析文件，再决定下载/导入范围。其内置 client 还处理持久化、队列、速率、端口和错误恢复。

参考：

- https://github.com/5rahim/seanime/blob/main/internal/torrent_clients/torrent_client/smart_select.go
- https://github.com/5rahim/seanime/blob/main/internal/torrents/autoselect/file_selection.go
- https://github.com/5rahim/seanime/blob/main/internal/torrent_clients/builtin_client/builtin.go

可借鉴：Provider / client / analyzer / library 四层边界，以及“不确定就不自动选”的策略。

### MonoTorrent

MonoTorrent 是 MIT 许可的 .NET BitTorrent library。官方 sample 使用 `ClientEngine.AddAsync`、`TorrentManager.StartAsync`、progress/peer 状态和 `StopAsync` 完整管理下载生命周期，并提供 DHT、fast-resume、metadata cache 与随机监听端口。

参考：

- https://github.com/alanmcgovern/monotorrent/blob/master/src/Samples/SampleClient/StandardDownloader.cs
- https://github.com/alanmcgovern/monotorrent/blob/master/src/Samples/SampleClient/MagnetLinkStreaming.cs

选择原因：与 C#/.NET 进程内集成，不要求用户安装、配置并保管 qBittorrent WebUI 凭据；MIT 许可适合直接依赖。

## 架构

```text
NyaaImportDialog
  → NyaaImportDialogViewModel
      → INyaaClient (Nyaa RSS search)
      → ITorrentDownloadService (MonoTorrent lifecycle)
      → IResourcePackageImportService
          → ResourcePackageAnalyzer
          → INovelLibraryService
          → ISasayakiMatchService
          → IVideoLibraryService
```

ViewModel 只管理搜索、状态和命令；网络、磁盘、BT 和媒体库写入全部由 Service 完成。

## 匹配规则

### 小说

1. 整包恰好包含一个 EPUB、一个支持音频和一个 SRT：置信度 1.0，自动匹配。
2. 多资源包：去掉扩展名、括号 release tag、分辨率/codec/语言/`audiobook` 等噪声后计算 token 相似度。
3. 音频和 SRT 同目录增加小幅权重。
4. 最高分低于 0.72，或最高分与第二名相差小于 0.12：不自动匹配。
5. ASS/SSA/VTT 可用于视频，但 Sasayaki 自动匹配当前只接受 SRT。

### 视频

在同目录依次查找：

1. `video.srt` / `video.ass` 等完全同名字幕。
2. `video.ja.srt`、`video.en.ass` 等语言后缀字幕。

每个视频独立导入，单个失败不阻止其他视频。

## 安全与生命周期

- 索引 origin 固定为 `https://nyaa.si/`。
- RSS 详情 URL、torrent URL 以及 HTTP 重定向后的最终地址必须保持 HTTPS、同 host、同 port、无 credentials。
- RSS 响应上限 2 MiB。
- `.torrent` 响应上限 32 MiB，覆盖已确认的 15.1 MiB 多文件合法种子。
- MonoTorrent 注册 metadata 后、开始传输前验证所有 `FullPath` 仍在任务目录内。
- 资源扫描跳过 reparse point；未知扩展只列为 other，不执行。
- 下载取消或失败时，只清理由 Niratan 在 `Data/TorrentDownloads` 下新建且经过根目录验证的任务目录。
- 下载完成立即停止 torrent；任务在应用会话内由独立 manager 管理，关闭搜索对话框不取消任务。v1 不提供跨进程队列恢复、后台做种或通用 tracker UI。
- EPUB 继续走现有 zip slip 防护；Sasayaki 资源复制进书籍私有目录，避免后续清理下载目录导致引用失效。

## v1 范围与后续

v1 完成前台搜索、会话任务队列、进度、暂停/继续/取消/重试、筛选排序、自动分析和导入。

后续可独立增加：

- metadata 到达后先展示 torrent 文件列表并允许选择，减少不需要的文件流量；
- 后台持久化下载队列与断点恢复；
- 下载目录管理和安全清理；
- 多卷 audiobook/SRT 的章节级匹配；
- 可配置 Nyaa mirror，但仍需 origin allowlist 与证书校验；
- 用户明确需要时再增加 qBittorrent/Transmission provider，不替换内置 client。
