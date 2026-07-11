# Changelog

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
