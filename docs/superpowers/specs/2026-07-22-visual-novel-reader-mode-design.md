# Visual Novel Reader Mode Design

## 背景与边界

用户要求参考 Hoshi Reader Android 增加 VN（视觉小说）阅读模式。固定的 Niratan 参考版本没有 VN 模式，因此这是显式产品扩展，不改变 Niratan 对章节导航、书签、统计、查词、标注、Sasayaki 和安全边界的既有语义。

实现继续使用 WebView2 加载经过清洗的单个 EPUB spine 章节。VN 只改变当前章节在 viewport 内的分屏和显示方式，不引入 foliate-js、不建立另一套 EPUB 解析或排版引擎，也不把章节切换决定权移到 JavaScript。

## 用户行为

- 阅读布局提供互斥的“分页 / 连续 / VN”三种模式，并同时出现在全局设置和 Reader 外观面板。
- VN 可按块级段落分屏，或按每屏 1–12 句分屏。
- 可配置每秒 0–120 字；0 表示立即显示。
- 向前操作在当前屏仍在揭示时只补全文字；当前屏完整后再次操作才前进。
- 向后操作进入上一屏并立即显示完整内容。
- 可选保留成对的日文/中文引号内容，尽量避免把一段对话拆到两个屏幕。
- 可选点击正文空白处前进。选区、链接、图片、标注菜单和字典 popup 继续优先处理输入。

## 实现结构

`ReaderSettings` 持久化 VN 开关与参数；`SettingsPageViewModel` 负责三个模式互斥和设置投影。`NovelReaderPage` 将强类型配置注入 reader host，并通过版本化 host message 实时更新揭示速度。

`reader-visual-novel.js` 在私有 reader bridge 初始化期间接收当前章节 DOM，建立章节级原始字符与可匹配字符索引，再生成居中的屏幕内容。段落/句组超过 viewport 时继续切分，resize 后按逻辑进度重新分屏。

`reader-bridge.js` 仍是 native 导航的唯一入口。VN paginator 只返回 `scrolled` 或 `limit` 及最终逻辑进度；native 根据边界决定是否加载相邻 spine。逐字显示属于当前屏视觉状态，不产生额外 bookmark 或统计移动。

## 兼容性与安全

- 高亮和选择使用当前屏的章节级偏移基数，sidecar 中的位置不依赖临时屏号。
- Sasayaki 定位 cue 时先切换到包含目标字符的屏幕，再复用现有自动滚动/播放协调。
- 内部链接、外部导航限制、WebMessage source、render generation 和 active-content sanitizer 保持原契约。
- JavaScript 不查询词典、不访问 SQLite，也不暴露宽泛 native API。

## 验证

自动化覆盖设置默认值与归一化、CSS/资源注入、typed speed message、私有 bridge 路由，以及“揭示优先于前进”的运行时状态机。真实 WinUI/WebView2 验证还要覆盖两种分屏、窗口 reflow、章节边界、空白点击、查词、标注、Sasayaki 和书签恢复。
