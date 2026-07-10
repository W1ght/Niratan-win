# Changelog

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
