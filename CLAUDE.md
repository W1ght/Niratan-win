# Hoshi - Windows 日语 EPUB 阅读器

WinUI 3 + Windows App SDK + C#/.NET + WebView2 + Hoshi-compatible reader engine 的 Windows 原生日语 EPUB 阅读器。

> 详细架构设计、技术栈选型、测试规范、Hoshi 参考规则见 [agents.md](agents.md)。CLAUDE.md 为日常开发约束，agents.md 为补充细节，两者都必须遵守。

## 项目结构

```
Hoshi.sln
├── Hoshi/                    # 主 WinUI 3 应用
│   ├── Views/Pages/           # WinUI Pages (Library, Reader, Settings)
│   ├── ViewModels/Pages/      # MVVM ViewModels (CommunityToolkit.Mvvm)
│   ├── Views/Components/      # 可复用 UI 组件
│   ├── Models/                # 数据模型 & DTO
│   │   └── Novel/             # EPUB 解析模型 (EpubBook, EpubChapter, etc.)
│   ├── Services/              # 业务逻辑层
│   │   ├── Novels/            # 小说/EPUB 相关服务
│   │   └── UI/                # 导航/通知/主题服务
│   ├── Web/NovelReader/       # WebView2 前端资源
│   │   ├── reader-host.html   # 阅读宿主 HTML
│   │   ├── reader-bridge.js   # C# ↔ JS 通信桥
│   │   └── reader-styles.css  # 阅读器样式
│   └── Helpers/               # UI 工具 (Selectors, Converters)
├── Hoshi.Tests/              # xUnit 单元测试
└── docs/
    ├── reference/hoshi/       # Hoshi Reader 参考实现 (gitignored)
    └── superpowers/           # 测试制品 & 计划 & 规格
```

## 技术栈

- **UI**: WinUI 3, Windows App SDK, WebView2
- **MVVM**: CommunityToolkit.Mvvm (ObservableProperty, RelayCommand, Messenger)
- **DI**: Microsoft.Extensions.DependencyInjection
- **数据库**: Dapper + Microsoft.Data.Sqlite (WAL mode)
- **EPUB 渲染**: Hoshi-compatible reader engine (通过 WebView2)
- **测试**: xUnit + FluentAssertions
- **目标**: .NET 10, Windows 10.0.22621.0

## 构建 & 测试

```bash
# 还原依赖
dotnet restore

# 构建
dotnet build

# 运行所有测试
dotnet test

# 运行特定测试
dotnet test --filter "FullyQualifiedName~NovelReader"
```

## 架构规则

### MVVM
- ViewModel 只暴露状态和命令，不直接访问 SQLite，不直接调用 WebView2
- Service 负责 IO、数据库、字典等实际工作
- View 只写 XAML 和必要的 UI-only code-behind
- 业务逻辑不写在 code-behind

### WebView2 Reader Host
- 章节 HTML 直接从 `hoshi-novel-book.local` 加载，通过 `WebResourceRequested` 拦截并注入 CSS + JS
- CSS 由 `NovelReaderContentStyles.GenerateCss()` 生成，使用 `column-width: var(--page-width)` 实现分页
- JS 由 `reader-bridge.js` 提供，实现 Hoshi 风格分页、进度恢复、翻页
- IPC 使用 `version/type/payload` JSON 消息，version 固定为 1
- C# → JS: `PostWebMessageAsJson`，消息类型: `setChapter`, `restoreProgress`
- JS → C#: `window.chrome.webview.postMessage` → `WebMessageReceived`，消息类型: `readerReady`, `chapterReady`, `pageChanged`, `restoreCompleted`, `error`
- JS 只负责渲染、分页、选择文本、提取坐标、发送事件
- 日语字典逻辑放在 C# service，不放 WebView JavaScript

### 安全
- EPUB 内容视为不可信输入
- WebView2 使用 CSP 限制脚本/资源来源
- 通过 `WebResourceRequested` 拦截 `hoshi-novel-book.local` 提供书籍资源，注入 CSS/JS 并防止路径遍历
- 不向 JavaScript 暴露宽泛 native API
- Bridge API 窄、明确、强类型

### 数据
- SQLite WAL mode
- Dapper 做数据访问，不用 EF Core
- 书籍进度 debounce 写入
- 字典查询必须 async，不阻塞 UI

## 关键文件

| 用途 | 路径 |
|------|------|
| 阅读宿主 HTML | [Hoshi/Web/NovelReader/reader-host.html](Hoshi/Web/NovelReader/reader-host.html) |
| Bridge JS | [Hoshi/Web/NovelReader/reader-bridge.js](Hoshi/Web/NovelReader/reader-bridge.js) |
| 阅读器 CSS | [Hoshi/Web/NovelReader/reader-styles.css](Hoshi/Web/NovelReader/reader-styles.css) |
| Bridge 消息工厂 | [Hoshi/Services/Novels/NovelReaderBridgeMessageFactory.cs](Hoshi/Services/Novels/NovelReaderBridgeMessageFactory.cs) |
| Reader ViewModel | [Hoshi/ViewModels/Pages/NovelReaderPageViewModel.cs](Hoshi/ViewModels/Pages/NovelReaderPageViewModel.cs) |
| Reader Page | [Hoshi/Views/Pages/NovelReaderPage.xaml](Hoshi/Views/Pages/NovelReaderPage.xaml) |
| Reader Code-behind | [Hoshi/Views/Pages/NovelReaderPage.xaml.cs](Hoshi/Views/Pages/NovelReaderPage.xaml.cs) |
| 书架 ViewModel | [Hoshi/ViewModels/Components/NovelBookItemViewModel.cs](Hoshi/ViewModels/Components/NovelBookItemViewModel.cs) |
| 导航 Page | [Hoshi/Views/Pages/NavigationPage.xaml](Hoshi/Views/Pages/NavigationPage.xaml) |
| EPUB 解析器 | [Hoshi/Services/Novels/EpubParserService.cs](Hoshi/Services/Novels/EpubParserService.cs) |
| 内容样式生成 | [Hoshi/Services/Novels/NovelReaderContentStyles.cs](Hoshi/Services/Novels/NovelReaderContentStyles.cs) |
| EPUB 导入服务 | [Hoshi/Services/Novels/NovelEpubImportService.cs](Hoshi/Services/Novels/NovelEpubImportService.cs) |

## 行为参考

Hoshi Reader (HS) > Hoshi Reader Android (HSA) > Hoshi WinUI

- HS = Hoshi Reader (iOS)
- HSA = Hoshi Reader Android
- 行为优先级: HS 用户可见行为 → HSA 对该行为的复刻方式 → Hoshi 实现
- HSA 的 Kotlin/Compose UI 不能直接复用，但 JS 分页脚本和 CSS 可以直接参考移植
- Hoshi 本地参考: `docs/reference/hoshi/Hoshi-Reader/` (HS/iOS), `docs/reference/hoshi/Hoshi-Reader-Android/` (HSA)
- 阅读渲染已按 Hoshi Android 方案迁移：直接加载章节 HTML + CSS multi-column 分页 + JS 分页控制
- 参考仓库已加入 `.gitignore`，不直接提交

## 小说 EPUB 测试规范 (Superpowers)

### 截图验证流程

1. 启动 Hoshi
2. 使用 UI Automation 定位 `NovelNavItem`
3. 定位目标书卡 `NovelBookCard_<bookId>`
4. 触发书卡打开动作（不允许固定坐标点击）
5. 等待 `window.__hoshiReaderState.bridgeReady == true`
6. 等待 `hasRenderedText == true`
7. 使用 WebView2 截图接口保存 reader 内容截图
8. 保存 UIA tree 摘要、reader 诊断 JSON、截图文件

### 测试制品保存位置

```
docs/superpowers/artifacts/novel-reader/
  YYYY-MM-DD-001-library-after-import.png
  YYYY-MM-DD-002-reader-after-open.png
  YYYY-MM-DD-003-webview-capture.png
  YYYY-MM-DD-reader-state.json
  YYYY-MM-DD-uia-tree.txt
```

### 必需 AutomationId

```
NovelNavItem, ImportNovelButton, NovelBookCard, NovelWebView,
NovelReaderBackButton, NovelReaderPreviousPageRegion, NovelReaderNextPageRegion
```

### 布局验证要点

- `body` scroll 区域高度 > 0，内容通过 CSS columns 分页
- 内容不能只占顶部小块而底部大面积空白
- `blankSpaceRatio < 0.2`
- 大屏、常规窗口、窄窗口至少各验证一次

### 已知回归用例

```
书名：Harry Potter and the Sorcerer's Stone
路径：C:\Users\Wight\Downloads\哈利波特1魔法石.epub
期望：reader host 发送 chapterReady，body 中能看到实际 EPUB 分页内容
```

## 代码修改规则

- 优先小而可 review 的改动
- 保持 UI / Service / Infrastructure 分层
- 不要用原生文本控件替代 WebView2 阅读渲染
- 不要自研 EPUB 排版引擎
- JavaScript 不负责字典查询逻辑
- 新增依赖时说明原因
- C# 优先使用明确模型，避免大量 `Dictionary<string, object>`
- IPC message 必须有 version 和 type
- 默认不加注释，只在 WHY 不明显时写一行
- 不写多段文档字符串或大段注释块
- 不用 emoji
