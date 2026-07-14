# 小说 WebView2 Reader Host 实现计划

> **给 agentic workers：** 执行本计划时使用 `superpowers:executing-plans`，按任务逐项推进，并保持小说模块与漫画模块隔离。

**目标：** 把 `NovelReaderPage` 从占位页面升级为 WebView2 本地 reader host，并建立 C# <-> JavaScript bridge。短期只支持 EPUB。  
**架构：** WinUI 页面承载 WebView2，本地加载 `Niratan/Web/NovelReader/reader-host.html`；C# 使用强类型工厂生成带 `version` 和 `type` 的 JSON；Web 层用 `reader-bridge.js` 接收 `loadBook`，并通过本地 vendored `foliate-js@1.0.1` 渲染 EPUB。
**技术栈：** WinUI 3、WebView2、C#/.NET 10、System.Text.Json、foliate-js、xUnit、FluentAssertions。

---

## Task 1: Bridge Message Factory

**Files:**

- `Niratan/Models/DTO/NovelReaderWebMessage.cs`
- `Niratan/Services/Novels/NovelReaderBridgeMessageFactory.cs`
- `Niratan.Tests/Services/Novels/NovelReaderBridgeMessageFactoryTests.cs`

- [x] 写失败测试：`CreateLoadBookMessage_SerializesVersionTypeAndPayload`
- [x] 实现 `NovelReaderWebMessage`
- [x] 实现 `NovelReaderBridgeMessageFactory.CreateLoadBookMessage(NovelBook book)`
- [x] 增加 EPUB payload 重载：`CreateLoadBookMessage(NovelBook book, byte[] epubBytes)`
- [x] 测试 payload 包含 `fileName`、`mediaType`、`byteLength`、`base64`
- [x] 增加 URL payload：`CreateLoadBookUrlMessage(NovelBook book, string fileUrl, long byteLength)`
- [x] URL payload 不携带 base64，避免真实 EPUB 通过巨大 JSON 消息传输
- [x] 运行测试转绿

## Task 2: Local Web Assets

**Files:**

- `Niratan/Web/NovelReader/reader-host.html`
- `Niratan/Web/NovelReader/reader-bridge.js`
- `Niratan/Web/NovelReader/reader-styles.css`
- `Niratan/Web/NovelReader/Vendor/foliate-js/**`
- `Niratan/Web/NovelReader/Vendor/construct-style-sheets-polyfill/**`
- `Niratan/Niratan.csproj`

- [x] 新增本地 HTML/CSS/JS
- [x] `.csproj` 把 `Web\**` 作为 Content 复制到输出目录
- [x] 下载并 vendored `foliate-js@1.0.1`
- [x] 下载并 vendored `construct-style-sheets-polyfill@3.1.0`
- [x] `reader-host.html` 增加 CSP
- [x] `reader-bridge.js` 启动后发送 `{ version: 1, type: "readerReady" }`
- [x] `reader-bridge.js` 监听 C# 的 `loadBook`
- [x] Web 层把 base64 还原为 `File`
- [x] Web 层调用 `foliate-view.open(file)` 打开 EPUB
- [x] Web 层优先使用 `fileUrl`，仅保留 base64 作为兼容回退
- [x] 固定版式入口改为本地 polyfill 相对路径，避免裸模块名解析失败

## Task 3: WebView2 Host Integration

**Files:**

- `Niratan/Views/Pages/NovelReaderPage.xaml`
- `Niratan/Views/Pages/NovelReaderPage.xaml.cs`

- [x] XAML 使用 WebView2 替换占位正文区域
- [x] `NovelReaderPage.xaml.cs` 在导航后初始化 WebView2
- [x] WebView2 导航到输出目录下的 `Web/NovelReader/reader-host.html`
- [x] 收到 `readerReady` 后把当前 EPUB 目录映射到 WebView2 虚拟 host
- [x] 通过 bridge factory 发送带虚拟 host URL 的 `loadBook`
- [x] 收到 `error` 后通过通知服务展示错误
- [x] 保留 back 按钮和独立 `NovelReaderPage`

## Task 4: Verification

- [x] 使用代理 restore
- [x] build
- [x] 全量 test
- [x] 启动 App，确认进程响应

## 后续计划

- [ ] 增加 reader 的上一页/下一页命令。
- [ ] 监听 foliate-js `relocate`，写入 `NovelReadingProgress`。
- [ ] 评估大 EPUB 的流式读取或 WebView2 虚拟 host 映射，替换 base64 全量传输。
- [ ] 选中文本后接入日语词典模块。
