# Hoshi Windows 日语 EPUB 阅读器

WinUI 3 + Windows App SDK + C#/.NET 开发的 Windows 原生日语沉浸式 EPUB 阅读器。产品方向对齐 Hoshi Reader / Hoshi Reader Android。

---

## 0. 开发环境

- **目标平台**: Windows 10+ x64
- **构建**: `dotnet build -p:Platform=x64`
- **测试**: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64`
- **构建+启动**: `.\build-and-run.ps1`
- **发布版本**: `.\release.ps1 -Version x.y.z`
- **初始化子模块**: `git submodule update --init --recursive`
- **UTF-8 初始化**: `.\set-utf8-console.ps1`
- **读取中日文文档**: Windows PowerShell 5.1 必须显式使用 `Get-Content -Encoding UTF8`；`set-utf8-console.ps1` 只保证控制台输出编码，不改变文件读取解码。
- **不默认构建 ARM64**

参见 `.claude/skills/` 下的 `/build`、`/run`、`/test`、`/check` 快捷命令。

---

## 1. 核心规则

### 1.1 绝对禁止

- **禁止修改 `native/hoshidicts/` 下的任何代码。** 这是第三方子模块，字典功能只能通过 C# P/Invoke 调用 `hoshidicts_c_api` DLL 暴露的接口。
- **禁止自研 EPUB 排版引擎。** 阅读渲染使用 WebView2 + CSS multi-column 分页。
- **禁止用 WinUI TextBlock/RichTextBlock 替代 WebView2 阅读渲染。**
- **禁止把 foliate-js 引回主阅读链路。** 已于 2026-05-19 移除。
- **禁止把字典查询逻辑写进 WebView JavaScript。** JS 只负责渲染、选择文本、提取坐标、发送事件。
- **禁止在 code-behind 写业务逻辑。**
- **禁止 ViewModel 直接访问 SQLite。**

### 1.2 分层规则

```
View (XAML + UI-only code-behind)
  → ViewModel (状态 + 命令, CommunityToolkit.Mvvm)
    → Service (IO、数据库、字典、Anki 等实际工作)
```

- ViewModel 只暴露状态和命令。
- Service 负责 IO、数据库、字典、Anki 等实际工作。
- 非 Reader 相关服务不要直接调用 WebView2。

### 1.3 安全规则

- EPUB 内容视为不可信输入。
- WebView2 禁止任意外部跳转。
- 限制文件访问，通过受控 virtual host 提供书籍资源。
- 不要向 JavaScript 暴露宽泛 native API。
- 校验所有来自 WebView2 的消息。
- Bridge API 要窄、明确、强类型。

### 1.4 行为对齐

用户可见行为参考优先级：

```
Hoshi Reader iOS → Hoshi Reader Android → Hoshi Windows/WinUI → 其他阅读库
```

- 其他阅读库只能作为实现参考，不作为不可替换的核心前提。
- 与 Hoshi 行为冲突时优先对齐 Hoshi，在代码或文档中记录偏差原因。

---

## 2. 技术栈速查

| 层 | 技术 |
|---|---|
| UI 外壳 | WinUI 3 + Windows App SDK + CommunityToolkit.Mvvm |
| 阅读渲染 | WebView2 + Hoshi 风格 CSS multi-column 分页 |
| 字典引擎 | hoshidicts (C# P/Invoke), Yomitan 字典导入 |
| 业务数据 | SQLite + Dapper + Microsoft.Data.Sqlite |
| IPC | WebView2 `PostWebMessageAsJson` / `WebMessageReceived` |
| 测试 | xUnit v3 + FluentAssertions + Moq + coverlet |
| Anki | AnkiConnect HTTP API |
| 日志 | Serilog |

详细架构说明见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

---

## 3. 架构概览

```
WinUI 3 / C# App
  ├─ 书架 UI
  ├─ 设置 UI
  ├─ ReaderViewModel
  ├─ 字典服务 (hoshidicts P/Invoke)
  ├─ SQLite 本地数据库
  ├─ EpubParserService (OPF/spine/manifest/TOC 解析)
  └─ WebView2 ReaderHost
       ├─ WebResourceRequested 拦截章节 HTML
       ├─ NovelReaderContentStyles 注入分页 CSS
       └─ reader-bridge.js (分页/进度/翻页)
```

字典弹窗架构：

```
NovelReaderPage
  → DictionaryPopupOverlay        // 栈、遮罩、定位、关闭策略
      → root DictionaryLookupPopup
      → child DictionaryLookupPopup...
          → PopupHtmlGenerator → popup.js
```

---

## 4. 参考源码

### 4.1 本地参考仓库

```
docs/reference/hoshi/Hoshi-Reader-Mac      # 长期参考子模块，对齐 macOS 小说/设置/Sasayaki/统计行为
docs/reference/hoshi/Hoshi-Reader-Android
docs/reference/hoshi/Hoshi-Reader
```

`Hoshi-Reader-Mac` 作为 git submodule 提交，用于长期对齐；`Hoshi-Reader-Android`、`Hoshi-Reader` 为本地参考克隆，已加入 `.gitignore`，不直接作为 Hoshi 源码提交。

### 4.2 速查 grep 模式

```bash
# Mac 设置首页 / Reader 高级设置
rg "SettingsHomeView|AdvancedView|AppearanceView|SasayakiSettingsView|StatisticsSettingsView" docs/reference/hoshi/Hoshi-Reader-Mac/Features/Settings/

# Mac 小说阅读器
rg "ReaderWebView|NativeReaderView|reader.js|continuousMode|readerWheelPageTurnEnabled" docs/reference/hoshi/Hoshi-Reader-Mac/

# Mac Sasayaki / 统计
rg "Sasayaki|StatisticsAutostartMode|StatisticsSettingsView" docs/reference/hoshi/Hoshi-Reader-Mac/Features docs/reference/hoshi/Hoshi-Reader-Mac/Models

# Android EPUB 解析
rg "EpubBook|EpubChapter|EpubBookParser" docs/reference/hoshi/Hoshi-Reader-Android/

# Android 阅读渲染
rg "ReaderWebView|ReaderPaginationScripts|ReaderWebResourceBridge" docs/reference/hoshi/Hoshi-Reader-Android/

# Android 字典变形
rg "deinflector|Deinflector" docs/reference/hoshi/Hoshi-Reader-Android/

# Android 弹窗定位
rg "LookupPopupLayout|LookupPopupHost" docs/reference/hoshi/Hoshi-Reader-Android/

# iOS 阅读器
rg "ReaderWebView|ReaderPagination" docs/reference/hoshi/Hoshi-Reader/

# iOS 查词流程
rg "LookupViewModel|DictionaryService" docs/reference/hoshi/Hoshi-Reader/
```

### 4.3 hoshidicts 参考

```bash
# 变形还原
docs/reference/hoshi/Hoshi-Reader-Android/third_party/hoshidicts-kotlin-bridge/app/src/main/cpp/hoshidicts/src/deinflector.cpp
docs/reference/hoshi/Hoshi-Reader-Android/third_party/hoshidicts-kotlin-bridge/app/src/main/cpp/hoshidicts/src/lookup.cpp
```

---

## 5. 关键约束

### 5.1 阅读器

- 按 spine 章节加载，章节切换由 native/WinUI 侧控制。
- 分页尺寸必须来自当前 viewport，窗口 resize 后重新计算。
- 翻页 scroll offset 按 `context.pageSize` 对齐，`column-gap` 不得加进翻页步长。
- 高 DPI 下横排分页宽度按 CSS `window.innerWidth` 计算，`devicePixelRatio` 禁止乘进 `--page-width`。
- 安全区沿用 Hoshi Android 的 reader padding：`--reader-safe-inline` / `--reader-safe-block`。
- 翻页边界由 native/WinUI 决定章节切换，reader JS 只报告状态。

### 5.2 字典

- 字典查询必须 async，不阻塞 UI 线程。
- popup 首屏限制词条数量，详细释义按需展开。
- 缓存最近查询和常见表层词变形还原结果。
- 弹窗定位对齐 Android `LookupPopupLayout`：横排优先上下，竖排优先左右。
- 弹窗栈行为对齐 Android `LookupPopupStack`：`tapOutside` ≠ dismiss。
- `scanNonJapaneseText` 默认允许非日文扫描，设置页提供可见开关。
- Yomitan 字典导入对齐 Android 的 `Term` / `Frequency` / `Pitch` 类型目录；启用、排序、删除均按类型独立处理。
- Windows 上 hoshidicts 遇到非 ASCII 标题/路径或旧 zip 编码导致 code-page/SEH 导入失败时，先保留直导路径，再 retry 生成 lookup-safe 兼容 zip：只保留 `index.json`、`styles.css`、`term_bank_*`、`term_meta_bank_*`、`tag_bank_*`，临时标题改为 ASCII `hoshi-import-*`，导入后恢复显示标题，字典类型只以 native `termCount` / `freqCount` / `pitchCount` 为准。

### 5.3 弹窗渲染

- 使用 WinUI 原生外层 + WebView2 渲染词典正文，不要用 WinUI TextBlock 重写 Yomitan structured content。
- 待渲染时保持 `Opacity=0`，收到 `contentReady` 后切到 `Opacity=1`。
- 禁止在查词路径里 `await contentReady`，否则 Shift hover 会卡顿。
- 每次显示生成新 render generation，旧 generation 的 ready 消息必须失效。

### 5.4 EPUB 导入

- 导入后进入应用私有存储，按书籍目录保存。
- 解包时必须防止 zip slip，所有条目路径限制在目标书籍目录内。
- 元数据、书签、统计、高亮使用 sidecar JSON，命名优先兼容 Hoshi：`metadata.json`、`bookmark.json`、`bookinfo.json`、`statistics.json`、`highlights.json`。

### 5.5 代码规范

- 优先做小而可 review 的改动。
- 没有明确理由不引入第二套数据库技术或 EF Core。
- 新增依赖时说明原因。
- C# 优先使用明确模型，避免 `Dictionary<string, object>`。
- IPC message 要有版本和类型。

---

## 6. 验证

### 6.1 快速检查

```powershell
# 构建 + 测试（/check）
dotnet build -p:Platform=x64 && dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
```

### 6.2 Reader 修改后

每次修改 `reader-bridge.js`、reader CSS/HTML、WebView2 宿主、NovelReaderPage 后：

1. 构建并启动 Hoshi
2. UI Automation 打开测试 EPUB（`C:\Users\Wight\Downloads\哈利波特1魔法石.epub`）
3. 连续翻页检查漂移、裁切、空白页
4. 调整窗口大小验证 reflow
5. 捕获诊断状态，确认 `scrollPosition`、`pageCount`、`pageIndex`、`sectionIndex` 一致

详见 [docs/VERIFICATION.md](docs/VERIFICATION.md)。

### 6.3 字典修改后

修改 `JapaneseDeinflector`、`DictionaryLookupService`、`PopupHtmlGenerator`、popup/overlay 后：

1. 运行 `dotnet test --filter "FullyQualifiedName~Dictionary"`
2. 启动应用，查词验证首次不卡 UI、Shift hover 正常、嵌套查词正常、Yomitan structured content 正确渲染、深色/浅色主题可读

详见 [docs/VERIFICATION.md](docs/VERIFICATION.md)。

### 6.4 发布版本

发布 GitHub 版本或标签时，使用 `.\release.ps1 -Version x.y.z`，不要手工创建、移动、删除或复用 `v*` 标签。

- 发布前必须在干净的 `main` 工作树上运行脚本；未提交改动、非 `main` 分支、已有本地/远端 tag 或已有 GitHub Release 都必须停止。
- 发布脚本负责更新 `Hoshi/Hoshi.csproj` 版本、提交版本提交、推送 `main`、创建并推送不可变 `vX.Y.Z` 标签。
- 发布不在本地跑构建或测试；脚本只触发并等待 GitHub Actions，以 Actions 结果作为发布依据。
- Actions 失败时不得创建 GitHub Release；先修复问题，再发布新的 patch 版本。
- 发布资产必须来自对应 tag 的 GitHub Actions artifacts，不使用本地 publish 目录手工打包。
- 发布前必须验证 `Hoshi.Minimal.x64.zip` 内含 `hoshidicts_c_api.dll`，并确认存在 `Hoshi.Setup.x64*.exe`。
- 已发布版本发现问题时，默认发布新的 patch 版本（例如 `v0.1.1`），不要静默替换旧 release 资产；除非用户明确要求，才允许编辑既有 Release。
- 如需预览发布步骤，使用 `.\release.ps1 -Version x.y.z -PlanOnly`；该模式不得产生 git、GitHub 或文件发布副作用。

---

## 7. 文档路由

| 文件 | 内容 | 规则 |
|---|---|---|
| `agents.md` | 核心规则 + 约束 + 速查（本文件） | 保持精简，不放架构细节、验证流程、Bug 日志 |
| `docs/ARCHITECTURE.md` | 技术栈细节、架构决策、数据模型、性能规则、安全规则 | 架构级内容，决策需记录原因 |
| `docs/VERIFICATION.md` | Reader/字典/音频验证流程、截图规范、AutomationId | 流程级内容，跟着功能迭代更新 |
| `docs/CHANGELOG.md` | 已修复问题记录 | 只记根因和解决方案，不记流水账 |
| `.claude/skills/` | 可复用斜杠命令 | 每个 skill 对应一个常见操作 |
| `docs/superpowers/plans/` | 实现计划 | 计划阶段产物 |
| `docs/superpowers/specs/` | 功能规格 | 设计阶段产物 |

---

## 8. 高风险区域

| 风险 | 区域 |
|---|---|
| 高 | WebView2 竖排选择坐标、DPI/多显示器 popup 定位、ruby 文本提取、Yomitan structured content 渲染、hoshidicts native interop、EPUB 安全加载 |
| 中 | 字体/主题变化后位置锚定、大型 EPUB 性能、WebView2 字体加载 |
| 低 | 书架 CRUD、设置 UI、基础 AnkiConnect 调用 |

---

## 9. MVP 顺序

1. WinUI 3 + MVVM + DI 项目骨架
2. SQLite 数据库与 migrations
3. 书架页：导入 EPUB、展示书籍、打开书籍
4. WebView2 reader host + CSS multi-column 分页
5. 基础阅读：翻页、保存/恢复进度
6. 阅读设置：主题、字号、行高、边距
7. 排版方向：auto / 横排 / 竖排
8. WebView2 文字选择 → 字典 popup
9. hoshidicts 集成 + Yomitan 字典导入
10. 真实字典查询
11. AnkiConnect 集成
12. 高亮和书签
13. 书内搜索
