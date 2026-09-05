# Shoko 动画刮削对齐契约

本文定义 Niratan Win 的动画文件识别与刮削边界。长期上游参考是
`docs/reference/ShokoServer`，当前固定在提交
`b6fdba59b6154860948c6fba03f8809ed35894cc`（`v6.0.0-dev.427`）。升级子模块时必须重新核对本文矩阵和相关契约测试。

## 产品边界

Niratan 对齐 Shoko 的内容能力，而不是复制服务器外壳：

- 对齐文件哈希、AniDB release、AID/EID、relation group、TMDB 补充、图片缓存、MyList 和失败恢复。
- 不引入服务端 HTTP API、SignalR、多用户权限、Plex、drop folder、插件装载、文件移动、重命名或物理删除。
- 动画自动刮削只允许 `AniDB -> TMDB`。AniDB 是文件和动画身份真源；TMDB 只补充展示季、简介、演员和图片。
- UDP 与 HTTP API 使用 Niratan 分别注册的 client ID/version；账号验证必须同时覆盖 UDP AUTH 和 HTTP Anime XML，不能借用或冒充 Shoko 的 identity。
- AniList 可作为独立发现源，但不参与本地动画身份匹配。Bangumi 不再注册为视频刮削或发现源。
- 所有源视频、NFO 和 sidecar 保持只读。

## 权威链路

```text
只读扫描 / 文件变化
  -> 单次顺序读取计算 ED2K + CRC32 + MD5 + SHA1
  -> ED2K + file size 内容身份
  -> 持久 import job（去重、重启恢复、指数退避）
  -> AniDB UDP FILE
  -> stored release（FID/AID/release 属性）
  -> file <-> 全部 EID（percentage/isOther/ordinal）
  -> AniDB HTTP anime XML
  -> 替换式 episode/title/tag/creator/character/resource/relation/similar 实体缓存
  -> verified relation graph -> 持久 AnimeGroup
  -> 每个 AID 独立 Series、每个 EID 独立 Episode
  -> TMDB 展示信息与 artwork 补充
  -> MyList 双向对账 / 持久 outbox
```

任何一步失败都不得清空已确认身份、远端缓存、用户覆盖、图片、播放进度或源文件。普通失败最多重试 8 次，30 秒指数退避、上限 1 小时；AniDB ban/backoff 的 `RetryAt` 优先于普通退避。HTTP `<error>` 不得解释为空实体或完成任务；HTTP client identity 被拒绝时任务持久失败并停止重复 HTTP 请求。未显式配置独立 HTTP identity 时，后台不得让每个文件先经历 compatibility identity 的 HTTP 超时/拒绝，应直接进入受限 UDP 降级路径；显式账号验证仍可单次探测 compatibility identity 并给出注册错误。为避免 FILE 已命中的本地集只剩 scanner 骨架，Niratan 可对这些已确认 AID/EID 发出受限 UDP `ANIME`/`EPISODE` 查询并投影核心标题、日期、标签、AniDB 封面和分集标题，但必须把实体及任务保持为 degraded/Needs Review；这不是 Shoko 完整 XML 能力。修正且显式完整验证 HTTP identity 后重排已有 FILE match并再次发布完整投影。App 启动并恢复 catalog 后必须立即启动持久 import/MyList worker，不依赖访问 Video 页面；启动恢复旧误完成任务时，只恢复“已有 FILE match/AID、但缺少 Anime 实体”的记录；当前详情页收到后续投影完成事件后必须原位重载。

Auto/Anime 来源必须等待 AniDB FILE 的确定结论。never/pending/retry/failed、已匹配但 Anime XML 未完成都不得先用标题走 TMDB/TVmaze；Auto 仅在 AniDB 明确返回 unrecognized/ignored 后允许通用回退，Anime 来源则保持待人工 link/rescan。投影完成后再触发一次 `AniDB -> TMDB` enrichment。

## 身份和分组

| 层 | 权威键 | 归属 | 禁止行为 |
|---|---|---|---|
| 物理位置 | catalog asset/location | 一个内容可有多个位置 | 不用路径代替内容身份 |
| 内容/release | `ED2K + file size`，FID 是 AniDB release 属性 | `stored_release` | 不把 FID 写到 Series |
| 分集交叉引用 | EID + percentage + ordinal | file-to-episode xref | 不只取第一个 EID |
| 分集编号 | AID + EID + type + number | Regular、S/C/T/P/O typed episode | 不把 `S1/C1/T1/P1/O1` 压成同一个整数 key |
| AniDB 系列 | AID | 一个 AID 一个 Series | 不把不同 AID 覆盖成一个“季” |
| 作品组 | 持久 group GUID | verified prequel/sequel/story relation component | 不用共享 TMDB、模糊候选或去季标题自动建组 |
| 展示顺序 | TMDB show/order | 展示投影 | 不覆盖 AID/EID 身份 |

`Same setting`、`Alternative version/setting`、`Character` 等弱关系默认不自动合组。人工 group/main series（引入管理 UI 后）必须优先于自动计算。

## 存储位置

Niratan 与 Shoko/Jellyfin 一样，不要求把在线刮削结果写回视频目录：

- 通用视频目录、provider snapshot、cross-reference 和图片索引：
  `%APPDATA%\Niratan\Data\video_library.sqlite3`
- AniDB release/entity/group/job/MyList 状态：
  `%APPDATA%\Niratan\Data\anidb.sqlite3`
- 在线图片内容缓存：
  `%APPDATA%\Niratan\Cache\VideoMetadataArtwork`
- 视频目录中的 `tvshow.nfo`、`season.nfo`、单集/movie NFO 与 poster/fanart/thumb/logo/banner 仅由 `LocalVideoMetadataProvider` 读取，不由刮削器创建、修改或删除。

因此，视频目录里已经存在的 NFO/海报来自用户、Jellyfin、Shoko 或其他工具，而不是 Niratan 在线刮削写入。

## 能力矩阵

| 能力 | Shoko 基准 | Niratan 桌面等价实现 |
|---|---|---|
| 文件身份 | ED2K + size，多哈希诊断 | 同一次读取计算 ED2K/CRC32/MD5/SHA1；ED2K + size 为权威 |
| release | provider release + match attempts | `stored_release`、`release_match_attempt`，按 `ED2K + size` 复用；显式区分 never queried、unrecognized、matched、manual、ignored，并持久化 retry/rescan gate |
| 多集文件 | 全部 EID、百分比、顺序 | normalized xref，并投影到多个 Episode 节点 |
| AniDB graph | AID/EID 与完整 anime XML 实体 | JSON 原始快照加 normalized title/tag/staff/character/resource/relation/similar |
| Group | relation graph、稳定 main series | 持久 GUID、verified 强关系、最早开播 main AID；UI 只消费 group key |
| TMDB | show/movie/order/episode cross-ref | 持久 typed show/order/episode xref、match rating 和 preferred alternate order；仅作为 AniDB 身份后的 enrichment，不得成为动画身份真源 |
| 图片 | provider 图片下载到服务数据目录 | 原子、限大小、主机白名单的 AppData cache；不写媒体目录 |
| MyList | HTTP 全量快照、watched/unwatched 双向规则和持久任务 | 一次 HTTP 拉取完整远端快照并原子保存；按本地 FID 双向对账，watch/unwatch outbox 可恢复 |
| 任务 | DB queue、去重、重试、ban gate | asset import/MyList 持久任务、启动恢复、8 次指数退避、provider RetryAt gate |
| 人工纠错 | release preview/link/unlink/ignored | 权威 `ED2K + size` manual link/unlink/ignore API 直接维护 release 与全部 AID/EID xref；通用 metadata identity lock 不得覆盖它 |

## Re:Zero 等多季度作品

正确模型不是把所有季度改写成一个 AniDB AID 的 S1-S4：

```text
AnimeGroup: Re:Zero（持久 GUID）
  |- AnimeSeries: AID 11370
  |- AnimeSeries: 后续季度 AID
  |- AnimeSeries: 后续季度 AID
  `- AnimeSeries: 后续季度 AID

每个 Series -> 自己的 EID Episode
多个 AID -> TMDB show cross-ref -> 展示为 TV seasons/order
```

播放窗口关闭、详情页重建和 App 重启后，Group、AID/EID、选季和展示顺序都必须来自持久状态，不能靠 ViewModel 的标题猜测重新计算。

## 验证门槛

相关改动至少覆盖：

- 标准 ED2K/CRC32/MD5/SHA1 向量和哈希中途文件变化。
- 老 `anidb.sqlite3` 的非破坏加列/加表迁移。
- 一个文件多个 EID 及 percentage/ordinal。
- 同一 Anime XML 内的 `E1/S1/C1/T1/P1/O1`、两个 AID 各自的 `S1` 与旧的缺失 RawNumber 记录都不会崩溃或静默丢失；C/T/P/O 不生成普通集下载任务。
- 两个不同 AID 同 group；相同 TMDB/标题但不同或缺失 group 时不得合并。
- weak relation 不自动合组；relation 刷新后的稳定 main AID。
- import/MyList job 在 Running 状态退出后恢复，失败退避和尝试历史保留。
- AniDB UDP session/overload/ban 与 HTTP banned gate。
- UDP/HTTP 独立 client identity、HTTP 302 持久失败与同 identity 短路、配置修正验证后的 FILE-match 重排，以及旧误完成任务恢复。
- 无独立 HTTP identity 时后台零 HTTP 请求并立即使用 UDP 降级；App 冷启动不访问 Video 页面也会恢复到期 import/MyList job。
- Auto pending 不调用通用 provider、Auto unrecognized 才回退、AniDB 投影完成触发二阶段 enrichment。
- 在线图片只进入 AppData cache，媒体目录零写入。
- Re:Zero、split-cour、OVA/Special、movie 和单文件多集 fixture。

测试不得访问用户现有媒体、凭据、AppData catalog 或实时 AniDB/TMDB。
