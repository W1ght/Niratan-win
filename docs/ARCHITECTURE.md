# Niratan 架构文档

Niratan Win 是一个同时包含 Reader、Video 和 Manga 的 WinUI 3 应用。三个内容模块拥有独立的资料库与阅读/播放边界，并共享 Dictionary、Popup、Profile、Audio 和 Anki 管线；共享层不得反向依赖某个具体内容来源。快捷键保留各窗口现有 scope，尚未统一的模块不得被文档描述为已经接入。

## 1. 技术栈详情

### 1.1 UI 外壳

| 项 | 选型 | 原因 |
|---|---|---|
| 框架 | WinUI 3 + Windows App SDK | Windows 11 原生 Fluent 风格，Mica/Acrylic 效果 |
| MVVM | CommunityToolkit.Mvvm | 源码生成器，轻量，社区标准 |
| DI | Microsoft.Extensions.DependencyInjection | .NET 内置 DI 容器 |

避免：
- WPF 作为主 UI
- UWP-only API
- 大量 code-behind

### 1.2 EPUB 阅读渲染

| 项 | 选型 | 原因 |
|---|---|---|
| 渲染层 | WebView2 | Chromium 对 CJK 排版、竖排、ruby 支持远强于 WinUI 原生文本控件 |
| 分页 | CSS multi-column | Niratan 行为的直接章节加载 + `column-width: var(--page-width)` 分页 |
| JS 层 | `reader-bridge.js` | Niratan 行为的分页/进度/翻页，无嵌套 shadow DOM/iframe |

foliate-js 已于 2026-05-19 移除，禁止引回主阅读链路。

### 1.3 字典引擎

| 项 | 选型 | 原因 |
|---|---|---|
| 字典后端 | hoshidicts (C# P/Invoke) | 与 Niratan 的 hoshidicts 查词行为一致 |
| 字典格式 | Yomitan zip | 生态成熟，可直接导入 |
| 变形还原 | C# 重实现 | 对齐上游 hoshidicts `src/language/ja/deinflector.cpp` |

重要原则：
- hoshidicts 作为“字典查询后端”；业务真源按模块选择：小说/漫画继续使用 Niratan 兼容 JSON，视频目录使用本应用拥有的 SQLite；字典后端不得直接访问任何业务库。
- 高频字典查询数据不塞进主 App 业务存储。
- `native/hoshidicts/` 不可修改，所有功能通过 C API DLL P/Invoke 实现。

### 1.4 App 数据存储

| 项 | 选型 | 原因 |
|---|---|---|
| 小说/书架/统计 | Niratan 兼容 JSON sidecar | 每本书可独立迁移、备份和同步，文件即真源 |
| 漫画目录/进度 | `Data/Manga/catalog.json` + 可重建缓存 | 源媒体只读，目录、隐藏状态和阅读进度独立持久化 |
| 视频资料库 | `video_library.sqlite3`（SQLite/WAL） | source、层级节点、资产、匹配、metadata、artwork、集合与任务的事务真源 |
| 视频播放历史 | `video_playback_history.json` | 对齐 Niratan 的进度、完成状态、字幕选择和恢复选项 |
| 视频挖卡历史 | `video_mining_history.json` | 对齐 Niratan 的字幕上下文、媒体身份和时间字段 |
| 旧小说迁移 | Dapper + Microsoft.Data.Sqlite（只读入旧表） | 一次性导出后退役旧小说表 |
| JSON | System.Text.Json + 原子替换 | 强类型、可恢复，不暴露半写文件 |

`video_library.json` 只作为一次性 legacy v0 输入和迁移失败时的只读恢复视图；成功迁移后 SQLite 是唯一 catalog 真源，不双写也不回退到过期 JSON。历史 `niratan.db` 不属于新视频库；外部音频数据库仍按原有只读边界访问。

### 1.5 测试

| 项 | 选型 |
|---|---|
| 框架 | xUnit v3 |
| 断言 | FluentAssertions |
| Mock | Moq |
| 覆盖率 | coverlet |

---

## 2. 项目目录结构

```text
Niratan.slnx

Niratan/
  App.xaml / App.xaml.cs
  Views/
    Pages/           NovelLibraryPage, MangaLibraryPage, VideoLibraryPage, SettingsPage
    Manga/           MangaReaderWindow, MangaPageView
    Video/           VideoPlayerWindow and playback surfaces
    Dialogs/         ReaderAppearanceDialog, ReaderChapterListDialog
    Dictionary/      DictionaryLookupPopup, DictionaryPopupOverlay
  ViewModels/
    Pages/           Novel, Manga, Video, Dictionary and Settings page ViewModels
    Components/      Library item, Sasayaki and shared UI projections
  Models/
    Novel/           Novel books, sidecars, statistics and Reader state
    Manga/           Manga catalog, pages, text regions and Reader session
    Video/           Video catalog, playback and mining documents
    Settings/        AppSettings, ReaderSettings, DictionaryDisplaySettings, AudioSettings, AnkiSettings
    Anki/            AnkiMiningPayload
    Sasayaki/        SasayakiModels
    Dictionary/      InstalledDictionary
  Services/
    Novels/          NovelLibraryService, NovelReaderContentStyles, EpubParserService
    Manga/           MangaLibraryService, MangaSourceIndexer, MangaPageProvider
    Video/           VideoLibraryService, playback engine, subtitle and mining services
    Dictionary/      DictionaryLookupService, DictionaryImportService, JapaneseDeinflector, PopupHtmlGenerator
    Audio/           AudioService
    Storage/         VideoDataService, NovelStorageMigrationService
    UI/              NavigationService
    Anki/            AnkiService, AnkiHandlebarRenderer, LapisPreset
    Sasayaki/        SasayakiPlayer, SasayakiMatcher
    Settings/        SettingsService
  Web/
    NovelReader/     reader-bridge.js, selection.js
    DictionaryPopup/ popup.js
    VideoSubtitle/   subtitle-overlay.js
  Helpers/           AppDataHelper

Niratan.Tests/
  Services/          Novel, Manga, Video, Dictionary, Storage and integration contracts
  Web/               Reader and selection runtime contracts
```

---

## 3. 阅读渲染架构

### 3.1 章节加载流程

```
EpubParserService 解析 EPUB
  → WebResourceRequested 拦截章节 HTML 请求
    → NovelReaderContentStyles.GenerateCss() 注入分页 CSS
    → reader-bridge.js 注入私有分页/进度/翻页 bridge
      → 普通分页 / 连续阅读 / VN 屏幕分页共享同一章节事务
```

### 3.2 IPC 消息

C# → JS:

| 消息 | 用途 |
|---|---|
| `setChapter` | 章节信息、目标进度和可选 navigation generation |
| `restoreProgress` | 恢复阅读进度 (0-1)，可携带 navigation generation |
| `jumpToFragment` | 跳到当前章节锚点并回传最终分页进度 |
| `setVisualNovelRevealSpeed` | VN 模式中实时更新逐字显示速度 |

JS → C#:

| 消息 | 用途 |
|---|---|
| `readerReady` | bridge 就绪 |
| `chapterReady` | 章节渲染完成，含诊断状态 |
| `pageChanged` | 翻页事件 (direction, result, progress) |
| `restoreCompleted` | 进度/fragment 恢复完成，回显 navigation generation |
| `internalLink` | 被拦截的同源 EPUB 链接；native 校验并解析到 spine |
| `readerBlankClick` | 已验证的 Reader 空白点击坐标与 viewport；native 决定控制条开关 |
| `error` | 错误信息 |

消息格式: `{ version: 1, type: "...", payload: {...} }`

### 3.3 横排与竖排

```css
/* 横排 */
html, body { writing-mode: horizontal-tb; }

/* 日语竖排 */
html, body {
  writing-mode: vertical-rl;
  text-orientation: mixed;
}

ruby { ruby-position: over; }
```

- EPUB 自带 writing-mode 时优先尊重 EPUB。
- 用户可覆盖：auto / horizontal / vertical。
- 横排、竖排都要保证 popup 坐标正确。
- 注意 ruby 文本提取，不要把振假名错误混进正文。

### 3.4 分页规则

- 分页尺寸必须来自当前 viewport，窗口 resize 后重新计算。
- 高 DPI 下横排分页宽度按 `window.innerWidth` 计算，`devicePixelRatio` 禁止乘进 `--page-width`。
- 翻页 scroll offset 按 `context.pageSize` 对齐，`column-gap` 只作间距，不得加进翻页步长。
- 安全区：`column-width = pageWidth - 2 * safeInline`，`column-gap = 2 * safeInline`。
- reflow 后优先按逻辑进度恢复位置。

#### 3.4.1 VN 屏幕分页

- VN 是用户明确要求的扩展模式；固定的 Niratan 参考版本没有该功能。交互参考 Hoshi Reader Android，但 Niratan 仍是分页、章节事务、查词和统计语义的事实源。
- VN 不建立第二套 EPUB 引擎：章节仍由 native 按 spine 加载，WebView2 将已清洗的当前章节正文划分为居中的段落屏或句子屏。
- 向前翻页在逐字显示未完成时只补全当前屏；再次向前才移动。向后翻页直接显示上一屏完整内容。章节边界仍由 native 决定。
- 章节级原始/可匹配字符偏移必须跨 VN 屏保持稳定，以便书签、进度、标注、查词和 Sasayaki 继续复用现有模型。
- 窗口 resize、字体和排版变化后按逻辑进度重新分屏；不能把 EPUB 内容或宽泛的 native API 暴露给脚本。

### 3.5 Windows Reader chrome

- 主窗口沿用 Windows 原生 caption buttons，客户区标题栏固定为 32px 空白拖拽区，不在标题栏放应用名称、图标或搜索框。
- Reader 顶部 Acrylic 控制条默认隐藏。隐藏时只有 `y <= 64` CSS px 的空白点击可以打开；打开后任意空白点击关闭。该控制条覆盖在 WebView 上，不参与 viewport 尺寸和分页步长计算。
- 这是 Windows 端相对 Niratan 默认“任意空白切换 focus mode”的明确偏差：桌面窗口顶部需要稳定、容易发现且不干扰正文查词的激活区，64 CSS px 同时兼顾窄标题栏下的命中容错与正文误触控制。
- 专注模式优先级更高：进入或退出专注模式后控制条均保持关闭，必须重新点击顶部激活区；popup 打开时空白点击先关闭已打开的控制条并关闭 popup，不会借此打开控制条。

### 3.6 阅读统计会话与导航事务

`ReaderStatisticsSession` 是阅读时间、字符基线、本地 reporting-day rollover、TTU 统计公式和 `statistics.json` 写入的唯一所有者。reporting day 使用 Profile 中 0–1439 分钟的 reset time；边界前的本地时间归入前一天。`NovelReaderPageViewModel` 只投影状态并转发 typed operation；Page 只分类 WebView2/WinUI 事件。

```text
真实阅读移动
  → 保存 canonical bookmark（不触发统计写）
  → Checkpoint(ReadingMovement / AdjacentChapter)

程序化跳转
  → Checkpoint(ProgrammaticDeparture)       // 结算旧位置一次
  → generation-scoped restore/fragment
  → 保存解析后的 canonical bookmark         // 不二次 flush
  → ResetBaseline                           // 新位置重新计时
```

- PageTurn 自动开始只接受真实 `moved`、自然相邻章节或实际 Sasayaki 自动滚动；边界 `limit` 和同进度回调不启动统计。
- On 自动开始发生在普通初始 restore 完成后；程序化 restore 的 generation 回调不被误判为普通打开。
- 目录、字符、搜索、高亮、内部链接、历史前进/后退和显式 Sasayaki 跳转共用程序化事务。
- 内部链接只允许当前 virtual host 且必须解析到 EPUB spine；外部、危险或非 spine 链接不导航。
- Reader history 保存章节/逻辑进度；自然手动翻页保留 back 栈但清空 forward 栈。
- tracking 且未 paused 时，原生一秒计时器只更新内存投影；移动、最小化、关闭等 checkpoint 才落盘。
- 最小化对应 Windows Background checkpoint；返回书架、页面消失和主窗口关闭共用一个可等待、幂等的 Close checkpoint。
- 日期键使用 Windows 本地日期。跨日时先归档旧 Today、建立新日期，再把本次完整 checkpoint 计入新日期，保持 Niratan 当前语义。

### 3.7 Reader 歌词模式

- 歌词模式是 Reader 内的原生沉浸层，只在 Sasayaki 已启用、音频已加载且 SRT 匹配有效时开放；不建立第二套音频或匹配状态。
- `ReaderLyricsViewModel` 投影当前 cue、播放进度、遮罩与横竖排状态，`ReaderLyricsCanvasRenderer` 使用 Win2D 绘制并命中文字；小说正文仍只由 WebView2 渲染。
- 自然播放跨 cue 会把书签推进到匹配的章节/字符并产生阅读 checkpoint；上一句、下一句、15 秒跳转和显式 seek 只更新位置并重置统计基线，不把跳过文本计入阅读量。
- 歌词查词复用 Reader 的 `DictionaryPopupOverlay`、Sasayaki 音频制卡与相邻 cue 上下文，弹窗打开或鼠标悬停时歌词遮罩恢复清晰。
- Windows 竖排歌词使用 Win2D 按文本元素分列，避免用 WinUI `TextBlock` 重写正文；部分日文标点的字形旋转与 macOS 原生纵排可能略有差异，这是 Win2D 文本 API 的平台约束。

### 3.8 Reader 图片库

- `ReaderImageGalleryService` 只扫描 spine 章节中的 `<img src>` 与 SVG `<image href/xlink:href>`，按阅读顺序去重，并把相对 content root 的 JPG/JPEG/PNG 路径写入 `bookinfo.json.images`。外部 URL、data URL、越出 content root 的路径、缺失文件和 `gaiji` 图片全部拒绝。
- 每个运行时图片项同时记录 spine index 与图片标签之前的可读字符比例。`ReaderGalleryProgressPolicy` 用当前章节和章节内逻辑进度判断图片是否已读；未知旧索引保持可见，避免兼容数据永久锁定。
- 图片库外层、缩略图列表和缩放查看使用 WinUI 原生控件；面板按当前 XamlRoot 尺寸尽可能扩展，大图查看器嵌在同一面板中，不关闭或重建图片列表。大图使用左右按钮/方向键在索引内切换；未读大图继续模糊，仅点击图片才在当次图片库会话中显式揭示。
- 未读图片库图片通过 Win2D `GaussianBlurEffect` + Composition 模糊，`BlurUnreadGalleryImages` 默认开启。外观中独立的 `BlurImages` 默认关闭，对 Reader WebView 内非 `gaiji` 的大图施加 CSS 模糊：第一次点击只揭示，再次点击才通过受校验的 `imageTapped` bridge 打开原生大图。两个开关均持久化到 Reader 设置且互不覆盖。
- Hoshi-Reader 仅作为该功能的实现参考。Windows 使用自适应 GridView 和 1×–5× `ScrollViewer` 缩放，是相对 iOS 纵向 sheet 的平台化呈现；小说正文渲染仍只走 WebView2。

---

## 4. 字典查询架构

### 4.1 查询流程

```
用户在 WebView2 中点击或选中文字
  ↓
JS 提取周边文本、句子、选择区域坐标
  ↓
C# 接收 lookup request
  ↓
TextNormalizer 标准化文本
  ↓
DictionaryService 查询词典 + 变形还原
  ↓
词条排序与分组
  ↓
ReaderViewModel 显示 DictionaryPopup
  ↓
用户可一键创建 Anki 卡片
```

### 4.2 核心模型

```csharp
public sealed record LookupRequest(
    string SurfaceText,
    string ContextBefore,
    string ContextAfter,
    string Sentence,
    Rect SelectionRect,
    string BookId,
    string? Location
);

public sealed record DictionaryEntry(
    string Expression,
    string? Reading,
    IReadOnlyList<string> Glossary,
    IReadOnlyList<string> Tags,
    IReadOnlyList<PitchAccent> PitchAccents,
    IReadOnlyList<FrequencyInfo> Frequencies,
    string DictionaryName,
    int Score
);
```

### 4.3 弹窗栈架构

```
NovelReaderPage
  → DictionaryPopupOverlay        // 栈、遮罩、定位、关闭策略
      → root DictionaryLookupPopup
      → child DictionaryLookupPopup...
          → PopupHtmlGenerator
          → Web/DictionaryPopup/popup.js
```

约束：
- 不要用 WinUI TextBlock 重写 Yomitan structured content renderer。
- 根弹窗可预热复用，子弹窗按需创建，嵌套层数有限制。
- 弹窗关闭、滚动、章节切换时清理子弹窗。
- `popup.js` 的 `lookupRedirect` 是嵌套查词入口。
- 弹窗定位接收 writing mode 信息：竖排优先左右，横排优先上下。
- 弹窗定位对齐 Niratan `PopupLayout`：横排只在选区下方空间足够时放下方。
- 全局查词对齐 Niratan `SelectionSnapshot`：优先使用 UI Automation 的选区文本与屏幕矩形，标准 Win32 Edit/RichEdit 回退读取原生选区并使用 caret 矩形；两者都无法读取时，最后执行一次有界的干净 `Ctrl+C` 捕获，以覆盖 Anki、Qt/Chromium 等不暴露可用 TextPattern 的宿主。复制兜底只在热键触发时运行，先保存并在结束后恢复完整 OLE 剪贴板对象，不做后台剪贴板监控；无法取得精确矩形时才使用鼠标位置。
- 全局查词快捷键由统一 `ShortcutRegistry` 的 `global.lookupSelectedText` action 管理，默认 `Ctrl+Alt+D`；`GlobalLookupSettings` 只保存启用状态，不再保存第二份快捷键字符串。快捷键编辑器写入 `ShortcutConfiguration` 后，运行中的全局协调器监听 `ShortcutsChanged`，仅在该 binding 实际变化时注销并重新注册 Win32 hotkey；不支持、被系统占用或注册失败会更新全局查词状态。
- 全局查词按 Niratan `QuickLookupPanelController` 为 popup 栈中的每一层创建一个独立、精确裁切的原生 tool-window HWND。child 的 WebView 选区矩形先加上 WebView 在父 popup 内的真实可视原点，再由父 popup 本地坐标转换为父窗口屏幕坐标；每层按目标显示器 DPI 和工作区独立布局，水平以选区中心对齐并夹取到工作区，垂直只允许以固定间距出现在选区正下方或正上方，因此可以自然越出父窗口边界而不覆盖锚点。每个 HWND 只暴露当前圆角 popup 表面，不出现标题栏、DWM 边框、宿主背景或透明画布余量。全局服务在热键注册时预热两个空窗口，并将关闭的根/子窗口连同已初始化的 WebView2 返回待用池复用，避免连续查词重新支付 WebView2 冷启动成本。点击父层只关闭其后的 child，点击所有 popup 外部清空整栈；窗口保持 non-activating/topmost，不因 Deactivated 自动关闭。该外部子窗口模式默认关闭且只由全局查词宿主启用，小说和视频继续使用原有 `DictionaryPopupOverlay` 内部 Canvas 嵌套。无结果时显示精确裁切的 3 秒状态浮层。

### 4.4 变形还原

`JapaneseDeinflector` 对齐上游 `native/hoshidicts/src/language/ja/deinflector.cpp`：
- 条件位与上游 `Conditions` 语义一致。
- `AddRule(...)` 输入/输出条件、规则组名称和说明与参考实现一致。
- 特殊动词与例外规则不能被通用后缀规则吞掉。
- `PosToConditions()` 正确解析 Yomitan term `rules`。

---

## 5. Anki 集成

- 使用 AnkiConnect HTTP API。
- 功能：测试连接、deck 列表、note type 列表、字段列表、创建 note、重复卡检查。
- Anki 逻辑不写在 ViewModel 里。
- 调用链：`ReaderViewModel → IAnkiService → AnkiConnectClient`
- Popup 同一可见页的查重先在 JavaScript 短窗口内聚合，再由 AnkiConnect 的批量 `canAddNotesWithErrorDetail` 与按需 `multi/findNotes` 完成；短 TTL 缓存与在途合流按 Anki settings generation 隔离，Profile/deck/model/scope 变化必须失效。提交前仍对同一 expression 串行执行最终查重，禁止缓存或并发竞态生成重复卡。
- Popup 的 mining attempt 由 `render generation + page revision + entry + attempt + expression` 标识；只有当前 attempt 可以更新按钮、宿主模块层的制卡反馈和 note ID。Popup 只发布带本地化标题与语义级别的反馈事件，不在自身视觉树内创建 toast；Reader、Video、Manga、Lookup 及全局查词窗口各自在 Popup 外层承载同一反馈组件。不同词条可以并行，同词条普通/上下文制卡共享门控。
- EPUB 封面和上传型媒体在字段渲染前完成并验证非空，使用 Anki 返回的稳定文件名生成标签，禁止把应用私有本地路径写入卡片字段。视频直写 `collection.media` 对齐 Niratan 的 optimistic 路径：先以内容身份生成确定性文件名并立即提交卡片，截图与音频在后台并发生成；后台仍使用同目录临时文件、相同目标合流和原子发布。无法取得直写目录时继续等待媒体生成，并把失败项放入单次 `multi/storeMediaFile` fallback 后才提交。

模板变量：
```
{expression} {reading} {glossary} {sentence} {sentence_with_highlight}
{book_title} {author} {location} {dictionary} {pitch} {frequency}
```

---

## 6. 数据模型与持久化边界

### 6.1 小说文件布局

```text
AppData/Roaming/Niratan/Novels/
  book_order.json
  shelves.json
  novel_storage_migration_v1.json
  <book-id>/
    metadata.json
    bookmark.json
    bookinfo.json
    statistics.json
    highlights.json
    sasayaki_match.json
    sasayaki_source.json
    sasayaki_playback.json
    <book-id>.epub
    ...受控解包资源
```

- `metadata.json` 是书名、作者、相对 EPUB/封面路径、导入与最近打开时间的真源。
- `bookmark.json` 保存章节、逻辑进度和字符位置；Reader 每次保存只写一次 canonical bookmark。
- `bookinfo.json`、`statistics.json`、`highlights.json` 按 Niratan sidecar 语义独立演进。
- `sasayaki_match.json` 是跨端配准真源，严格使用 Niratan/Hoshi 的 `matches + unmatched` 结构；每条 match 自带 `id`、音频时间、文本、章节和字符范围，不保存 Windows 路径或冗余 cue 表。
- `sasayaki_source.json` 只保存 Windows 本地音频/SRT 路径，`sasayaki_playback.json` 独立保存播放位置、延迟、速率和本地 cue 索引；下载或跨端交换配准文件时不携带本机绝对路径。
- 旧 Windows schema v3 在读取时合并 `cues` 与 `matches`，生成 portable match，并把路径拆入 source sidecar；原播放位置在迁移和重新配准时保留。
- `book_order.json` 保存全局/未归档顺序；`shelves.json` 保存自定义书架及书架内顺序。
- 所有路径必须限制在对应书籍目录内；所有 JSON 写入使用同目录临时文件和原子替换。
- JSON 缺失与损坏必须区分。损坏文件保留原件、显示非阻断警告，并禁止归一化流程覆盖它。

### 6.2 服务边界

```text
NovelLibraryPage / NovelReaderPage
  → ViewModel
    → NovelLibraryService / NovelShelfService / NovelStatisticsService
      → NovelBookStorageService / NovelBookSidecarService / NiratanJsonFileStore
```

ViewModel 不访问文件或 SQLite。`NovelShelfService` 串行化所有创建、重命名、删除、移动和排序操作；每次成功写入后返回完整 `NovelShelfState`，ViewModel 再重建 Reading、自定义书架和 Unshelved 投影。Google Drive 远端书籍保持独立 rail，不混入本地书架文件。

Google Drive OAuth token 与客户端密钥只保存在 Windows Credential Manager。令牌刷新返回结构化 `invalid_grant` 时，认证服务必须立即把凭据标记为失效并删除保存项，通过无敏感数据的状态消息把当前设置页切换为“需要重新连接”；不得继续用“存在凭据”冒充已连接，也不得把 token endpoint 原始响应写入用户提示。网络错误和服务端临时失败不能清除仍可能有效的凭据。

Profile 行为对齐 Niratan：global lookup 使用 global active profile，书籍优先显式 profile、其次按内容语言选择 primary profile，视频使用视频项的显式 profile。主导航、Reader 和视频窗口在重新成为活动窗口时必须重新激活各自上下文，避免共享 native 查询 session 保留另一个窗口最后使用的 profile。Profile 拥有词典配置、词典展示设置、阅读外观和 Anki mining 设置；新建 profile 必须从当前 active profile 复制这些文件。Windows 设置页在 Active Profile 卡片内列出并切换全部 profile，不引入额外的 “Installed profiles” 概念。

### 6.3 备份与恢复

设置页备份行为以 Niratan `BackupView` 为准，由 `BackupService` 负责文件 IO，ViewModel 只负责命令、进度和文件选择：

- 书籍和词典分别导出无父目录的 `.niratan` ZIP；文件名使用 `Books_yyyy-MM-dd_HH-mm-ss.niratan` 与 `Dictionaries_yyyy-MM-dd_HH-mm-ss.niratan`。恢复文件选择器继续接受旧 `.hoshi` 备份。
- 书籍恢复覆盖整个 `Data/Novels` 收藏。词典恢复覆盖物理 `dictionaries` 收藏，同时通过 `.niratan-profiles` 合并 Profile 索引，并覆盖备份中同 ID Profile 的 `dictionary-settings.json` 与 `dictionaries/dictionary-config.json`；旧 `.hoshi-profiles` 元数据目录仍可读取。
- `.niratan` 与旧 `.hoshi` 恢复先在受控临时目录解包，拒绝绝对路径、zip slip 和符号链接；目标目录在同卷准备 replacement，再以 `current → previous`、`replacement → current` 交换，失败时回滚。
- 词典目录替换前先清空 hoshidicts session，提交后重新加载 Profile 设置并重建 native query，避免 Windows 文件句柄阻止替换或继续引用旧集合。
- ッツ Backup ZIP 保持 Niratan 的顶层“每书一目录”布局；导出包含 `bookdata_1_6_*`、封面、`statistics_1_6_*` 与 `progress_1_6_*`，导入按原始书名添加新书，并覆盖已有书籍的统计和进度。

Windows 使用系统文件选择器直接写入用户选择的目标路径，不经过 SwiftUI `fileMover`；这是平台 API 差异，归档内容、命名与完成后的用户可见结果保持一致。

### 6.4 视频 SQLite catalog 与独立 JSON 历史

- `IVideoCatalogRepository` / `SQLiteVideoCatalogRepository` 是 catalog 唯一持久化入口。单消费者 `Channel` 串行 SQLite 操作，连接启用 WAL、外键和 5 秒 busy timeout；UI 只消费不可变 `VideoCatalogSnapshot`，SQL 错误保留最后成功快照并作为持久错误展示。
- `%APPDATA%\Niratan\Data\video_library.sqlite3` 保存来源与 provider route、movie/series/season/episode 节点、资产多对多关系、external ID/alias、metadata provenance/锁定、artwork、用户数据、标签/集合、Review 候选、任务 generation、provider cache 与 migration ledger。在线图片只写入 `%APPDATA%\Niratan\Cache\VideoMetadataArtwork`；媒体目录中的 NFO/图片是只读输入，catalog 只保存其字段投影与绝对路径引用，不在源目录生成、修改或删除 sidecar。扫描和网络在仓库队列外执行，只用短事务提交结果。
- `IVideoPlaybackHistoryStore` 继续逐字节兼容 `video_playback_history.json`；`video_mining_history.json` 也不迁移。本地 identity 是标准化绝对路径，远程 identity 是 `remote://<provider>/<id>`；移除 source 或资产不可用不删除历史。
- 进度小于 2 秒不持久化；距离结尾 5 秒以内标记完成；字幕选择独立于进度清理。该边界直接对齐 Niratan `VideoPlaybackHistoryStore`。
- legacy JSON 在进程级锁内完整解析、验证并哈希，导入 app-owned 临时库的单一事务；数量、`foreign_key_check`、`quick_check` 与 ledger 全部成功后才原子提升。原 JSON 永不修改、重命名或删除；失败回滚并只读展示 legacy snapshot，成功后不再读取它。
- 增量扫描每次枚举来源以发现新增/缺失，并以轻量目录/文件名分类检查现有层级；媒体大小或 mtime 未变化时不重复读取 NFO。Local provider 按 Jellyfin 目录语义向上发现 `tvshow.nfo`，分别读取系列 `tvshow.nfo`、季 `season.nfo`、电影 `movie.nfo` 和同名分集 NFO，并把 poster/backdrop/thumb/logo/banner 按 series/season/episode/movie owner 归档；单集同名图片是 episode thumb，不得污染 series poster。完整重读使用同步语义：已删除的 Local 字段、external ID 与图片引用同时从 catalog 移除，重复扫描不得累计相同 artwork。`jellyfin-folder-hierarchy-v11` 与 `local-sidecar-scopes-v12` 兼容修复会一次性把本地资产标记为待重解析；v12 还先按 null-safe artwork identity 去重并重映射用户首选图片，成功后恢复普通增量规则。显式 Movie 来源若曾被旧逻辑建成单资产 episodic hierarchy，只在无 Local/锁定/用户状态且不共享节点时安全降级，其他情况保留层级并重新进入 Needs Review。任务按“发现文件 / 读取元数据 / 保存 catalog”分段上报；文件分析最多四路并行，结果仍按自然路径顺序以短事务分批提交，UI 进度节流而不逐文件重载 snapshot。只有完整枚举才标记未见资产不可用；取消、权限/I/O 错误和迟到 generation 都不能制造丢失。来源重叠以多对多 membership 去重，媒体目录和 NFO/图片 sidecar 始终只读。
- `IVideoFileNameParser` 仅对匹配副本执行 NFKC/全角数字规范化，识别季集、多集、绝对集数、第 N 話、第 N 期、`S3`/`3rd Season`、cour、SP/OVA/OAD/NCOP/NCED、电影年份与显式 TMDB/TVDB/AniDB/AniList/MAL/Bangumi ID，并把集号后的副标题单独保留为 episode title；显式 Movie 来源保留标题中的 OVA/PV/SxxExx 字面量，不据此制造分集。系列所有权优先来自 `Show/Season/Episode` 目录；来源根本身是单一发布包时才以根目录和正篇标题作兼容回退，无法确定 owner 的平铺混合来源不得跨作品激进合并。Shoko renamer/import 目标目录作为本地 Anime 来源时，新发现或变化文件进入同一分类器；不读取或迁移 Shoko 自身的旧 catalog。
- 显式 `Season 00` / `Specials` / `S00Eyy` 继续作为有编号 Special；`PV`、`menu`、trailers、featurettes、shorts、NCOP/NCED 与 extras 作为无编号 supplemental 投影到同一系列的 Special Features，不按扫描顺序伪造集号，也不进入正篇计数、Next Up 或自动连播。多集文件只建立一个逻辑 Episode，结束集号保留在 media asset；文件名、目录和媒体字节均不改写。
- metadata 合并顺序固定为用户锁定/人工绑定、Local NFO/图片、主 provider、补充 provider 填空。动画采用 Shoko 式固定 `AniDB -> TMDB` 身份链：文件名、NFO 或 FILE 响应中的 AniDB AID 作为 catalog `AnimeSeries` 主身份，每个不同 AID 保持独立 series node；TMDB 只能补充展示季、简介、演员、图片和 cross-reference，不得替换已确认的 AID/EID 身份。AniList 仅供视频发现页及其在线详情，不参与本地动画 catalog 刮削或资料库详情补全；Bangumi 不注册为视频刮削或发现 provider。旧 NFO、文件名和 catalog 中已经存在的 Bangumi external ID 可继续作为只读历史数据解析，但不会触发网络请求、匹配或身份合并。provider 自动发现的 external ID 只作为后续查询提示，不因同一节点上另一个锁定 ID 而反向升级成人工身份锁；FILE 已确认 AID 后，未锁定的旧 TMDB ID 也不得作为显式候选。AniDB 完整客户端使用 Niratan 自己分别获批的 UDP 与 HTTP client ID/version 以及用户账号；兼容旧设置时显式账号验证可暂时探测 UDP identity 的 HTTP 注册状态，但后台只有在配置了独立 HTTP identity 时才请求 Anime XML，完整验证必须同时通过 UDP AUTH 与 HTTP Anime XML。App 启动恢复 AniDB catalog 后立即实例化持久 import/MyList worker，不依赖用户打开 Video 页面。扫描完成后在后台一次读取计算 ED2K/CRC32/MD5/SHA1，通过受串行限流和 ban/backoff 保护的 UDP AUTH/FILE/MYLIST 协议识别文件，并用安全 XML HTTP API 持久化 Anime、Episode、Relation、Tag、Creator 与 MyList 状态。HTTP `<error>` 是持久任务失败而不是空实体；client identity 被拒绝或未显式配置时保留已有哈希、FID/AID/EID并停止/跳过重复 HTTP 请求，同时仅针对本地 FILE 已确认的 AID/EID 使用受同一 UDP 限流保护的 `ANIME`/`EPISODE` 精确回退，投影标题、日期、标签、封面文件名和本地分集标题。该实体必须标记为 degraded，metadata 任务保持 Needs Review、AniDB import job 保持 AnimeMetadata 失败，不能冒充完整 Shoko Anime XML；修正并显式验证 HTTP identity 后自动重排 FILE match，并以完整投影再次通知 UI。旧版本误标为完成但缺失 Anime 实体的任务在启动时恢复到 AnimeMetadata 阶段。启动时已经落库的投影按当前 scrape generation 和联网同意安全重放，AniDB 后续单资产完成会立即重载当前系列详情，不要求用户退出页面再进入。Auto/Anime 来源在 FILE 仍为 never、pending、retry、failed 或已匹配但 Anime XML 未完成时不得抢跑到 TMDB/TVmaze；只有 Auto 来源得到明确且尚未到重试时间的 unrecognized/ignored 结果后才允许通用回退，AniDB 投影完成则触发 `AniDB -> TMDB` 二阶段丰富。哈希、release、FID/EID/AID、关系、分组、持久任务和 MyList 独立保存在可迁移的 `anidb.sqlite3`，账号密码只存 Windows Credential Manager；哈希只读媒体，任何失败不阻断扫描或改写媒体/NFO/海报。AniDB 分集身份必须保留 `(AID, EID, type, number)`；展示可使用 `S/C/T/P/O` RawNumber，但不能只以整数集号建 key 或跨 AID 静默去重。常规集冲突先匹配本地 EID，再按 metadata 完整度和稳定 provider identity 选择；Credits/Trailer/Parody/Other 属 supplemental，可展示但不得伪造普通 `S00Eyy` 下载。播放完成/清除进度分别排队同步 viewed 状态，关联深度由设置限制；不读取或迁移 Shoko catalog，也不得冒充 Shoko 的已注册客户端。Series/Anime metadata 只丰富 scanner 已确定的 series owner，不压平 season/episode 绑定；结构化系列下的自动 Movie 结果必须拒绝，不能形成 `Series -> Season -> Movie`。同一 verified relation group 的多个 AID 在 UI 聚合时采用持久 group main series；各 AID 按已缓存关系组内的开播日期得到稳定展示季号并保留所属 series 海报；不同或缺失 group 的 AID 即使共享标题/TMDB ID 也不得自动合并。TMDB 默认 regular season 只有一个、但存在可展开为多季的 type 7 `TV` episode group 时，详情自动使用该 alternate ordering，保留 Season 0 Specials，并把组内连续 provider 集号重新投影为每季从 1 开始的逻辑集号；alternate endpoint 无效时回退 TMDB 默认顺序。兼容重排可修复未锁定子层级，并保留人工锁定的 series 身份、用户字段和播放状态；清理空 scaffold 只丢弃可重建的 provider cache，Local field/artwork、锁定字段和 node user data 都是删除保护条件。模糊匹配要求总分至少 0.92、领先至少 0.15且无年份/编号硬冲突，否则保留完整候选进入 Needs Review。Unorganized 只表示没有集合覆盖，与 Review 分离。
- 在线 provider 通过 `IVideoMetadataTransport` 的 HTTPS host allowlist、并发门、请求间隔、Retry-After、条件缓存、最多三次幂等重试和取消访问；凭据只进 Windows Credential Manager，30 天 normalized cache 在 SQLite，poster/backdrop/logo、首屏演员头像和相关推荐海报经原子 2 GiB LRU cache。图片下载每个 URL 并发去重，TMDB 同时不超过四路，UI 永不直连 provider CDN。首次联网前必须取得隐私同意。
- metadata 刷新是独立于页面生命周期的后台 `catalog_jobs` 任务：来源扫描完成后只为新增/变化、尚未尝试或 TTL 已过期的可用视频排队，最近一次完整任务同时作为未匹配结果的 30 天负缓存；完整扫描或 mtime 变化可重读并应用 Local metadata，但 owner/binding 未变化时不得取消该负缓存，只有实际层级重绑才使对应来源的完成任务失效。手动“刮削元数据”才强制刷新整个来源。离开 Video 页面不会取消任务，取消来源刮削则只终止该任务并保留已有详情。不同资产最多两路并行，同一 provider 查询仍服从 transport 并发门、请求间隔和 `Retry-After`；相同的幂等查询以 cache key 合并，等待者在首个请求写入 30 天 catalog cache 后直接复用。候选始终按来源 route 顺序进入评分。
- 扫描和后台刮削进度只投影在 Video 的 `Import` 任务区；来源管理使用主内容区全宽卡片，不使用固定宽度 `ContentDialog`，并显示扫描/刮削各自的计数、匹配数、待确认数、失败及取消入口。`Home` / `Discover` 不显示内容区命令栏，`Series` 保留搜索和排序，`All videos` 另集中提供全部、电影、动画、文件夹、集合和标签筛选；`Import` 顶部按 Fushi 主动作样式提供带图标和文字的“扫描文件夹 / YouTube / 刷新 / 重新刮削 / 清理全部刮削记录 / 后台任务”六个按钮，来源设置、打开文件夹和移除等单来源操作留在来源卡片内。任务进度与后台任务详情都只属于 `Import` 内容区，切离该页必须关闭或隐藏，不得覆盖其他 Video 页签。在线作品发现是 Windows 扩展：发现页不提供来源、Feed/内容或电影/剧集/动画选择器；标题搜索固定使用 `All`，筛选浏览也固定聚合 AniList 与 TMDB，不能由旧 `ExploreProviderOrder` 或隐藏的首个 Feed 退化为单来源。生产来源严格只有 AniList 与 TMDB；每次标题搜索并发扇出 AniList Anime、TMDB Movie 和 TMDB Series，TVmaze/AniDB/TVDB、下载资源源和字幕源不得混入发现卡聚合。来源优先级固定为 AniList→TMDB，TMDB 内部固定为 Movie→Series，并按该顺序稳定轮询，再按强 ID 优先、无共享 ID 时同年规范化标题相交的规则合并；强 ID 冲突、显式 Movie/Series 冲突或缺失年份不得弱合并。Anime 摘要缺少可靠电影/剧集类型时视为未知；组内有 Movie 时使用电影主身份，否则动画优先 AniList、普通作品优先 TMDB，并合并别名、外部 ID 与缺失图片。筛选浏览的每个逻辑页固定为 20 项；第 N 页必须先累积各来源第 1..N 页形成同一稳定聚合前缀，再按全局偏移切片，不能只聚合各来源第 N 页而丢弃上一来源页尚未进入前一逻辑页的尾部。单来源或 TMDB 单类别失败保留其他结果并显示局部警告，全部失败才返回错误；快速重复搜索或浏览会取消旧 generation，迟到结果不得覆盖新请求。搜索响应携带的海报/背景图地址直接写入应用图片缓存，不得为每个结果再发一次 artwork metadata 请求；AniList 候选以站点 `romaji` 标题为主标题，`native` 作为原题，英文名继续保留在别名中。聚合身份进入在线详情后必须保留补充来源 external ID，并只按精确 AniList/TMDB ID 并发补充缺失文本、演职员、季表和图片；补充来源失败保留主详情，主 provider/item 身份不得被替换，也不得回退到跨源模糊标题搜索。各来源详情继续独立缓存，缓存键包含 provider media kind，避免 TMDB movie/tv 同数字 ID 串缓存。
- 在线发现的动画展示标题统一由 AniList 标题字段负责：`romaji` 为主标题，`native` 为原题，英文仅保留在 aliases。动画电影即使为保持 Movie 路由而继续使用 TMDB 主身份，也从同一聚合组的 AniList 候选取得展示标题；详情有精确 AniList 补充身份时执行相同规则，只替换展示标题而不替换主 provider/item。其他补充字段仍按填空合并；缺少精确 AniList ID 时不得为取得罗马音回退到跨源模糊标题搜索。
- 默认推荐按 Fushi 的概念架组织为 `Trending / Seasonal / Popular`，不得再按 TMDB/AniList provider 或电影/剧集来源拆架。Trending 与 Popular 在架内聚合 AniList、TMDB Movie 和 TMDB Series，Seasonal 只使用 AniList；三架全部加载后还要在已取得的候选窗口间复用同一精确 ID/同年别名合并结果，使某架中的 TMDB 卡可以继承另一架已经取得的 AniList canonical title，而不追加跨源模糊请求。
- “清理全部刮削记录”是需二次确认的全局 reset：先停止并排空正在运行的 metadata refresh 与 AniDB 自动导入，再删除 series/season/episode/movie 刮削目录、已导入的 Local/在线字段与图片投影、匹配候选、任务历史、provider 响应缓存、AniDB 在视频 catalog 中的投影、TMDB 映射和 App Data 在线图片缓存；每个保留的媒体资产只重新绑定一个根级 `Unmatched` 占位，因此 Series 页立即为空，All videos 仍可访问源视频。源视频和 Local sidecar 文件、来源与 membership、播放进度、asset 收藏/标签/集合、AniDB MyList、账号、凭据及 `anidb.sqlite3` 中的人工 release/episode link 必须保留；被删除层级上的收藏先迁移到其媒体资产。清理事务递增所有扫描 generation 并取消旧扫描任务，持久 reset marker 阻止普通后台增量扫描和自动 metadata/AniDB 队列立刻重建目录；显式“重新刮削”解除对应来源 marker 并重建 catalog，显式完整扫描也可解除 marker 并重新导入 Local sidecar。
- Home 采用 Jellyfin 式媒体中心层级：`Continue watching`、`Next up`、`Recently added media` 独立横向行，空行隐藏，不显示 `My media` 快捷库，也不重复渲染完整资料库列表。Continue Watching 按系列折叠，仅保留该系列最近播放的一集，并优先横版 thumb/backdrop；系列书架按 series node 聚合并使用竖版 poster。系列详情使用横版 hero、竖版 poster 和可选 logo，原题、简介与年份明确取 series owner，不用某一集 NFO 回填系列资料；同时投影标题、标语、年份区间、分级、评分、状态、类型、标签、工作室、季、正篇、Specials、演员、相关推荐及 provider 归属。图片只读取本地 sidecar 或应用图片缓存，不由 UI 直接加载 provider URL。
- Windows 视频可选 Anime4K 由 `IAnime4KShaderService` 管理：固定下载 Anime4K `v4.0.1` GLSL，使用 SHA-256 校验并原子写入 `%APPDATA%\Niratan\VideoShaders`；`MpvPlaybackEngine` 只接收强类型预设并通过 `change-list glsl-shaders` 应用，不接受任意 URL、路径或 mpv 配置。入口位于播放器侧边栏“视频增强”，预设仅属于当前播放会话，每次打开视频都强制恢复 `Off`，避免高 GPU 负载被自动继承；这是相对 Niratan macOS 默认画质链路的显式 Windows 可选偏差。
- 视频打开采用首帧优先路径：来源和必要播放属性应用后立即解除暂停；外部字幕 CPU 解析在线程池执行，章节、轨道轮询、交互字幕与侧边栏投影不得阻塞首帧。底部控制栏层级必须高于透明字幕选择画布，重叠区域由控制栏优先接收输入。
- 交互字幕面板按当前视频 viewport 的实际可用宽度估算显式换行与自动折行，并为阴影/模糊效果保留垂直空间；窗口缩放或侧栏切换后必须重新布局。只有单条字幕在当前 viewport 内确实无法完整容纳时才缩小该条的有效字号，用户保存的全局字号不变；可见 Canvas 与隐藏选词 WebView 必须使用同一有效字号，保持字符命中坐标一致。
- 仅当物理 `niratan.db` 已存在时，旧 `NovelBooks`、`NovelReadingProgress`、`NovelReaderSettings` 才由 `NovelStorageMigrationService` 在启动时读取；探测不得创建空数据库。
- 迁移顺序固定为：备份数据库 → 导出 sidecar → 重扫并校验 manifest → 同一事务退役旧小说表 → 最后原子写完成 manifest。
- 任何导出或校验失败都 fail closed：保留旧表与备份，小说写入切为只读，原始文件不删除。
- 如果进程在退役旧表后、写 manifest 前中断，下次启动校验文件目录后补写 manifest，不重建小说 SQLite 表。

### 6.5 Niratan 统计 Dashboard

```text
metadata.json + bookinfo.json + statistics.json + shelves.json
  → NovelStatisticsDashboardService（最近一年 immutable snapshot）
    → NovelStatisticsDashboardCalculator（纯计算）
      → Today / Week / Range / Speed / Trend / Calendar / Ranking / Shelves
        → NovelStatisticsDashboardViewModel（展示投影 + selector 生命周期）
          → NovelStatisticsDashboardView（WinUI 全页 Dashboard）
```

- Dashboard 读取当前可见书籍；损坏 `statistics.json` 按书报告并跳过，绝不因扫描或缓存恢复覆盖原文件。
- 总字符/时长包含所有合法记录；速度仅使用 `characters > 0 && readingTime >= 60s` 的贡献，避免短 burst 产生虚高速度。
- 最近一年窗口以配置 reset time 计算出的 Windows 本地 reporting day 结束。周从周一开始并固定提供 7 个 cell；未来日期没有目标百分比。
- Speed 提供加权、active-day median、最近 7 active days、非重叠 14+14 active days 变化和最快/最慢日期。
- Range 的 year/month/week/day 与 anchor 会重算所有 Dashboard 卡片；Trend 的 day/week/month grain 和 characters/duration/speed metric 独立切换，Ranking 也可按三种 metric 排序。Ranking 首屏 12 本并按 12 本递增，range/metric 改变时重置分页；行项目复用本地封面并使用固定数值列，使所有进度轨占用相同宽度。点击后在居中的宽版详情面板中选择日期、编辑字符/时长、删除单日或经确认删除全部统计。修改由 `INovelStatisticsSidecarService` 原子保存为带新 `lastStatisticModified` 的记录或零值墓碑；`INovelStatisticsMutationCoordinator` 在同一本书仍有活动 Reader 时把“当前统计 checkpoint → 外部修改 → Reader 重载”放入同一写队列，避免后续计时覆盖编辑或复活删除记录。损坏或不可用 sidecar 只显示警告，不执行修复或覆盖。
- Calendar 覆盖最近一年并支持选择日期查看字符、时长和书籍数；目标类型、字符/时长阈值与周目标天数可在 Dashboard 内调整，修改后重算历史目标与 streak 并持久化到应用设置。
- `statistics_dashboard_cache_v1.json` 只是 schema-versioned 派生缓存。key 包含本地日期、书籍身份及 metadata/bookinfo/statistics 文件投影；损坏、key/schema 不匹配或 `NovelLibraryChangedMessage` 只删除缓存自身。命中缓存时先同步展示，再后台重读 sidecar、更新缓存并在 UI 线程发布新 snapshot。
- `NovelLibraryPageViewModel` 只负责 Bookshelf/Statistics 全页切换，并把当前可见书籍与 `NovelShelfState` 交给子 ViewModel；统计格式化、selector、目标设置和 refresh 订阅不再通过父 ViewModel 转发。
- Dashboard 只有一个纵向 `ScrollViewer`。Trend 为全宽卡片；其余九个模块在 `1260` 与 `840` effective pixels 处切换三列、两列和单列布局。selector 行与最近一年七行 Calendar 只允许横向滚动。
- `NovelStatisticsTrendChart` 是纯 UI 控件：消费已经归一化的 display points，在 Canvas 上绘制 Bar 或 `Polyline`，不依赖数据库、sidecar 或第三方图表包，并为每个点保留 tooltip/UI Automation 文本。
- 每次激活创建 generation 与 linked cancellation source。离开 Dashboard、重复进入或书库重载会取消旧 generation；旧 load completion 与排队的 refresh 事件不能覆盖新页面。`SnapshotRefreshed` 只在激活期间订阅并回到捕获的 UI synchronization context 后应用。

---

### 6.6 Z-Library 获取与导入（Windows 扩展）

Z-Library 获取不是 Niratan 的用户可见行为，而是 Windows 端显式记录的平台扩展。入口位于小说书架 CommandBar，并通过独立 `ZLibraryDialogViewModel → IZLibraryService → IZLibraryClient` 链路工作；Reader、WebView2 与 JavaScript 不参与认证、搜索或下载。

- 用户必须输入 Z-Access 提供的当前 HTTPS server address；应用不内置或自动发现镜像域名，避免过期域名接收账号凭据。
- email、password 与 server address 只保存在 Windows Credential Manager 的 `Niratan.ZLibrary.Credentials` generic credential 中，不写入 settings JSON、sidecar 或日志。
- 客户端使用非公开 `/eapi` 协议登录，并只通过 `POST /eapi/book/search` 搜索书籍；不调用需要浏览器验证的 `/s` 或 `/fulltext` HTML 页面。
- 搜索支持精确匹配、年份、语言和格式，结果计数与分页读取 EAPI 的 `exactBooksCount` 和 `pagination`。
- 格式默认 EPUB；其他格式可用于查看筛选结果，但只有 EPUB 提供“加入书架”。下载限制为 512 MB，拒绝 HTTP URL、HTML challenge/配额页面、非 ZIP 文件、缺少 `META-INF/container.xml` 或正确 `mimetype` 的归档。
- API redirect 只在 HTTPS 下跟随；credential-bearing POST 不允许跨 origin redirect，下载跨 origin redirect 会移除 session Cookie 和 Referer。
- 下载先落到系统临时目录，验证后调用 `INovelLibraryService.ImportEpubAsync`；现有私有书籍目录、zip-slip 防护、metadata 与 sidecar 写入仍是唯一导入真源。无论成功或失败都清理临时文件。

### 6.7 漫画 JSON 存储与只读媒体边界

```text
MangaLibraryPage / MangaReaderWindow
  → MangaLibraryPageViewModel / MangaReaderViewModel
    → MangaLibraryService / MangaSourceIndexer / MangaTextRegionService / MangaOcrService
      → MangaCatalogStore / MangaPageProvider / SuwayomiService / MihonExtensionService
```

- 漫画目录、阅读进度、隐藏记录和全局 Reader 偏好只写入 `Data/Manga/catalog.json`；`MangaCatalogStore` 使用同目录临时文件和原子替换，不读写旧 `Comics`、`Chapters` 或其他 SQLite 表。
- 用户选择的图片目录、Mokuro、CBZ/ZIP 和 EPUB 都是只读媒体。移除书库卡片、刷新目录、修改封面或读取 Mokuro 不得移动、重命名、改写或删除源文件。
- 压缩包页面按需解到 `Data/Manga/Cache/<book-id>/pages`，条目必须命中已索引页、限制单页解压大小并使用由 App 生成的目标文件名。`book-id` 必须验证为单一安全路径段，最终规范化缓存路径必须仍位于 Manga cache root；目录页同样拒绝 rooted path 和 `..` 越界。
- 普通图片目录只读取直接子级；CBZ/ZIP 排除 `__MACOSX`、`.DS_Store` 和 AppleDouble 项并自然排序。EPUB 页序优先使用 `container.xml → OPF spine → 正文图片引用`，仅在正文未产生页面时才回退到 manifest 图片。
- Mokuro 页按 `img_path` 配对，文字 `box` / `lines_coords` 转为以图片左上角为原点的归一化坐标。Google Lens 几何也在协议边界保留为 WinUI 左上角坐标，不沿用 AppKit 的左下角转换；Reader 用原生 XAML 图像画布呈现文字命中层，并复用共享 Dictionary Popup、嵌套查词和 Anki 服务。漫画查词先按当前 Profile 语言和 scan length 解析候选词，将候选 UTF-16 起点传给制卡高亮；命中页作为 `{book-cover}` mining 媒体，由 Anki 管线按页面内容哈希生成稳定 `niratan_manga_page_*` 文件名，不把源绝对路径暴露给 JavaScript，也不使用可能跨书覆盖的源页 basename。
- 单页、双页和连续布局共用同一个 `MangaReaderViewModel`。阅读方向决定双页排列和物理左右键语义；布局、方向、50%–200% 缩放及源页索引持久化到 JSON。左键命中文字查词，右键移动超过 4 CSS px 后拖动画布，未移动的右键释放打开页菜单；`Ctrl+滚轮` 以 5% 步长缩放。
- 无 Mokuro 文字层时，用户可在 Reader 明确确认上传披露后启动 Google Lens OCR。图片最长边限制为 1500 px；无需缩放且不超过 16 MiB 的已验证图片保留原始编码，其他页面使用高质量插值缩小，避免不必要的二次有损压缩。当前页优先、随后环绕处理；启动命令只创建受控后台 scan 后立即返回，每完成一页就立刻发布该页文字命中层，当前页无需等待全章即可查词。Lens 段落按左上角坐标重建阅读顺序：竖排从右列到左列且列内从上到下，横排从上行到下行且行内从左到右；服务把同一气泡的相邻列拆成多个段落时，按方向、流向重叠和列/行间距重新聚合为一个文字块，远隔段落保持分离。方向对齐 Niratan，以接近 90° 的旋转或明显纵长的文字框判定，段落方向不足时使用多数行，词级几何可用时用于字符命中框。每页结果以 `google-lens-v3-ja-niratan-layout` 引擎签名、页 identity 和源修改时间写入 `Data/Manga/OCR`，暂停、取消、换书和 generation 变化均禁止旧结果回写当前 UI。重新打开 Reader 时，若 OCR 仍处于显示状态且用户已经接受上传披露，则自动按“当前页到末页、再回到开头”的顺序续扫；每页先读取已完成的 OCR cache，并在内存中兼容聚合已有 v3 分列结果，只有缺页才读取页面 payload 并请求 Google Lens。已有 Mokuro 的页面不发送网络请求。
- Mihon 生态有两条显式接入路径。Suwayomi 模式继续连接用户自行管理的服务器；`Data/Manga/suwayomi.json` 只保存服务地址、鉴权模式、用户名和凭据 identity，密码/令牌写入 Windows Credential Manager。源浏览、搜索、章节准备、页面缓存与进度回写使用 Suwayomi `/api/v1`；只允许 HTTP/HTTPS，JSON 和图片分别限制为 16 MiB、256 MiB。
- 直接 Mihon APK 模式对齐 Mangayomi 的桌面分进程方案：主进程不加载 APK、DEX 或 JVM，只通过 `MihonExtensionService` 调用 Niratan 自主管理的 M-Extension-Server sidecar。x64 build/publish 固定包含 M-Extension-Server 1.0.4 与其私有 Java 21 runtime；构建先校验上游 bundle 的固定 SHA-256，再把 runtime、MPL-2.0 和对应源码 notice 复制到输出的 `MihonBridge`。用户界面不提供下载选择、bridge 地址、Java 或 JAR 路径，旧配置中的这些字段不再参与运行。配置保存在 `Data/Manga/mihon.json`，其中 `Repositories[]` 按用户顺序保存多个仓库，旧版单一 `RepositoryUrl` 在读取后无损迁移；APK 与强类型安装清单保存在 `Data/Manga/Extensions`，sidecar 私有工作目录为 `Data/Manga/MihonBridge`。刷新按仓库隔离失败并合并结果，同一 package/source identity 重复时由列表中靠前的仓库优先；移除仓库不删除已经安装的 APK。sidecar 在首次需要执行扩展时使用随机本机端口以及 Windows 分发包要求的内存、`--add-opens` 与 `-noverify` 参数启动，并随服务释放而终止。
- Mihon 仓库必须使用 HTTPS（测试用回环 HTTP 除外），索引限制为 8 MiB；APK 限制为 64 MiB，写入前校验 ZIP、`AndroidManifest.xml`、DEX、条目数并记录 SHA-256。兼容协议只允许固定的 manga 方法并传 Base64 APK 与字符串 `sourceId`；Niratan 的 MPL-2.0 overlay 在 sidecar 内从 SourceFactory 结果中按 64 位 source ID 精确选择，再交给原 invoker，因而同一 APK 暴露的多语言/多 Source 条目可独立安装和调用。overlay loader 还只在 DEX 转换后的私有临时 JAR 内修复 dex2jar 2.4 对近期 R8 无字段 companion/serializer、可唯一识别的无状态 lambda/interceptor，以及 enum/单例错误实例化父类所产生的构造 owner；不改写用户 APK，也不在 App 主进程执行 DEX。overlay 源码与小型 JAR 随仓库保存，构建按固定 SHA-256 校验并在 class path 中先于固定上游 1.0.4 JAR 加载。Niratan 客户端只接受 `localhost` / `127.0.0.1` / `::1` bridge URL，JSON 限制为 16 MiB，图片限制为 256 MiB。原版 M-Extension-Server 可能监听全部网卡且协议没有认证，UI 必须提示用户检查 Windows 防火墙；客户端的回环限制不能被描述成 sidecar 自身只监听回环。
- 漫画主页默认承载本地/在线书架，并在页内使用与 Video library 一致的顶部平面分段入口：`Library` 显示书架，`Discover`、`Extensions` 和 `Source settings` 嵌入复用的 `BrowsePage` 内容。`BrowsePage` 仍保留 App 侧边栏一级入口，便于直接进入来源发现与扩展管理；两条入口共享同一 Browse 信息架构和同样的只读/凭据边界。当前 Windows 扩展运行时只覆盖漫画，因此该页只显示“漫画源 / 漫画扩展 / 来源设置”，不提供无实现的动画或小说扩展页签。三者都是同一导航行的平面页签；来源设置在主内容区承载 Suwayomi 连接与凭据、Mihon 仓库和内置 runtime 状态，不使用遮挡列表的 Flyout，也不暴露 M-Extension-Server 下载、bridge 地址或 Java/JAR 路径。Mihon 仓库配置显示为可添加、编辑和移除的列表，不退化为单值输入框或来源下拉框。漫画源把 Suwayomi 与已安装 Mihon 来源合并为按语言分组、可滚动的全宽列表，点击行尾“热门”后才进入复用书架的来源结果页；当前 bridge/service 没有 Latest 契约，不得把 Popular 误标成 Latest。Suwayomi 返回的同源 `iconUrl` 在列表项实现时按需下载并显示，缺少或无效图标使用稳定的来源占位。Mihon 扩展先按 Mangayomi 的 `<repo>/icon/<package>.png` 读取图标；仓库未发布该资产时按需下载对应 APK，只从受大小限制的 `res/` 光栅资源中选取最大候选并缓存，仍失败才显示拼图占位。Mihon 扩展由独立 `MihonExtensionBrowser` 承载，列表按“已安装 / 语言”分组并虚拟化，支持名称/语言/包名搜索、语言筛选、安装状态优先排序和逐行图标安装/更新；多 Source APK 的每个仓库条目按 source ID 独立安装，不把完整仓库塞进 ComboBox。Suwayomi 与 Mihon 的浏览结果都投影为同一个 `RemoteMangaLibraryItemViewModel`，复用 `NovelBookCard`、`UniformGridLayout` 与封面占位；浏览书架在末尾六项进入布局时依据服务 `hasNextPage` 预加载下一页，按 Provider、来源与查询隔离状态并跨页去重。远端卡片点击先打开共用详情面板，显示完整海报、元数据、继续阅读、可切换的已安装 Mihon 扩展和章节选择；切换扩展按当前标题在目标扩展中检索并重新加载章节，只有用户选择阅读动作后才创建 Reader 会话。Suwayomi 详情通过服务器 `/library` 契约加入或移出书库；直接 Mihon APK 没有服务器书库契约，因此 Windows 扩展把用户明确加入的远端条目保存到 `mihon.json` 的 `Library[]`，并与 Suwayomi `category` 收藏合并显示在在线书架。本地书架仍只来自 `catalog.json`，任何远端收藏都不会写入本地 catalog。
- Suwayomi 来源图标缓存到 `Data/Manga/Cache/Suwayomi/SourceIcons/<server-id>`，页缓存到 `Data/Manga/Cache/Suwayomi/<chapter-id>`；图标 URL 必须回到同一个 Suwayomi origin 和 `/api/v1`，响应必须是受大小限制的图片。Mihon 扩展图标、封面与章节页按 package/source/book identity 缓存到 `Data/Manga/Cache/Mihon`，并在读取已安装 APK 前校验 SHA-256。两者的 Reader session 都是远程临时会话，不混入本地只读媒体 catalog；Mihon sidecar 没有进度 API，因此其页进度当前不跨会话保存。全局布局、方向、缩放与 OCR 披露仍复用 Manga JSON 偏好。

### 6.8 漫画发现元数据源

- Manga `Discover` 使用独立的 `IMangaDiscoveryService` 从网站元数据获取发现分区、搜索结果和网络海报；当前主来源为 Bangumi，AniList 作为可切换补充来源。TMDB 的公开发现接口面向电影、剧集和人物，不作为漫画发现源。
- 元数据卡片只负责展示远端标题、年份、评分、简介来源和缓存海报；点击后先打开漫画详情面板，再按标题、原名和别名遍历已安装 Mihon source，优先精确归一化匹配，命中后进入现有详情、扩展切换、章节和 Reader 流程。未安装或未匹配时详情仍保留，用户可以从扩展列表直接尝试，不写入本地 `catalog.json`。
- Bangumi/AniList JSON 与海报请求分别使用 HTTPS host allowlist、响应大小限制、图片 MIME/文件头校验；海报写入 `Data/Manga/Cache/Discovery`，以 URL 哈希和 sidecar 持久缓存，并用同 URL 串行门闩避免重复下载，可重建，不属于用户目录状态。发现页的 provider/category/search/pagination 状态只存在于页面 ViewModel。

## 7. 性能规则

### 7.1 阅读器

- 尽量不要整本 EPUB 一次性读入内存。
- 切换阅读设置时尽量复用 WebView2。
- 阅读进度写入要 debounce。
- 在翻页 checkpoint、窗口最小化和关闭书籍时保存进度。
- 缓存封面和元数据。

### 7.2 字典

- 字典查询必须 async，不阻塞 UI 线程。
- 缓存最近查询和常见表层词的变形还原结果。
- popup 首屏限制词条数量，详细释义按需展开。

### 7.3 存储

- 小说 sidecar 使用共享原子 JSON store，写入前校验目录边界。
- 元数据损坏时不得覆盖原文件，书架归一化必须暂停。
- 视频 catalog 使用独立 SQLite，播放/挖卡历史保持 Niratan 兼容 JSON；小说、书架、统计和漫画仍不得迁入该库。
- legacy catalog JSON 解码或结构验证失败时保留原字节并进入只读恢复，禁止用空模型覆盖；成功迁移后数据库错误不得回退 JSON。
- provider 图片和 mpv 帧缩略图是可重新生成的 cache；选择和 provenance 进入 catalog，但缓存文件本身不是用户数据真源。
- 除非有明确架构理由并得到确认，否则不要引入 EF Core 或第二套业务持久化技术。

---

## 8. 推荐依赖

C# / .NET：
- CommunityToolkit.Mvvm
- Microsoft.Extensions.DependencyInjection
- Microsoft.Data.Sqlite + Dapper
- System.Text.Json
- Serilog

Windows：
- Microsoft.WindowsAppSDK
- Microsoft.Web.WebView2

测试：
- xUnit v3 + FluentAssertions + Moq + coverlet

JavaScript：
- `reader-bridge.js`、`selection.js`、`popup.js`

字典：
- hoshidicts native interop（不可修改子模块）

---

## 9. 安全规则

- EPUB、CBZ/ZIP、Mokuro、字幕、torrent 元数据、远端响应和 WebView2 消息均视为不可信输入。
- WebView2 禁止任意外部跳转。
- 限制文件访问，通过受控 virtual host 提供书籍资源。
- 不要向 JavaScript 暴露宽泛 native API。
- 校验所有来自 WebView2 的消息。
- Bridge API 要窄、明确、强类型。
- EPUB 解包时防止 zip slip，所有条目路径限制在目标书籍目录内。
- 漫画目录页必须保持在已选择的源根目录内；归档页只能写入由 App 生成文件名的缓存路径，并受单页解压大小限制。来自 catalog 的书籍 ID 不能未经验证参与缓存路径组合。
- Mihon APK 与仓库索引属于可执行的不可信输入；扩展只能在回环 sidecar 中执行，仓库、APK、bridge 方法、响应、媒体 URL、大小与 SHA-256 必须在服务边界校验，主进程不得反射加载 APK/JAR。
- 本地漫画与视频源媒体保持只读；移除 catalog 记录不得删除源文件。
- WebView 使用受控 origin 加载章节和资源，禁止让 EPUB 内容任意访问本机路径。

---

## 10. 高风险区域

| 风险 | 区域 | 说明 |
|---|---|---|
| 高 | WebView2 竖排选择坐标 | 竖排模式下坐标系统与横排不同 |
| 高 | DPI/多显示器 popup 定位 | 不同缩放比下坐标换算 |
| 高 | ruby 文本提取 | 不能把振假名错误混入正文 |
| 高 | Yomitan structured content 渲染 | 结构化释义的 HTML 渲染 |
| 高 | hoshidicts native interop | P/Invoke 打包与内存管理 |
| 高 | EPUB 安全加载 | 本地资源访问控制 |
| 高 | Manga 归档与只读源媒体 | 路径越界、解压炸弹和用户文件保护 |
| 高 | Mihon 扩展 sidecar | 第三方 APK 代码、未认证回环协议、远端响应与进程生命周期 |
| 高 | Video 原生播放与字幕坐标 | mpv 生命周期、DPI、字幕命中与媒体采集 |
| 中 | 字体/主题变化后位置锚定 | 版式变化影响阅读进度 |
| 中 | 大型 EPUB 性能 | 超长章节、大量图片 |
| 中 | 大型漫画与视频资料库 | 延迟解码、缓存、索引刷新和内存压力 |
| 中 | WebView2 字体加载 | CORS 类似的资源限制 |
| 低 | 设置 UI | 简单数据绑定 |
| 低 | 基础 AnkiConnect 调用 | HTTP API 调用 |

---

## 11. YouTube 远程视频（实验性）

- 产品行为对齐 Niratan，Windows 端使用固定版本 `YoutubeExplode 6.6.1`，并在其对部分公开视频返回错误时，通过受限的 YouTube watch page / Android VR player 响应 fallback 解析元数据、匿名公开流和字幕；该非官方接口具有易失性，UI 必须明确标注“实验性”。
- 不使用 YouTube IFrame/Data API 作为播放链路，因为它们不能同时满足主动画质选择、libmpv 分离流播放、字幕查词与音频制卡；禁止引入 yt-dlp、youtube-dl、Deno、Node、converter/helper 下载或子进程。
- `IRemoteVideoResolver` 是唯一接触 YoutubeExplode 类型的适配边界。其他层只使用 `RemoteVideoIdentity`、`ResolvedRemoteVideoSource`、`VideoPlaybackRequest` 等自有强类型模型。
- Windows libmpv 对带 `clen` 的 HTTPS `*.googlevideo.com` 签名流使用只读 callback protocol：真实 URL 与 headers 保持在进程内，由 `HttpClient` 逐段发出有明确起止位置的有限 `Range` 请求，并校验 `206 Content-Range` 与解析时长度；交给 libmpv 的只有不透明会话 URI。其他本地或远程来源不经过该桥。此适配用于规避 Google Video CDN 拒绝 FFmpeg 开放式 `Range: bytes=n-` 后产生的 `MPV_ERROR_LOADING_FAILED`，且不得放宽到任意 host、HTTP 或重定向。
- 签名流 URL、字幕 URL、请求 headers 和过期时间只驻留内存；视频 SQLite 仅保存 `remote://youtube/{videoId}` 稳定键、原始/规范 URL、远程身份、缩略图 URL 与字幕语言。日志只记录 provider/id，不记录签名 URL。
- 视频恢复状态按媒体身份保存进度、字幕选择、播放速度、音频选择及音频/字幕延迟；音轨优先以 ff-index 恢复，并以轨道元数据作唯一匹配回退。音量、硬解、去隔行、HDR、色彩校正和字幕外观是全局偏好；循环、A-B 点、旋转、画面比例、远程临时画质和 Anime4K 是会话态，不自动恢复。
- 解析缓存以稳定键索引，优先使用流 URL 的 `expire`，提前 5 分钟失效；无过期参数时使用 4 小时 TTL。首次播放失败强制刷新一次，随后仅允许一次 muxed fallback。
- 匿名 v1 只支持公开、非直播、非播放列表视频，最高 1080p。画质切换重开流但恢复位置、暂停、音量、速度、延迟、循环、宽高比、旋转与字幕覆盖层。
- 字幕轨道保留发布者字幕和自动生成字幕，发布者轨道优先、无发布者轨道时自动生成轨道作为 fallback；字幕下载到应用临时目录并继续走现有 SRT 解析、透明字幕覆盖层、查词和 transcript，不交给 mpv 渲染，也不持久化临时路径。字幕/页面响应有固定大小上限，只接受 YouTube timedtext 和 googlevideo 来源。
- 远程挖卡截图复用当前 libmpv 实例；音频导出使用当前解析的音频流或 muxed fallback。挖卡历史保存稳定媒体键，重开时经资料库重新解析，不对远程键调用 `File.Exists`。

---

## 12. Nyaa / BitTorrent 资源包导入（实验性）

- `INyaaClient` 只读取固定 HTTPS origin `https://nyaa.si/` 的 RSS，不执行 HTML 抓取，也不接受任意索引站脚本。RSS 上限 2 MiB；详情、torrent URL 和 HTTP 重定向后的最终地址必须同源、同端口且不得包含 credentials。
- `ITorrentDownloadService` 是 BitTorrent 边界。Windows 实现使用固定版本 `MonoTorrent 3.9.0-alpha.unstable.rev0000`，引擎缓存位于应用缓存目录；完成文件默认位于 `Data/TorrentDownloads`，也可为之后新加入的任务选择另一个可写绝对目录。每个任务在入队时冻结自己的下载根，更改设置不会移动、删除或重定向已排队、正在下载和已完成的内容；任务仍使用独立子目录承载不可信 torrent 路径和失败清理。下载完成后停止 torrent，不提供后台做种。`AppSettings.MonoTorrentSettings` 可配置下载根、附加 HTTP(S)/UDP Tracker、监听端口、UPnP/NAT-PMP、DHT、PEX、LPD、连接/文件上限和上下行限速；保存前以唯一探针验证自定义目录可创建且可写，空配置继续解析到兼容默认路径。附加 Tracker 只追加到公开 torrent，私有 torrent 保持自身 announce 列表。该版本是当前 NuGet 可用的预览版，升级必须经过 x64 构建和下载契约测试。
- `INyaaDownloadManager` 持有应用会话内的任务队列。搜索对话框只负责入队，关闭对话框不会取消任务；搜索结果可按可信度/重制/做种状态筛选并按做种数/时间/下载数/体积/标题排序。管理页展示进度与错误，并提供暂停、继续、取消、重试、打开目录、移除记录、状态筛选和排序。
- `.torrent` 元数据限制为 32 MiB，以覆盖文件数量较多的合法资源包。启动下载前必须验证每个 torrent 文件的规范化路径仍位于该任务目录内；资源包递归扫描跳过 reparse point，并再次执行目录边界校验。
- `ResourcePackageAnalyzer` 只分类强类型扩展：EPUB、音频、SRT/字幕和视频。单 EPUB + 单音频 + 单 SRT 直接视为高置信度；多资源包使用规范化文件名评分，低分或前两名接近时不得自动匹配。
- `IResourcePackageImportService` 是唯一编排入口：EPUB 复用 `INovelLibraryService`，有声书/SRT 复制到书籍私有 `Resources/Sasayaki` 后调用 `ISasayakiMatchService`，视频复用 `IVideoLibraryService` 并绑定同名或语言后缀字幕。ViewModel 不直接访问文件存储或持久化实现。
- 下载内容保持不可信。资源包中的可执行文件、脚本和未知格式不会启动或导入；只有用户明确选择的结果才会下载。UI 必须提示用户仅下载有权获取的内容。

## 13. 下载发现与 qBittorrent（Windows 扩展）

- 一级“下载”模块包含“发现 / 下载任务 / 订阅 / 下载设置”四个平面区。发现区复用 `INyaaClient` 的固定 HTTPS Nyaa RSS 搜索，不执行 HTML 抓取；搜索结果只有在用户明确点击后才会发送到当前选择的下载器。Video 发现卡只进入独立详情页，详情中的资源搜索、字幕搜索和订阅管理再分别进入独立页面；页面间使用强类型路由参数，不把获取流程重新塞回发现列表。
- `AppSettings.DownloadBackend` 在下载设置中选择 `MonoTorrent` 或 `Qbittorrent`，默认是内置 `MonoTorrent`。发现按钮、任务列表和任务操作均按当前选择路由；切换不会复制或迁移另一后端已有任务。
- `IQbittorrentDownloadCoordinator` 是发现与 qBittorrent WebAPI 的唯一编排边界。`DownloadsPageViewModel` 不直接访问 HTTP、Credential Manager 或 `ISettingsService` 的持久化细节。
- `QbittorrentApiClient` 只使用 `/api/v2` WebAPI：旧版 WebUI 使用登录 Cookie，qBittorrent 5.2+ 可使用 Bearer API Key；任务页以 qBittorrent 返回的任务状态为准，应用重启后重新读取远端任务，不把 qB 任务复制成第二份本地下载状态。
- 任务列表项点击后打开详情 Panel；概览、文件和 Tracker 页分别读取 qB 的 `torrents/properties`、`torrents/files` 和 `torrents/trackers`，详情不落地为第二份用户状态。取消调用 qB `stop`，恢复调用 `start`，打开位置复用 `IFileRevealService`。
- 删除任务必须先经过 `IDialogService` 确认，确认后仅以 `deleteFiles=false` 从 qB 移除任务；不删除已下载文件。详情 Panel 关闭后才显示确认框，避免嵌套 WinUI `ContentDialog`。
- qB 地址只接受 HTTP(S) origin，不接受 URL 中的 credentials、query 或 fragment；HTTP 只允许 loopback，远程连接必须使用 HTTPS。Nyaa torrent URL 发送前再次限制为 `https://nyaa.si/` 默认 origin。
- qBittorrent 密码和 API Key 只写入 Windows Credential Manager；服务器地址、默认保存路径、默认分类和“暂停添加”写入 `AppSettings.QbittorrentSettings`。不把密码、API Key、Cookie 或 Authorization header 写入日志。
- qB 远程保存路径不被自动当成本机媒体路径。下载完成后的小说、视频或漫画导入必须另行实现路径映射或用户显式选择，不得因为搜索或下载完成而自动改写本地资料库。
- `MonoTorrentDownloadService → NyaaDownloadManager → ResourcePackageImportService` 是内置后端链路，任务在下载页展示并在完成后自动导入；`IQbittorrentDownloadCoordinator → QbittorrentApiClient` 是外部后端链路，任务状态仍以 qBittorrent WebAPI 为准。两套任务列表和恢复语义保持隔离，切换后端只改变后续添加与当前任务视图，不会删除另一后端任务。
- Video 发现详情的获取动作保持 provider 单一所有权：资源搜索和订阅只走固定 HTTPS Nyaa RSS，字幕搜索只走 Jimaku API，不以向 Nyaa 查询追加 `srt` 伪装字幕来源。Jimaku API key 只保存在 Windows Credential Manager；条目/文件 JSON 有大小上限，字幕只接受 Jimaku HTTPS host 上的 SRT/ASS/SSA/VTT。用户可选择“另存为”、现有视频旁或指定目录；重名时生成未占用文件名，服务以同目录临时文件加不覆盖移动完成落盘，绝不替换已有字幕。
- Nyaa 订阅必须先由用户从搜索结果选择一个含发布组、分辨率和可识别单集的非 batch、非 remake 发布版；创建时立即把所选发布版发送到当时固定的下载后端，后续从该集开始（inclusive）检查。订阅持久化 provider identity、查询、Nyaa 分类、发布组、分辨率、精确可信状态、起始集、已见 item id、已处理逻辑季度/集号、启用状态、固定下载后端、最近检查状态，以及封面 URL 与受控 metadata artwork cache 路径；不保存 torrent 内容。下载页订阅卡保留固定 40×60 封面位，缓存缺失时经 `IVideoDiscoveryService` 的受控图片管线重新获取，仍不可用才显示同尺寸占位。新建、手动检查和每 30 分钟检查只处理严格匹配同作品、季度、发布组、分辨率、可信状态与分类的版本，并按逻辑季度/集号防止同集换 item id 后重复入队；内置 MonoTorrent 只有在任务完成后才记为已见，失败可重试，qBittorrent 在远端接受后记为已见。电影成功一次后停用；暂停、替换或移除规则会先取消在途检查。旧 `SubscribedVideoKeys` 仅作为禁用、待重新配置的管理项投影，不静默丢失。移除订阅必须确认，只移除规则和状态，不取消任务、不删除下载或已有媒体。

## 14. Galgame 游戏捕获（Windows 扩展）

- 游戏库使用 `Data/Games/galgame-library.json`，通过 `IGalGameRepository` 与原子 JSON store 读写；损坏文件只报告错误，不自动清空或覆盖。游戏 exe、工作目录和启动参数属于用户配置，源游戏文件保持只读。
- `GalGameSessionService` 只负责会话状态、启动/附着 injector 和停止 helper；注入器、hook DLL、LunaHook 与游戏进程保持独立边界，不链接进 `Niratan.exe`。启动参数先按 Windows `CommandLineToArgvW` 规则拆分，再以重复 `--arg` 传递，避免含空格路径被二次切分。
- `voice_hook/<arch>/` 是随构建输出的离线 helper 运行时。启动前复制到 `%APPDATA%/Niratan/Data/Games/voice_hook_runtime/<content-version>/<arch>/`，注入器永不回退到安装目录副本，以避免游戏进程长期锁住更新文件。
- IPC 仅通过 `Local\\FushiVoiceHook_<pid>` 只读映射读取 `HVH1`、共享版本、稳定 IPC 版本、hook/文本/音频健康头字段。文本 lane、音频 ring 和游戏内查词消息必须继续按 native `voice_hook_ipc.h` 的强类型布局扩展，不能在 C# 中猜偏移。
- 游戏主界面按资料库、工作台、导入和设置分区。资料库只索引用户选择或拖入的 `.exe`，支持名称/路径搜索、状态筛选和排序；导入、刷新、移除都不得移动、重命名或删除游戏文件。工作台承载启动/附着、文本线程、实时台词、音频与浮窗入口，不再把内部诊断矩阵暴露为独立用户页面。
- 游戏浮窗外观随 `AppSettings.GalGameSettings` 持久化，包含字体、字号、字距、行高、粗体、水平/垂直对齐、文字/背景/描边颜色、背景透明度、描边宽度、内边距和圆角。设置变更即时投影到现有浮窗；滑杆连续变化只合并持久化写入。
- native helper 与 Fushi 固定参考保持同一 adapter/IPC 契约，当前共享版本为 v21，并同时打包 x86/x64；C# reader 使用 native `SharedHeader` 的完整尺寸和字段偏移，并在启用查词时发布 native-only 几何准入，不能把 Unity event table 之前的标量前缀误当成整个 header。`engine-support.yaml` 是引擎与具体游戏证据状态的真源。移植代码和离线测试不会自动提升支持等级；没有原始游戏进程证据时仍保持 `implemented_unverified`，不得在支持列表或发布说明中宣称真机可用。
- KiriKiri/KAGEX 的 `TextRender.getCharacters()` 几何传感器在完成 renderer 到 KAG 逻辑消息槽的归属后，复用同一精确整句快照发布 native `TextRender` lane；线程身份按逻辑消息槽区分，不从安装路径、ASLR 地址或未验证 RVA 派生。Games 页从后台轮询收到的 session 状态必须经页面创建线程的 `DispatcherQueue` 投影到 UI，不能在 helper 线程直接修改可观察属性或集合。
