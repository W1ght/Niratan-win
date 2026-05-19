# Windows 日语 EPUB 阅读器技术栈与架构选型

## 0. 开发环境

- **目标平台**: Windows 10+ x64
- **默认构建**: `dotnet build -p:Platform=x64`
- **测试启动**: 构建 x64 后启动 `Hoshi\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Hoshi.exe`
- **构建 + 启动脚本**: `.\build-and-run.ps1`
- 不默认构建 ARM64，仅在需要时显式指定

> 目标：使用 WinUI 3 + Windows App SDK + C#/.NET 开发一套现代 Windows 日语 EPUB 阅读器。产品方向参考 Hoshi Reader / Hoshi Reader Android，但实现上采用更适合 Windows 的技术路线：WinUI 原生外壳 + WebView2 阅读渲染（Hoshi 风格直接章节加载 + CSS multi-column 分页）+ hoshidicts 风格字典后端 + SQLite 本地数据。

---

## 1. 产品目标

开发一个 Windows 原生的日语沉浸式 EPUB 阅读器。

核心目标：

- 使用 WinUI 3 / Fluent Design 构建现代 Windows UI。
- 正确显示日语横排与竖排。
- 正确显示 ruby / 振假名。
- EPUB 阅读启动快、翻页流畅。
- 支持 Yomitan 字典导入与查询。
- 支持日语动词/形容词变形还原。
- 提供类似 Hoshi Reader / Yomitan 的弹窗查词体验。
- 支持 AnkiConnect 制卡。
- 本地优先，不依赖云服务。
- 架构清晰，方便 Codex 持续维护和扩展。

第一版暂不做：

- DRM / LCP。
- OPDS 书库同步。
- 云同步。
- 自研 EPUB 排版引擎。
- 完整接入 Readium 2。
- 用 C# 原生控件重写 Chromium 的文字排版能力。

---

## 2. 推荐技术栈

### 2.1 UI 外壳

使用：

- WinUI 3
- Windows App SDK
- C# / .NET
- MVVM
- CommunityToolkit.Mvvm

用途：

- Windows 11 原生外壳。
- Fluent 风格导航。
- Mica / Acrylic 效果。
- 设置页。
- 书架页。
- 阅读器外层 UI。
- 原生弹窗、工具栏、侧边栏。

避免：

- 用 WPF 作为主 UI。
- 依赖 UWP-only API。
- 大量 code-behind。
- 用 TextBlock / RichTextBlock 构建阅读正文。

---

### 2.2 EPUB 阅读渲染

使用：

- WebView2 作为阅读显示层。
- Hoshi 风格直接章节加载 + CSS multi-column 分页（已替代 foliate-js）。

原因：

- EPUB 本质是 HTML / CSS / 图片等资源的打包格式。
- Chromium 对 CJK 排版、竖排、ruby、CSS writing-mode、文字选择的支持远强于 WinUI 原生文本控件。
- Hoshi Reader Android 的方案更简单：直接加载章节 HTML，CSS `column-width: var(--page-width)` 分页，JS 控制翻页与进度恢复，无嵌套 shadow DOM / iframe。
- foliate-js 已废弃，于 2026-05-19 移除。

目标结构：

```text
WinUI 3 / C# App
  ├─ 书架 UI
  ├─ 设置 UI
  ├─ ReaderViewModel
  ├─ 字典服务
  ├─ SQLite 本地数据库
  ├─ EpubParserService (OPF/spine/manifest/TOC 解析)
  └─ WebView2 ReaderHost
       ├─ WebResourceRequested 拦截章节 HTML
       ├─ NovelReaderContentStyles 注入分页 CSS
       └─ reader-bridge.js (Hoshi 风格分页/进度/翻页)
```

第一版不要自研原生 EPUB renderer。

---

### 2.3 字典引擎

优先研究并使用 hoshidicts，或者实现一个 hoshidicts 风格的原生字典后端。

用途：

- 导入 Yomitan 字典 zip。
- 查询词条。
- 查询汉字。
- 支持音高字典。
- 支持词频字典。
- 支持结构化释义。
- 支持日语变形还原。

重要原则：

- hoshidicts 不是 SQLite 的替代品。
- hoshidicts 应该作为“字典查询后端”。
- SQLite 仍然作为 App 业务数据库。

#### 方案 A：通过 native interop 使用 hoshidicts

```text
C# / WinUI
  ↓ P/Invoke / C ABI wrapper
hoshidicts native library
  ↓
Yomitan dictionary storage and lookup
```

优点：

- 最接近 Hoshi Reader 生态。
- 可以复用已有的 Yomitan 字典查询逻辑。
- 比自己从零实现 Yomitan backend 更稳。

缺点：

- 需要 native build pipeline。
- 需要稳定的 C ABI wrapper。
- 需要仔细检查许可证。

#### 方案 B：用 C# 实现最小字典后端

优点：

- 部署简单。
- 纯 .NET。
- 调试方便。

缺点：

- 工作量更大。
- 容易把 lookup 排序、变形还原、结构化内容处理做错。
- 更难对齐 Hoshi / Yomitan 的行为。

建议：

- 如果 native interop 阻塞开发，可以先做最小 C# importer 原型。
- 长期优先 hoshidicts 或兼容 hoshidicts 思路的后端。

---

### 2.4 App 数据存储

普通 App 数据使用 SQLite。

推荐：

- Dapper + Microsoft.Data.Sqlite

可选：

- sqlite-net-pcl

第一版不建议：

- EF Core

原因：

- 阅读器本地数据比较简单，但对启动速度和 IO 效率敏感。
- Dapper / SQLite 轻量、透明、易调试。
- EF Core 对桌面阅读器来说偏重。

SQLite 存储：

- 书籍信息。
- 作者信息。
- 文件路径。
- 封面路径。
- 阅读进度。
- 最近打开位置。
- 单本书设置覆盖。
- 全局阅读设置。
- 高亮。
- 笔记。
- 书签。
- 搜索索引元数据。
- Anki 模板。
- 字典元数据。

不要把高频字典查询数据塞进主 App SQLite，除非你决定自己实现完整字典后端。

---

## 3. 项目目录结构建议

推荐 solution 结构：

```text
HibikiReader.Windows.sln

src/
  HibikiReader.App/
    App.xaml
    MainWindow.xaml
    Views/
      LibraryPage.xaml
      ReaderPage.xaml
      SettingsPage.xaml
      DictionaryPage.xaml
      AnkiSettingsPage.xaml
    ViewModels/
      LibraryViewModel.cs
      ReaderViewModel.cs
      SettingsViewModel.cs
      DictionaryViewModel.cs
    Controls/
      ReaderToolbar.xaml
      DictionaryPopup.xaml
      BookCard.xaml
    Services/
      NavigationService.cs
      FilePickerService.cs
      ThemeService.cs
      AppSettingsService.cs
    Web/
      reader-host.html
      reader-bridge.js
      foliate-adapter.js
      themes/
        default.css
        vertical.css
        dark.css

  HibikiReader.Core/
    Models/
      Book.cs
      ReadingProgress.cs
      Highlight.cs
      Bookmark.cs
      ReaderSettings.cs
      DictionaryEntry.cs
      AnkiTemplate.cs
    Abstractions/
      IBookRepository.cs
      IReaderEngine.cs
      IDictionaryService.cs
      IAnkiService.cs
      IBookImportService.cs
    Reader/
      ReaderCommand.cs
      ReaderLocation.cs
      ReaderSelection.cs
      ReaderTheme.cs
    Japanese/
      TextNormalizer.cs
      TokenCandidate.cs
      LookupRequest.cs

  HibikiReader.Infrastructure/
    Data/
      AppDbConnectionFactory.cs
      Migrations/
      Repositories/
        BookRepository.cs
        ProgressRepository.cs
        HighlightRepository.cs
    Dictionary/
      HoshiDictsService.cs
      HoshiDictsNative.cs
      DictionaryImportService.cs
    Anki/
      AnkiConnectClient.cs
      AnkiNoteBuilder.cs
    Epub/
      BookImportService.cs
      CoverExtractor.cs
      MetadataExtractor.cs

  HibikiReader.Tests/
    DictionaryTests.cs
    TextNormalizerTests.cs
    AnkiTemplateTests.cs
    ReaderLocationTests.cs
```

---

## 4. 阅读渲染架构

### 4.1 ReaderPage 职责

ReaderPage 负责：

- 承载 WebView2。
- 承载阅读工具栏。
- 承载侧边栏。
- 协调原生弹窗。
- 绑定 ReaderViewModel。

ReaderPage 不应该：

- 直接解析 EPUB。
- 在 code-behind 写业务逻辑。
- 直接进行字典查询。

---

### 4.2 WebView2 Reader Host

章节 HTML 直接从 `hoshi-novel-book.local` 加载，通过 `WebResourceRequested` 拦截并注入 CSS + JS：

```text
EpubParserService 解析 EPUB
  → WebResourceRequested 拦截章节 HTML 请求
    → NovelReaderContentStyles.GenerateCss() 注入分页 CSS
    → reader-bridge.js 注入分页/进度/翻页 JS
      → window.hoshiReader 接管渲染
```

C# 发给 JS 的命令：

```text
setChapter        (章节信息: index, totalChapters)
restoreProgress   (恢复阅读进度: 0-1)
```

JS 发给 C# 的事件：

```text
readerReady       (bridge 就绪)
chapterReady      (章节渲染完成，含诊断状态)
pageChanged       (翻页事件: direction, result, progress)
restoreCompleted  (进度恢复完成)
error             (错误信息)
```

IPC 使用：

- WebView2 `PostWebMessageAsJson`
- WebView2 `WebMessageReceived`
- 消息格式: `{ version: 1, type: "...", payload: {...} }`

---

### 4.3 横排与竖排

渲染层必须支持：

```css
/* 横排 */
html, body {
  writing-mode: horizontal-tb;
}

/* 日语竖排 */
html, body {
  writing-mode: vertical-rl;
  text-orientation: mixed;
}

ruby {
  ruby-position: over;
}
```

要求：

- EPUB 自带 writing-mode 时优先尊重 EPUB。
- 用户可覆盖：auto / horizontal / vertical。
- 横排、竖排都要保证 popup 坐标正确。
- 横排、竖排都要保证文字选择可用。
- 注意 ruby 文本提取，不要把振假名错误混进正文。

---

## 5. 字典查询架构

### 5.1 查询流程

```text
用户在 WebView2 中点击或选中文字
  ↓
JS 提取周边文本、句子、选择区域坐标
  ↓
C# 接收 lookup request
  ↓
TextNormalizer 标准化文本
  ↓
DictionaryService 查询词典
  ↓
DictionaryService 执行变形还原
  ↓
词条排序与分组
  ↓
ReaderViewModel 显示 DictionaryPopup
  ↓
用户可一键创建 Anki 卡片
```

### 5.2 LookupRequest 示例

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
```

### 5.3 DictionaryEntry 示例

```csharp
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

---

## 6. Anki 集成

使用 AnkiConnect HTTP API。

功能：

- 测试连接。
- 读取 deck 列表。
- 读取 note type 列表。
- 读取字段列表。
- 创建 note。
- 可选：重复卡检查。
- 后续可选：截图、音频、图片。

模板变量：

```text
{expression}
{reading}
{glossary}
{sentence}
{sentence_with_highlight}
{book_title}
{author}
{location}
{dictionary}
{pitch}
{frequency}
```

Anki 逻辑不要写在 ViewModel 里。

调用链：

```text
ReaderViewModel
  ↓
IAnkiService
  ↓
AnkiConnectClient
```

---

## 7. 数据模型

### 7.1 books

```sql
CREATE TABLE books (
  id TEXT PRIMARY KEY,
  title TEXT NOT NULL,
  author TEXT,
  file_path TEXT NOT NULL,
  cover_path TEXT,
  imported_at TEXT NOT NULL,
  last_opened_at TEXT,
  language TEXT,
  unique_identifier TEXT
);
```

### 7.2 reading_progress

```sql
CREATE TABLE reading_progress (
  book_id TEXT PRIMARY KEY,
  location_json TEXT NOT NULL,
  progression REAL,
  chapter_href TEXT,
  updated_at TEXT NOT NULL
);
```

### 7.3 highlights

```sql
CREATE TABLE highlights (
  id TEXT PRIMARY KEY,
  book_id TEXT NOT NULL,
  location_json TEXT NOT NULL,
  selected_text TEXT NOT NULL,
  note TEXT,
  color TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
```

### 7.4 reader_settings

```sql
CREATE TABLE reader_settings (
  scope TEXT NOT NULL,
  scope_id TEXT NOT NULL,
  settings_json TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  PRIMARY KEY (scope, scope_id)
);
```

---

## 8. MVVM 规则

使用 CommunityToolkit.Mvvm。

规则：

- ViewModel 只暴露状态和命令。
- Service 负责 IO、数据库、字典、Anki 等实际工作。
- View 只写 XAML 和必要的 UI-only code-behind。
- 不在 code-behind 写业务逻辑。
- ViewModel 不直接访问 SQLite。
- 非 Reader 相关服务不要直接调用 WebView2。

示例：

```csharp
public partial class ReaderViewModel : ObservableObject
{
    private readonly IDictionaryService _dictionaryService;
    private readonly IAnkiService _ankiService;
    private readonly IBookRepository _bookRepository;

    [ObservableProperty]
    private Book? currentBook;

    [ObservableProperty]
    private DictionaryPopupState? popup;

    [RelayCommand]
    private async Task LookupAsync(LookupRequest request)
    {
        var result = await _dictionaryService.LookupAsync(request);
        Popup = DictionaryPopupState.From(result, request.SelectionRect);
    }
}
```

---

## 9. 参考项目怎么“抄”

### 9.1 从 Hoshi Reader / Hoshi Reader Android 抄什么

可以抄：

- 弹窗查词 UX。
- 沉浸式阅读流程。
- Yomitan 字典行为。
- Anki mining 流程。
- 阅读设置模型。
- 后续的音频 cue sync 思路。
- 后续的阅读统计思路。

不要直接抄：

- Android Compose UI。
- iOS SwiftUI/UIKit 代码。
- Android WebView 实现细节。

---

### 9.2 从 foliate-js / Foliate 抄什么

可以抄：

- 浏览器内 EPUB 渲染方案。
- 模块化 book loading。
- 分页行为。
- 位置 / 进度模型。
- 主题注入。
- 高亮锚定思路。

可直接使用：

- foliate-js。

---

### 9.3 从 Readium 2 抄什么

第一版只参考概念：

- Publication manifest。
- Locator / progression 模型。
- fetcher / streamer / navigator 的分层思想。

第一版不要完整集成 Readium 2，除非必须支持：

- DRM。
- OPDS。
- 图书馆借阅。
- 企业级 EPUB 兼容。

---

## 10. 性能规则

### 10.1 阅读器

- 尽量不要整本 EPUB 一次性读入内存。
- 切换阅读设置时尽量复用 WebView2。
- 阅读进度写入要 debounce。
- 在翻页停止、App suspend、关闭书籍时保存进度。
- 缓存封面。
- 缓存元数据。

### 10.2 字典

- 字典查询必须 async。
- 不阻塞 UI 线程。
- 缓存最近查询。
- 缓存常见表层词的变形还原结果。
- popup 首屏限制词条数量。
- 详细释义按需展开。

### 10.3 数据库

- SQLite 使用 WAL mode。
- 使用 migration。
- 使用统一的 AppDbConnectionFactory。
- 没有明确理由不要引入 EF Core。

---

## 11. 第一版 MVP 顺序

按这个顺序实现：

1. 创建 WinUI 3 + MVVM + DI 项目骨架。
2. 建立 SQLite 数据库与 migrations。
3. 书架页：导入 EPUB、展示书籍、打开书籍。
4. WebView2 reader host。
5. 集成 foliate-js。
6. 基础阅读：上一页、下一页、保存/恢复进度。
7. 阅读设置：主题、字号、行高、边距。
8. 排版方向：auto / 横排 / 竖排。
9. WebView2 文字选择事件。
10. 使用 mock data 显示字典 popup。
11. 集成 hoshidicts 或最小 Yomitan 字典 importer。
12. 实现真实字典查询。
13. 集成 AnkiConnect。
14. 实现高亮和书签。
15. 实现书内搜索。

---

## 12. Codex 实现规则

修改项目时遵守：

- 优先做小而可 review 的改动。
- 保持 UI、Service、Infrastructure 分层。
- 没有明确理由不要引入第二套数据库技术。
- 不要用原生文本控件替代 WebView2 阅读渲染。
- 不要自研 EPUB 排版引擎。
- 不要把字典查询逻辑写进 WebView JavaScript。
- JavaScript 只负责渲染、选择文本、提取坐标、发送事件。
- 日语字典逻辑放在 C# service 或 native backend。
- 为文本标准化、变形还原映射、Anki 模板渲染、数据库 migration 添加测试。
- 新增依赖时说明原因。
- C# 中优先使用明确模型，避免大量 `Dictionary<string, object>`。
- IPC message 要有版本和类型。

---

## 13. 推荐依赖

C# / .NET：

- CommunityToolkit.Mvvm
- Microsoft.Extensions.DependencyInjection
- Microsoft.Extensions.Hosting，可选
- Microsoft.Data.Sqlite
- Dapper
- System.Text.Json

Windows：

- Microsoft.WindowsAppSDK
- Microsoft.Web.WebView2

测试：

- xUnit 或 NUnit
- FluentAssertions，可选

JavaScript：

- foliate-js
- 自定义最小 adapter layer

字典：

- hoshidicts native interop，或兼容实现

---

## 14. 高风险区域

高风险：

- WebView2 在竖排模式下的选择坐标。
- 不同 DPI / 多显示器下 popup 定位。
- ruby 文本提取，不能把振假名错误混入正文。
- Yomitan structured content 渲染。
- hoshidicts native interop 与打包。
- EPUB 资源在 WebView2 中的安全加载。

中风险：

- 字体/主题变化后的阅读位置锚定。
- 版式变化后的高亮锚定。
- 大型 EPUB 性能。
- WebView2 字体加载和类似 CORS 的资源限制。

低风险：

- 书架 CRUD。
- 设置 UI。
- 基础 AnkiConnect 调用。

---

## 15. 安全规则

- EPUB 内容视为不可信输入。
- WebView2 禁止任意外部跳转。
- 限制文件访问。
- 通过受控的 local resource mapping 或 virtual host 提供书籍资源。
- 不要向 JavaScript 暴露宽泛 native API。
- 校验所有来自 WebView2 的消息。
- Bridge API 要窄、明确、强类型。

---

## 16. 最终架构决策

第一版采用：

```text
WinUI 3 + Windows App SDK + C#/.NET
  ├─ CommunityToolkit.Mvvm 做 MVVM
  ├─ Dapper + SQLite 存 App 数据
  ├─ WebView2 作为阅读显示层
  ├─ foliate-js 作为 EPUB 渲染核心
  ├─ C# ReaderBridge 做 IPC
  ├─ hoshidicts 风格字典后端
  ├─ Yomitan 字典导入/查询
  └─ AnkiConnect 集成
```

这个方案在以下方面平衡最好：

- Windows 原生体验。
- 日语 EPUB 正确渲染。
- 可实现性。
- Hoshi-like 沉浸阅读流程。
- 方便 Codex 维护和扩展。

---

## 17. 小说 EPUB 自动化测试与截图规范

后续开发小说 EPUB 阅读功能时，不能只依赖单元测试或“进程能启动”。凡是涉及导入、打开、WebView2 reader host、foliate-js 渲染、分页、布局、字典弹窗等阅读链路，都必须执行可重复的 UI 自动化验证，并保存截图与诊断状态。

### 17.1 测试基本原则

- 测试必须产出截图，截图用于人工复核阅读区域是否真实渲染。
- 截图不能依赖固定像素坐标判断成功。
- 自动化点击不能控制用户的鼠标，不能用 `SetCursorPos`、`mouse_event`、固定坐标点击作为正式验证手段。
- 优先使用 UI Automation / UIA3 / FlaUI 通过 `AutomationId`、控件名称、控件类型定位元素并触发 `InvokePattern`、`SelectionItemPattern` 或等价自动化动作。
- 如果 WinUI 控件无法被 UIA 稳定触发，应调整控件可访问性或结构，例如给书卡增加可 Invoke 的按钮/容器，而不是退回坐标点击。
- WebView2 内容截图应优先使用 `CoreWebView2.CapturePreviewAsync` 或应用内测试/诊断接口，不要只依赖 `PrintWindow`。`PrintWindow` 对 WebView2 合成层可能截到空白。
- 窗口截图可以作为辅助产物，但不能作为唯一断言依据。

### 17.2 必须添加的 AutomationId

小说模块的关键 UI 必须提供稳定的自动化标识：

```text
NovelNavItem
ImportNovelButton
NovelBookCard
NovelBookCard_<bookId>
NovelReaderBackButton
NovelWebView
```

如需测试分页、设置、字典弹窗，继续补充稳定 ID：

```text
NovelReaderPreviousPageRegion
NovelReaderNextPageRegion
NovelReaderSettingsButton
NovelDictionaryPopup
NovelDictionaryCloseButton
```

### 17.3 Reader 诊断状态

`reader-bridge.js` 应暴露轻量诊断对象，便于 C# 或测试工具通过 WebView2 执行脚本读取状态：

```javascript
window.__hoshiReaderState = {
  bridgeReady: true,
  bookTitle: "",
  statusText: "",
  sectionIndex: 0,
  sectionCount: 0,
  hasRenderedText: false,
  readerRect: null,
  contentRect: null,
  error: null
}
```

测试断言不要只看截图，应同时读取该状态并检查：

- `bridgeReady == true`
- `statusText` 不包含 `Reader bridge error`
- `sectionCount > 0`
- `hasRenderedText == true`
- `readerRect.height > 0`
- `contentRect.height > 0`
- 阅读区域底部空白比例低于阈值，例如 `< 20%`

### 17.4 推荐端到端测试流程

小说 EPUB reader 的端到端验证流程如下：

1. 启动 Hoshi。
2. 使用 UI Automation 定位并打开 `NovelNavItem`。
3. 如果测试 EPUB 尚未导入，通过 `ImportNovelButton` 导入固定测试书；如果导入弹窗难以自动化，应提供测试专用导入入口或预置测试数据库。
4. 使用 UI Automation 定位目标书卡，例如 `NovelBookCard_<bookId>`。
5. 触发书卡打开动作，不允许使用固定坐标点击。
6. 等待 `NovelReaderPage` 出现。
7. 等待 WebView2 中的 `window.__hoshiReaderState.bridgeReady == true`。
8. 等待 `statusText` 进入 `EPUB loaded` 或等价成功状态。
9. 等待 `hasRenderedText == true`。
10. 使用 WebView2 截图接口保存 reader 内容截图。
11. 保存 UIA tree 摘要、reader 诊断 JSON、截图文件。
12. 断言阅读区域不是空白、不是纯色块、不是只停在标题/错误状态。

### 17.5 截图与日志产物

测试产物保存到：

```text
docs/superpowers/artifacts/novel-reader/
```

推荐命名：

```text
YYYY-MM-DD-001-library-after-import.png
YYYY-MM-DD-002-reader-after-open.png
YYYY-MM-DD-003-webview-capture.png
YYYY-MM-DD-reader-state.json
YYYY-MM-DD-uia-tree.txt
```

失败时必须保留：

- 当前窗口截图。
- WebView2 内容截图。
- `window.__hoshiReaderState` JSON。
- UIA tree 摘要。
- 如果 WebView2 有 JS 错误，必须显示或记录具体错误信息，不能只显示 `Reader bridge error`。

### 17.6 布局验证要求

阅读区域必须单独验证布局，不允许只确认 `EPUB loaded`。

必须检查：

- `#reader-view` 高度接近可用阅读区域高度。
- `foliate-view` 高度不是 0。
- 实际渲染内容不能只占顶部小块而底部大面积空白。
- 大屏、常规窗口、窄窗口至少各验证一次。
- 深色/浅色主题下文字与背景对比可读。

如果截图显示底部存在大量空白，应优先排查 WebView2 host CSS、`foliate-view` 高度、分页器布局和初始 section 选择，而不是继续堆分页、字典或 Anki 功能。

### 17.7 当前 Harry Potter 回归用例

当前已知回归场景：

```text
书名：Harry Potter and the Sorcerer's Stone
示例路径：C:\Users\Wight\Downloads\哈利波特1魔法石.epub
期望：打开后 reader host 不停在 Starting WebView2 bridge，不显示 Reader bridge error，状态进入 EPUB loaded，并能看到实际 EPUB 内容。
```

该用例后续应固化为自动化验证，不再依赖人工坐标点击。

---

## 18. Hoshi Reader 参考实现规则

从本节开始，小说 EPUB 阅读器的行为参考优先级调整为：

```text
Hoshi Reader iOS 用户可见行为
  -> Hoshi Reader Android 对该行为的复刻方式
  -> Hoshi Windows/WinUI 实现
  -> foliate-js 或其他阅读库的默认行为
```

也就是说，foliate-js 只能作为当前 reader engine 的临时适配层或实现候选，不再作为不可替换的核心前提。只要 Hoshi 的行为与 foliate 默认行为冲突，后续开发应优先对齐 Hoshi，并在代码或文档中记录偏差原因。

### 18.1 本地参考仓库

已拉取到本地的参考仓库：

```text
docs/reference/hoshi/Hoshi-Reader-Android
docs/reference/hoshi/Hoshi-Reader
```

这些仓库只作为开发期参考，不直接作为 Hoshi 源码提交。相关目录已加入 `.gitignore`，避免误提交完整第三方仓库。

每次处理小说阅读器相关功能前，必须先查看本地参考实现中对应路径，至少覆盖：

- 导入与存储：`BookRepository.kt`、`BookStorage.swift`、`BookProcessor.swift`。
- EPUB 解析模型：`EpubBookParser.kt`、iOS `EPUBKit` 相关模型。
- WebView 阅读宿主：Android `ReaderWebView.kt`、`ReaderWebResourceBridge.kt`，iOS `ReaderWebView.swift`。
- 阅读脚本：Android `ReaderPaginationScripts.kt`，iOS `reader.js`、`selection.js`。
- 行为测试：Android `ReaderPaginationScriptsTest.kt`、`ReaderPaginationWebViewTest.kt`、`EpubBookModelTest.kt`。

### 18.2 必须继承的 Hoshi 行为

后续实现小说阅读器时，以下行为按 Hoshi 对齐：

- EPUB 导入后进入应用私有存储，按书籍目录保存，不依赖外部原始文件路径长期存在。
- 解包 EPUB 时必须防止 zip slip，所有条目路径必须限制在目标书籍目录内。
- 书籍元数据、书签、阅读统计、章节信息、高亮等使用 sidecar JSON 文件保存，命名优先兼容 Hoshi：`metadata.json`、`bookmark.json`、`bookinfo.json`、`statistics.json`、`highlights.json`。
- 阅读器按 spine 章节加载内容，章节切换由 native/WinUI 侧控制，阅读脚本只返回分页结果或边界状态。
- WebView 使用受控 origin 或虚拟 host 加载章节和资源，禁止随意扩大 file/content 访问权限。
- 资源加载必须通过受控映射或拦截器提供 HTML、CSS、图片、字体等，不允许让 EPUB 内容任意访问本机路径。
- 阅读进度按章节内可阅读字符位置计算，忽略 ruby 注音、脚本、样式等非正文内容，尽量兼容 Hoshi 的字符统计方式。
- 翻页、上一章、下一章、恢复进度、fragment 跳转、图片尺寸约束、竖排/横排布局，都要以 Hoshi 的 reader JS 行为为准。
- 测试必须覆盖章节边界、末页空白、图片页、长文本分页、进度恢复和 WebView 实际渲染截图。

### 18.3 Android 可复用内容判断

Hoshi Reader Android 的 Kotlin/Compose UI 不能直接复用到 WinUI，但以下内容可以复用其设计或在 GPLv3 兼容前提下移植：

- `EpubBookParser.kt` 的 EPUB 模型思路：`EpubBook`、`EpubChapter`、resource map、TOC、cover、bookInfo。
- `ReaderWebResourceBridge.kt` 的受控资源桥思路：`https://hoshi.local/epub/...` 与字体/EPUB 资源分流。
- `ReaderPaginationScripts.kt` 与 iOS `reader.js` 的分页、进度恢复、图片边界处理、selection/highlight 行为。
- `ReaderPaginationScriptsTest.kt`、`ReaderPaginationWebViewTest.kt` 的行为用例，可以转成 Hoshi 的 C# 单元测试、WebView2 集成测试或 UIA 截图测试。
- hoshidicts/native bridge 的集成方向可作为字典阶段参考，除非明确记录缺口，不要另造一套与 Yomitan/Hoshi 行为明显不兼容的字典导入和查询逻辑。

Hoshi 当前仓库是 GPLv3，因此允许在遵守 GPLv3、保留版权与 SPDX 说明、标明修改来源的前提下移植 Hoshi GPLv3 代码。但优先级仍是“先复刻行为，再决定是否直接移植代码”，不要未经审查整文件复制。

### 18.4 当前 reader engine 调整方向

小说模块应逐步抽象为：

```text
NovelReaderPage / ViewModel
  -> ReaderEngine 接口
      -> 当前 FoliateReaderEngine 适配层
      -> 后续 HoshiCompatibleReaderEngine 候选
  -> WebView2ReaderHost
  -> ReaderResourceBridge
  -> ReaderDiagnostics / Screenshot artifacts
```

下一阶段优先修复“导入后打开仍不可读 / 内容宽度异常 / 底部大面积空白”的问题。排查顺序：

1. 对照 Hoshi 的 `ReaderWebResourceBridge`，确认 Hoshi 的 virtual host 与 EPUB 资源映射是否稳定。
2. 对照 Hoshi 的 `ReaderPaginationScripts`，记录 WebView2 中 `scrollWidth`、`scrollHeight`、`clientWidth`、`clientHeight`、正文 rect、图片 rect。
3. 将当前 `window.__hoshiReaderState` 扩展为 Hoshi 风格分页诊断状态，不能只记录 `EPUB loaded`。
4. 先让单本 EPUB 可稳定显示，再继续做字典、Anki、朗读等功能。

---

## 19. Reader 修改后的强制验证流程

每次修改以下任何内容后，必须执行完整 reader 验证，不能只跑单元测试：

```text
reader-bridge.js
reader-styles.css / reader-host.html
WebView2 宿主代码
NovelReaderPage.xaml / NovelReaderPage.xaml.cs
vendored foliate paginator/view 相关代码
```

验证流程固定为：

1. 构建并启动 Hoshi，确认真实 WinUI 顶层窗口出现。
2. 使用 UI Automation 打开小说书架和测试 EPUB，不允许用固定像素或控制用户鼠标。
3. 使用 `NovelReaderNextPageRegion` / `NovelReaderPreviousPageRegion` 连续翻页多次，检查是否出现内容漂移、裁切、空白页或页码/章节状态错乱。顶部不再放置上一页/下一页可见按钮，翻页自动化入口应保持为阅读区透明可 Invoke 区域。
4. 调整窗口大小后再次验证 reflow，至少覆盖常规窗口和缩小窗口。窗口 resize 后必须确认正文重新布局，不允许沿用旧 scroll/page 位置导致漂移。
5. 捕获 reader 日志和诊断状态，确认 `scrollPosition`、`pageCount`、`pageIndex`、`sectionIndex` 一致且没有越界。
6. 如果设置了 `HOSHI_NOVEL_READER_ARTIFACT_DIR`，必须保存 WebView2 截图和 `window.__hoshiReaderState` JSON；失败时也必须保留 artifacts。

Hoshi 对齐要求：

- 分页尺寸必须来自当前 viewport，窗口大小变化后重新计算。
- Windows/WebView2 高 DPI 环境下，横排分页宽度必须按 CSS viewport 的 `window.innerWidth` 计算；`devicePixelRatio` 与 `visualViewportWidth` 只用于诊断记录，禁止乘进 CSS `--page-width`，否则真实窗口会出现右侧裁切、图片偏移或长行缺失。
- 翻页 scroll offset 必须按 Hoshi 的 `context.pageSize` 对齐；`column-gap` 只作为 CSS 多列间距，不得加进翻页步长，否则会累计漂移并露出下一页。
- Windows/WebView2 MVP 阶段的安全区按 Hoshi 风格由 `column-width = pageWidth - 2 * safeInline` 与 `column-gap = 2 * safeInline` 共同实现；实际 column pitch 仍必须等于 viewport pageWidth，并用截图和 DOM 诊断确认，不允许只改 CSS。
- 安全区沿用 Hoshi Android 的 reader padding 思路：内容页必须有显式 `--reader-safe-inline` / `--reader-safe-block`。横排分页时 `column-width = pageWidth - 2 * safeInline`，`column-gap = 2 * safeInline`，翻页步长仍按 `pageWidth`，这样每一列都有左右安全区且不会改变页面 pitch。
- 诊断中的安全区像素必须从 `getComputedStyle(document.body).paddingLeft/paddingTop` 读取；CSS `clamp(...)` 自定义属性不能直接 `parseFloat`，否则会误报为 0。
- reflow 后优先按逻辑进度恢复位置，而不是继续使用旧像素 scroll offset。
- 翻页边界由 native/WinUI 侧决定章节切换，reader JS 只报告当前页内滚动/分页状态。
- 任何漂移修复都要对照本地 Hoshi `ReaderPaginationScripts.kt`、iOS `ReaderWebView.swift` 的 pageHeight/pageWidth、restoreProgress、paginationMetrics 行为。
