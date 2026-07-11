# 独立查词页 Popup 连续查词设计

## 目标

修复独立查词页中点击下方词典结果时整个结果区立即消失的问题，并让词典正文中的鼠标单击和按住 Shift 悬停都可以继续查词。继续查词使用子 popup 压栈：父级结果保持可见，子级中还可以继续创建下一层查询。

## 当前问题与根因

`Hoshi/Web/DictionaryPopup/popup.js` 已经具备 popup 内连续查词入口：

- 鼠标单击正文时调用 `lookupAtPopupPoint(...)`。
- 按住 Shift 移动鼠标时调用同一文本命中流程。
- 命中后发送带查询文本、来源和选区矩形的 `lookupRedirect` 消息。
- `DictionaryPopupOverlay` 收到带选区坐标的请求后按 `Nested` 模式查询并创建子 popup。

独立查词页同时在页面根节点以 `handledEventsToo` 注册 `PointerPressed`。该处理器依赖 `OriginalSource` 的 WinUI 可视树祖先来判断点击是否位于 `DictionaryPanelRoot` 内；WebView2 输入经过独立的可视/窗口边界时，这个祖先判断不可靠。页面处理器会在 WebView2 的 JavaScript `click` 之前调用 `_popupOverlay.Dismiss()`，导致根结果区被关闭，后续 `lookupRedirect` 无法完成。

这也违反项目既有的弹窗规则：`tapOutside` 不等于关闭根 popup。

## 方案比较

### 方案 A：移除独立查词页的全页指针关闭逻辑（采用）

独立查词页不再监听页面级 `PointerPressed` 来关闭嵌入式根 popup。关闭和子级清理由 `DictionaryPopupOverlay` 的窄事件处理负责。

优点：消除 WebView2 与 WinUI 输入顺序竞态；符合 `tapOutside ≠ dismiss`；不新增坐标系统；变更范围最小。缺点：点击搜索栏或页面空白不再关闭根结果，但独立查词页本身是持久结果界面，这一行为更符合用户预期。

### 方案 B：保留页面级关闭并改为几何命中

使用指针相对 `DictionaryPanelRoot` 的坐标和实际尺寸判断是否点在结果区内。

优点：仍可点击页面空白关闭。缺点：继续维护第二套关闭策略，并需要处理布局变化、DPI、子 popup 超出根面板边界等情况；仍与 Hoshi 的点外策略冲突。

### 方案 C：在 WebView2 或 overlay 中转发并消费指针

由 JavaScript 或 overlay 显式通知页面哪些指针事件应被消费。

优点：可以定制复杂关闭行为。缺点：扩大 IPC 和事件状态，容易引入重复查询、事件顺序和过期消息问题；当前需求不需要。

## 交互设计

### 根结果生命周期

- 主搜索成功后显示嵌入式根结果。
- 点击根结果正文不会关闭根结果。
- 按住 Shift 在根结果正文悬停不会关闭根结果。
- 点击页面空白、搜索框或其他页面控件不会隐式关闭根结果。
- 新的主搜索成功时，根结果原位更新并清理旧的子 popup。
- 新的主搜索无结果时，沿用现有行为关闭根结果并显示 `No results.`。
- 页面卸载、显式关闭命令或宿主生命周期结束时关闭并清理弹窗。

### 子 popup 栈

- 在可查词正文上鼠标单击后，JavaScript 选择点击位置的词并发送 `lookupRedirect`，来源为 `click`。
- 按住 Shift 悬停时立即选择鼠标位置的词并发送 `lookupRedirect`，来源为 `shift`，不引入延迟设置。
- 带选区坐标或来源的请求继续由 `DictionaryPopupRedirectRouter` 解析为 `Nested`。
- native 字典查询完成后创建或复用一个子 `DictionaryLookupPopup`，父 popup 保持可见。
- 在子 popup 中重复相同操作可以继续压栈；打开同一父级的新子结果时，关闭该父级已有的更深后代。
- 查询无结果、请求过期或查询失败时保持当前已提交 popup 栈，不关闭根结果。
- `tapOutside` 只按当前 overlay 规则清理相关子级，不得关闭根 popup。

### 非查词交互

- `summary` 展开/折叠、音频、Anki、操作栏、链接和滚动继续执行各自现有行为，不触发嵌套查词。
- JavaScript 只负责文本命中、选区矩形和发送窄消息；字典查询仍全部在 C# service 中执行。
- 所有 WebView2 消息继续经过现有类型和字段校验，不扩大 native API。

## 架构与改动边界

- `Hoshi/Views/Pages/NovelLookupPage.xaml.cs`
  - 移除页面级 `PointerPressed` 注册、注销和 `OnPagePointerPressed` 关闭处理器。
  - 保留主搜索、无结果关闭、尺寸同步、预热和生命周期清理。
- `Hoshi/Web/DictionaryPopup/popup.js`
  - 复用已有单击和 Shift 连续查词实现；只有回归测试证明存在遗漏时才做最小修正。
- `Hoshi/Views/Dictionary/DictionaryPopupOverlay.cs`
  - 复用已有 `lookupRedirect`、`Nested` 路由、子 host 池和栈清理逻辑。
  - 不改变当前工作区中已有的嵌入式根 popup 尺寸修正。
- `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs` 与现有 popup 单元测试
  - 先增加失败的回归契约，再实现最小修复。

不修改 `native/hoshidicts/`，不增加依赖，不把字典查询逻辑放进 JavaScript，不重写 Yomitan structured content。

## 测试策略

1. 新增独立查词页回归测试，确认页面不再注册会调用根 popup `Dismiss()` 的全页 `PointerPressed` 处理器。
2. 保留并运行 popup Web 资源契约，确认正文单击调用 `lookupAtPopupPoint(..., 'click')` 并发送 `lookupRedirect`。
3. 保留并运行 Shift 悬停契约，确认按住 Shift 时调用 `lookupAtPopupPoint(..., 'shift')`，且 miss 不关闭 popup。
4. 运行 `DictionaryPopupRedirectRouterTests`，确认带点击/Shift 来源和选区坐标的请求解析为 `Nested`。
5. 运行字典专项测试后执行完整 x64 测试与构建。
6. 启动应用并在独立查词页手工验证：
   - 搜索一个有多段释义的词。
   - 单击释义文字后父结果仍在，子 popup 出现。
   - 在子 popup 内继续单击，能够创建下一层查询。
   - 按住 Shift 在父级和子级正文移动，均能连续查询且不会关闭根结果。
   - 点击音频、Anki、折叠标题、链接、滚动区域和页面空白，行为符合上述策略。
   - 快速连续移动 Shift 查询时只有当前有效请求能更新子 popup。

## 成功标准

- 独立查词页中的根结果不会因在其内部或页面其他位置按下鼠标而消失。
- 鼠标单击与 Shift 悬停都能从 popup 正文发起 native 字典查询。
- 新结果以子 popup 显示，父级保持可见，并可继续向下查词。
- 无结果、过期请求和非查词控件不会意外关闭根结果。
- 阅读器、视频查词、全局 popup 窗口以及现有 popup 尺寸行为不发生回归。

## 非目标

- 不改变主搜索输入框和结果排序。
- 不新增 Shift 延迟、弹窗关闭或嵌套深度设置。
- 不允许点击被子 popup 实际遮挡的父级文字。
- 不重构字典查询 service、WebView2 renderer 或 popup transaction 协议。
