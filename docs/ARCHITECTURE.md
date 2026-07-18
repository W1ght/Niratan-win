# Niratan 架构文档

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
- hoshidicts 作为“字典查询后端”；主 App SQLite 只保存视频业务数据，不保存小说、书架或小说统计。
- 高频字典查询数据不塞进主 App SQLite。
- `native/hoshidicts/` 不可修改，所有功能通过 C API DLL P/Invoke 实现。

### 1.4 App 数据存储

| 项 | 选型 | 原因 |
|---|---|---|
| 小说/书架/统计 | Niratan 兼容 JSON sidecar | 每本书可独立迁移、备份和同步，文件即真源 |
| 视频业务数据 | SQLite | 保留现有视频功能的关系型查询与迁移能力 |
| 旧小说迁移 | Dapper + Microsoft.Data.Sqlite（只读入旧表） | 一次性导出后退役旧小说表 |
| JSON | System.Text.Json + 原子替换 | 强类型、可恢复，不暴露半写文件 |

不引入 EF Core 或第二套数据库技术。外部音频数据库仍按原有只读边界访问，不成为 Niratan 的业务真源。

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
    Pages/           NovelReaderPage, NovelLibraryPage, SettingsPage, DictionarySettingsPage
    Dialogs/         ReaderAppearanceDialog, ReaderChapterListDialog
    Dictionary/      DictionaryLookupPopup, DictionaryPopupOverlay
  ViewModels/
    Pages/           NovelReaderPageViewModel, NovelLibraryPageViewModel, SettingsPageViewModel
    Components/      NovelBookItemViewModel, SasayakiViewModel
  Models/
    NovelBook, DictionaryEntry, AnkiTemplate, ...
    Settings/        AppSettings, ReaderSettings, DictionaryDisplaySettings, AudioSettings, AnkiSettings
    Anki/            AnkiMiningPayload
    Sasayaki/        SasayakiModels
    Dictionary/      InstalledDictionary
  Services/
    Novels/          NovelLibraryService, NovelReaderContentStyles, EpubParserService
    Dictionary/      DictionaryLookupService, DictionaryImportService, JapaneseDeinflector, PopupHtmlGenerator
    Audio/           AudioService
    Storage/         VideoDataService, DatabaseMigrator, NovelStorageMigrationService
    UI/              NavigationService
    Anki/            AnkiService, AnkiHandlebarRenderer, LapisPreset
    Sasayaki/        SasayakiPlayer, SasayakiMatcher
    Settings/        SettingsService
  Web/
    DictionaryPopup/ popup.js
  Helpers/           AppDataHelper

Niratan.Tests/
  Services/          Dictionary tests, Novel tests
```

---

## 3. 阅读渲染架构

### 3.1 章节加载流程

```
EpubParserService 解析 EPUB
  → WebResourceRequested 拦截章节 HTML 请求
    → NovelReaderContentStyles.GenerateCss() 注入分页 CSS
    → reader-bridge.js 注入分页/进度/翻页 JS
      → window.niratanReader 接管渲染
```

### 3.2 IPC 消息

C# → JS:

| 消息 | 用途 |
|---|---|
| `setChapter` | 章节信息、目标进度和可选 navigation generation |
| `restoreProgress` | 恢复阅读进度 (0-1)，可携带 navigation generation |
| `jumpToFragment` | 跳到当前章节锚点并回传最终分页进度 |

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

### 3.5 Windows Reader chrome

- 主窗口沿用 Windows 原生 caption buttons，客户区标题栏固定为 32px 空白拖拽区，不在标题栏放应用名称、图标或搜索框。
- Reader 顶部 Acrylic 控制条默认隐藏。隐藏时只有 `y <= 64` CSS px 的空白点击可以打开；打开后任意空白点击关闭。该控制条覆盖在 WebView 上，不参与 viewport 尺寸和分页步长计算。
- 这是 Windows 端相对 Niratan 默认“任意空白切换 focus mode”的明确偏差：桌面窗口顶部需要稳定、容易发现且不干扰正文查词的激活区，64 CSS px 同时兼顾窄标题栏下的命中容错与正文误触控制。
- 专注模式优先级更高：进入或退出专注模式后控制条均保持关闭，必须重新点击顶部激活区；popup 打开时空白点击先关闭已打开的控制条并关闭 popup，不会借此打开控制条。

### 3.6 阅读统计会话与导航事务

`ReaderStatisticsSession` 是阅读时间、字符基线、本地日期 rollover、TTU 统计公式和 `statistics.json` 写入的唯一所有者。`NovelReaderPageViewModel` 只投影状态并转发 typed operation；Page 只分类 WebView2/WinUI 事件。

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
- 图片库外层、缩略图列表和缩放查看使用 WinUI 原生控件；面板按当前 XamlRoot 尺寸尽可能扩展，大图查看器嵌在同一面板中，不关闭或重建图片列表。未读缩略图通过 Win2D `GaussianBlurEffect` + Composition 模糊。`BlurUnreadGalleryImages` 默认开启并持久化到 Reader 设置。
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
- 全局查词对齐 Niratan `SelectionSnapshot`：优先使用 UI Automation 的选区屏幕矩形，Win32 编辑控件回退使用 caret 矩形，均不可用时才使用鼠标位置。
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
- EPUB 封面、视频截图和音频片段必须在字段渲染前完成并验证非空；上传型媒体使用 Anki 返回的稳定文件名生成标签，禁止把应用私有本地路径写入卡片字段。直写 `collection.media` 的视频媒体也必须等待原子替换完成后才允许提交卡片。

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

Profile 行为对齐 Niratan：global lookup 使用 global active profile，书籍优先显式 profile、其次按内容语言选择 primary profile，视频使用视频项的显式 profile。主导航、Reader 和视频窗口在重新成为活动窗口时必须重新激活各自上下文，避免共享 native 查询 session 保留另一个窗口最后使用的 profile。Profile 拥有词典配置、词典展示设置、阅读外观和 Anki mining 设置；新建 profile 必须从当前 active profile 复制这些文件。Windows 设置页在 Active Profile 卡片内列出并切换全部 profile，不引入额外的 “Installed profiles” 概念。

### 6.3 备份与恢复

设置页备份行为以 Niratan `BackupView` 为准，由 `BackupService` 负责文件 IO，ViewModel 只负责命令、进度和文件选择：

- 书籍和词典分别导出无父目录的 `.hoshi` ZIP；文件名使用 `Books_yyyy-MM-dd_HH-mm-ss.hoshi` 与 `Dictionaries_yyyy-MM-dd_HH-mm-ss.hoshi`。
- 书籍恢复覆盖整个 `Data/Novels` 收藏。词典恢复覆盖物理 `dictionaries` 收藏，同时通过 `.hoshi-profiles` 合并 Profile 索引，并覆盖备份中同 ID Profile 的 `dictionary-settings.json` 与 `dictionaries/dictionary-config.json`。
- `.hoshi` 恢复先在受控临时目录解包，拒绝绝对路径、zip slip 和符号链接；目标目录在同卷准备 replacement，再以 `current → previous`、`replacement → current` 交换，失败时回滚。
- 词典目录替换前先清空 hoshidicts session，提交后重新加载 Profile 设置并重建 native query，避免 Windows 文件句柄阻止替换或继续引用旧集合。
- ッツ Backup ZIP 保持 Niratan 的顶层“每书一目录”布局；导出包含 `bookdata_1_6_*`、封面、`statistics_1_6_*` 与 `progress_1_6_*`，导入按原始书名添加新书，并覆盖已有书籍的统计和进度。

Windows 使用系统文件选择器直接写入用户选择的目标路径，不经过 SwiftUI `fileMover`；这是平台 API 差异，归档内容、命名与完成后的用户可见结果保持一致。

### 6.4 SQLite 边界与旧数据迁移

- `IVideoDataService` / `VideoDataService` 是主 App SQLite 的唯一业务入口，数据库迁移只创建视频相关表。
- Windows 视频可选 Anime4K 由 `IAnime4KShaderService` 管理：固定下载 Anime4K `v4.0.1` GLSL，使用 SHA-256 校验并原子写入 `%APPDATA%\Niratan\VideoShaders`；`MpvPlaybackEngine` 只接收强类型预设并通过 `change-list glsl-shaders` 应用，不接受任意 URL、路径或 mpv 配置。入口位于播放器侧边栏“视频增强”，预设仅属于当前播放会话，每次打开视频都强制恢复 `Off`，避免高 GPU 负载被自动继承；这是相对 Niratan macOS 默认画质链路的显式 Windows 可选偏差。
- 视频打开采用首帧优先路径：来源和必要播放属性应用后立即解除暂停；外部字幕 CPU 解析在线程池执行，章节、轨道轮询、交互字幕与侧边栏投影不得阻塞首帧。底部控制栏层级必须高于透明字幕选择画布，重叠区域由控制栏优先接收输入。
- 旧 `NovelBooks`、`NovelReadingProgress`、`NovelReaderSettings` 仅由 `NovelStorageMigrationService` 在启动时读取。
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
- 最近一年窗口以 Windows 本地今天结束。周从周一开始并固定提供 7 个 cell；未来日期没有目标百分比。
- Speed 提供加权、active-day median、最近 7 active days、非重叠 14+14 active days 变化和最快/最慢日期。
- Range 的 year/month/week/day 与 anchor 会重算所有 Dashboard 卡片；Trend 的 day/week/month grain 和 characters/duration/speed metric 独立切换，Ranking 也可按三种 metric 排序。
- Calendar 覆盖最近一年并支持选择日期查看字符、时长和书籍数；目标类型、字符/时长阈值与周目标天数可在 Dashboard 内调整，修改后重算历史目标与 streak 并持久化到应用设置。
- `statistics_dashboard_cache_v1.json` 只是 schema-versioned 派生缓存。key 包含本地日期、书籍身份及 metadata/bookinfo/statistics 文件投影；损坏、key/schema 不匹配或 `NovelLibraryChangedMessage` 只删除缓存自身。命中缓存时先同步展示，再后台重读 sidecar、更新缓存并在 UI 线程发布新 snapshot。
- `NovelLibraryPageViewModel` 只负责 Bookshelf/Statistics 全页切换，并把当前可见书籍与 `NovelShelfState` 交给子 ViewModel；统计格式化、selector、目标设置和 refresh 订阅不再通过父 ViewModel 转发。
- Dashboard 只有一个纵向 `ScrollViewer`。Trend 为全宽卡片；其余九个模块在 `1260` 与 `840` effective pixels 处切换三列、两列和单列布局。selector 行与最近一年七行 Calendar 只允许横向滚动。
- `NovelStatisticsTrendChart` 是纯 UI 控件：消费已经归一化的 display points，在 Canvas 上绘制 Bar 或 `Polyline`，不依赖数据库、sidecar 或第三方图表包，并为每个点保留 tooltip/UI Automation 文本。
- 每次激活创建 generation 与 linked cancellation source。离开 Dashboard、重复进入或书库重载会取消旧 generation；旧 load completion 与排队的 refresh 事件不能覆盖新页面。`SnapshotRefreshed` 只在激活期间订阅并回到捕获的 UI synchronization context 后应用。

---

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
- 视频 SQLite 使用 WAL mode、migration 和统一的 `AppDbConnectionFactory`。
- SQLite schema 不得重新引入小说、书架或小说统计表。
- 没有明确理由不引入 EF Core。

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

- EPUB 内容视为不可信输入。
- WebView2 禁止任意外部跳转。
- 限制文件访问，通过受控 virtual host 提供书籍资源。
- 不要向 JavaScript 暴露宽泛 native API。
- 校验所有来自 WebView2 的消息。
- Bridge API 要窄、明确、强类型。
- EPUB 解包时防止 zip slip，所有条目路径限制在目标书籍目录内。
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
| 中 | 字体/主题变化后位置锚定 | 版式变化影响阅读进度 |
| 中 | 大型 EPUB 性能 | 超长章节、大量图片 |
| 中 | WebView2 字体加载 | CORS 类似的资源限制 |
| 低 | 书架 CRUD | 标准 CRUD 操作 |
| 低 | 设置 UI | 简单数据绑定 |
| 低 | 基础 AnkiConnect 调用 | HTTP API 调用 |

---

## 11. YouTube 远程视频（实验性）

- 产品行为对齐 Niratan，Windows 端使用固定版本 `YoutubeExplode 6.6.0` 在进程内解析元数据、匿名公开流与发布者字幕；该非官方接口具有易失性，UI 必须明确标注“实验性”。
- 不使用 YouTube IFrame/Data API 作为播放链路，因为它们不能同时满足主动画质选择、libmpv 分离流播放、字幕查词与音频制卡；禁止引入 yt-dlp、youtube-dl、Deno、Node、converter/helper 下载或子进程。
- `IRemoteVideoResolver` 是唯一接触 YoutubeExplode 类型的适配边界。其他层只使用 `RemoteVideoIdentity`、`ResolvedRemoteVideoSource`、`VideoPlaybackRequest` 等自有强类型模型。
- 签名流 URL、字幕 URL、请求 headers 和过期时间只驻留内存；SQLite 仅保存 `remote://youtube/{videoId}` 稳定键、原始/规范 URL、远程身份、缩略图 URL与字幕语言。日志只记录 provider/id，不记录签名 URL。
- 解析缓存以稳定键索引，优先使用流 URL 的 `expire`，提前 5 分钟失效；无过期参数时使用 4 小时 TTL。首次播放失败强制刷新一次，随后仅允许一次 muxed fallback。
- 匿名 v1 只支持公开、非直播、非播放列表视频，最高 1080p。画质切换重开流但恢复位置、暂停、音量、速度、延迟、循环、宽高比、旋转与字幕覆盖层。
- 发布者字幕过滤自动生成轨道，下载到应用临时目录并继续走现有 SRT 解析、透明字幕覆盖层、查词和 transcript；不交给 mpv 渲染，也不持久化临时路径。
- 远程挖卡截图复用当前 libmpv 实例；音频导出使用当前解析的音频流或 muxed fallback。挖卡历史保存稳定媒体键，重开时经资料库重新解析，不对远程键调用 `File.Exists`。
