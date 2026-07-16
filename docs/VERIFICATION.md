# Niratan 验证流程

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
window.__niratanReaderState = {
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

1. 启动 Niratan
2. UI Automation 打开 `NovelNavItem`
3. 导入测试 EPUB（或使用预置测试数据库）
4. UI Automation 定位目标书卡 `NovelBookCard_<bookId>`
5. 触发打开动作（不允许固定坐标点击）
6. 等待 `NovelReaderPage` 出现
7. 等待 `window.__niratanReaderState.bridgeReady == true`
8. 等待 `statusText` 进入 `EPUB loaded` 或等价成功状态
9. 等待 `hasRenderedText == true`
10. 保存 WebView2 截图、`__niratanReaderState` JSON、UIA tree 摘要
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
- `window.__niratanReaderState` JSON
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
- 旧 SQLite fixture 首次迁移前生成 `niratan.db.pre-novel-files-v1.bak`，导出校验成功后旧小说表被退役，视频表仍存在。
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
7. 同步设置页：关闭全局同步时只保留 Syncing 区；重新开启后恢复 Client、Connection、Behaviour、Data 及原偏好。连接后 Client Secret 继续以 PasswordBox 掩码显示，离开/返回页面和重启应用后从 Windows Credential Manager 恢复；清缓存不清凭据，退出登录清凭据。
8. 书籍右键同步：全局同步关闭时不显示 Sync；Auto 模式显示单个 Sync；Manual 模式显示 Import/Export 子菜单。使用鼠标、Shift+F10 和菜单键逐项验证，mock 断言方向和 book/statistics/audio payload 与设置快照一致。

必跑自动化命令：

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Statistics|FullyQualifiedName~TtuSync|FullyQualifiedName~GoogleDrive|FullyQualifiedName~NovelReaderWebAssetTests"
```

真实 UI/runtime 还要确认 Niratan 顶层窗口响应、Reader 可打开、同章翻页与 compact panel 状态同步，并在返回书架、最小化/恢复和关闭路径检查最终 sidecar。真实 Google Drive import/export 会修改远端账户或书籍，**只有用户显式确认可修改的测试账户与测试书后才允许执行**；否则以 coordinator/mock 测试为远端调用证据，并在报告中明确写“真实 Drive 未执行”。

#### 1.10.4 Reader 原子跳转事务

1. 准备相邻章节 A、B，从 A 最后一页进入 B 第一页，再从 B 第一页返回上一章；必须直接显示 A 最后一页，Reader chrome、ViewModel、`bookmark.json` 和统计 baseline 在隐藏渲染期间保持源位置，最终只发布一次 WebView 回报的 page-aligned progress，不能临时出现 `1.0`/100%、第一页进度或二次闪烁。
2. 在事务 `Rendering` 阶段触发 Background/Close：事务必须恢复并确认源位置后再写 lifecycle checkpoint；在 `Committing` 阶段触发时必须等待已接纳的目的地 bookmark 写入和终态渲染确认，再保存目的地终态，不能取消后复活旧位置。
3. 分别在 `Rendering` 和 `Committing` 注入 bridge error。前者恢复不可变源位置；后者等待持久化结果，并按 durable bookmark 选择目的地或源位置。每个 generation 只允许一个 recovery，Reader 最终必须恢复可见和可输入。
4. 事务未完成时，目录、搜索、内部链接、历史、字符、高亮、翻页和 Sasayaki 的 auto-scroll/load/progress/save 都不得改变位置；Sasayaki 播放 UI 与非位置 cue 高亮可以继续。异步 Sasayaki callback 必须在 await 后再次检查 mutation gate。
5. 自动化只使用 mock/fake remote store 验证 sync 调度、TTU rollback/empty Replace 与 statistics exact-once；禁止真实 Google Drive import/export。只有精确确认启动的是本工作树 `Niratan.exe` 且没有 single-instance 重定向时才执行 UI 边界测试，否则报告“运行态边界未验证”，不得借用或操作其他 Niratan 进程。
6. 在 destination bookmark writer 阻塞时并发发送两次同 generation `restoreCompleted`：第一条只提交一次 bookmark/baseline/export，第二条必须返回 `Ignored`，不得触发 recovery、章节 reload、可见闪烁或 revision 消耗。
7. 在程序化跨章事务中分别触发 Pause、Stop 和关闭统计：操作必须等待事务 settlement，并使用 settlement 的 source/destination 字符位置；字符差不得为负，Stop 不得因 lifecycle barrier 丢失。Back/Forward 只在 destination settlement 后修改栈，保存失败、bridge error 或 lifecycle source recovery 必须保持原栈。
8. 使用包含 `<script>`、`on*`、`javascript:`/`vbscript:`、refresh、iframe/object、`xml:base`、别名前缀 XLink、SVG/MathML 与伪造 terminal message 的恶意章节 fixture；资源响应必须按 manifest media type 识别 HTML（包括非常规扩展名），先经 `EpubActiveContentSanitizer`，并携带 `script-src 'none'` CSP，清洗异常不得回退原始 virtual-host 内容。外部/子框架/new-window 导航必须被 host 拒绝，WebMessage source 必须精确匹配当前 render attempt。完整 bridge 和分页引擎必须位于 IIFE；native 翻页、滚轮和 Sasayaki 位置操作只通过 typed host message 进入 closure，`window.handleNavigate` / `window.handleMessage` / `window.niratanReader` 及直接 paginate API 必须为 `undefined`，synthetic message 不得绕过 gate。

`NovelReaderBridgeRuntimeTests` 使用 Node.js 内置 `node:vm` 执行真实 `reader-bridge.js`，不依赖 npm 包。测试按 `NIRATAN_NODE_PATH`、兼容的 `HOSHI_NODE_PATH`、`PATH`、`Program Files\\nodejs`、Codex bundled runtime 的顺序定位 `node.exe`；本地未安装 Node 时设置 `NIRATAN_NODE_PATH`，不得静默跳过该安全回归。

### 1.11 Niratan Dashboard 验证

1. 运行所有 `NovelStatisticsDashboard*Tests`，覆盖 repository、目标/区间、速度、趋势、日历、排名、书架和缓存。
2. 准备一条 `<60s` 且字符数为正的记录：总字符/时长必须增加，所有速度模块不得使用该记录。
3. 放入损坏的 `statistics.json`：Dashboard 显示/记录 skipped book，原文件 hash 不变，其余书籍仍正常聚合。
4. 验证最近一年边界、周一到周日 7 格、未来周 cell 无百分比、目标完成度允许超过 100%。
5. 逐一切换 year/month/week/day range、day/week/month grain、characters/duration/speed trend metric 和 ranking metric，确认所有卡片使用同一范围且显示单位正确；界面不得再出现 anchor 日期控件。
6. 拖动趋势图下方常驻可见的横向范围拖动条，确认日/月/周均按完整日历窗口吸附，Range、Trend、Calendar、Speed、Ranking 和 Shelf 同步更新；Year 覆盖完整最近一年且拖动条禁用。点击 Calendar 任意日期后，拖动条应移动到包含该日期的窗口，详情显示字符、时长和 active books。
7. 在 Dashboard 修改目标类型、字符/时长目标和周目标天数，确认 Today/Week/Selected Range/streak 立即重算，重启后设置仍保留。
8. 验证 Book Ranking 最多 12 行，以及自定义书架/Unshelved 对比；损坏 sidecar 时必须显示可见警告。
9. 重开 Dashboard 验证 `statistics_dashboard_cache_v1.json` 先命中再后台重读 sidecar；新 snapshot 发布后 UI 更新且缓存被替换。
   自动化测试必须创建第二个 cache 实例从磁盘读取 snapshot，不能只验证同一实例的内存命中。
10. 使用包含非空 `bookContributions` 的 `statistics_dashboard_cache_v1.json` 重启并进入 Dashboard；缓存必须正常反序列化。再放入结构有效但模型不兼容的派生缓存，确认只删除该缓存并从各书 `statistics.json` 重建，应用不得退出，原始 sidecar、EPUB 和视频 SQLite 均不得改变。
11. 从小说 CommandBar 进入 Statistics，确认书架 rail、排序、导入和书架管理退出布局；使用 Bookshelf 按钮返回后，原 rail 和书籍卡仍可操作。
12. 验证全宽 Range & Trend，以及 Today、Goal、This Week、Reading Calendar、Selected Range、Speed Summary、Book Ranking、Shelf Comparison 全部存在；趋势图高度固定为 260 effective pixels，纵轴显示 0、三个中间刻度和最大值，横轴显示当前窗口首/中/末标签；切换字符、时长、速度时单位正确，Bar/Line 切换不改变其他卡片数据。
13. 分别把窗口调整到 `>=1260`、`840..1259` 和 `<840` effective pixels，确认三列、两列、单列状态生效，无裁切、重叠或第二个纵向滚动条；Today 目标环保持 118×118 effective pixels；This Week 卡片高度随自身内容收紧，不得因同一 Grid 行中的更高卡片而纵向拉伸；Calendar 保持 12×12 effective-pixel 方块、4-pixel 可见间距和七行紧凑布局，只允许横向滚动。点击不同日期后，选中范围与详情必须同步更新。
    连续在断点两侧调整窗口，确认每次只发生一次布局切换，统计视图保持响应且不出现 DispatcherQueue 重排循环。
14. 在加载未完成时返回 Bookshelf，再次进入 Dashboard；旧 load/refresh 不得覆盖新 snapshot，loading/refresh 状态不得残留，refresh 订阅始终只有一个。
15. 在英文和简体中文下检查所有 header、metric、empty/loading/warning 文案；用键盘遍历 range、日期范围拖动条、grain、metric、style、goal、calendar、ranking 和返回按钮，并确认 UI Automation name 非空。拖动条方向键移动一个窗口，Page 键移动多个窗口。
16. Light、Dark 与 High Contrast 下检查趋势线/柱、calendar heat、range/selection outline、ranking/shelf bars 和损坏警告均可辨认。

---

### 1.12 设置页备份验证

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~BackupServiceTests|FullyQualifiedName~TtuBookDataConverterTests"
.\build-and-run.ps1
```

手动验证：

1. 打开“设置 → 备份”，确认书籍、词典、ッツ Backup 三个分区和 Backup/Restore、Export/Import 操作均可用，处理中显示不可重复触发的进度遮罩。
2. 备份书籍后导入或删除一本书，再恢复 `.hoshi`；返回书架确认当前收藏被备份内容完整覆盖，EPUB、封面、书签、统计、高亮和 Sasayaki sidecar 均可读。
3. 在两个 Profile 中设置不同词典顺序、启用状态和折叠规则并备份词典；再修改集合和 Profile 配置后恢复，确认物理词典被覆盖、备份中的 Profile 配置恢复、只存在于当前环境的 Profile 仍保留，并立即可查词。
4. 将带 `../`、绝对路径或 Unix symlink entry 的伪造 `.hoshi` 传给恢复，确认显示错误、应用目录外没有新文件，当前书籍/词典收藏未改变。
5. 导出 ッツ ZIP，确认每本 EPUB 目录包含 `bookdata_1_6_*`，有数据时同时包含 `statistics_1_6_*`、`progress_1_6_*` 与封面；在 TTU Reader 或 Niratan 导入确认正文、CSS、图片和章节可读。
6. 导入同一 ッツ ZIP：不存在的原始书名应新增，已存在的原始书名不得重复创建，只覆盖 `statistics.json` 与 `bookmark.json`；返回书架确认统计与阅读进度刷新。

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
2. 启动 Niratan，确认真实 WinUI 顶层窗口出现
3. UI Automation 打开测试 EPUB（不允许固定像素或控制用户鼠标）
4. 连续翻页多次，检查内容漂移、裁切、空白页或页码/章节状态错乱
5. 调整窗口大小后验证 reflow：至少覆盖常规窗口和缩小窗口；resize 后正文必须重新布局
6. 捕获 reader 日志和诊断状态，确认 `scrollPosition`、`pageCount`、`pageIndex`、`sectionIndex` 一致且无越界
7. 如果设置了 `NIRATAN_NOVEL_READER_ARTIFACT_DIR`，必须保存 WebView2 截图和 `__niratanReaderState` JSON

### 2.2 Niratan 对齐要求

- 分页尺寸必须来自当前 viewport，窗口大小变化后重新计算
- 高 DPI 下横排分页宽度按 CSS `window.innerWidth` 计算；`devicePixelRatio` 禁止乘进 `--page-width`
- 翻页 scroll offset 按 `context.pageSize` 对齐；`column-gap` 不得加进翻页步长
- 安全区：`column-width = pageWidth - 2 * safeInline`，`column-gap = 2 * safeInline`，翻页步长仍按 `pageWidth`
- 诊断中的安全区像素从 `getComputedStyle(document.body).paddingLeft/paddingTop` 读取
- reflow 后优先按逻辑进度恢复位置
- 翻页边界由 native/WinUI 侧决定章节切换，reader JS 只报告状态
- 任何漂移修复都要对照 `docs/reference/Niratan/Features/Reader/ReaderWebView/reader.js` 及其 Swift 宿主

---

## 3. 字典查词验证

### 3.1 受影响文件

修改以下文件时，必须按本节验证：

```
Niratan/Services/Dictionary/JapaneseDeinflector.cs
Niratan/Services/Dictionary/DictionaryLookupService.cs
Niratan/Services/Dictionary/PopupHtmlGenerator.cs
Niratan/Views/Dictionary/DictionaryLookupPopup.cs
Niratan/Views/Dictionary/DictionaryPopupOverlay.cs
Niratan/Web/DictionaryPopup/popup.js
Niratan/Views/Pages/NovelReaderPage.xaml.cs
```

`native/hoshidicts/` 子模块绝对不能修改。

### 3.2 必跑验证

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64
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

`JapaneseDeinflector` 的目标是对齐上游 hoshidicts 日语变形还原实现：

- 条件位与上游 `Conditions` 语义一致
- `AddRule(...)` 的输入/输出条件、规则组名称和说明与参考实现一致
- 特殊动词与例外规则不能被通用后缀规则吞掉
- `PosToConditions()` 必须正确解析 Yomitan term `rules`
- 新增或调整规则时补充 `JapaneseDeinflectorTests`

参考路径：
```
native/hoshidicts/src/language/ja/deinflector.cpp
native/hoshidicts/src/lookup.cpp
```

### 3.4 词典设置与 i18n 规则

- 词典设置页对齐 Niratan：查词区包含 `scanNonJapaneseText`、`maxResults`、`scanLength`；
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
7. 在记事本或浏览器选词触发全局查词，确认只显示圆角 popup 表面，顶边没有
   宿主横条、标题栏或透明画布；在释义中继续点词后，每个 child 都是新的独立
   原生 popup 窗口并可越出 root 的边界。逐层确认弹框水平中心跟随当前选区，
   垂直只出现在选区正上或正下且保留间距；靠近屏幕边缘时只允许水平夹取或在
   上下方向间切换，不得覆盖选区。点击父层空白只关闭其后的 child，点击所有
   可见 popup 外部才关闭整栈。随后分别在小说和视频中做同样的嵌套查词，确认
   仍使用原窗口内的 overlay 层级，不产生新的原生窗口。
8. 应用启动后连续触发两次全局查词，再在释义中快速切换多个嵌套词。日志中
   `overlay warmed` 应为 `0ms`，关闭/替换窗口应复用待用池，不应再次出现约
   150–500ms 的 WebView2 初始化停顿；首屏 `contentReady` 仍须先于窗口 reveal。
9. 在“设置 → 键盘快捷键 → 全局”修改“查询选中文本”，确认状态日志立即显示
   新组合且旧组合不再触发；用新组合在记事本选词验证后点击重置，确认恢复
   `Ctrl+Alt+D`。再设置一个被其他全局 action 或系统占用的组合，确认编辑器显示
   冲突或注册失败状态，且应用不会保留两个全局 hotkey。

---

## 4. 音频验证

### 4.1 受影响文件

```
Niratan/Services/Audio/AudioService.cs
Niratan/Services/Audio/IAudioService.cs
Niratan/Models/Settings/AudioSettings.cs
Niratan/Views/Dictionary/DictionaryLookupPopup.cs (playWordAudio handler)
Niratan/Services/Dictionary/PopupHtmlGenerator.cs (SerializeAudioSources, audio injection)
Niratan/Web/DictionaryPopup/popup.js (fetchAudioUrl, expandAudioTemplate, playWordAudio)
Niratan/ViewModels/Pages/AudioSettingsPageViewModel.cs
```

### 4.2 验证流程

```powershell
dotnet build -p:Platform=x64
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Audio"
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

---

## 5. Video Anime4K 验证

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Anime4K|FullyQualifiedName~VideoEnhancement|FullyQualifiedName~MpvNative"
.\build-and-run.ps1
```

手动验证：
1. 打开视频侧边栏的“视频”页，在“视频增强”中选择已缓存的 `Anime4K Fast`，确认无需按钮即立即应用；选择尚未缓存的档位时只显示“下载”按钮，下载完成后自动应用。
2. 临时断网后重试已下载档位，确认通过本地 SHA-256 校验直接完成；删除一个文件后断网，确认失败且不会启用不完整预设。
3. 打开 1080p 动画，确认 `%APPDATA%\Niratan\VideoShaders\Anime4K\v4.0.1` 下六个文件存在，窗口缩放和全屏后画面持续渲染；重新打开视频后预设必须回到关闭且不加载着色器。
4. 分别切换 Fast、High Quality 和关闭，检查 libmpv `glsl-shaders` 列表按预设顺序出现或清空；不得使用 `glsl-shaders-append` property。
5. 检查高画质档 GPU 占用、掉帧、音画同步、HDR、硬解、截图和 Anki 视频媒体采集；性能不足时应能回到 Fast 或关闭。
6. 打开带外部字幕的视频，确认视频源和必要播放属性就绪后立即出首帧；字幕、章节和轨道稍后补齐时界面仍可操作。
7. 分别打开 16:9、4:3、竖屏和带旋转元数据的视频，确认窗口在 `file-loaded` 后按实际显示宽高适配当前显示器工作区；拖动任意窗口边缘或底部角落时，视频区域持续保持片源比例，全屏不受窗口比例约束。
8. 在 100%、125%、150% DPI 下切换右侧视频面板并调整其宽度，确认原生视频画面、字幕层与底部控制栏左/右/下边界始终重合，没有一像素漂移、越过侧栏或悬空。
7. 将字幕位置调到底部并显示字幕，移动鼠标唤出控制栏，确认侧边栏按钮、进度条和其余底部按钮都能点击；字幕未与控制栏重叠的区域仍可点选查词。

---

## 6. YouTube 视频验证（时间敏感）

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~YouTube|FullyQualifiedName~RemoteVideo"
dotnet build -p:Platform=x64
```

使用参考链接 `https://www.youtube.com/watch?v=yrL6Qny0E5M`：

1. 从资料库“添加链接”，确认实验性提示、输入校验、解析进度和取消；解析成功后先关闭对话框，再打开播放器。
2. 确认最高只显示到 1080p、每个高度一个选项，分离音视频有声播放；切换画质后位置、播放/暂停、音量、速度、延迟、循环与字幕不变。
3. 确认只列发布者字幕，自动生成日语字幕不出现；切换字幕后可查词、滚动 transcript 并在重启后恢复语言。
4. 返回资料库确认远程标题、缩略图和“YouTube 视频”分类；“在文件资源管理器中显示”隐藏，删除只移除记录。
5. 从资料库重开并确认进度恢复；验证截图和音频制卡，挖卡历史可通过稳定键重新打开远程条目。
6. 断网或等待签名 URL 过期后重试，确认只进行一次强制刷新和一次 muxed 降级；最终错误本地化且不包含响应正文、签名 URL或 headers。
7. 检查项目、发布目录和日志，确认不存在 `yt-dlp`、`youtube-dl`、`YoutubeExplode.Converter`、Deno、Node、helper 下载或子进程调用。

### 6.1 Anki 媒体验证

1. 在本地视频和 YouTube 视频中分别选择字幕词条制卡，字段映射同时启用 `{video-screenshot}` 与 `{video-audio-clip}`。
2. 提交成功后立即检查 Anki 卡片及 `collection.media`：截图和 `.m4a` 必须已经存在且非空，不能稍后才出现。
3. 临时让媒体目录只读或使用无有效音轨的片段，确认显示截图/音频采集错误且不提交引用缺失文件的卡片。
4. 打开带封面的 EPUB，使用含 `{book-cover}` 的字段映射制卡；卡片字段必须为 Anki 媒体文件名的 `<img>`，不得包含应用私有目录或盘符路径。
5. 对 `rules` 为 `v1`、`v5 adj-i` 和空字符串的词条分别制卡，确认弹窗不再因 `.some()` 报错。
