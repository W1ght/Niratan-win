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
