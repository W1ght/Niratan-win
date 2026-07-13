# Changelog

## Reader 反向跨章曾闪过错误进度并重复结算

**原因**：
- 相邻跨章、普通程序化跳转、Page 可见状态和 lifecycle writer 曾由多个 tracker/coordinator 与可变字段分别拥有；从 B 第一页返回 A 时，native 会先发布近似端点或旧候选位置，再等待 WebView 算出 A 的最后一页，因此出现临时 `1.0`/100%、二次进度更新和 baseline/bookmark 竞争。
- bridge error、关闭/后台与 Sasayaki 异步回调没有共享 point-of-no-return；目的地写入开始后仍可能被源位置恢复或另一条位置写入穿插，迟到的同章 render callback 也可能误认成当前完成。

**解决**：
- 使用单一 `ReaderNavigationTransactionCoordinator` 持有不可变源/目的地、generation 和独立 `renderAttemptId`；目的章节隐藏分页，WebView 返回最终 page-aligned progress 后才按“保存 bookmark → 重置 baseline → 原子发布 → reveal”完成一次提交，旧 tracker/coordinator 与候选字段全部移除。
- `Rendering` 失败或 lifecycle 取消恢复源位置；`Committing` 进入不可取消的持久化边界，lifecycle 等待并按 durable 结果恢复目的地或源位置。bridge error、重复/过期 completion 和 recovery 都按事务身份收敛到一个终态。
- 事务存续期间统一阻止翻页、目录/搜索/链接/history 与 Sasayaki auto-scroll/load/progress/save 等位置突变；播放 UI 和非位置高亮仍可继续，异步回调在 await 后再次校验 gate。

---

## Reader 同章翻页未结算统计且最终同步可能混用位置

**原因**：
- Web bridge 的 `scrolled` 只表示同章滚动成功，native 曾把它当成命令已处理而不是位置已 `moved`；只有章节边界进入统计 checkpoint，导致同章翻页未及时更新字符、Session/Today 和 sidecar。
- bookmark、statistics 与延迟同步曾读取可变的当前进度；writer 排队或 Close/Background final flush 期间位置从 X 变为 Y 时，可能把不同时间点的数据写入同一次提交，或在最后 export 完成前取消 coordinator。

**解决**：
- 用 `ReaderPageNavigationEvent` 明确传递 `Scrolled`/`Limit`、方向与最终进度，再由 `ReaderPageNavigationOutcome` 统一表达 `DidMove`、同章移动或相邻章节；真实同章移动立即保存 bookmark 并写一次 typed reading checkpoint，程序化跳转仍保持独立事务。
- Reader writer 按 admission 顺序串行，并为 bookmark、statistics 和 sync 捕获同一份进度 snapshot；自动同步 coordinator 负责 open import、30 秒 debounce、single-flight follow-up，以及 Close/Background 的可等待 final flush，Close 只在最终 export 后取消。

---

## 小说书架分区、云端导入与统计入口回归

**原因**：
- 本地书卡依赖 code-behind 点击事件，分区模板重构后未归档书卡没有稳定绑定打开命令；Reading 仍被当作可选 rail，且各分区使用单行横向布局。
- Google Drive 列表只展示占位图，没有复用带鉴权的缩略图请求与本地缓存；页面级取消源在任一本书导入后刷新目录时会取消其他导入，因此无法并行。
- Dashboard 的 XAML AdaptiveTrigger 与 code-behind 同时修改同一组 Grid 属性，SizeChanged 又通过 DispatcherQueue 重入；统计缓存键还会在 UI 线程同步扫描所有 sidecar 文件时间。
- Dashboard 首次实例化还引用了 WinUI 3 中不存在的 `AccentStrokeColorDefaultBrush`；磁盘缓存中的 snapshot 有多个构造函数但没有指定 JSON 构造函数，重启后读取缓存会抛出未处理异常。两者都会表现为点击统计后卡住或进程退出。

**解决**：
- 所有本地书卡显式绑定 `OpenNovelCommand`，Reading 从未读完且有进度的书籍派生；Reading、自定义书架、Google Drive、Unshelved 统一为可换行的自适应多行分区。
- Google Drive 封面使用鉴权缩略图、格式校验、原子写入和磁盘缓存；导入改为每书独立状态与页面生命周期取消，最多 3 本并行，排队、下载、失败重试互不影响。
- 统计入口先让出 UI 帧，缓存键扫描移到后台线程；Dashboard 只保留 code-behind 单一布局所有者，并仅在跨越 840/1260 effective-pixel 断点时重排。
- 日历选区改用有效的 accent brush，并显式标注 snapshot 的 JSON 构造函数；新增“新缓存实例从磁盘重载”测试，覆盖应用重启后的真实缓存读取。

---

## Google Drive OAuth 回调显示成功但连接失败

**原因**：
- loopback 已收到授权码后就向浏览器显示 `Google Drive connected`，但此时 token 交换尚未完成。
- 桌面 OAuth 客户端要求 token 请求携带 `client_secret`；Windows 实现只接收和发送 `client_id`，首次交换返回 `client_secret is missing`，刷新路径也缺少同一参数。

**解决**：
- 设置页使用 `PasswordBox` 接收客户端密钥，成功授权后将其与 token 一起存入 Windows Credential Manager，不写入普通设置。
- 授权码交换和 refresh token 请求都发送客户端密钥，并兼容读取不含密钥的旧凭据。
- loopback 页面只提示已收到授权，最终连接成功状态由 token 交换和凭据保存完成后的 WinUI 页面显示。

---

## 视频 popup 显示后无法继续查字幕

**原因**：
- 视频 popup 的透明外层和 overlay Canvas 覆盖字幕并参与命中，popup 显示后空白区域也会截获单击和 Shift hover。
- 根 popup 替换曾在新首屏 ready 前隐藏或清空已显示内容；渲染器失联时 generation 所有权不明确，过期回调、无结果、取消或失败可能替换或关闭最后一次成功结果。
- 视频查询与 popup 提交没有共同的 request-version 显示所有权，快速连续查询时，旧请求的迟到提交可能取得新锚点和高亮。
- overlay 曾在新 generation 提交前覆盖嵌套查词设置和根锚点；旧 popup 仍可交互时会读取新请求上下文，abort 后也无法恢复。
- 字幕 Canvas 与 JS bridge 的空命中没有携带点击/Shift hover 来源，hover 到字符间隙或扫描边界会被误判为显式关闭并清除已提交 popup、高亮和所有权。

**解决**：
- 视频宿主启用 `DictionaryPopupCanvasInputMode.VisibleHostsOnly`：实际 popup host 保持可交互，透明空白把输入交给字幕 Canvas；默认 modal 行为不影响小说和其他宿主。
- 根 popup 使用 committed/pending 两阶段事务。JavaScript 按 document epoch 暂存 DOM 和完整交互数据并发送 prepared；native 只接受精确 epoch + generation，线性化 commit 后再原子替换。提交状态无法确认时导航到新 epoch shell，待其 ready 后才精确终止旧 generation 并恢复 latest queue，旧文档迟到消息不能完成新事务。
- 视频侧为每个 request version 分配唯一显示身份；只有当前或已被 renderer 接受的精确事务能在 committed 事件后提交锚点和高亮，queued drop、abort、显式关闭也按同一身份终止所有权。
- 新查询无结果、被取代、取消或失败时保留最后一次 committed popup、交互上下文和高亮；只有新的成功 generation 原子提交后才整体切换。
- overlay 的 context、anchor 与 layout 按 generation + trace 暂存；嵌套查词在每个异步边界校验 root/parent generation，root 高亮脚本也拒绝不同 generation 的 DOM；resize 同时刷新 committed 与 exact pending layout 但只显示 committed，精确 commit 才整体提升，abort/drop/stale terminal 不改变旧状态。
- Canvas → JS → native 显式传递 `dismissOnEmpty` / `isHover`：点击空白仍关闭，Shift hover 的空白、间隙和扫描失败只重置 hover 去重并保留 committed popup、高亮及 accepted transaction。
---

## 小说统计已有完整数据，但书架内联面板无法呈现 Niratan Dashboard

**原因**：
- typed sidecar repository 与纯计算器已经覆盖最近一年、目标、速度、趋势、日历、排行和书架对比，但 UI 仍是书架顶部的限高内联面板。
- 统计展示状态和格式化通过 `NovelLibraryPageViewModel` 转发，无法建立 Niratan 的全页切换、独立生命周期、三档自适应布局和完整键盘/UI Automation 契约。
- 该问题是展示投影和页面架构缺口，不是统计引擎或 `statistics.json` 数据缺失。

**解决**：
- 新增独立 `NovelStatisticsDashboardViewModel` 与全页 `NovelStatisticsDashboardView`；父 ViewModel 只切换 Bookshelf/Statistics 并提供当前书籍、书架状态。
- 补齐 Range & Trend、Today、Goal、This Week、Reading Calendar、Selected Range、Speed Summary、Book Ranking、Shelf Comparison；自研 UI-only Canvas 控件绘制 Bar/Line，不新增图表依赖。
- Dashboard 使用单一纵向滚动所有者，在 1260/840 effective pixels 切换三列、两列、单列；Calendar 仅横向滚动，所有 selector 有稳定 AutomationId 与中英文资源。
- 激活 generation、linked cancellation source 与激活期 refresh 订阅保证离开/重进时旧 load 和旧 refresh 不能覆盖新 snapshot；损坏 sidecar 仍保持原文件不变。
- 小说、书架和统计继续以 JSON sidecar 为真源；SQLite 只保留视频业务边界，视频功能未移除。

---

## Reader 跳转会污染阅读统计，关闭时可能丢失最后一段时间

**原因**：
- Reader ViewModel 曾同时负责时钟、字符差、日期聚合和 sidecar 写入，真实翻页、程序化跳转与生命周期事件没有统一 checkpoint 边界。
- 搜索、目录、高亮、字符和 Sasayaki 跳转直接改写章节/进度，分页对齐后的回调无法区分普通 restore 与跳转 restore，长距离跳转可能被计为阅读字符。
- Reader 没有 generation-scoped destination、内部链接/history 事务、一秒投影和可等待的窗口关闭边界。

**解决**：
- `ReaderStatisticsSession` 独占 TTU 公式、TimeProvider、本地日期 rollover、基线和 `statistics.json` 写入，ViewModel 只投影状态。
- 真实阅读移动使用 typed checkpoint；所有程序化入口统一执行“结算旧位置 → 等待 generation 目标 → 保存最终书签 → 重置基线”，过期 bridge callback 不能完成新跳转。
- WebView2 拦截 EPUB 链接，native 只允许同源 spine 目标；补齐 fragment 与 Back/Forward 历史导航并复用同一统计事务。
- tracking 且未 paused 时每秒更新内存统计；窗口最小化写 Background checkpoint，返回书架、页面消失和主窗口关闭共享幂等 Close checkpoint。
- Dashboard 改为最近一年 typed snapshot 与纯计算器，补齐可选范围/anchor、目标、速度窗口、趋势粒度与指标、日历详情、Book Ranking 指标和 Shelf Comparison，并移除旧 By Book distribution。
- 损坏统计 sidecar 会显示可见警告并保持原文件不变；派生 Dashboard cache 使用 schema/key 校验和书库事件失效，命中后后台重读 sidecar，缓存损坏只删除缓存自身。

---

## 小说存储与书架状态曾被 SQLite 绑定

**原因**：
- 小说元数据、进度与排序曾以主 App SQLite 为真源，无法按 Niratan 的单书 sidecar 结构独立迁移、恢复和同步。
- 书架缺少持久化服务边界，书籍移动、书架排序和损坏 JSON 恢复无法保证原子性。

**解决**：
- 小说元数据、书签、书籍信息、统计和高亮改为每书目录 sidecar；全局顺序与书架分别写入 `book_order.json`、`shelves.json`。
- 启动时先备份并校验导出旧小说表，再退役小说 SQLite schema；失败时保留旧表和原文件并切换为只读恢复模式。
- 主 App SQLite 缩小为视频业务边界，视频功能和外部只读音频数据库保持不变。
- 新增 Reading、自定义书架、Unshelved 与独立 Google Drive rail，以及创建、重命名、删除、排序和书籍移动入口。

---

## 视频字幕软阴影出现双命中或黑色矩形

**原因**：
- 可见 Canvas 与可交互 WebView2 同时处理字幕点击时，一次操作会产生两套坐标和两次查询；后到的空结果可能立即关闭刚打开的 popup。
- 把 WebView2 改为唯一可见字幕层虽能恢复 DOM 选中，但透明 WebView2 无法合成到原生视频 HWND 上，会暴露整块黑色 backing surface。
- Canvas 自定义行距只设置了 `LineSpacing`、没有同步设置 `LineSpacingBaseline`，导致字形基线与 `GetCharacterRegions` 返回的选区行框纵向错位。

**解决**：
- `CanvasControl` 成为唯一可见、可命中的字幕表面，统一负责文字、Niratan 单层高斯阴影、字符命中和选中高亮。
- WebView2 仅保留为 `Opacity=0`、`IsHitTestVisible=False` 的无头选择桥，继续复用非日文扫描和边界提取逻辑，不参与输入或视频合成。
- 普通点击和 Shift hover 都先由同一个 `CanvasTextLayout` 命中，再把 UTF-16 字符偏移发送到窄 JS bridge；popup 关闭或字幕切换时同步清除 Canvas 选中范围。
- 自定义行距使用 1.25 倍字号，并把基线设置为字号本身，使选区行框、字形和查词锚点共享同一纵向布局。

---

## 视频查词首次打开和大词条结果卡顿

**原因**：
- 视频窗口在 native lookup 完成后才预热根/子 WebView2，首次查询承担完整冷启动成本。
- 全部 `maxResults` 结果曾被序列化进单个 `ExecuteScriptAsync`；大型 structured content 可产生 1 MB 以上 payload，使 WebView2 传输远慢于 native lookup。
- 字幕 Shift hover 只有 in-flight 布尔锁，没有 latest-request-wins，旧结果可能继续占用热路径。

**解决**：
- 字幕 WebView ready 后后台预热 popup，保留按需 warm 作为失败回退。
- 首条结果独立注入并显示，剩余结果以不超过三条的 generation-scoped 小批次追加，保留用户配置的最终结果数量和顺序。
- 视频查词请求使用版本和取消令牌；新请求使旧请求失效，旧结果不能再高亮、显示或替换当前 popup。
- `DictionaryPopupRequest.TraceId` 贯穿视频 overlay，并分别记录首批/延后批次的序列化字节数和传输耗时。

---

## popup 先显示释义、后闪出主词

**原因**：
- popup 同时存在两套 `contentReady`：shell observer 在第一块释义出现时提前通知，完整 renderer 又在所有词条完成后通知。
- renderer 隐藏了网页根节点，但双列布局给释义卡写入内联 `visibility: visible`，导致 native 提前显示 WebView2 时只有释义能穿透根隐藏，标题和标签仍不可见。
- 全部结果按词典逐帧渲染，放大了半成品暴露时间；native lookup、反序列化和 rebuild 还可能在 WinUI 线程同步执行。

**解决**：
- `popup.js` 成为唯一 ready 来源：先一次性构造并布局完整首词，再发送当前 generation 的唯一 `contentReady`；其余词条随后逐帧追加。
- 保留 native `Opacity=0` generation gate，过期 renderer 不能显示旧内容或继续追加。
- hoshidicts lookup、styles、media 和 rebuild 通过同一 worker executor 串行访问 native session；styles 缓存到下次 rebuild。

---

## popup 圆角出现黑色角块

**原因**：
- WinUI 3 WebView2 不能把透明网页像素合成到同窗口的兄弟 XAML 视频内容上；圆角外的透明像素会退回到视频窗口的黑色宿主背景。
- 对 WebView2 父级 Grid 添加 Composition 圆角裁剪不能改变 WebView2 的透明合成限制。

**解决**：
- 使用 WinUI 原生 Border 绘制 popup 外轮廓，并按圆角半径计算 12→4 DIP、8→3 DIP 的安全内缩，使矩形 WebView2 完全位于圆角轮廓内。
- 原生护边、WebView2 默认背景和网页根背景统一使用不透明主题色，避免初始化、导航和主题切换期间露出黑色 backing surface。

---

## popup 嵌套查词冻结/崩溃

**原因**：
- popup 内 Shift hover 会在 `mousemove` 高频路径中连续触发 `lookupRedirect`，同一个 query 也可能重复进入 native lookup。
- Windows 侧每次 nested 查词都会新建 child `WebView2`，并在关闭/替换 child 时立即 `Dispose()` 旧 WebView2。高频创建和销毁 WebView2 会触发 native heap corruption，WER 表现为 `ntdll.dll` `0xc0000374`，有时先表现为窗口冻结。
- 隐藏后的 child host 仍然 `Visibility=Visible`，命中测试只看 `Visibility` 时会把已隐藏 host 当成可点击区域，进一步放大关闭/重建异常。

**解决**：
- `DictionaryPopupOverlay` 新增 redirect version + async semaphore，过期 redirect 结果不再创建 popup，child redirect 串行更新。
- child popup 改为池化复用：关闭时只 `Hide()`，不在查词热路径销毁 WebView2；只在 overlay `Dispose()` 时统一释放。
- 隐藏 host 的命中测试同时检查 `Opacity` 与 `IsHitTestVisible`。
- `popup.js` 对 Shift hover nested lookup 按 query 去重，避免同一文本连续触发重复 redirect。

---

字典查词链路的已修复问题记录。只记根因和解决方案，不记流水账。

---

## 嵌套查词无结果或空白

**原因**：
- 子弹窗创建后立即同步导航，`CoreWebView2` 还没有初始化完成，导致首次或嵌套查词出现空白、脚本未注入或 WebView2 生命周期竞态。
- 弹窗内选区脚本只处理 `caretPositionFromPoint`，在 WebView2/Chromium 下部分点击位置需要 `caretRangeFromPoint` 或 DOM rect fallback。

**解决**：
- 子弹窗改为 `ShowResultsNavigatedAsync`，创建后先 `await EnsureWebViewAsync()`。
- `PopupHtmlGenerator` 注入 Android 风格的选区 fallback：优先 caret API，失败后扫描文本节点 rect。

---

## 弹窗位置覆盖原窗口/父弹窗

**原因**：
- Windows 侧横排定位曾把 popup height 当成 screen height 传入 `SpaceBelow`。
- 嵌套弹窗曾固定按父弹窗偏移量摆放，没有使用弹窗内当前选区 rect。
- 子弹窗滚动时直接清理全部子弹窗，和 Android `closeChildPopupsForScrolledParent` 的栈行为不一致。

**解决**：
- `DictionaryPopupOverlay.ShowBelow` 改为使用真实 `screenHeight`，按 Android 规则 `spaceBelow >= popupHeight` 判断。
- `lookupRedirect` payload 携带弹窗内选区 rect，C# 侧换算后用同一套 Android-style `PositionHost` 定位。
- 子弹窗滚动改为 `ClearChildrenAfter(parent)`。

---

## 英文单词无法查词

**原因**：
- Android 默认 `scanNonJapaneseText = true`，Windows 曾无条件把非日文字符当作扫描边界。
- 弹窗内的 selection shim 也有同样的无条件非日文边界判断。

**解决**：
- `selection.js` 改为读取 `window.scanNonJapaneseText`，默认允许非日文扫描。
- `PopupHtmlGenerator` 的 `isScanBoundary` 改成仅在 `window.scanNonJapaneseText === false` 时阻断非日文字符。

---

## 弹窗图片、词频、音调与栈关闭

**原因**：
- Android popup 通过 `https://hoshi.local/image` 加载媒体，Windows 曾设为空导致图片加载失败。
- 字典导入曾只按文件名前缀判断类型，漏掉 Yomitan metadata bank 内的 freq/pitch。
- lookup rebuild 曾只加载 term 字典，没把 frequency/pitch 一起加入查询。
- 子弹窗关闭时只移除当前 popup，可能留下子孙 WebView2。

**解决**：
- `DictionaryLookupPopup` 为 `https://hoshi-dictionary-media.local/image` 增加 `WebResourceRequested` 拦截。
- `DictionaryImportService` 检测 metadata bank 内容，导入后分别写入 Frequency/Pitch 配置。
- `DictionaryLookupService` rebuild 时分别加载 Term、Frequency、Pitch 已启用字典。
- 弹窗内查词统一走 `DictionaryPopupOverlay.HandleRedirectAsync`。

---

## 弹窗振假名影响与父子层关闭

**原因**：
- Windows popup 内 selection shim 曾直接使用 caret range，可能把振假名当正文查词。
- `tapOutside` 语义与 Android 不一致：Windows 曾把 root 和 child 的 tapOutside 混淆。
- 阅读页 Shift hover 移到新句子时，旧 popup 曾等新结果回来才替换。

**解决**：
- `PopupHtmlGenerator` 的 popup selection shim 过滤 `rt/rp`，用 `getCharacterAtPoint` 校准真实正文字符。
- `popup.js` 按 Android 原版区分 `tapOutside` 行为：root 只关闭 child，child 只关闭其后代。
- `DictionaryPopupOverlay` 把 `tapOutside` 和 `dismiss/close` 分成两条事件。
- 阅读页收到新 lookup request 后先 `Dismiss()` 当前 overlay 再查新词。

---

## 词频显示与 popup 闪烁

**原因**：
- hoshidicts 支持三种 frequency 形态，Windows 曾只读平铺字段导致值掉成 0。
- 同一本词频词典的多条 frequency 曾不聚合，导致重复显示多个标签。
- Windows 曾没有把词频纳入排序，结果顺序与 Android 偏离。
- root/child popup 曾在渲染完成前就 `Visible`，导致空壳或闪烁。
- warm root WebView2 复用上一轮 DOM，旧 `contentReady` 可能在新位置短暂显示残影。

**解决**：
- `ParseFrequency` 递归解析嵌套 `frequency` 字段，保留真实 `value` 和 `displayValue`。
- 同一词典的频率聚合到现有 `FrequencyEntry`，去重。
- lookup 排序加入最小 frequency rank，对齐 hoshidicts 低 rank 优先原则。
- popup 对齐 Android alpha 模型：先 `Opacity=0` + `visibility:hidden`，收到当前 generation 的 `contentReady` 后再显示。
- 每次显示生成新 render generation，旧 generation 的 ready 消息失效。
- 待渲染和关闭状态下禁止把 WebView2 `Visibility` 设为 `Collapsed`。
- 预热后保持 `_overlayPopup.IsOpen = true`，关闭查词只 `Hide()` root host。

---

## MK3 字典导入失败

**原因**：
- `MK3Fix0213.zip` 的原始 `index.json` 标题是非 ASCII；Windows 上 hoshidicts 直导会在创建/读取导入目录时触发代码页或 SEH 异常。
- 参考 Hoshi Reader Windows `codex/anki-sasayaki-sidecar` 分支后确认，该分支也不是直接导入成功，而是先失败再走 lookup-safe 兼容 zip retry。

**解决**：
- `DictionaryImportService` 保留正常直导路径；仅在 Windows code-page/SEH 类失败时创建临时兼容 zip。
- 兼容 zip 只保留查词核心文件：`index.json`、`styles.css`、`term_bank_*`、`term_meta_bank_*`、`tag_bank_*`，并把临时标题改为 ASCII `hoshi-import-*`。
- native 导入成功后把 `index.json` 显示标题恢复为原始标题，字典是否进入 Term/Frequency/Pitch 目录只以 native import count 为准。
- `MK3Fix0213.zip` 验证结果为 `term=140821`、`freq=0`、`pitch=0`、`media=0`；这本 zip 是词条字典，不包含可导入词频/音调数据。
