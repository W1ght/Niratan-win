# 视频 popup 显示期间连续查词设计

## 目标

视频字幕字典 popup 已显示时，用户无需先关闭 popup，仍可在未被 popup 遮挡的字幕上继续使用鼠标单击或 Shift hover 查词。新查询使用同一个根 popup 原位替换结果，并保持当前的 popup 内滚动、嵌套查词和点外关闭行为。

## 已确认交互

- 鼠标单击和 Shift hover 使用同一套连续查词行为。
- 新查询不先关闭当前 popup。
- 查询和新内容渲染期间继续显示旧 popup 与旧高亮。
- 新内容首屏完成后，一次性提交新内容、锚点和高亮。
- 新查询无结果时保留旧 popup 与旧高亮，只把状态更新为无结果。
- 被更新请求取代、取消或失败的查询不得关闭或替换最后一次成功显示的 popup。
- 点击非字幕空白区域仍关闭 popup。
- popup 实际窗口区域仍接收滚动、点击、音频、Anki 和嵌套查词输入。

## 根因

视频 popup 的宿主 `VideoDictionaryPanelChrome` 和 `PopupOverlayCanvas` 都覆盖大部分视频窗口，并使用透明背景参与命中测试。popup 显示后，这个全屏透明命中面位于字幕 `CanvasControl` 上方，导致 popup 窗口外的透明区域也截获单击和鼠标移动。字幕查词协调器本身已经支持 latest-request-wins；问题发生在输入到达字幕 Canvas 之前。

## 方案比较

### 方案 A：视频专用空白区域穿透 + generation 原子替换（采用）

仅视频播放器让 popup 宿主的透明空白区域不参与命中测试，popup 的实际 `VisualRoot` 仍可交互。新结果在同一个 WebView2 内以新 generation 暂存，首屏 ready 后再提交。

优点：保留单一字幕命中坐标、变更范围受控、不影响 EPUB 阅读器；可同时满足连续查词和无闪烁替换。缺点：需要为 popup 渲染器增加“最后一次已提交内容”和“待提交 generation”的明确状态。

### 方案 B：所有 DictionaryPopupOverlay 全局穿透

统一移除 overlay Canvas 的透明命中面。

优点：实现较少。缺点：会改变小说阅读器和其他 popup 宿主的点外行为，扩大回归面，不采用。

### 方案 C：从 popup overlay 手动转发鼠标坐标

overlay 截获输入后，再换算并调用字幕命中。

优点：不改变 overlay 命中属性。缺点：重新引入第二套坐标和事件路径，容易出现重复查询、DPI 偏移和焦点问题，不采用。

## 架构设计

### 1. 视频专用 pointer passthrough

`DictionaryPopupOverlay.UseCanvas` 增加明确的宿主选项，默认保持现有行为；视频播放器传入“只让可见 popup host 命中”的模式。

在该模式下：

- `PopupOverlayCanvas` 与外层 `VideoDictionaryPanelChrome` 的空白区域不绘制透明命中背景。
- overlay Canvas 保持可见，子级 `DictionaryLookupPopup.VisualRoot` 继续参与命中。
- 点击 popup 内部仍由 popup 处理。
- 点击未被 popup 遮挡的字幕会直接命中 `SubtitleCanvas`；字幕处理器将事件标记为 handled，父级点外关闭逻辑不会先关闭 popup。
- 点击其他视频空白仍冒泡到现有 `TryDismissLookupPopupFromOutsidePointer`，关闭 popup 并消费该次点击。

默认模式不变，避免影响小说阅读器和其他字典入口。

### 2. 连续查询数据流

```text
字幕单击 / Shift hover
  -> CanvasTextLayout 字符命中
  -> lookupAtOffset 分词桥
  -> VideoSubtitleLookupCoordinator.Begin（新版本取消旧版本）
  -> native dictionary lookup
  -> popup pending generation
  -> 首屏 contentReady
  -> 原子提交内容 + popup 锚点 + 字幕高亮
```

当前 popup 在 native lookup 和 pending generation 期间保持最后一次成功内容。查词路径不得等待 `contentReady`；ready 消息继续异步驱动提交，确保 Shift hover 不被阻塞。

### 3. popup 内容事务

根 popup 区分两个状态：

- **committed**：当前可见、可交互的最后一次成功 generation。
- **pending**：正在后台构造首屏的新 generation，带请求版本、trace id、目标锚点和目标高亮。

`popup.js` 不再在收到新注入时立即清空可见 `entries-container`。它在独立的暂存容器中构造新 generation 的首个词条；generation 仍为当前且首屏布局完成后，使用一次 DOM 替换提交暂存节点，然后发送带 generation 的 `contentReady`。

native 层在已有 committed 内容时不把 `VisualRoot.Opacity` 或 `IsHitTestVisible` 置为隐藏；只有首次冷显示仍保持现有的 `Opacity=0` ready gate。收到当前 generation 的 `contentReady` 后，overlay 提交目标锚点和字幕高亮。过期 ready 消息不能改变内容、位置、高亮或可见性。

取消 pending generation 时仅丢弃暂存内容并恢复 committed 状态，不调用根 popup 的 `Dismiss`。没有 committed 内容的首次显示若被取消，则沿用现有隐藏行为。

### 4. 无结果和错误

- popup 已显示：新查询无结果、取消或失败时，保留 committed popup 与旧高亮；更新状态文字，不改变 popup 位置。
- popup 未显示：无结果时维持无 popup 状态。
- 新请求到达时继续取消前一个 pending 请求；旧请求即使稍后完成也不能提交。
- 用户明确点外关闭后，清除 committed/pending 状态和字幕高亮；关闭前启动的请求不能重新打开 popup。

## 组件改动边界

- `VideoPlayerWindow.xaml`：视频 popup 外层空白区域改为非命中背景。
- `VideoPlayerWindow.xaml.cs` / `VideoPlayerWindow.SubtitleOverlay.cs`：启用视频专用 passthrough；把新查询的锚点和高亮作为 pending 状态提交；无结果时不销毁已显示 popup。
- `DictionaryPopupOverlay`：增加默认关闭的视频专用 passthrough 选项；管理 committed/pending 根 popup 布局提交。
- `DictionaryLookupPopup`：区分首次显示与已有内容替换；发布 generation-scoped ready/取消结果。
- `popup.js`：首屏使用暂存容器，generation 校验后原子替换可见 DOM。
- 不修改 `native/hoshidicts/`，不把字典查询逻辑移入 JavaScript。

## 测试策略

1. overlay 命中契约：视频模式不让空白 Canvas 截获输入，默认模式行为不变，popup `VisualRoot` 仍可交互。
2. 视频连续查词：popup 可见时，单击和 Shift hover 都能启动新的 lookup；不会先调用 `Dismiss`。
3. latest-request-wins：连续请求只有最后一个 generation 能提交内容、位置和高亮。
4. 保留旧内容：pending、无结果、失败和取消期间 committed popup 保持可见并可交互。
5. 原子提交：`popup.js` 在首屏完成前不清空可见容器，过期 generation 不替换 DOM。
6. 点外关闭：非字幕空白仍关闭 popup；popup 内滚动、音频、Anki 和嵌套查词不触发关闭。
7. 运行视频字幕专项测试、字典 popup 测试和全量 x64 测试；实机验证同一字幕连续单击、Shift hover、无结果词和快速移动竞态。

## 非目标

- 不允许点击被 popup 实际窗口遮挡的字幕文字。
- 不改变 popup 的尺寸算法、字典结果排序或嵌套 popup 栈规则。
- 不改变小说阅读器和全局查词窗口的点外关闭策略。
- 不新增设置项。
