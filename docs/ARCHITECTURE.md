# Hoshi 架构文档

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
| 分页 | CSS multi-column | Hoshi 风格直接章节加载 + `column-width: var(--page-width)` 分页 |
| JS 层 | `reader-bridge.js` | Hoshi 风格分页/进度/翻页，无嵌套 shadow DOM/iframe |

foliate-js 已于 2026-05-19 移除，禁止引回主阅读链路。

### 1.3 字典引擎

| 项 | 选型 | 原因 |
|---|---|---|
| 字典后端 | hoshidicts (C# P/Invoke) | 与 Hoshi Android 行为一致 |
| 字典格式 | Yomitan zip | 生态成熟，可直接导入 |
| 变形还原 | C# 重实现 | 对齐 Android `deinflector.cpp` |

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

不引入 EF Core 或第二套数据库技术。外部音频数据库仍按原有只读边界访问，不成为 Hoshi 的业务真源。

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
Hoshi.slnx

Hoshi/
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

Hoshi.Tests/
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
      → window.hoshiReader 接管渲染
```

### 3.2 IPC 消息

C# → JS:

| 消息 | 用途 |
|---|---|
| `setChapter` | 章节信息 (index, totalChapters) |
| `restoreProgress` | 恢复阅读进度 (0-1) |

JS → C#:

| 消息 | 用途 |
|---|---|
| `readerReady` | bridge 就绪 |
| `chapterReady` | 章节渲染完成，含诊断状态 |
| `pageChanged` | 翻页事件 (direction, result, progress) |
| `restoreCompleted` | 进度恢复完成 |
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
- 弹窗定位对齐 Android `LookupPopupLayout`：横排只在选区下方空间足够时放下方。

### 4.4 变形还原

`JapaneseDeinflector` 对齐 Android `hoshidicts/deinflector.cpp`：
- 条件位与 Android `Conditions` 语义一致。
- `AddRule(...)` 输入/输出条件、规则组名称和说明与参考实现一致。
- 特殊动词与例外规则不能被通用后缀规则吞掉。
- `PosToConditions()` 正确解析 Yomitan term `rules`。

---

## 5. Anki 集成

- 使用 AnkiConnect HTTP API。
- 功能：测试连接、deck 列表、note type 列表、字段列表、创建 note、重复卡检查。
- Anki 逻辑不写在 ViewModel 里。
- 调用链：`ReaderViewModel → IAnkiService → AnkiConnectClient`

模板变量：
```
{expression} {reading} {glossary} {sentence} {sentence_with_highlight}
{book_title} {author} {location} {dictionary} {pitch} {frequency}
```

---

## 6. 数据模型与持久化边界

### 6.1 小说文件布局

```text
AppData/Roaming/Hoshi/Novels/
  book_order.json
  shelves.json
  novel_storage_migration_v1.json
  <book-id>/
    metadata.json
    bookmark.json
    bookinfo.json
    statistics.json
    highlights.json
    <book-id>.epub
    ...受控解包资源
```

- `metadata.json` 是书名、作者、相对 EPUB/封面路径、导入与最近打开时间的真源。
- `bookmark.json` 保存章节、逻辑进度和字符位置；Reader 每次保存只写一次 canonical bookmark。
- `bookinfo.json`、`statistics.json`、`highlights.json` 按 Niratan/Hoshi sidecar 语义独立演进。
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

### 6.3 SQLite 边界与旧数据迁移

- `IVideoDataService` / `VideoDataService` 是主 App SQLite 的唯一业务入口，数据库迁移只创建视频相关表。
- 旧 `NovelBooks`、`NovelReadingProgress`、`NovelReaderSettings` 仅由 `NovelStorageMigrationService` 在启动时读取。
- 迁移顺序固定为：备份数据库 → 导出 sidecar → 重扫并校验 manifest → 同一事务退役旧小说表 → 最后原子写完成 manifest。
- 任何导出或校验失败都 fail closed：保留旧表与备份，小说写入切为只读，原始文件不删除。
- 如果进程在退役旧表后、写 manifest 前中断，下次启动校验文件目录后补写 manifest，不重建小说 SQLite 表。

---

## 7. 性能规则

### 7.1 阅读器

- 尽量不要整本 EPUB 一次性读入内存。
- 切换阅读设置时尽量复用 WebView2。
- 阅读进度写入要 debounce。
- 在翻页停止、App suspend、关闭书籍时保存进度。
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
