# Hoshi 验证流程

本文档包含 Reader 渲染验证、字典查词验证、音频验证的完整流程。修改相关代码后必须按对应节验证。

---

## 1. 小说 EPUB 自动化测试与截图规范

### 1.1 测试基本原则

- 测试必须产出截图，截图用于人工复核。
- 截图不能依赖固定像素坐标判断成功。
- 自动化点击不能控制用户的鼠标，不能用 `SetCursorPos`、`mouse_event`。
- 优先使用 UI Automation / UIA3 / FlaUI 通过 `AutomationId`、控件名称、控件类型定位元素。
- 如果 WinUI 控件无法被 UIA 稳定触发，应调整控件可访问性或结构。
- WebView2 内容截图优先使用 `CoreWebView2.CapturePreviewAsync` 或应用内诊断接口。
- 窗口截图可作为辅助产物，不能作为唯一断言依据。

### 1.2 必须添加的 AutomationId

```
NovelNavItem
ImportNovelButton
NovelLibraryCommandBar
NovelShelfSectionsControl
NovelShelfManagementButton
NovelStorageWarningInfoBar
NovelUnshelvedBooksRepeater
NovelBookCard
NovelBookCard_<bookId>
NovelReaderBackButton
NovelWebView
NovelReaderSettingsButton
NovelDictionaryPopup
NovelDictionaryCloseButton
```

阅读区不得添加左/右透明翻页层或 `NovelReaderPreviousPageRegion` / `NovelReaderNextPageRegion`。翻页测试使用键盘、WebView2 诊断/脚本接口或测试专用入口。

### 1.3 Reader 诊断状态

`reader-bridge.js` 应暴露诊断对象：

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

断言：
- `bridgeReady == true`
- `statusText` 不包含 `Reader bridge error`
- `sectionCount > 0`
- `hasRenderedText == true`
- `readerRect.height > 0` / `contentRect.height > 0`
- 底部空白比例 < 20%

### 1.4 推荐端到端测试流程

1. 启动 Hoshi
2. UI Automation 打开 `NovelNavItem`
3. 导入测试 EPUB（或使用预置测试数据库）
4. UI Automation 定位目标书卡 `NovelBookCard_<bookId>`
5. 触发打开动作（不允许固定坐标点击）
6. 等待 `NovelReaderPage` 出现
7. 等待 `window.__hoshiReaderState.bridgeReady == true`
8. 等待 `statusText` 进入 `EPUB loaded` 或等价成功状态
9. 等待 `hasRenderedText == true`
10. 保存 WebView2 截图、`__hoshiReaderState` JSON、UIA tree 摘要
11. 断言阅读区域不是空白

### 1.5 截图与日志产物

产物保存到 `docs/superpowers/artifacts/novel-reader/`。

命名：
```
YYYY-MM-DD-001-library-after-import.png
YYYY-MM-DD-002-reader-after-open.png
YYYY-MM-DD-003-webview-capture.png
YYYY-MM-DD-reader-state.json
YYYY-MM-DD-uia-tree.txt
```

失败时必须保留：
- 当前窗口截图
- WebView2 内容截图
- `window.__hoshiReaderState` JSON
- UIA tree 摘要
- WebView2 JS 错误信息

### 1.6 布局验证要求

- `#reader-view` 高度接近可用阅读区域高度
- reader content container 高度不是 0
- 实际渲染内容不能只占顶部小块而底部大面积空白
- 大屏、常规窗口、窄窗口至少各验证一次
- 深色/浅色主题下文字与背景对比可读

### 1.7 Harry Potter 回归用例

```
书名：Harry Potter and the Sorcerer's Stone
路径：C:\Users\Wight\Downloads\哈利波特1魔法石.epub
期望：打开后 reader host 不停在 Starting WebView2 bridge，不显示 Reader bridge error，
      状态进入 EPUB loaded，能看到实际 EPUB 内容。
```

### 1.8 Niratan 文件存储与迁移验证

自动化测试至少覆盖：

- 新导入 EPUB 写入私有 `<book-id>` 目录，并生成合法 `metadata.json`；重新扫描后书名、封面、Profile、字符进度不变。
- `bookmark.json` 的章节/字符位置在关闭 Reader、重启应用后可恢复，单次保存不产生第二条 SQLite 写入。
- 旧 SQLite fixture 首次迁移前生成 `hoshi.db.pre-novel-files-v1.bak`，导出校验成功后旧小说表被退役，视频表仍存在。
- 强制导出失败时，备份和旧小说表仍存在，小说库进入只读状态；修复 fixture 后重试可完成。
- 缺失 JSON 可按定义初始化；损坏 `metadata.json`/`shelves.json` 必须保留原字节并显示可恢复警告，不能被自动覆盖。
- fresh database 只创建视频业务表，不创建 `NovelBooks`、`NovelReadingProgress` 或 `NovelReaderSettings`。

所有破坏性故障测试必须使用复制到临时目录的 fixture，禁止直接修改用户 AppData。

### 1.9 书架交互与持久化验证

1. 在小说库创建两个书架，验证同名（忽略大小写）被拒绝。
2. 重命名、拖动调整书架顺序，关闭并重开管理窗口，顺序保持。
3. 从书卡上下文菜单移动到自定义书架，再移动到 Unshelved；各区只出现一次。
4. 调整书架内和 Unshelved 顺序，关闭并重启应用，顺序保持。
5. 删除书架前必须出现确认；确认后仅删除书架，书籍进入 Unshelved，EPUB 不删除。
6. 删除一本书后，`shelves.json` 与 `book_order.json` 不再包含该 ID。
7. 已有进度且未读完的书始终出现在派生 Reading 区；该派生区不写入 `shelves.json`，为空时不显示。
8. Reading、自定义书架、Google Drive、Unshelved 都使用自适应多行布局；未归档书卡可直接打开，窄窗口也不产生横向 rail。
9. Google Drive 书籍保持独立分区，不进入本地书架状态；缩略图首次使用鉴权下载，刷新或重启后命中磁盘缓存。
10. 同时点击 4 本云端书，前三本进入下载、第四本显示排队；任一本完成或失败不取消其他任务，失败卡片显示重试。
11. 窄窗口下 CommandBar 可访问，页面只有一个纵向滚动所有者，书籍分区不抢占纵向滚动。

### 1.10 Niratan Reader 统计语义验证

自动化测试必须覆盖 `ReaderStatisticsMathTests`、`ReaderStatisticsSessionTests`、`ReaderNavigationTransactionCoordinatorTests`、`NovelReaderPageViewModelTests`、`ReaderNavigationHistoryTests` 和 `NovelReaderStatisticsLifecycleTests`。

手工验证矩阵：

1. Off 模式下普通打开、翻页和跳转都不自动开始；手动开始后秒级时间持续更新。
2. PageTurn 在有效手动翻页请求到达时开始，即使结果为 NoMovement；NoMovement 不产生 bookmark/statistics checkpoint 或字符增量。实际 Sasayaki 自动滚动仍按移动结果开始。
3. On 在普通 restore 完成后开始；目录、字符、搜索、高亮、内部链接、历史和显式 Sasayaki 跳转的 restore callback 不重复触发。
4. 每个程序化跳转验证顺序：旧位置只 checkpoint 一次 → 最终分页位置写入 `bookmark.json` → baseline 重置；跳转距离不得增加 `charactersRead`。
5. 同章节 `#fragment` 不重载章节；跨章节链接等待 fragment 对齐完成。外部 URL、`javascript:` 和非 spine 资源不得离开 Reader。
6. 产生至少两个历史位置，验证 Back/Forward 显示目标字符位置且往返正确；历史恢复不计作阅读字符。
7. tracking 时最小化窗口后检查 Background checkpoint；关闭主窗口或返回书架后检查 Close checkpoint 只有一次。
8. 在本地时间午夜前开始、午夜后 checkpoint，确认旧日期归档，新日期只出现一条记录，并按 Niratan 语义接收完整跨日 checkpoint。
9. 重启应用后 `bookmark.json` 与 `statistics.json` 可恢复；`statistics.json` 同一 `dateKey` 只保留 `lastStatisticModified` 最新记录。

诊断失败时保留 Reader 日志，并重点搜索 `ProgrammaticDeparture`、`navigationGeneration`、`Background`、`Close` 和 `Restore completed`。

#### 1.10.1 同章翻页与 typed movement 回归

自动化测试必须同时覆盖 WebView 与 native 两侧的 typed contract：

- `reader-bridge.js` 对每次手动翻页返回 `ReaderPageNavigationEvent` 等价数据，明确区分 `Scrolled` / `Limit`、`Forward` / `Backward` 和最终 `Progress`；禁止把“命令已处理”当作“位置已移动”。
- native 将结果归一为 `ReaderPageNavigationOutcome`：同章实际滚动为 `SameChapterMovement`，跨章边界为带目标章节及目标端点的 `AdjacentChapter(index, progress)`，首章向前、末章向后和同位置回调为 `NoMovement`。向前跨章 restore 到目标第一页；向后跨章必须等待 WebView 回报上一章最后一页的 resolved progress，再保存 bookmark 并重置 baseline。
- Page Turn 自动开始模式下，同章向前或向后翻一页必须立即更新 `progress`、当前字符、`bookmark.json`、Session/Today 与 `statistics.json`，并且只产生一次 `ReadingMovement` checkpoint；不必等到跨章才结算。
- 覆盖分页与 continuous mode、自然相邻章节、首/末边界、resize/reflow 和 reopen；程序化目录/字符/搜索/高亮/history/internal-link/Sasayaki 跳转继续走程序化事务，不得伪装成真实 page movement 或增加阅读字符。

真实运行时使用 `C:\Users\Wight\Downloads\哈利波特1魔法石.epub`：在同一章节内记录翻页前后的 `pageIndex`、`pageCount`、`progress`、`scrollPosition`、当前字符和 sidecar hash/mtime。断言 `pageIndex` 与 `scrollPosition / pageSize` 对齐、所有值无越界，而且 `statistics.json` 在跨章前已经变化。

#### 1.10.2 Reader compact statistics panel

1. 打开 `NovelReaderStatisticsPanelDialog`，确认 compact dialog 宽度约为 520–560 effective pixels，只有一个纵向滚动所有者；窗口缩小时无裁切、嵌套滚动或不可达操作。
2. Session、Today、All Time 三组均显示字符/近似词数、时间和速度；日文内容使用 characters，英文内容使用 approximate words，语言切换后单位和数值投影一致。
3. Start/Stop 与 Reader chrome 状态同步；remaining time 使用原始字符余量与原始速度计算，速度不足时显示可理解的占位状态。
4. 使用键盘、鼠标和触摸完成打开、滚动、Start/Stop 和关闭；在 200% text scaling 下无截断，Automation name 非空。
5. Light、Dark、High Contrast 下检查 Session/Today/All Time、按钮、分隔和滚动提示均可辨认。

#### 1.10.3 Reader 自动同步、writer 与生命周期

自动化必须使用 mock remote store/coordinator，不得依赖真实 Google Drive：

1. Open：仅在全局 Sync、凭据、自动导入及 statistics 选项都允许时执行一次 import；若导入改变书籍，必须先重新加载 sidecar，再恢复 Reader 位置。取消、缺凭据和受控远端失败不得让 Reader 打不开。
2. Debounce：连续 bookmark/statistics 变化合并为一次 30 秒延迟 export；延迟期间再次变化重置/合并 pending work。
3. Single-flight：export 运行中到达新变化时不能并行上传，只允许当前 export 完成后再跑一次 follow-up；并发 `FlushAsync` 调用加入同一个 active export。
4. Final boundary：Background 和 Close 都先阻止/排空旧 writer，保存最终 bookmark 与 statistics checkpoint，再 `FlushAsync`；Close 最后才 `Cancel()` 且幂等，Background 完成后恢复 writer admission。mock 调用序列必须证明最后一次 export 看见最终 checkpoint。
5. Writer lifecycle：让 writer A 以位置 X 入队并阻塞，随后把 UI 位置改为 Y，再放行 A；断言 bookmark、statistics checkpoint 和 sync schedule 对每个 admitted request 使用同一份 snapshot，A 不得混入 Y。后续 writer/final Close/Background 必须明确使用它们各自 admission 时的 Y（或更新后）snapshot。
6. 设置页：关闭全局 Google Drive/ッツ Sync 时，statistics Sync 控件隐藏或禁用，但 `EnableStatisticsSync`、同步模式等已存值保持不变；重新开启全局 Sync 后恢复显示和值。断开凭据也不得静默重置统计偏好。

必跑自动化命令：

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Statistics|FullyQualifiedName~TtuSync|FullyQualifiedName~GoogleDrive|FullyQualifiedName~NovelReaderWebAssetTests"
```

真实 UI/runtime 还要确认 Hoshi 顶层窗口响应、Reader 可打开、同章翻页与 compact panel 状态同步，并在返回书架、最小化/恢复和关闭路径检查最终 sidecar。真实 Google Drive import/export 会修改远端账户或书籍，**只有用户显式确认可修改的测试账户与测试书后才允许执行**；否则以 coordinator/mock 测试为远端调用证据，并在报告中明确写“真实 Drive 未执行”。

#### 1.10.4 Reader 原子跳转事务

1. 准备相邻章节 A、B，从 A 最后一页进入 B 第一页，再从 B 第一页返回上一章；必须直接显示 A 最后一页，Reader chrome、ViewModel、`bookmark.json` 和统计 baseline 在隐藏渲染期间保持源位置，最终只发布一次 WebView 回报的 page-aligned progress，不能临时出现 `1.0`/100%、第一页进度或二次闪烁。
2. 在事务 `Rendering` 阶段触发 Background/Close：事务必须恢复并确认源位置后再写 lifecycle checkpoint；在 `Committing` 阶段触发时必须等待已接纳的目的地 bookmark 写入和终态渲染确认，再保存目的地终态，不能取消后复活旧位置。
3. 分别在 `Rendering` 和 `Committing` 注入 bridge error。前者恢复不可变源位置；后者等待持久化结果，并按 durable bookmark 选择目的地或源位置。每个 generation 只允许一个 recovery，Reader 最终必须恢复可见和可输入。
4. 事务未完成时，目录、搜索、内部链接、历史、字符、高亮、翻页和 Sasayaki 的 auto-scroll/load/progress/save 都不得改变位置；Sasayaki 播放 UI 与非位置 cue 高亮可以继续。异步 Sasayaki callback 必须在 await 后再次检查 mutation gate。
5. 自动化只使用 mock/fake remote store 验证 sync 调度、TTU rollback/empty Replace 与 statistics exact-once；禁止真实 Google Drive import/export。只有精确确认启动的是本工作树 `Hoshi.exe` 且没有 single-instance 重定向时才执行 UI 边界测试，否则报告“运行态边界未验证”，不得借用或操作其他 Hoshi 进程。
6. 在 destination bookmark writer 阻塞时并发发送两次同 generation `restoreCompleted`：第一条只提交一次 bookmark/baseline/export，第二条必须返回 `Ignored`，不得触发 recovery、章节 reload、可见闪烁或 revision 消耗。
7. 在程序化跨章事务中分别触发 Pause、Stop 和关闭统计：操作必须等待事务 settlement，并使用 settlement 的 source/destination 字符位置；字符差不得为负，Stop 不得因 lifecycle barrier 丢失。Back/Forward 只在 destination settlement 后修改栈，保存失败、bridge error 或 lifecycle source recovery 必须保持原栈。
8. 使用包含 `<script>`、`on*`、`javascript:`/`vbscript:`、refresh、iframe/object、`xml:base`、别名前缀 XLink、SVG/MathML 与伪造 terminal message 的恶意章节 fixture；资源响应必须按 manifest media type 识别 HTML（包括非常规扩展名），先经 `EpubActiveContentSanitizer`，并携带 `script-src 'none'` CSP，清洗异常不得回退原始 virtual-host 内容。外部/子框架/new-window 导航必须被 host 拒绝，WebMessage source 必须精确匹配当前 render attempt。完整 bridge 和分页引擎必须位于 IIFE；native 翻页、滚轮和 Sasayaki 位置操作只通过 typed host message 进入 closure，`window.handleNavigate` / `window.handleMessage` / `window.hoshiReader` 及直接 paginate API 必须为 `undefined`，synthetic message 不得绕过 gate。

`NovelReaderBridgeRuntimeTests` 使用 Node.js 内置 `node:vm` 执行真实 `reader-bridge.js`，不依赖 npm 包。测试按 `HOSHI_NODE_PATH`、`PATH`、`Program Files\\nodejs`、Codex bundled runtime 的顺序定位 `node.exe`；本地未安装 Node 时设置 `HOSHI_NODE_PATH`，不得静默跳过该安全回归。

### 1.11 Niratan Dashboard 验证

1. 运行所有 `NovelStatisticsDashboard*Tests`，覆盖 repository、目标/区间、速度、趋势、日历、排名、书架和缓存。
2. 准备一条 `<60s` 且字符数为正的记录：总字符/时长必须增加，所有速度模块不得使用该记录。
3. 放入损坏的 `statistics.json`：Dashboard 显示/记录 skipped book，原文件 hash 不变，其余书籍仍正常聚合。
4. 验证最近一年边界、周一到周日 7 格、未来周 cell 无百分比、目标完成度允许超过 100%。
5. 逐一切换 year/month/week/day range、anchor、day/week/month grain、characters/duration/speed trend metric 和 ranking metric，确认所有卡片使用同一范围且显示单位正确。
6. 点击 Calendar 任意日期，确认范围 anchor 与选择日期同步，详情显示字符、时长和 active books；Calendar 覆盖最近一年。
7. 在 Dashboard 修改目标类型、字符/时长目标和周目标天数，确认 Today/Week/Selected Range/streak 立即重算，重启后设置仍保留。
8. 验证 Book Ranking 最多 12 行，以及自定义书架/Unshelved 对比；损坏 sidecar 时必须显示可见警告。
9. 重开 Dashboard 验证 `statistics_dashboard_cache_v1.json` 先命中再后台重读 sidecar；新 snapshot 发布后 UI 更新且缓存被替换。
   自动化测试必须创建第二个 cache 实例从磁盘读取 snapshot，不能只验证同一实例的内存命中。
10. 使用包含非空 `bookContributions` 的 `statistics_dashboard_cache_v1.json` 重启并进入 Dashboard；缓存必须正常反序列化。再放入结构有效但模型不兼容的派生缓存，确认只删除该缓存并从各书 `statistics.json` 重建，应用不得退出，原始 sidecar、EPUB 和视频 SQLite 均不得改变。
11. 从小说 CommandBar 进入 Statistics，确认书架 rail、排序、导入和书架管理退出布局；使用 Bookshelf 按钮返回后，原 rail 和书籍卡仍可操作。
12. 验证全宽 Range & Trend，以及 Today、Goal、This Week、Reading Calendar、Selected Range、Speed Summary、Book Ranking、Shelf Comparison 全部存在；Bar/Line 切换不改变其他卡片数据。
13. 分别把窗口调整到 `>=1260`、`840..1259` 和 `<840` effective pixels，确认三列、两列、单列状态生效，无裁切、重叠或第二个纵向滚动条；Calendar 保持七行横向滚动。
    连续在断点两侧调整窗口，确认每次只发生一次布局切换，统计视图保持响应且不出现 DispatcherQueue 重排循环。
14. 在加载未完成时返回 Bookshelf，再次进入 Dashboard；旧 load/refresh 不得覆盖新 snapshot，loading/refresh 状态不得残留，refresh 订阅始终只有一个。
15. 在英文和简体中文下检查所有 header、metric、empty/loading/warning 文案；用键盘遍历 range、anchor、grain、metric、style、goal、calendar、ranking 和返回按钮，并确认 UI Automation name 非空。
16. Light、Dark 与 High Contrast 下检查趋势线/柱、calendar heat、range/selection outline、ranking/shelf bars 和损坏警告均可辨认。

---

## 2. Reader 修改后的强制验证

每次修改以下文件后必须执行完整验证：

```
reader-bridge.js
reader-styles.css / reader-host.html
WebView2 宿主代码
NovelReaderPage.xaml / NovelReaderPage.xaml.cs
reader paginator/view 相关代码
```

### 2.1 验证流程

1. `dotnet build -p:Platform=x64`
2. 启动 Hoshi，确认真实 WinUI 顶层窗口出现
3. UI Automation 打开测试 EPUB（不允许固定像素或控制用户鼠标）
4. 连续翻页多次，检查内容漂移、裁切、空白页或页码/章节状态错乱
5. 调整窗口大小后验证 reflow：至少覆盖常规窗口和缩小窗口；resize 后正文必须重新布局
6. 捕获 reader 日志和诊断状态，确认 `scrollPosition`、`pageCount`、`pageIndex`、`sectionIndex` 一致且无越界
7. 如果设置了 `HOSHI_NOVEL_READER_ARTIFACT_DIR`，必须保存 WebView2 截图和 `__hoshiReaderState` JSON

### 2.2 Hoshi 对齐要求

- 分页尺寸必须来自当前 viewport，窗口大小变化后重新计算
- 高 DPI 下横排分页宽度按 CSS `window.innerWidth` 计算；`devicePixelRatio` 禁止乘进 `--page-width`
- 翻页 scroll offset 按 `context.pageSize` 对齐；`column-gap` 不得加进翻页步长
- 安全区：`column-width = pageWidth - 2 * safeInline`，`column-gap = 2 * safeInline`，翻页步长仍按 `pageWidth`
- 诊断中的安全区像素从 `getComputedStyle(document.body).paddingLeft/paddingTop` 读取
- reflow 后优先按逻辑进度恢复位置
- 翻页边界由 native/WinUI 侧决定章节切换，reader JS 只报告状态
- 任何漂移修复都要对照本地 Hoshi `ReaderPaginationScripts.kt`、iOS `ReaderWebView.swift`

---

## 3. 字典查词验证

### 3.1 受影响文件

修改以下文件时，必须按本节验证：

```
Hoshi/Services/Dictionary/JapaneseDeinflector.cs
Hoshi/Services/Dictionary/DictionaryLookupService.cs
Hoshi/Services/Dictionary/PopupHtmlGenerator.cs
Hoshi/Views/Dictionary/DictionaryLookupPopup.cs
Hoshi/Views/Dictionary/DictionaryPopupOverlay.cs
Hoshi/Web/DictionaryPopup/popup.js
Hoshi/Views/Pages/NovelReaderPage.xaml.cs
```

`native/hoshidicts/` 子模块绝对不能修改。

### 3.2 必跑验证

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
dotnet build -p:Platform=x64
.\build-and-run.ps1  # 弹窗或 WebView2 生命周期相关时
```

验证重点：
- 首次查词不应长时间卡住 UI
- 普通查词、Shift hover 查词、弹窗内嵌套查词都能返回结果
- Yomitan structured content 不得以原始 JSON 显示
- 深色/浅色主题下弹窗文字、边框、遮罩都可读
- 横排和竖排下弹窗定位不遮挡选区主体

### 3.3 变形还原对齐

`JapaneseDeinflector` 的目标是对齐 Android `hoshidicts/deinflector.cpp`：

- 条件位与 Android `Conditions` 语义一致
- `AddRule(...)` 的输入/输出条件、规则组名称和说明与参考实现一致
- 特殊动词与例外规则不能被通用后缀规则吞掉
- `PosToConditions()` 必须正确解析 Yomitan term `rules`
- 新增或调整规则时补充 `JapaneseDeinflectorTests`

参考路径：
```
docs/reference/hoshi/Hoshi-Reader-Android/third_party/hoshidicts-kotlin-bridge/app/src/main/cpp/hoshidicts/src/deinflector.cpp
docs/reference/hoshi/Hoshi-Reader-Android/third_party/hoshidicts-kotlin-bridge/app/src/main/cpp/hoshidicts/src/lookup.cpp
```

### 3.4 词典设置与 i18n 规则

- 词典设置页对齐 Hoshi Android：查词区包含 `scanNonJapaneseText`、`maxResults`、`scanLength`；
  折叠词典区包含 `collapseMode`、`expandFirstDictionary`；
  行为区包含 `compactGlossaries`、`showExpressionTags`、`harmonicFrequency`、`deduplicatePitchAccents`、`compactPitchAccents`
- `maxResults` 与 `scanLength` 默认值为 16，阅读页 JS、弹窗 JS、C# `LookupAsync` 必须使用同一份 `DictionaryDisplaySettings`
- 词典类型切换使用 Term / Frequency / Pitch 分段控件，不用 `RadioButtons ItemsSource + SelectedItem enum x:Bind`
- Novel 模块下保留独立查词页面
- 新增用户可见功能必须同步 i18n：XAML 用 `x:Uid` + `Strings/en-US/Resources.resw` + `Strings/zh-CN/Resources.resw`
- 不要在 `App.xaml.cs` 强制 `ApplicationLanguages.PrimaryLanguageOverride = "en-US"`（临时测试分支除外）
- 阅读器设置统一放在 `Settings → Appearance`，不要再把大量控件堆在顶层设置页
- 小说阅读器内打开的 reader appearance 复用 `ReaderAppearanceSettingsContent`
- 独立查词页和阅读器内查词共用 `DictionaryPopupOverlay` / `DictionaryLookupPopup` / `PopupHtmlGenerator` 链路
- Shift 悬停查词不暴露延迟设置，按住 Shift 时立即触发查词

### 3.5 Popup 外观

1. 在 `外观 → 弹窗` 验证宽度 `100...1400`、高度 `100...800`、缩放
   `0.8...1.5`、显示操作栏和全宽显示。
2. 验证新建/缺失配置使用 `320 × 250`、缩放 `1.00`，两个开关默认关闭。
3. 在阅读器和视频查词中分别测试 `320 × 250`、`1400 × 800`、缩放
   `0.8`、缩放 `1.5`、浅色/深色主题和窗口 resize 后的边界限制。
4. 打开显示操作栏，通过 structured content 链接跳转，使用鼠标和键盘验证
   后退、前进和关闭。
5. 在弹窗正文中选择文本打开嵌套查词，确认 child 继承宽度、高度、缩放、
   操作栏和全宽配置；关闭 child 后父弹窗仍可见。
6. 开启全宽显示，确认每一层弹窗使用窗口可用宽度并靠底部显示，同时配置
   高度继续生效。

---

## 4. 音频验证

### 4.1 受影响文件

```
Hoshi/Services/Audio/AudioService.cs
Hoshi/Services/Audio/IAudioService.cs
Hoshi/Models/Settings/AudioSettings.cs
Hoshi/Views/Dictionary/DictionaryLookupPopup.cs (playWordAudio handler)
Hoshi/Services/Dictionary/PopupHtmlGenerator.cs (SerializeAudioSources, audio injection)
Hoshi/Web/DictionaryPopup/popup.js (fetchAudioUrl, expandAudioTemplate, playWordAudio)
Hoshi/ViewModels/Pages/AudioSettingsPageViewModel.cs
```

### 4.2 验证流程

```powershell
dotnet build -p:Platform=x64
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Audio"
.\build-and-run.ps1  # 弹窗音频播放需要启动应用
```

手动验证：
1. Settings → Audio 添加/编辑/删除音源，重启后确认持久化
2. 打开书查词，点音频图标，确认播放
3. 切换 autoplay：开→查词自动播放，关→不播放
4. 测试 interrupt / duck / mix 播放模式
5. URL 模板展开：确认 `{term}` → 单词 `{reading}` → 读音
6. 嵌套弹窗内音频：子窗口播放子窗口的音频
7. 本地音频（需 AnkiConnect）：`localhost:8765` 离线时能优雅降级
