# Google Drive OAuth Client Secret Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复桌面 Google Drive OAuth 的 `client_secret is missing`，安全保存客户端密钥，并让回调页面准确描述授权状态。

**Architecture:** 设置页用 `PasswordBox` 临时接收客户端密钥，ViewModel 不把它写入普通设置；授权成功后，密钥随 access/refresh token 一起进入现有 Windows Credential Manager。OAuth 服务把密钥贯穿授权码交换和刷新请求，loopback 页面只报告“已收到授权”，最终连接状态仍由 WinUI 设置页在 token 保存后更新。

**Tech Stack:** C#/.NET、WinUI 3、CommunityToolkit.Mvvm、Windows Credential Manager、Google OAuth 2.0 + PKCE、xUnit v3、FluentAssertions、Moq

## Global Constraints

- 目标平台保持 Windows 10+ x64；不默认构建 ARM64。
- 保持 View → ViewModel → Service 分层，不在 code-behind 写业务逻辑。
- 客户端密钥不得进入 `TtuSyncSettings`、普通设置 JSON、日志或状态消息。
- 不更改 loopback 随机端口、PKCE、Google Drive `drive.file` scope 或同步文件格式。
- 使用 WinUI 原生 `PasswordBox`，保留中英文资源和稳定 `AutomationId`。
- 不修改 `native/hoshidicts/`。
- 每个生产行为先写失败测试并确认 RED，再做最小实现并确认 GREEN。

---

## File Map

- `Hoshi/Models/Sync/GoogleDriveSyncModels.cs`：在安全凭据模型中持久化客户端密钥，并兼容旧凭据。
- `Hoshi/Services/Sync/GoogleDriveTokenClient.cs`：在授权码交换和刷新表单中发送客户端密钥。
- `Hoshi/Services/Sync/GoogleDriveAuthAbstractions.cs`：把客户端密钥加入授权服务窄接口。
- `Hoshi/Services/Sync/GoogleDriveAuthService.cs`：校验、裁剪并传递客户端 ID/密钥。
- `Hoshi/ViewModels/Pages/TtuSyncSettingsPageViewModel.cs`：维护仅驻内存的密钥输入、验证连接参数并在成功后清空。
- `Hoshi/Views/Pages/TtuSyncSettingsPage.xaml`：增加密码输入卡片。
- `Hoshi/Strings/en-US/Resources.resw`、`Hoshi/Strings/zh-CN/Resources.resw`：增加客户端密钥本地化文案。
- `Hoshi/Services/Sync/GoogleOAuthLoopbackReceiver.cs`：把提前显示的 connected 文案改成 authorization received。
- `Hoshi.Tests/Services/Sync/GoogleDriveTokenClientTests.cs`：覆盖首次交换、刷新和旧凭据兼容。
- `Hoshi.Tests/Services/Sync/GoogleDriveAuthServiceTests.cs`：覆盖密钥从授权服务到 token 请求及 Credential Manager 抽象的传递。
- `Hoshi.Tests/ViewModels/Pages/TtuSyncSettingsPageViewModelTests.cs`：覆盖输入验证、裁剪、成功清空和失败保留。
- `Hoshi.Tests/Services/Sync/TtuSyncSettingsAssetTests.cs`：覆盖 PasswordBox、AutomationId 和资源键。
- `Hoshi.Tests/Services/Sync/GoogleOAuthLoopbackReceiverTests.cs`：通过真实 loopback HTTP 回调验证浏览器文案。
- `Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`、`Hoshi.Tests/Services/Sync/GoogleDriveTtuSyncRemoteStoreTests.cs`：同步更新授权服务测试替身签名。
- `docs/CHANGELOG.md`：记录根因和解决方案。

---

### Task 1: Token 请求和安全凭据模型

**Files:**
- Modify: `Hoshi.Tests/Services/Sync/GoogleDriveTokenClientTests.cs`
- Modify: `Hoshi/Models/Sync/GoogleDriveSyncModels.cs`
- Modify: `Hoshi/Services/Sync/GoogleDriveTokenClient.cs`

**Interfaces:**
- Produces: `GoogleDriveCredentials(..., string Scope, string ClientSecret = "")`
- Produces: `Task<GoogleDriveCredentials> ExchangeCodeAsync(string clientId, string clientSecret, string code, string redirectUri, string codeVerifier, CancellationToken ct = default)`
- Preserves: `Task<GoogleDriveCredentials> RefreshAsync(GoogleDriveCredentials credentials, CancellationToken ct = default)`

- [ ] **Step 1: 修改授权码交换测试，使它要求发送并保存客户端密钥**

将 `ExchangeCodeAsync_PostsAuthorizationCodeWithPkceVerifier` 的调用和断言改为：

```csharp
var credentials = await client.ExchangeCodeAsync(
    "1234567890-abcdef.apps.googleusercontent.com",
    "desktop-client-secret",
    "authorization-code",
    "http://127.0.0.1:49152/",
    "code-verifier",
    ct);

handler.LastBody.Should().Contain("client_secret=desktop-client-secret");
credentials.Should().BeEquivalentTo(new GoogleDriveCredentials(
    AccessToken: "access-1",
    RefreshToken: "refresh-1",
    ClientId: "1234567890-abcdef.apps.googleusercontent.com",
    ExpiresAtUtc: credentials.ExpiresAtUtc,
    Scope: "https://www.googleapis.com/auth/drive.file",
    ClientSecret: "desktop-client-secret"));
```

- [ ] **Step 2: 修改刷新测试并新增旧凭据兼容测试**

把现有刷新测试的凭据改为包含 `ClientSecret: "desktop-client-secret"`，并加入：

```csharp
handler.LastBody.Should().Contain("client_secret=desktop-client-secret");
refreshed.ClientSecret.Should().Be("desktop-client-secret");
```

新增测试：

```csharp
[Fact]
public async Task RefreshAsync_OmitsClientSecretForLegacyCredentialsWithoutOne()
{
    var ct = TestContext.Current.CancellationToken;
    var existing = new GoogleDriveCredentials(
        AccessToken: "old-access",
        RefreshToken: "refresh-legacy",
        ClientId: "1234567890-abcdef.apps.googleusercontent.com",
        ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5),
        Scope: GoogleDriveTokenClient.DriveFileScope);
    var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = JsonContent("""
            {
              "access_token": "access-legacy",
              "expires_in": 1800,
              "scope": "https://www.googleapis.com/auth/drive.file"
            }
            """),
    });
    var client = new GoogleDriveTokenClient(new HttpClient(handler));

    var refreshed = await client.RefreshAsync(existing, ct);

    handler.LastBody.Should().NotContain("client_secret=");
    refreshed.ClientSecret.Should().BeEmpty();
}
```

- [ ] **Step 3: 运行 token 客户端测试并确认 RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~GoogleDriveTokenClientTests"
```

Expected: FAIL to compile because `ExchangeCodeAsync` has no client-secret parameter and `GoogleDriveCredentials` has no `ClientSecret` member.

- [ ] **Step 4: 扩展凭据模型**

把记录定义改为：

```csharp
public sealed record GoogleDriveCredentials(
    string AccessToken,
    string RefreshToken,
    string ClientId,
    DateTimeOffset ExpiresAtUtc,
    string Scope,
    string ClientSecret = "")
{
    public bool ShouldRefresh(DateTimeOffset now) =>
        ExpiresAtUtc <= now.AddMinutes(2);
}
```

- [ ] **Step 5: 在 token 客户端中发送并保留密钥**

将授权码交换签名和校验改为：

```csharp
public async Task<GoogleDriveCredentials> ExchangeCodeAsync(
    string clientId,
    string clientSecret,
    string code,
    string redirectUri,
    string codeVerifier,
    CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
    ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
    ArgumentException.ThrowIfNullOrWhiteSpace(code);
    ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
    ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
```

授权码表单加入：

```csharp
["client_id"] = clientId,
["client_secret"] = clientSecret,
["code"] = code,
["code_verifier"] = codeVerifier,
["grant_type"] = "authorization_code",
["redirect_uri"] = redirectUri,
```

刷新路径按旧凭据兼容规则构造表单：

```csharp
var clientSecret = credentials.ClientSecret ?? "";
var form = new Dictionary<string, string>
{
    ["client_id"] = credentials.ClientId,
    ["grant_type"] = "refresh_token",
    ["refresh_token"] = credentials.RefreshToken,
};
if (!string.IsNullOrWhiteSpace(clientSecret))
    form["client_secret"] = clientSecret;

var response = await PostTokenAsync(form, ct);
return ToCredentials(
    response,
    credentials.ClientId,
    clientSecret,
    response.RefreshToken ?? credentials.RefreshToken);
```

把 `ToCredentials` 改为接收密钥并写入记录：

```csharp
private static GoogleDriveCredentials ToCredentials(
    TokenResponse response,
    string clientId,
    string clientSecret,
    string refreshToken)
{
    var expiresIn = response.ExpiresIn > 0 ? response.ExpiresIn : 3600;
    return new GoogleDriveCredentials(
        response.AccessToken!,
        refreshToken,
        clientId,
        DateTimeOffset.UtcNow.AddSeconds(expiresIn),
        string.IsNullOrWhiteSpace(response.Scope) ? DriveFileScope : response.Scope,
        clientSecret);
}
```

授权码交换的返回调用使用：

```csharp
return ToCredentials(response, clientId, clientSecret, response.RefreshToken);
```

- [ ] **Step 6: 运行 token 客户端测试并确认 GREEN**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~GoogleDriveTokenClientTests"
```

Expected: all `GoogleDriveTokenClientTests` pass.

- [ ] **Step 7: 提交 Task 1**

```powershell
git add -- Hoshi/Models/Sync/GoogleDriveSyncModels.cs Hoshi/Services/Sync/GoogleDriveTokenClient.cs Hoshi.Tests/Services/Sync/GoogleDriveTokenClientTests.cs
git commit -m "fix(sync): send google oauth client secret"
```

---

### Task 2: 授权服务和 ViewModel 数据流

**Files:**
- Create: `Hoshi.Tests/Services/Sync/GoogleDriveAuthServiceTests.cs`
- Modify: `Hoshi/Services/Sync/GoogleDriveAuthAbstractions.cs`
- Modify: `Hoshi/Services/Sync/GoogleDriveAuthService.cs`
- Modify: `Hoshi/ViewModels/Pages/TtuSyncSettingsPageViewModel.cs`
- Modify: `Hoshi.Tests/ViewModels/Pages/TtuSyncSettingsPageViewModelTests.cs`
- Modify: `Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`
- Modify: `Hoshi.Tests/Services/Sync/GoogleDriveTtuSyncRemoteStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 `ExchangeCodeAsync(clientId, clientSecret, code, redirectUri, codeVerifier, ct)`
- Produces: `Task IGoogleDriveAuthService.AuthenticateAsync(string clientId, string clientSecret, CancellationToken ct = default)`
- Produces: `TtuSyncSettingsPageViewModel.GoogleClientSecret`

- [ ] **Step 1: 新增授权服务失败测试，证明 ID/密钥会裁剪并贯穿 token 请求**

创建 `GoogleDriveAuthServiceTests.cs`，测试主体为：

```csharp
using System.Net;
using FluentAssertions;
using Hoshi.Models.Sync;
using Hoshi.Services.Sync;

namespace Hoshi.Tests.Services.Sync;

public sealed class GoogleDriveAuthServiceTests
{
    [Fact]
    public async Task AuthenticateAsync_UsesTrimmedClientCredentialsAndStoresSecret()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "access_token": "access-1",
                  "refresh_token": "refresh-1",
                  "expires_in": 3600,
                  "scope": "https://www.googleapis.com/auth/drive.file"
                }
                """, System.Text.Encoding.UTF8, "application/json"),
        });
        var store = new RecordingCredentialStore();
        var service = new GoogleDriveAuthService(
            store,
            new GoogleDriveTokenClient(new HttpClient(handler)),
            new SuccessfulLoopbackReceiver(),
            new RecordingBrowserLauncher());

        await service.AuthenticateAsync(
            " 1234567890-abcdef.apps.googleusercontent.com ",
            " desktop-client-secret ",
            ct);

        handler.LastBody.Should().Contain("client_id=1234567890-abcdef.apps.googleusercontent.com");
        handler.LastBody.Should().Contain("client_secret=desktop-client-secret");
        store.Saved.Should().NotBeNull();
        store.Saved!.ClientId.Should().Be("1234567890-abcdef.apps.googleusercontent.com");
        store.Saved.ClientSecret.Should().Be("desktop-client-secret");
    }
```

同文件加入完整测试替身：

```csharp
    private sealed class SuccessfulLoopbackReceiver : IGoogleOAuthLoopbackReceiver
    {
        public Task<GoogleOAuthLoopbackSession> StartAsync(
            string state,
            CancellationToken ct = default)
        {
            var callback = Task.FromResult(new GoogleOAuthCallback(
                "authorization-code",
                state,
                null));
            return Task.FromResult(new GoogleOAuthLoopbackSession(
                new Uri("http://127.0.0.1:49152/"),
                callback,
                () => ValueTask.CompletedTask));
        }
    }

    private sealed class RecordingBrowserLauncher : IBrowserLauncher
    {
        public Uri? LaunchedUri { get; private set; }

        public Task LaunchAsync(Uri uri, CancellationToken ct = default)
        {
            LaunchedUri = uri;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCredentialStore : IGoogleDriveCredentialStore
    {
        public bool HasCredentials => Saved != null;
        public GoogleDriveCredentials? Saved { get; private set; }

        public Task<GoogleDriveCredentials?> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(Saved);

        public Task SaveAsync(GoogleDriveCredentials credentials, CancellationToken ct = default)
        {
            Saved = credentials;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken ct = default)
        {
            Saved = null;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) =>
            _responseFactory = responseFactory;

        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastBody = request.Content == null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responseFactory(request);
        }
    }
}
```

- [ ] **Step 2: 修改 ViewModel 测试以定义密钥输入行为**

把成功测试改名为 `ConnectGoogleDriveCommand_AuthenticatesWithTrimmedClientCredentialsAndClearsSecret`，设置并断言：

```csharp
viewModel.GoogleClientId = "  1234567890-abcdef.apps.googleusercontent.com  ";
viewModel.GoogleClientSecret = "  desktop-client-secret  ";

await viewModel.ConnectGoogleDriveCommand.ExecuteAsync(null);

auth.AuthenticatedClientId.Should().Be("1234567890-abcdef.apps.googleusercontent.com");
auth.AuthenticatedClientSecret.Should().Be("desktop-client-secret");
viewModel.GoogleClientSecret.Should().BeEmpty();
```

在缺少 ID 测试中提供有效密钥，另新增：

```csharp
[Fact]
public async Task ConnectGoogleDriveCommand_RequiresClientSecret()
{
    var auth = new FakeGoogleDriveAuthService();
    var viewModel = CreateViewModel(auth);
    viewModel.GoogleClientId = "1234567890-abcdef.apps.googleusercontent.com";
    viewModel.GoogleClientSecret = " ";

    await viewModel.ConnectGoogleDriveCommand.ExecuteAsync(null);

    auth.AuthenticatedClientId.Should().BeNull();
    viewModel.GoogleDriveConnectionStatus.Should().Contain("client secret");
}
```

新增失败保留测试：

```csharp
[Fact]
public async Task ConnectGoogleDriveCommand_PreservesClientSecretWhenAuthenticationFails()
{
    var auth = new FakeGoogleDriveAuthService
    {
        AuthenticationException = new InvalidOperationException("token failed"),
    };
    var viewModel = CreateViewModel(auth);
    viewModel.GoogleClientId = "1234567890-abcdef.apps.googleusercontent.com";
    viewModel.GoogleClientSecret = "desktop-client-secret";

    await viewModel.ConnectGoogleDriveCommand.ExecuteAsync(null);

    viewModel.GoogleClientSecret.Should().Be("desktop-client-secret");
    viewModel.GoogleDriveConnectionStatus.Should().Contain("token failed");
}
```

把测试替身改为：

```csharp
public string? AuthenticatedClientId { get; private set; }
public string? AuthenticatedClientSecret { get; private set; }
public Exception? AuthenticationException { get; init; }

public Task AuthenticateAsync(
    string clientId,
    string clientSecret,
    CancellationToken ct = default)
{
    AuthenticatedClientId = clientId;
    AuthenticatedClientSecret = clientSecret;
    if (AuthenticationException != null)
        return Task.FromException(AuthenticationException);

    HasCredentials = true;
    return Task.CompletedTask;
}
```

在 `UpdatingSettings_SavesTtuSyncSettings` 的 ViewModel 初始化器中加入：

```csharp
GoogleClientSecret = "must-not-enter-app-settings",
```

并在现有保存断言后加入（文件顶部增加 `using System.Text.Json;`）：

```csharp
JsonSerializer.Serialize(saved).Should().NotContain("ClientSecret");
JsonSerializer.Serialize(saved).Should().NotContain("must-not-enter-app-settings");
```

- [ ] **Step 3: 运行授权服务和 ViewModel 测试并确认 RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~GoogleDriveAuthServiceTests|FullyQualifiedName~TtuSyncSettingsPageViewModelTests"
```

Expected: FAIL to compile because the auth interface/service and ViewModel do not accept or expose a client secret.

- [ ] **Step 4: 更新授权服务接口和实现**

接口改为：

```csharp
Task AuthenticateAsync(
    string clientId,
    string clientSecret,
    CancellationToken ct = default);
```

实现签名开头改为：

```csharp
public async Task AuthenticateAsync(
    string clientId,
    string clientSecret,
    CancellationToken ct = default)
{
    clientId = clientId.Trim();
    clientSecret = clientSecret.Trim();
    ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
    ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
```

token 交换调用改为：

```csharp
var credentials = await _tokenClient.ExchangeCodeAsync(
    clientId,
    clientSecret,
    callback.Code,
    session.RedirectUri.ToString(),
    codeVerifier,
    ct);
```

- [ ] **Step 5: 更新 ViewModel 的仅内存密钥状态和连接命令**

在 `GoogleClientId` 后加入：

```csharp
[ObservableProperty]
public partial string GoogleClientSecret { get; set; } = "";
```

不要在 `LoadSettings`、`SaveSettings` 或 `TtuSyncSettings` 中读写该属性。连接命令在 ID 校验后加入：

```csharp
var clientSecret = GoogleClientSecret.Trim();
if (string.IsNullOrWhiteSpace(clientSecret))
{
    GoogleDriveConnectionStatus = "Enter a client secret first.";
    return;
}
```

授权调用和成功清空改为：

```csharp
await _googleDriveAuthService.AuthenticateAsync(clientId, clientSecret);
GoogleClientSecret = "";
UpdateConnectionStatus();
```

- [ ] **Step 6: 更新其余测试替身签名**

在以下文件的 `IGoogleDriveAuthService` 测试替身中使用相同的无操作签名：

```csharp
public Task AuthenticateAsync(
    string clientId,
    string clientSecret,
    CancellationToken ct = default) =>
    Task.CompletedTask;
```

Files:

```text
Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs
Hoshi.Tests/Services/Sync/GoogleDriveTtuSyncRemoteStoreTests.cs
```

- [ ] **Step 7: 运行授权相关测试并确认 GREEN**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~GoogleDriveAuthServiceTests|FullyQualifiedName~TtuSyncSettingsPageViewModelTests|FullyQualifiedName~NovelLibraryPageViewModelTests|FullyQualifiedName~GoogleDriveTtuSyncRemoteStoreTests"
```

Expected: all selected tests pass.

- [ ] **Step 8: 提交 Task 2**

```powershell
git add -- Hoshi/Services/Sync/GoogleDriveAuthAbstractions.cs Hoshi/Services/Sync/GoogleDriveAuthService.cs Hoshi/ViewModels/Pages/TtuSyncSettingsPageViewModel.cs Hoshi.Tests/Services/Sync/GoogleDriveAuthServiceTests.cs Hoshi.Tests/ViewModels/Pages/TtuSyncSettingsPageViewModelTests.cs Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs Hoshi.Tests/Services/Sync/GoogleDriveTtuSyncRemoteStoreTests.cs
git commit -m "fix(sync): carry oauth secret through authentication"
```

---

### Task 3: WinUI 密钥输入和本地化契约

**Files:**
- Modify: `Hoshi.Tests/Services/Sync/TtuSyncSettingsAssetTests.cs`
- Modify: `Hoshi/Views/Pages/TtuSyncSettingsPage.xaml`
- Modify: `Hoshi/Strings/en-US/Resources.resw`
- Modify: `Hoshi/Strings/zh-CN/Resources.resw`

**Interfaces:**
- Consumes: Task 2 `TtuSyncSettingsPageViewModel.GoogleClientSecret`
- Produces: `PasswordBox` with `AutomationId="TtuSyncGoogleClientSecretPasswordBox"`

- [ ] **Step 1: 扩展 XAML/资源契约测试**

在 `TtuSyncSettingsPage_UsesLocalizedSettingsControls` 中加入：

```csharp
pageXaml.Should().Contain("x:Uid=\"TtuSyncGoogleClientSecretPasswordBox\"");
pageXaml.Should().Contain("AutomationProperties.AutomationId=\"TtuSyncGoogleClientSecretPasswordBox\"");
pageXaml.Should().Contain("Password=\"{x:Bind ViewModel.GoogleClientSecret, Mode=TwoWay}\"");
```

资源键数组加入：

```csharp
"TtuSyncGoogleClientSecretPasswordBox.Header",
"TtuSyncGoogleClientSecretPasswordBox.Description",
```

- [ ] **Step 2: 运行资源契约测试并确认 RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~TtuSyncSettingsAssetTests"
```

Expected: FAIL because the PasswordBox and localization keys do not exist.

- [ ] **Step 3: 在客户端 ID 卡片后增加 PasswordBox 卡片**

插入：

```xml
<toolkit:SettingsCard x:Uid="TtuSyncGoogleClientSecretPasswordBox"
                      HorizontalAlignment="Stretch"
                      HorizontalContentAlignment="Stretch"
                      HeaderIcon="{ui:FontIcon Glyph=&#xE72E;}">
    <PasswordBox MinWidth="360"
                 AutomationProperties.AutomationId="TtuSyncGoogleClientSecretPasswordBox"
                 Password="{x:Bind ViewModel.GoogleClientSecret, Mode=TwoWay}"
                 PasswordRevealMode="Peek" />
</toolkit:SettingsCard>
```

- [ ] **Step 4: 增加中英文资源**

`en-US/Resources.resw`：

```xml
<data name="TtuSyncGoogleClientSecretPasswordBox.Header" xml:space="preserve"><value>Client secret</value></data>
<data name="TtuSyncGoogleClientSecretPasswordBox.Description" xml:space="preserve"><value>Client secret from the same Google Cloud desktop OAuth client</value></data>
```

`zh-CN/Resources.resw`：

```xml
<data name="TtuSyncGoogleClientSecretPasswordBox.Header" xml:space="preserve"><value>客户端密钥</value></data>
<data name="TtuSyncGoogleClientSecretPasswordBox.Description" xml:space="preserve"><value>同一个 Google Cloud 桌面 OAuth 客户端的客户端密钥</value></data>
```

- [ ] **Step 5: 运行资源契约测试和 x64 构建并确认 GREEN**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~TtuSyncSettingsAssetTests"
dotnet build -p:Platform=x64
```

Expected: asset tests pass and build succeeds with no XAML compiler errors.

- [ ] **Step 6: 提交 Task 3**

```powershell
git add -- Hoshi/Views/Pages/TtuSyncSettingsPage.xaml Hoshi/Strings/en-US/Resources.resw Hoshi/Strings/zh-CN/Resources.resw Hoshi.Tests/Services/Sync/TtuSyncSettingsAssetTests.cs
git commit -m "feat(sync): add oauth client secret input"
```

---

### Task 4: 准确的 loopback 回调文案

**Files:**
- Create: `Hoshi.Tests/Services/Sync/GoogleOAuthLoopbackReceiverTests.cs`
- Modify: `Hoshi/Services/Sync/GoogleOAuthLoopbackReceiver.cs`
- Modify: `docs/CHANGELOG.md`

**Interfaces:**
- Preserves: `IGoogleOAuthLoopbackReceiver.StartAsync(string state, CancellationToken ct = default)`
- Changes only: successful callback HTML copy; callback parsing and state/code results remain unchanged.

- [ ] **Step 1: 新增真实 loopback 回调测试**

创建：

```csharp
using FluentAssertions;
using Hoshi.Services.Sync;

namespace Hoshi.Tests.Services.Sync;

public sealed class GoogleOAuthLoopbackReceiverTests
{
    [Fact]
    public async Task SuccessfulCallback_ReportsAuthorizationReceivedWithoutClaimingConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var receiver = new GoogleOAuthLoopbackReceiver();
        await using var session = await receiver.StartAsync("expected-state", ct);
        using var httpClient = new HttpClient();
        var callbackUri = new Uri(
            session.RedirectUri,
            "?code=authorization-code&state=expected-state");

        var responseTask = httpClient.GetStringAsync(callbackUri, ct);
        var callback = await session.CallbackTask;
        var html = await responseTask;

        callback.Should().Be(new GoogleOAuthCallback(
            "authorization-code",
            "expected-state",
            null));
        html.Should().Contain("Google Drive authorization received");
        html.Should().Contain("Return to Hoshi to finish connecting");
        html.Should().NotContain("Google Drive connected");
    }
}
```

- [ ] **Step 2: 运行 loopback 测试并确认 RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~GoogleOAuthLoopbackReceiverTests"
```

Expected: FAIL because the current HTML contains `Google Drive connected`.

- [ ] **Step 3: 把成功回调改成中性授权文案**

在 `WriteResponseAsync` 中使用独立 title/body：

```csharp
var succeeded = string.IsNullOrWhiteSpace(callback.Error);
var title = succeeded
    ? "Google Drive authorization received"
    : "Google Drive authorization failed";
var message = succeeded
    ? "Google Drive authorization received. Return to Hoshi to finish connecting."
    : "Google Drive authorization failed. Return to Hoshi for details.";
var body = $"""
    <!doctype html>
    <html lang="en">
    <head><meta charset="utf-8"><title>{WebUtility.HtmlEncode(title)}</title></head>
    <body>{WebUtility.HtmlEncode(message)}</body>
    </html>
    """;
```

- [ ] **Step 4: 运行 loopback 与全部 Google OAuth 测试并确认 GREEN**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~GoogleOAuth"
```

Expected: all selected tests pass.

- [ ] **Step 5: 在 Changelog 顶部记录根因和解决方案**

在 `# Changelog` 后加入：

```markdown
## Google Drive OAuth 回调显示成功但连接失败

**原因**：
- loopback 已收到授权码后就向浏览器显示 `Google Drive connected`，但此时 token 交换尚未完成。
- 桌面 OAuth 客户端要求 token 请求携带 `client_secret`；Windows 实现只接收和发送 `client_id`，首次交换返回 `client_secret is missing`，刷新路径也缺少同一参数。

**解决**：
- 设置页使用 `PasswordBox` 接收客户端密钥，成功授权后将其与 token 一起存入 Windows Credential Manager，不写入普通设置。
- 授权码交换和 refresh token 请求都发送客户端密钥，并兼容读取不含密钥的旧凭据。
- loopback 页面只提示已收到授权，最终连接成功状态由 token 交换和凭据保存完成后的 WinUI 页面显示。

---
```

- [ ] **Step 6: 提交 Task 4**

```powershell
git add -- Hoshi/Services/Sync/GoogleOAuthLoopbackReceiver.cs Hoshi.Tests/Services/Sync/GoogleOAuthLoopbackReceiverTests.cs docs/CHANGELOG.md
git commit -m "fix(sync): report oauth callback state accurately"
```

---

### Task 5: 全量验证和 WinUI 运行检查

**Files:**
- Verify only; no planned source edits.

**Interfaces:**
- Verifies all outputs from Tasks 1–4.

- [ ] **Step 1: 检查改动范围和空白错误**

Run:

```powershell
git status --short
git diff --check HEAD~4..HEAD
git diff --stat HEAD~4..HEAD
```

Expected: only the files listed in this plan are committed; unrelated `.codex/` and existing popup plan remain untouched; `git diff --check` prints nothing.

- [ ] **Step 2: 运行 Google Drive/ッツ Sync 相关测试**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~GoogleDrive|FullyQualifiedName~GoogleOAuth|FullyQualifiedName~TtuSyncSettings"
```

Expected: all selected tests pass with zero failures.

- [ ] **Step 3: 运行完整 x64 测试套件**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
```

Expected: all tests pass with zero failures.

- [ ] **Step 4: 运行完整 x64 构建**

Run:

```powershell
dotnet build -p:Platform=x64
```

Expected: build succeeds with zero errors.

- [ ] **Step 5: 构建并启动 WinUI 应用**

Run:

```powershell
.\build-and-run.ps1
```

Expected: Hoshi opens a responsive top-level window and does not exit immediately.

- [ ] **Step 6: 客观确认窗口已启动并保留运行实例**

Run in a second PowerShell session:

```powershell
Get-Process Hoshi | Where-Object { $_.MainWindowHandle -ne 0 } |
    Select-Object Id, MainWindowTitle, MainWindowHandle, Responding
```

Expected: one Hoshi process has non-zero `MainWindowHandle`, expected title, and `Responding=True`. Leave that verified instance running.

- [ ] **Step 7: 手动检查设置页安全行为**

Navigate to Settings → ッツ Sync and verify:

```text
1. Client secret is a masked PasswordBox with a reveal affordance.
2. The field is empty on entry and is not populated from saved settings.
3. Clicking Connect with an empty secret does not launch the browser and reports “Enter a client secret first.”
4. No status text exposes the entered secret.
```

Expected: all four checks pass. End-to-end Google consent is left for the user’s own client secret; do not request or record that value during verification.
