# Google Drive OAuth Client Secret 修复设计

## 背景

Niratan Windows 使用桌面 OAuth 的 loopback 回调和 PKCE。Google 已能把授权码回传到 `127.0.0.1`，但当前 token 请求只发送 `client_id`，实际客户端返回 `client_secret is missing`。同时，本地回调页在 token 交换完成前就显示 “Google Drive connected”，会把“收到授权码”误报成“连接完成”。

## 目标

- 允许用户输入 Google Cloud 桌面 OAuth 客户端密钥。
- 首次授权码交换和后续 refresh token 交换都携带客户端密钥。
- 客户端密钥不进入 `AppSettings` 明文文件，只随 Google Drive 凭据保存在 Windows Credential Manager。
- 回调页只说明已收到授权结果，不提前宣称连接成功。
- 保持现有 loopback、PKCE、Drive `drive.file` scope 和同步行为不变。

## 非目标

- 不导入或解析 Google 下载的 OAuth JSON 文件。
- 不改用 Web OAuth、UWP `ms-app://` 回调或固定端口。
- 不更改 Google Drive 同步文件格式、书架同步策略或缓存结构。

## 设计

### 设置页与 ViewModel

在客户端 ID 卡片后增加一个本地化的客户端密钥卡片，使用 WinUI `PasswordBox`，并提供稳定的 `AutomationId`。密钥只绑定到 `TtuSyncSettingsPageViewModel.GoogleClientSecret` 的内存状态，不加入 `TtuSyncSettings`，因此普通设置保存流程不会写盘。

连接命令要求 ID 和密钥均非空，分别去除首尾空白后传给授权服务。成功连接后清空 ViewModel 中的密钥；失败时保留输入，方便用户修正其他配置后重试。界面不显示、记录或回填已保存的密钥。

### OAuth 服务与 token 请求

`IGoogleDriveAuthService.AuthenticateAsync` 接受 `clientId` 和 `clientSecret`。`GoogleDriveAuthService` 在已有 PKCE 流程中把二者传给 `GoogleDriveTokenClient.ExchangeCodeAsync`。

`GoogleDriveTokenClient` 在授权码交换表单中加入 `client_secret`。成功后，`GoogleDriveCredentials` 保存该密钥；刷新表单也从凭据中加入 `client_secret`，刷新后的凭据继续保留它。

`GoogleDriveCredentials.ClientSecret` 采用向后兼容的空字符串默认值。读取旧版 Credential Manager 数据时允许缺少该字段；刷新旧凭据时仅在值非空时发送 `client_secret`。新连接始终要求并保存密钥。

### 安全边界

客户端 ID 继续保存在普通设置中，客户端密钥、access token 和 refresh token 一起由现有 `WindowsCredentialGoogleDriveCredentialStore` 写入 Windows Credential Manager。异常和状态消息不得包含密钥，测试也只使用虚构值。

### 回调页面

loopback 接收器在收到无 OAuth 错误的回调时显示中性文案，例如 “Google Drive authorization received. Return to Niratan to finish connecting.”。只有 Niratan 完成 token 交换并保存凭据后，应用设置页才显示 `Connected`。

## 错误处理

- 缺少客户端 ID：连接命令直接提示输入客户端 ID，不启动浏览器。
- 缺少客户端密钥：连接命令直接提示输入客户端密钥，不启动浏览器。
- Google 回调返回错误、state 不一致、缺少授权码或 token 请求失败：沿用现有异常路径，在设置页显示失败原因。
- 旧凭据没有密钥且 Google 拒绝刷新：保留 Google 的 token 错误，用户可退出登录后用 ID 和密钥重新连接。

## 测试与验证

- token 客户端测试证明授权码交换发送 `client_secret` 并把密钥写入凭据。
- token 客户端测试证明刷新请求发送并保留 `client_secret`，同时覆盖旧凭据空密钥兼容路径。
- ViewModel 测试证明连接命令验证、去除空白并传递 ID/密钥，成功后清空内存密钥。
- XAML/资源契约测试证明 `PasswordBox`、`AutomationId` 和中英文资源键存在。
- loopback 集成测试证明成功回调页使用“已收到授权”文案且不再声称已连接。
- 运行 x64 构建、相关测试、完整测试并启动 WinUI 应用，确认设置页可访问且应用窗口正常。
