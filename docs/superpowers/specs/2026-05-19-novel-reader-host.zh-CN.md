# 小说 WebView2 EPUB Reader Host 设计

日期：2026-05-19
状态：已实现基础 host/bridge，并进入 foliate-js EPUB 加载阶段

## 目标

在现有 `NovelReaderPage` 中接入 WebView2，本地加载 `reader-host.html`，并通过 C# bridge 把当前小说的 EPUB 内容发送给 Web 层。短期只支持 EPUB，因此 reader host 直接围绕 EPUB 文件、`application/epub+zip` 和 foliate-js 的 `foliate-view` 组件设计。

## 当前阶段范围

当前阶段做：

- `NovelReaderPage` 使用 WebView2 承载本地 reader host。
- `Niratan/Web/NovelReader/reader-host.html` 提供本地 HTML 宿主。
- `reader-host.html` 声明 Content Security Policy，限制脚本来源，降低 EPUB 脚本内容风险。
- `reader-bridge.js` 与 C# 使用 `version/type/payload` 消息协议通信。
- C# 收到 `readerReady` 后把 EPUB 所在目录映射到 WebView2 虚拟 host。
- `loadBook` payload 包含 `bookId`、`title`、`filePath`、`fileName`、`mediaType`、`byteLength`、`fileUrl`。
- Web 层把 `fileUrl` 交给本地 vendored `foliate-js@1.0.1` 的 `foliate-view` 打开。
- 固定版式 EPUB 需要的 `construct-style-sheets-polyfill@3.1.0` 也以本地 vendor 形式提供，避免 WebView2 本地宿主解析裸模块名失败。

当前阶段不做：

- 字典查询。
- Anki。
- 高亮、书签、进度保存。
- 大文件流式传输和资源虚拟化。
- 多格式支持。短期只接受 EPUB。

## 架构边界

```text
NovelReaderPage
  -> WebView2
  -> reader-host.html
  -> reader-bridge.js
  -> Vendor/foliate-js/view.js
```

C# 仍然只做 WinUI glue、文件读取和消息发送，不把 EPUB 正文解析逻辑塞进 ViewModel。Web 层负责浏览器内 EPUB 渲染，不访问 SQLite，也不承担字典/Anki 等业务逻辑。

## 消息协议

所有 bridge 消息必须带版本和类型：

```json
{
  "version": 1,
  "type": "loadBook",
  "payload": {}
}
```

Web -> C#：

- `readerReady`
- `error`

C# -> Web：

- `loadBook`

`loadBook` payload：

```json
{
  "bookId": "string",
  "title": "string",
  "filePath": "string",
  "fileName": "book.epub",
  "mediaType": "application/epub+zip",
  "byteLength": 123,
  "fileUrl": "https://niratan-novel-book.local/book.epub"
}
```

## 安全策略

foliate-js 官方 README 提醒 EPUB 可以包含脚本内容，因此 host 使用 CSP 限制脚本只能来自 `'self'`，同时允许 `blob:` 作为 EPUB 渲染 iframe 和资源载体。EPUB 文件通过 `https://niratan-novel-book.local` 虚拟 host 暴露给 WebView2，CSP 只允许这个 host 作为图片、字体和 fetch 来源。

## 测试

自动测试覆盖：

- `NovelReaderBridgeMessageFactory` 能生成 version/type/payload 正确的 JSON。
- 特殊字符标题能被正确 JSON 转义。
- EPUB payload 包含文件名、MIME、字节长度和虚拟 host URL。
- Web host 声明 CSP。
- Web bridge 接入 foliate-js，并使用 `foliate-view.open(file)` 打开 EPUB。
- foliate-js 的固定版式入口使用本地 polyfill 路径，不依赖网络或 npm 运行时。

手动/启动验证：

- restore/build/test 通过。
- App 能启动并保持响应。
- 进入 Novel reader 时 WebView2 host 不导致启动崩溃。

## 后续阶段

下一阶段建议做：

- 从 base64 全量传输改成更适合大 EPUB 的受控资源加载。
- 增加 `nextPage` / `previousPage` / `locationChanged`。
- 把阅读进度写回 `NovelReadingProgress`。
- 在 reader 选中文本后再接入日语词典查询。
