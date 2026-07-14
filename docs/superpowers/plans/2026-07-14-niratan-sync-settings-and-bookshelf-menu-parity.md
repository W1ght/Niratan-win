# Niratan Sync Settings and Bookshelf Menu Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Align the Windows ッツ Sync settings page and local-book context menu with Niratan, including secure local Client Secret restoration and per-book manual/automatic synchronization.

**Architecture:** Keep XAML and code-behind presentation-only, project settings and connection state through `TtuSyncSettingsPageViewModel`, and route per-book work from `NovelLibraryPageViewModel` to the existing `ITtuSyncService`. Reuse Windows Credential Manager as the only secret store, extend the existing cover-cache service for Niratan-compatible cache clearing, and preserve all unrelated settings fields when the unified sync page changes statistics or Sasayaki preferences.

**Tech Stack:** WinUI 3, Windows App SDK, CommunityToolkit.Mvvm, CommunityToolkit WinUI SettingsCard, C#/.NET, xUnit v3, FluentAssertions, Moq.

## Global Constraints

- Target Windows 10+ x64; do not add an ARM64 build step.
- Do not modify `native/hoshidicts/`.
- Keep the layering `View → ViewModel → Service`; no business logic in code-behind and no SQLite access from ViewModels.
- Keep the Client Secret out of `AppSettings`, JSON settings, logs, notifications, telemetry, and test output.
- Preserve all existing settings when a condition hides its controls.
- Use `x:Uid` or resource lookup for every new visible string in both `en-US` and `zh-CN`.
- Do not run real Google Drive import/export without explicit user authorization for a test account and test book.
- Preserve the user's unrelated dirty files; stage only files named by the active task.

---

## File Map

- `Hoshi/Models/Sync/TtuSyncModels.cs` — Niratan-aligned defaults for global sync settings.
- `Hoshi/Services/Sync/IGoogleDriveCoverCacheService.cs` — cover-cache clearing contract.
- `Hoshi/Services/Sync/GoogleDriveCoverCacheService.cs` — file-backed cover-cache clearing implementation.
- `Hoshi/ViewModels/Pages/TtuSyncSettingsPageViewModel.cs` — unified global/statistics/Sasayaki settings projection, credential restoration, confirmations, and connection actions.
- `Hoshi/Views/Pages/TtuSyncSettingsPage.xaml` — Niratan-aligned settings hierarchy and connection-state presentation.
- `Hoshi/Views/Pages/TtuSyncSettingsPage.xaml.cs` — async navigation initialization only.
- `Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs` — per-book sync commands, option snapshot, in-flight guard, and result notifications.
- `Hoshi/Views/Pages/NovelLibraryPage.xaml` — automatic Sync item and manual Import/Export submenu.
- `Hoshi/Strings/en-US/Resources.resw` and `Hoshi/Strings/zh-CN/Resources.resw` — settings, confirmation, menu, status, result, and error strings.
- `Hoshi.Tests/Services/Sync/GoogleDriveCoverCacheServiceTests.cs` — cover-cache clearing behavior.
- `Hoshi.Tests/ViewModels/Pages/TtuSyncSettingsPageViewModelTests.cs` — defaults, credential persistence, confirmation, and projected preferences.
- `Hoshi.Tests/Services/Sync/TtuSyncSettingsAssetTests.cs` — settings XAML/localization contract.
- `Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs` — direction/option/result/duplicate command behavior.
- `Hoshi.Tests/Services/Sync/NovelLibraryTtuSyncAssetTests.cs` — bookshelf context-menu contract.
- `docs/VERIFICATION.md` and `docs/CHANGELOG.md` — repeatable runtime checks and concise root-cause/fix record.

---

### Task 1: Niratan Defaults and Complete Drive Cache Clearing

**Files:**
- Modify: `Hoshi.Tests/ViewModels/Pages/TtuSyncSettingsPageViewModelTests.cs`
- Modify: `Hoshi.Tests/Services/Sync/GoogleDriveCoverCacheServiceTests.cs`
- Modify: `Hoshi/Models/Sync/TtuSyncModels.cs`
- Modify: `Hoshi/Services/Sync/IGoogleDriveCoverCacheService.cs`
- Modify: `Hoshi/Services/Sync/GoogleDriveCoverCacheService.cs`
- Modify: `Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`

**Interfaces:**
- Produces: `Task IGoogleDriveCoverCacheService.ClearAsync(CancellationToken ct = default)`.
- Produces: `TtuSyncSettings.UploadBooks == true` for a new instance.
- Preserves: explicit persisted `UploadBooks = false`.

- [ ] **Step 1: Change the default contract test and add a reflection-based cache-clear behavior test**

In `TtuSyncSettingsPageViewModelTests.Defaults_AreAlignedWithNiratanTtuSyncSettings`, change the final assertion and add explicit-false coverage:

```csharp
settings.UploadBooks.Should().BeTrue();

var explicitlyDisabled = JsonSerializer.Deserialize<TtuSyncSettings>(
    "{\"uploadBooks\":false}",
    new JsonSerializerOptions(JsonSerializerDefaults.Web));
explicitlyDisabled.Should().NotBeNull();
explicitlyDisabled!.UploadBooks.Should().BeFalse();
```

Add this test to `GoogleDriveCoverCacheServiceTests` without referencing a not-yet-existing method at compile time:

```csharp
[Fact]
public async Task ClearAsync_RemovesCachedCoversAndAllowsCacheToBeRecreated()
{
    var ct = TestContext.Current.CancellationToken;
    using var temp = new TempDirectory();
    var service = new GoogleDriveCoverCacheService(
        new HttpClient(new RecordingHandler(ImageResponse(PngBytes))),
        new FakeGoogleDriveAuthService(),
        temp.Path);
    var cover = new TtuRemoteFile(
        "cover-id",
        "cover_1_6.png",
        ThumbnailLink: "https://thumb.test/image=s220");
    (await service.GetCoverPathAsync(cover, ct)).Should().NotBeNull();

    var clearMethod = typeof(GoogleDriveCoverCacheService).GetMethod(
        "ClearAsync",
        [typeof(CancellationToken)]);
    clearMethod.Should().NotBeNull();
    await ((Task)clearMethod!.Invoke(service, [ct])!);

    Directory.Exists(temp.Path).Should().BeFalse();
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~TtuSyncSettingsPageViewModelTests.Defaults_AreAlignedWithNiratanTtuSyncSettings|FullyQualifiedName~GoogleDriveCoverCacheServiceTests.ClearAsync_RemovesCachedCoversAndAllowsCacheToBeRecreated"
```

Expected: the default test reports `Expected true, but found false`, and the cache test reports that `ClearAsync` is missing.

- [ ] **Step 3: Implement the new default and cache contract**

In `TtuSyncModels.cs`:

```csharp
public sealed class TtuSyncSettings
{
    public bool EnableSync { get; set; }
    public TtuSettingsSyncMode SyncMode { get; set; } = TtuSettingsSyncMode.Auto;
    public bool EnableAutoSync { get; set; }
    public string GoogleClientId { get; set; } = "";
    public bool UploadBooks { get; set; } = true;
}
```

In `IGoogleDriveCoverCacheService.cs`:

```csharp
public interface IGoogleDriveCoverCacheService
{
    Task<string?> GetCoverPathAsync(
        TtuRemoteFile? cover,
        CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}
```

In `GoogleDriveCoverCacheService.cs`, add:

```csharp
public Task ClearAsync(CancellationToken ct = default) =>
    Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(_cacheRoot))
            return;

        Directory.Delete(_cacheRoot, recursive: true);
    }, ct);
```

Update `FakeGoogleDriveCoverCacheService` in `NovelLibraryPageViewModelTests` so it continues to implement the interface:

```csharp
public Task ClearAsync(CancellationToken ct = default)
{
    ct.ThrowIfCancellationRequested();
    return Task.CompletedTask;
}
```

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run the Step 2 command again. Expected: both tests pass with no warnings or errors.

- [ ] **Step 5: Commit the foundation change**

```powershell
git add -- Hoshi/Models/Sync/TtuSyncModels.cs Hoshi/Services/Sync/IGoogleDriveCoverCacheService.cs Hoshi/Services/Sync/GoogleDriveCoverCacheService.cs Hoshi.Tests/Services/Sync/GoogleDriveCoverCacheServiceTests.cs Hoshi.Tests/ViewModels/Pages/TtuSyncSettingsPageViewModelTests.cs Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs
git commit -m "feat(sync): align defaults and clear Drive cover cache"
```

---

### Task 2: Secure Credential Restoration and Unified Sync Settings State

**Files:**
- Modify: `Hoshi.Tests/ViewModels/Pages/TtuSyncSettingsPageViewModelTests.cs`
- Modify: `Hoshi/ViewModels/Pages/TtuSyncSettingsPageViewModel.cs`

**Interfaces:**
- Consumes: `IGoogleDriveCredentialStore.LoadAsync` and `IGoogleDriveCoverCacheService.ClearAsync`.
- Produces: `InitializeAsync(CancellationToken)`, `IsGoogleDriveConnected`, `IsGoogleDriveDisconnected`, `CanEditGoogleDriveCredentials`, `CanRunGoogleDriveActions`, `ShowStatisticsSync`, `ShowSasayakiSync`, `EnableStatisticsSync`, `EnableSasayakiSync`, and localized `TtuSyncModeItem` values.
- Preserves: every non-sync field in `NovelStatisticsSettings` and `SasayakiSettings`.

- [ ] **Step 1: Add ViewModel tests for the approved behavior**

Replace the test helper with a version that constructs all new dependencies:

```csharp
private static TtuSyncSettingsPageViewModel CreateViewModel(
    IGoogleDriveAuthService authService,
    IGoogleDriveSyncCache? cache = null,
    AppSettings? appSettings = null,
    IGoogleDriveCredentialStore? credentialStore = null,
    IDialogService? dialogService = null,
    IGoogleDriveCoverCacheService? coverCache = null)
{
    var settings = appSettings ?? new AppSettings();
    var settingsService = new Mock<ISettingsService>();
    settingsService.SetupGet(service => service.Current).Returns(settings);
    settingsService
        .Setup(service => service.Set(
            It.IsAny<Expression<Func<AppSettings, TtuSyncSettings>>>(),
            It.IsAny<TtuSyncSettings>()))
        .Callback<Expression<Func<AppSettings, TtuSyncSettings>>, TtuSyncSettings>(
            (_, value) => settings.TtuSyncSettings = value);
    settingsService
        .Setup(service => service.Set(
            It.IsAny<Expression<Func<AppSettings, NovelStatisticsSettings>>>(),
            It.IsAny<NovelStatisticsSettings>()))
        .Callback<Expression<Func<AppSettings, NovelStatisticsSettings>>, NovelStatisticsSettings>(
            (_, value) => settings.StatisticsSettings = value);
    settingsService
        .Setup(service => service.Set(
            It.IsAny<Expression<Func<AppSettings, SasayakiSettings>>>(),
            It.IsAny<SasayakiSettings>()))
        .Callback<Expression<Func<AppSettings, SasayakiSettings>>, SasayakiSettings>(
            (_, value) => settings.SasayakiSettings = value);
    settingsService.Setup(service => service.SaveAsync()).Returns(Task.CompletedTask);

    var defaultDialog = new Mock<IDialogService>();
    defaultDialog
        .Setup(service => service.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>()))
        .ReturnsAsync(true);

    return new TtuSyncSettingsPageViewModel(
        settingsService.Object,
        authService,
        credentialStore ?? new FakeCredentialStore(),
        cache ?? new GoogleDriveSyncCache(),
        coverCache ?? new RecordingCoverCache(),
        dialogService ?? defaultDialog.Object);
}

private sealed class FakeCredentialStore : IGoogleDriveCredentialStore
{
    public GoogleDriveCredentials? Credentials { get; set; }
    public bool HasCredentials => Credentials != null;
    public Task<GoogleDriveCredentials?> LoadAsync(CancellationToken ct = default) =>
        Task.FromResult(Credentials);
    public Task SaveAsync(GoogleDriveCredentials credentials, CancellationToken ct = default)
    {
        Credentials = credentials;
        return Task.CompletedTask;
    }
    public Task DeleteAsync(CancellationToken ct = default)
    {
        Credentials = null;
        return Task.CompletedTask;
    }
}

private sealed class RecordingCoverCache : IGoogleDriveCoverCacheService
{
    public int ClearCount { get; private set; }
    public Task<string?> GetCoverPathAsync(
        TtuRemoteFile? cover,
        CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task ClearAsync(CancellationToken ct = default)
    {
        ClearCount++;
        return Task.CompletedTask;
    }
}
```

Add `using Hoshi.Models.Sasayaki;` and `using Hoshi.Services.UI;` to the test file.

Add these tests:

```csharp
[Fact]
public async Task InitializeAsync_RestoresSavedClientCredentials()
{
    var store = new FakeCredentialStore
    {
        Credentials = new GoogleDriveCredentials(
            "access",
            "refresh",
            "saved-client-id",
            DateTimeOffset.UtcNow.AddHours(1),
            GoogleDriveTokenClient.DriveFileScope,
            "saved-client-secret"),
    };
    var auth = new FakeGoogleDriveAuthService { HasCredentials = true };
    var viewModel = CreateViewModel(auth, credentialStore: store);

    await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

    viewModel.GoogleClientId.Should().Be("saved-client-id");
    viewModel.GoogleClientSecret.Should().Be("saved-client-secret");
    viewModel.IsGoogleDriveConnected.Should().BeTrue();
    viewModel.CanEditGoogleDriveCredentials.Should().BeFalse();
}

[Fact]
public async Task ConnectGoogleDriveCommand_RetainsClientSecretAfterSuccess()
{
    var auth = new FakeGoogleDriveAuthService();
    var viewModel = CreateViewModel(auth);
    viewModel.GoogleClientId = "client-id";
    viewModel.GoogleClientSecret = "desktop-secret";

    await viewModel.ConnectGoogleDriveCommand.ExecuteAsync(null);

    viewModel.GoogleClientSecret.Should().Be("desktop-secret");
    viewModel.IsGoogleDriveConnected.Should().BeTrue();
}

[Fact]
public void ProjectedSyncToggles_PreserveUnrelatedStatisticsAndSasayakiSettings()
{
    var settings = new AppSettings
    {
        StatisticsSettings = new NovelStatisticsSettings
        {
            EnableStatistics = true,
            DailyCharacterTarget = 12345,
            SyncMode = StatisticsSyncMode.Replace,
        },
        SasayakiSettings = new SasayakiSettings
        {
            EnableSasayaki = true,
            SearchWindowSize = 4321,
            PlaybackRate = 1.5,
        },
    };
    var viewModel = CreateViewModel(
        new FakeGoogleDriveAuthService(),
        appSettings: settings);

    viewModel.EnableStatisticsSync = true;
    viewModel.EnableSasayakiSync = true;

    settings.StatisticsSettings.EnableSync.Should().BeTrue();
    settings.StatisticsSettings.DailyCharacterTarget.Should().Be(12345);
    settings.StatisticsSettings.SyncMode.Should().Be(StatisticsSyncMode.Replace);
    settings.SasayakiSettings.EnableSync.Should().BeTrue();
    settings.SasayakiSettings.SearchWindowSize.Should().Be(4321);
    settings.SasayakiSettings.PlaybackRate.Should().Be(1.5);
}

[Fact]
public async Task ClearGoogleDriveCacheCommand_RequiresConfirmationAndKeepsSecret()
{
    var cache = new GoogleDriveSyncCache();
    cache.SetBookFolder("星を読む", "folder-id");
    var covers = new RecordingCoverCache();
    var dialog = new Mock<IDialogService>();
    dialog.Setup(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>()))
        .ReturnsAsync(true);
    var viewModel = CreateViewModel(
        new FakeGoogleDriveAuthService { HasCredentials = true },
        cache,
        dialogService: dialog.Object,
        coverCache: covers);
    viewModel.GoogleClientSecret = "keep-me";

    await viewModel.ClearGoogleDriveCacheCommand.ExecuteAsync(null);

    cache.TryGetBookFolder("星を読む", out _).Should().BeFalse();
    covers.ClearCount.Should().Be(1);
    viewModel.GoogleClientSecret.Should().Be("keep-me");
}
```

Update the existing successful-connect assertion from `BeEmpty()` to `Be("desktop-client-secret")`. Update the sign-out test to configure confirmation and assert the displayed secret becomes empty.

- [ ] **Step 2: Run the ViewModel tests and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~TtuSyncSettingsPageViewModelTests"
```

Expected: compilation/test failures identify the missing constructor dependencies, `InitializeAsync`, connection-state properties, projected toggles, and retained-secret behavior.

- [ ] **Step 3: Implement the ViewModel state model**

Add dependencies for `IGoogleDriveCredentialStore`, `IGoogleDriveCoverCacheService`, and `IDialogService` with this constructor shape, then retain the existing global-settings load and save calls:

```csharp
public TtuSyncSettingsPageViewModel(
    ISettingsService settingsService,
    IGoogleDriveAuthService googleDriveAuthService,
    IGoogleDriveCredentialStore credentialStore,
    IGoogleDriveSyncCache googleDriveSyncCache,
    IGoogleDriveCoverCacheService googleDriveCoverCacheService,
    IDialogService dialogService)
{
    _settingsService = settingsService;
    _googleDriveAuthService = googleDriveAuthService;
    _credentialStore = credentialStore;
    _googleDriveSyncCache = googleDriveSyncCache;
    _googleDriveCoverCacheService = googleDriveCoverCacheService;
    _dialogService = dialogService;
    LoadSettings();
    RefreshConnectionState();
    _isInitializing = false;
}
```

Add the following state and mode model:

```csharp
public IReadOnlyList<TtuSyncModeItem> AvailableSyncModes { get; } =
[
    new(TtuSettingsSyncMode.Auto,
        ResourceStringHelper.GetString("TtuSyncModeAuto", "Auto")),
    new(TtuSettingsSyncMode.Manual,
        ResourceStringHelper.GetString("TtuSyncModeManual", "Manual")),
];

[ObservableProperty]
[NotifyPropertyChangedFor(nameof(IsGoogleDriveDisconnected))]
[NotifyPropertyChangedFor(nameof(CanEditGoogleDriveCredentials))]
public partial bool IsGoogleDriveConnected { get; private set; }

[ObservableProperty]
[NotifyPropertyChangedFor(nameof(CanEditGoogleDriveCredentials))]
[NotifyPropertyChangedFor(nameof(CanRunGoogleDriveActions))]
public partial bool IsGoogleDriveBusy { get; set; }

[ObservableProperty]
public partial bool ShowStatisticsSync { get; private set; }

[ObservableProperty]
public partial bool ShowSasayakiSync { get; private set; }

[ObservableProperty]
public partial bool EnableStatisticsSync { get; set; }

[ObservableProperty]
public partial bool EnableSasayakiSync { get; set; }

[ObservableProperty]
public partial TtuSyncModeItem? SelectedSyncModeItem { get; set; }

public bool IsGoogleDriveDisconnected => !IsGoogleDriveConnected;
public bool CanEditGoogleDriveCredentials => !IsGoogleDriveConnected && !IsGoogleDriveBusy;
public bool CanRunGoogleDriveActions => !IsGoogleDriveBusy;
```

Extend `LoadSettings` and the mode hooks exactly as follows:

```csharp
private void LoadSettings()
{
    var global = _settingsService.Current.TtuSyncSettings;
    var statistics = _settingsService.Current.StatisticsSettings;
    var sasayaki = _settingsService.Current.SasayakiSettings;

    EnableSync = global.EnableSync;
    SelectedSyncMode = global.SyncMode;
    SelectedSyncModeItem = AvailableSyncModes.Single(item => item.Value == global.SyncMode);
    EnableAutoSync = global.EnableAutoSync;
    GoogleClientId = global.GoogleClientId;
    UploadBooks = global.UploadBooks;
    ShowStatisticsSync = statistics.EnableStatistics;
    EnableStatisticsSync = statistics.EnableSync;
    ShowSasayakiSync = sasayaki.EnableSasayaki;
    EnableSasayakiSync = sasayaki.EnableSync;
}

partial void OnSelectedSyncModeChanged(TtuSettingsSyncMode value)
{
    SelectedSyncModeItem = AvailableSyncModes.Single(item => item.Value == value);
    SaveSettings();
}

partial void OnSelectedSyncModeItemChanged(TtuSyncModeItem? value)
{
    if (value != null && SelectedSyncMode != value.Value)
        SelectedSyncMode = value.Value;
}
```

Load feature visibility and preferences from the existing settings objects. Implement credential restoration without saving the secret to app settings:

```csharp
public async Task InitializeAsync(CancellationToken ct = default)
{
    _isInitializing = true;
    var loadFailed = false;
    try
    {
        var credentials = await _credentialStore.LoadAsync(ct);
        if (credentials != null)
        {
            GoogleClientId = credentials.ClientId;
            GoogleClientSecret = credentials.ClientSecret;
        }
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        loadFailed = true;
        GoogleDriveConnectionStatus = ResourceStringHelper.FormatString(
            "TtuSyncCredentialLoadFailedFormat",
            "Failed to load saved credentials: {0}",
            ex.Message);
    }
    finally
    {
        _isInitializing = false;
        IsGoogleDriveConnected = _googleDriveAuthService.HasCredentials;
        if (!loadFailed)
            RefreshConnectionState();
    }
}
```

Replace connection-state refresh and connection logic with localized status handling that leaves `GoogleClientSecret` unchanged:

```csharp
private void RefreshConnectionState()
{
    IsGoogleDriveConnected = _googleDriveAuthService.HasCredentials;
    GoogleDriveConnectionStatus = IsGoogleDriveConnected
        ? ResourceStringHelper.GetString("TtuSyncStatusConnected", "Connected")
        : ResourceStringHelper.GetString("TtuSyncStatusNotConnected", "Not connected");
}

[RelayCommand]
private async Task ConnectGoogleDriveAsync()
{
    if (IsGoogleDriveBusy)
        return;

    var clientId = GoogleClientId.Trim();
    if (string.IsNullOrWhiteSpace(clientId))
    {
        GoogleDriveConnectionStatus = ResourceStringHelper.GetString(
            "TtuSyncClientIdRequiredStatus",
            "Enter a client ID first.");
        return;
    }

    var clientSecret = GoogleClientSecret.Trim();
    if (string.IsNullOrWhiteSpace(clientSecret))
    {
        GoogleDriveConnectionStatus = ResourceStringHelper.GetString(
            "TtuSyncClientSecretRequiredStatus",
            "Enter a client secret first.");
        return;
    }

    IsGoogleDriveBusy = true;
    GoogleDriveConnectionStatus = ResourceStringHelper.GetString(
        "TtuSyncStatusConnecting",
        "Connecting...");
    try
    {
        await _googleDriveAuthService.AuthenticateAsync(clientId, clientSecret);
        RefreshConnectionState();
    }
    catch (Exception ex)
    {
        GoogleDriveConnectionStatus = ResourceStringHelper.FormatString(
            "TtuSyncConnectionFailedFormat",
            "Connection failed: {0}",
            ex.Message);
    }
    finally
    {
        IsGoogleDriveBusy = false;
    }
}
```

Never interpolate secret/token values into status text.

Implement projected preference saves by copying every property from the current object and changing only `EnableSync`:

```csharp
partial void OnEnableStatisticsSyncChanged(bool value)
{
    if (_isInitializing)
        return;

    var current = _settingsService.Current.StatisticsSettings;
    _settingsService.Set(
        settings => settings.StatisticsSettings,
        new NovelStatisticsSettings
        {
            EnableStatistics = current.EnableStatistics,
            AutostartMode = current.AutostartMode,
            DailyTargetType = current.DailyTargetType,
            DailyCharacterTarget = current.DailyCharacterTarget,
            DailyDurationTargetMinutes = current.DailyDurationTargetMinutes,
            WeeklyTargetDays = current.WeeklyTargetDays,
            EnableSync = value,
            SyncMode = current.SyncMode,
        });
    _ = _settingsService.SaveAsync();
}

partial void OnEnableSasayakiSyncChanged(bool value)
{
    if (_isInitializing)
        return;

    var current = _settingsService.Current.SasayakiSettings;
    _settingsService.Set(
        settings => settings.SasayakiSettings,
        new SasayakiSettings
        {
            EnableSasayaki = current.EnableSasayaki,
            ReaderShowSasayakiToggle = current.ReaderShowSasayakiToggle,
            SearchWindowSize = current.SearchWindowSize,
            PlaybackRate = current.PlaybackRate,
            AutoScroll = current.AutoScroll,
            AutoPauseOnLookup = current.AutoPauseOnLookup,
            ShowSkipControls = current.ShowSkipControls,
            EnableSync = value,
            LightTextColor = current.LightTextColor,
            LightBackgroundColor = current.LightBackgroundColor,
            DarkTextColor = current.DarkTextColor,
            DarkBackgroundColor = current.DarkBackgroundColor,
        });
    _ = _settingsService.SaveAsync();
}
```

Implement confirmed cache clearing and sign-out:

```csharp
[RelayCommand]
private async Task ClearGoogleDriveCacheAsync()
{
    if (IsGoogleDriveBusy)
        return;

    if (!await _dialogService.ConfirmAsync(
            ResourceStringHelper.GetString("TtuSyncClearCacheTitle", "Clear Cache?"),
            ResourceStringHelper.GetString(
                "TtuSyncClearCacheMessage",
                "This will clear cached folder IDs and book covers.")))
        return;

    IsGoogleDriveBusy = true;
    try
    {
        _googleDriveSyncCache.Clear();
        await _googleDriveCoverCacheService.ClearAsync();
        GoogleDriveConnectionStatus = ResourceStringHelper.GetString(
            "TtuSyncCacheClearedStatus",
            "Cache cleared");
    }
    catch (Exception ex)
    {
        GoogleDriveConnectionStatus = ResourceStringHelper.FormatString(
            "TtuSyncClearCacheFailedFormat",
            "Clear cache failed: {0}",
            ex.Message);
    }
    finally
    {
        IsGoogleDriveBusy = false;
    }
}

[RelayCommand]
private async Task SignOutGoogleDriveAsync()
{
    if (IsGoogleDriveBusy)
        return;

    if (!await _dialogService.ConfirmAsync(
            ResourceStringHelper.GetString("TtuSyncSignOutTitle", "Sign out?"),
            ResourceStringHelper.GetString(
                "TtuSyncSignOutMessage",
                "Signing out clears authorization tokens, cached folder IDs, and book covers.")))
        return;

    IsGoogleDriveBusy = true;
    try
    {
        await _googleDriveAuthService.SignOutAsync();
        GoogleClientSecret = "";
        RefreshConnectionState();
        _googleDriveSyncCache.Clear();
        await _googleDriveCoverCacheService.ClearAsync();
    }
    catch (Exception ex)
    {
        GoogleDriveConnectionStatus = ResourceStringHelper.FormatString(
            "TtuSyncSignOutFailedFormat",
            "Sign out failed: {0}",
            ex.Message);
    }
    finally
    {
        IsGoogleDriveBusy = false;
    }
}
```

End the file with:

```csharp
public sealed record TtuSyncModeItem(
    TtuSettingsSyncMode Value,
    string DisplayName);
```

- [ ] **Step 4: Run the ViewModel tests and verify GREEN**

Run the Step 2 command. Expected: all `TtuSyncSettingsPageViewModelTests` pass.

- [ ] **Step 5: Commit the ViewModel behavior**

```powershell
git add -- Hoshi/ViewModels/Pages/TtuSyncSettingsPageViewModel.cs Hoshi.Tests/ViewModels/Pages/TtuSyncSettingsPageViewModelTests.cs
git commit -m "feat(sync): restore credentials and unify sync preferences"
```

---

### Task 3: Niratan-Aligned Settings Page and Localization

**Files:**
- Modify: `Hoshi.Tests/Services/Sync/TtuSyncSettingsAssetTests.cs`
- Modify: `Hoshi/Views/Pages/TtuSyncSettingsPage.xaml`
- Modify: `Hoshi/Views/Pages/TtuSyncSettingsPage.xaml.cs`
- Modify: `Hoshi/Strings/en-US/Resources.resw`
- Modify: `Hoshi/Strings/zh-CN/Resources.resw`

**Interfaces:**
- Consumes: Task 2 connection-state and projected-preference properties.
- Produces: stable AutomationIds for all sync settings controls and localized resources for every dynamic status/confirmation.

- [ ] **Step 1: Expand the XAML asset contract before editing XAML**

Extend `TtuSyncSettingsPage_UsesLocalizedSettingsControls` with assertions for:

```csharp
pageXaml.Should().Contain("x:Uid=\"TtuSyncExplanationText\"");
pageXaml.Should().Contain("ViewModel.CanEditGoogleDriveCredentials");
pageXaml.Should().Contain("ViewModel.IsGoogleDriveDisconnected");
pageXaml.Should().Contain("ViewModel.IsGoogleDriveConnected");
pageXaml.Should().Contain("AutomationProperties.AutomationId=\"TtuSyncStatisticsToggle\"");
pageXaml.Should().Contain("AutomationProperties.AutomationId=\"TtuSyncSasayakiToggle\"");
pageXaml.Should().Contain("ViewModel.ShowStatisticsSync");
pageXaml.Should().Contain("ViewModel.ShowSasayakiSync");
pageCode.Should().Contain("await ViewModel.InitializeAsync()");
```

Add these resource keys to the existing bilingual-key loop:

```csharp
"TtuSyncExplanationText.Text",
"TtuSyncClientCredentialsSectionHeader.Text",
"TtuSyncStatisticsToggle.Header",
"TtuSyncStatisticsToggle.Description",
"TtuSyncSasayakiToggle.Header",
"TtuSyncSasayakiToggle.Description",
"TtuSyncModeAuto",
"TtuSyncModeManual",
"TtuSyncStatusConnected",
"TtuSyncStatusNotConnected",
"TtuSyncStatusConnecting",
"TtuSyncClearCacheTitle",
"TtuSyncClearCacheMessage",
"TtuSyncSignOutTitle",
"TtuSyncSignOutMessage",
```

- [ ] **Step 2: Run the asset test and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~TtuSyncSettingsAssetTests.TtuSyncSettingsPage_UsesLocalizedSettingsControls"
```

Expected: assertions fail for the new sections, state bindings, controls, async initialization, and resources.

- [ ] **Step 3: Recompose the settings page with native controls**

Keep the existing page title/back button and replace the scroll content with this hierarchy:

```xml
<ScrollViewer Grid.Row="1">
    <StackPanel MaxWidth="1000"
                Padding="0,0,16,16"
                HorizontalAlignment="Stretch"
                Spacing="4">
        <TextBlock x:Uid="TtuSyncGeneralSectionHeader"
                   Style="{StaticResource TtuSyncSectionHeaderStyle}" />
        <toolkit:SettingsCard x:Uid="TtuSyncEnableToggle"
                              HorizontalAlignment="Stretch"
                              HeaderIcon="{ui:FontIcon Glyph=&#xE73E;}">
            <ToggleSwitch AutomationProperties.AutomationId="TtuSyncEnableToggle"
                          IsOn="{x:Bind ViewModel.EnableSync, Mode=TwoWay}" />
        </toolkit:SettingsCard>
        <TextBlock x:Uid="TtuSyncExplanationText"
                   Margin="12,8,12,0"
                   Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                   TextWrapping="Wrap" />

        <StackPanel Spacing="4"
                    Visibility="{x:Bind ViewModel.EnableSync, Mode=OneWay, Converter={StaticResource BooleanToVisibilityConverter}}">
            <TextBlock x:Uid="TtuSyncClientCredentialsSectionHeader"
                       Style="{StaticResource TtuSyncSectionHeaderStyle}" />
            <toolkit:SettingsCard x:Uid="TtuSyncGoogleClientIdTextBox">
                <TextBox MinWidth="360"
                         AutomationProperties.AutomationId="TtuSyncGoogleClientIdTextBox"
                         IsEnabled="{x:Bind ViewModel.CanEditGoogleDriveCredentials, Mode=OneWay}"
                         Text="{x:Bind ViewModel.GoogleClientId, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />
            </toolkit:SettingsCard>
            <toolkit:SettingsCard x:Uid="TtuSyncGoogleClientSecretPasswordBox">
                <PasswordBox MinWidth="360"
                             AutomationProperties.AutomationId="TtuSyncGoogleClientSecretPasswordBox"
                             IsEnabled="{x:Bind ViewModel.CanEditGoogleDriveCredentials, Mode=OneWay}"
                             Password="{x:Bind ViewModel.GoogleClientSecret, Mode=TwoWay}"
                             PasswordRevealMode="Peek" />
            </toolkit:SettingsCard>

            <TextBlock x:Uid="TtuSyncGoogleDriveSectionHeader"
                       Style="{StaticResource TtuSyncSectionHeaderStyle}" />
            <toolkit:SettingsCard x:Uid="TtuSyncConnectionStatusCard">
                <StackPanel Spacing="8">
                    <TextBlock x:Uid="TtuSyncConnectionStatusText"
                               Text="{x:Bind ViewModel.GoogleDriveConnectionStatus, Mode=OneWay}"
                               Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                    <StackPanel Orientation="Horizontal" Spacing="8">
                        <Button x:Uid="TtuSyncConnectGoogleDriveButton"
                                AutomationProperties.AutomationId="TtuSyncConnectGoogleDriveButton"
                                IsEnabled="{x:Bind ViewModel.CanRunGoogleDriveActions, Mode=OneWay}"
                                Visibility="{x:Bind ViewModel.IsGoogleDriveDisconnected, Mode=OneWay, Converter={StaticResource BooleanToVisibilityConverter}}"
                                Command="{x:Bind ViewModel.ConnectGoogleDriveCommand}" />
                        <Button x:Uid="TtuSyncClearGoogleDriveCacheButton"
                                AutomationProperties.AutomationId="TtuSyncClearGoogleDriveCacheButton"
                                IsEnabled="{x:Bind ViewModel.CanRunGoogleDriveActions, Mode=OneWay}"
                                Visibility="{x:Bind ViewModel.IsGoogleDriveConnected, Mode=OneWay, Converter={StaticResource BooleanToVisibilityConverter}}"
                                Command="{x:Bind ViewModel.ClearGoogleDriveCacheCommand}" />
                        <Button x:Uid="TtuSyncSignOutGoogleDriveButton"
                                AutomationProperties.AutomationId="TtuSyncSignOutGoogleDriveButton"
                                IsEnabled="{x:Bind ViewModel.CanRunGoogleDriveActions, Mode=OneWay}"
                                Visibility="{x:Bind ViewModel.IsGoogleDriveConnected, Mode=OneWay, Converter={StaticResource BooleanToVisibilityConverter}}"
                                Command="{x:Bind ViewModel.SignOutGoogleDriveCommand}" />
                    </StackPanel>
                </StackPanel>
            </toolkit:SettingsCard>

            <TextBlock x:Uid="TtuSyncBehaviorSectionHeader"
                       Style="{StaticResource TtuSyncSectionHeaderStyle}" />
            <toolkit:SettingsCard x:Uid="TtuSyncModeComboBox">
                <ComboBox MinWidth="140"
                          AutomationProperties.AutomationId="TtuSyncModeComboBox"
                          DisplayMemberPath="DisplayName"
                          ItemsSource="{x:Bind ViewModel.AvailableSyncModes}"
                          SelectedItem="{x:Bind ViewModel.SelectedSyncModeItem, Mode=TwoWay}" />
            </toolkit:SettingsCard>
            <toolkit:SettingsCard x:Uid="TtuSyncAutoSyncToggle">
                <ToggleSwitch AutomationProperties.AutomationId="TtuSyncAutoSyncToggle"
                              IsOn="{x:Bind ViewModel.EnableAutoSync, Mode=TwoWay}" />
            </toolkit:SettingsCard>

            <TextBlock x:Uid="TtuSyncDataSectionHeader"
                       Style="{StaticResource TtuSyncSectionHeaderStyle}" />
            <toolkit:SettingsCard x:Uid="TtuSyncUploadBooksToggle">
                <ToggleSwitch AutomationProperties.AutomationId="TtuSyncUploadBooksToggle"
                              IsOn="{x:Bind ViewModel.UploadBooks, Mode=TwoWay}" />
            </toolkit:SettingsCard>
            <toolkit:SettingsCard x:Uid="TtuSyncStatisticsToggle"
                                  Visibility="{x:Bind ViewModel.ShowStatisticsSync, Mode=OneWay, Converter={StaticResource BooleanToVisibilityConverter}}">
                <ToggleSwitch AutomationProperties.AutomationId="TtuSyncStatisticsToggle"
                              IsOn="{x:Bind ViewModel.EnableStatisticsSync, Mode=TwoWay}" />
            </toolkit:SettingsCard>
            <toolkit:SettingsCard x:Uid="TtuSyncSasayakiToggle"
                                  Visibility="{x:Bind ViewModel.ShowSasayakiSync, Mode=OneWay, Converter={StaticResource BooleanToVisibilityConverter}}">
                <ToggleSwitch AutomationProperties.AutomationId="TtuSyncSasayakiToggle"
                              IsOn="{x:Bind ViewModel.EnableSasayakiSync, Mode=TwoWay}" />
            </toolkit:SettingsCard>
        </StackPanel>
    </StackPanel>
</ScrollViewer>
```

Add these attributes to every `toolkit:SettingsCard` opening tag in the replacement:

```xml
HorizontalAlignment="Stretch"
HorizontalContentAlignment="Stretch"
```

Change navigation initialization to:

```csharp
protected override async void OnNavigatedTo(NavigationEventArgs e)
{
    base.OnNavigatedTo(e);
    TtuSyncSettingsBackButton.Visibility = e.Parameter is SettingsNavigationMode.Embedded
        ? Visibility.Collapsed
        : Visibility.Visible;
    await ViewModel.InitializeAsync();
}
```

- [ ] **Step 4: Add complete English and Chinese resources**

Add these entries to `en-US/Resources.resw`:

```xml
<data name="TtuSyncExplanationText.Text" xml:space="preserve"><value>Sync bookmarks and statistics with ッツ Reader or between Hoshi devices through Google Drive. Configure a desktop OAuth client, then right-click a book and choose Sync.</value></data>
<data name="TtuSyncClientCredentialsSectionHeader.Text" xml:space="preserve"><value>Client credentials</value></data>
<data name="TtuSyncStatisticsToggle.Header" xml:space="preserve"><value>Sync Stats</value></data>
<data name="TtuSyncStatisticsToggle.Description" xml:space="preserve"><value>Include Niratan-compatible reading statistics</value></data>
<data name="TtuSyncSasayakiToggle.Header" xml:space="preserve"><value>Sync Audiobook Progress</value></data>
<data name="TtuSyncSasayakiToggle.Description" xml:space="preserve"><value>Include Sasayaki audiobook playback position</value></data>
<data name="TtuSyncModeAuto" xml:space="preserve"><value>Auto</value></data>
<data name="TtuSyncModeManual" xml:space="preserve"><value>Manual</value></data>
<data name="TtuSyncStatusConnected" xml:space="preserve"><value>Connected</value></data>
<data name="TtuSyncStatusNotConnected" xml:space="preserve"><value>Not connected</value></data>
<data name="TtuSyncStatusConnecting" xml:space="preserve"><value>Connecting...</value></data>
<data name="TtuSyncClientIdRequiredStatus" xml:space="preserve"><value>Enter a client ID first.</value></data>
<data name="TtuSyncClientSecretRequiredStatus" xml:space="preserve"><value>Enter a client secret first.</value></data>
<data name="TtuSyncConnectionFailedFormat" xml:space="preserve"><value>Connection failed: {0}</value></data>
<data name="TtuSyncCredentialLoadFailedFormat" xml:space="preserve"><value>Failed to load saved credentials: {0}</value></data>
<data name="TtuSyncClearCacheTitle" xml:space="preserve"><value>Clear Cache?</value></data>
<data name="TtuSyncClearCacheMessage" xml:space="preserve"><value>This will clear cached folder IDs and book covers.</value></data>
<data name="TtuSyncCacheClearedStatus" xml:space="preserve"><value>Cache cleared</value></data>
<data name="TtuSyncClearCacheFailedFormat" xml:space="preserve"><value>Clear cache failed: {0}</value></data>
<data name="TtuSyncSignOutTitle" xml:space="preserve"><value>Sign out?</value></data>
<data name="TtuSyncSignOutMessage" xml:space="preserve"><value>Signing out clears authorization tokens, cached folder IDs, and book covers.</value></data>
<data name="TtuSyncSignOutFailedFormat" xml:space="preserve"><value>Sign out failed: {0}</value></data>
```

Add these entries to `zh-CN/Resources.resw`:

```xml
<data name="TtuSyncExplanationText.Text" xml:space="preserve"><value>通过 Google Drive 与 ッツ Reader 或其他 Hoshi 设备同步书签和统计。配置桌面 OAuth 客户端后，右键单本书籍并选择“同步”。</value></data>
<data name="TtuSyncClientCredentialsSectionHeader.Text" xml:space="preserve"><value>客户端凭据</value></data>
<data name="TtuSyncStatisticsToggle.Header" xml:space="preserve"><value>同步统计</value></data>
<data name="TtuSyncStatisticsToggle.Description" xml:space="preserve"><value>包含 Niratan 兼容的阅读统计</value></data>
<data name="TtuSyncSasayakiToggle.Header" xml:space="preserve"><value>同步有声书进度</value></data>
<data name="TtuSyncSasayakiToggle.Description" xml:space="preserve"><value>包含 Sasayaki 有声书播放位置</value></data>
<data name="TtuSyncModeAuto" xml:space="preserve"><value>自动</value></data>
<data name="TtuSyncModeManual" xml:space="preserve"><value>手动</value></data>
<data name="TtuSyncStatusConnected" xml:space="preserve"><value>已连接</value></data>
<data name="TtuSyncStatusNotConnected" xml:space="preserve"><value>未连接</value></data>
<data name="TtuSyncStatusConnecting" xml:space="preserve"><value>连接中...</value></data>
<data name="TtuSyncClientIdRequiredStatus" xml:space="preserve"><value>请先输入客户端 ID。</value></data>
<data name="TtuSyncClientSecretRequiredStatus" xml:space="preserve"><value>请先输入客户端密钥。</value></data>
<data name="TtuSyncConnectionFailedFormat" xml:space="preserve"><value>连接失败：{0}</value></data>
<data name="TtuSyncCredentialLoadFailedFormat" xml:space="preserve"><value>读取已保存凭据失败：{0}</value></data>
<data name="TtuSyncClearCacheTitle" xml:space="preserve"><value>清除缓存？</value></data>
<data name="TtuSyncClearCacheMessage" xml:space="preserve"><value>这将清除已缓存的文件夹 ID 和书籍封面。</value></data>
<data name="TtuSyncCacheClearedStatus" xml:space="preserve"><value>缓存已清除</value></data>
<data name="TtuSyncClearCacheFailedFormat" xml:space="preserve"><value>清除缓存失败：{0}</value></data>
<data name="TtuSyncSignOutTitle" xml:space="preserve"><value>退出登录？</value></data>
<data name="TtuSyncSignOutMessage" xml:space="preserve"><value>退出登录将清除授权令牌、已缓存的文件夹 ID 和书籍封面。</value></data>
<data name="TtuSyncSignOutFailedFormat" xml:space="preserve"><value>退出登录失败：{0}</value></data>
```

Also change these existing resource values:

| Key | English | Chinese |
| --- | --- | --- |
| `TtuSyncGeneralSectionHeader.Text` | `Syncing` | `同步` |
| `TtuSyncEnableToggle.Header` | `Enable` | `启用` |
| `TtuSyncEnableToggle.Description` | `Sync bookmarks and optional book data with ッツ-compatible Google Drive storage` | `通过兼容 ッツ 的 Google Drive 存储同步书签和可选书籍数据` |
| `TtuSyncGoogleDriveSectionHeader.Text` | `Connection` | `连接` |
| `TtuSyncConnectGoogleDriveButton.Content` | `Connect Google Drive` | `连接 Google Drive` |
| `TtuSyncBehaviorSectionHeader.Text` | `Behaviour` | `行为` |
| `TtuSyncGoogleClientSecretPasswordBox.Description` | `Stored securely in Windows Credential Manager after a successful connection` | `连接成功后安全保存在 Windows 凭据管理器中` |

- [ ] **Step 5: Run the settings tests and build**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~TtuSyncSettings"
dotnet build -p:Platform=x64
```

Expected: both commands succeed with no XAML compiler error and no new warnings.

- [ ] **Step 6: Commit the settings page**

```powershell
git add -- Hoshi/Views/Pages/TtuSyncSettingsPage.xaml Hoshi/Views/Pages/TtuSyncSettingsPage.xaml.cs Hoshi/Strings/en-US/Resources.resw Hoshi/Strings/zh-CN/Resources.resw Hoshi.Tests/Services/Sync/TtuSyncSettingsAssetTests.cs
git commit -m "feat(sync): align settings page with Niratan"
```

---

### Task 4: Per-Book Sync Command Behavior

**Files:**
- Modify: `Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`
- Modify: `Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs`

**Interfaces:**
- Consumes: `ITtuSyncService.SyncBookAsync(NovelBook, TtuSyncOptions, CancellationToken)`.
- Produces: `SyncNovelCommand`, `ImportNovelCommand`, `ExportNovelCommand`, `ShowAutomaticBookSyncAction`, and `ShowManualBookSyncAction`.
- Produces: at most one in-flight sync per book ID, with independent syncs allowed for different books.

- [ ] **Step 1: Add direction and payload mapping tests**

Add `ITtuSyncService? ttuSyncService = null` immediately before `ITtuSyncRemoteStore? syncRemoteStore = null` in the test `CreateSut` helper. Pass `ttuSyncService ?? new RecordingTtuSyncService()` immediately before the remote-store argument in `new NovelLibraryPageViewModel(...)`. Add this theory:

```csharp
[Theory]
[InlineData("SyncNovelCommand", TtuSyncDirection.Auto)]
[InlineData("ImportNovelCommand", TtuSyncDirection.ImportFromTtu)]
[InlineData("ExportNovelCommand", TtuSyncDirection.ExportToTtu)]
public async Task BookSyncCommands_MapDirectionAndPayloadPreferences(
    string commandName,
    TtuSyncDirection expectedDirection)
{
    var sync = new RecordingTtuSyncService();
    var settings = Mock.Of<ISettingsService>(service => service.Current == new AppSettings
    {
        TtuSyncSettings = new TtuSyncSettings
        {
            EnableSync = true,
            UploadBooks = true,
        },
        StatisticsSettings = new NovelStatisticsSettings
        {
            EnableSync = true,
            SyncMode = StatisticsSyncMode.Replace,
        },
        SasayakiSettings = new SasayakiSettings
        {
            EnableSasayaki = true,
            EnableSync = true,
        },
    });
    var sut = CreateSut(settingsService: settings, ttuSyncService: sync);
    var item = new NovelBookItemViewModel(new NovelBook
    {
        Id = "book-1",
        Title = "星を読む",
        ExtractedPath = "D:\\Books\\book-1",
    });

    await GetAsyncCommand(sut, commandName).ExecuteAsync(item);

    sync.Calls.Should().ContainSingle();
    var call = sync.Calls.Single();
    call.Options.Direction.Should().Be(expectedDirection);
    call.Options.SyncBookData.Should().BeTrue();
    call.Options.SyncStatistics.Should().BeTrue();
    call.Options.StatisticsSyncMode.Should().Be(StatisticsSyncMode.Replace);
    call.Options.SyncAudioBook.Should().BeTrue();
}
```

Add the recording fake and call model:

```csharp
private sealed record TtuSyncCall(
    NovelBook Book,
    TtuSyncOptions Options,
    CancellationToken CancellationToken);

private sealed class RecordingTtuSyncService : ITtuSyncService
{
    public ConcurrentQueue<TtuSyncCall> Calls { get; } = new();

    public Func<NovelBook, TtuSyncOptions, CancellationToken, Task<TtuSyncResult>>?
        Handler { get; init; }

    public Task<TtuSyncResult> SyncBookAsync(
        NovelBook book,
        TtuSyncOptions options,
        CancellationToken ct = default)
    {
        Calls.Enqueue(new TtuSyncCall(book, options, ct));
        return Handler?.Invoke(book, options, ct)
            ?? Task.FromResult(new TtuSyncResult(
                TtuSyncResultKind.Synced,
                book.Title));
    }
}
```

Add disabled/disconnected coverage:

```csharp
[Fact]
public async Task SyncNovelCommand_WhenSyncUnavailable_DoesNotCallServiceAndShowsError()
{
    var sync = new RecordingTtuSyncService();
    var notification = new Mock<INotificationService>();
    var settings = Mock.Of<ISettingsService>(service => service.Current == new AppSettings
    {
        TtuSyncSettings = new TtuSyncSettings { EnableSync = false },
    });
    var sut = CreateSut(
        settingsService: settings,
        googleDriveAuthService: new FakeGoogleDriveAuthService { HasCredentials = false },
        notificationService: notification.Object,
        ttuSyncService: sync);

    await sut.SyncNovelCommand.ExecuteAsync(new NovelBookItemViewModel(new NovelBook
    {
        Id = "book-1",
        Title = "Book One",
    }));

    sync.Calls.Should().BeEmpty();
    notification.Verify(service => service.ShowError(
        It.IsAny<string>(),
        It.IsAny<string>()), Times.Once);
}
```

Change `RecordingNovelLibraryService.GetNovelBooksAsync` from an expression body to increment `LoadCount`, then add imported/skipped behavior:

```csharp
public int LoadCount { get; private set; }

public Task<Result<NovelBookCatalogSnapshot>> GetNovelBooksAsync(
    string? queryText = null,
    CancellationToken ct = default)
{
    LoadCount++;
    return Task.FromResult(Result<NovelBookCatalogSnapshot>.Success(
        new NovelBookCatalogSnapshot(Books, [])));
}

[Fact]
public async Task SyncNovelCommand_WhenImported_ReloadsCatalogAndShowsSuccess()
{
    var library = new RecordingNovelLibraryService();
    var notification = new Mock<INotificationService>();
    var sync = new RecordingTtuSyncService
    {
        Handler = (book, _, _) => Task.FromResult(new TtuSyncResult(
            TtuSyncResultKind.Imported,
            book.Title,
            321)),
    };
    var sut = CreateSut(
        novelService: library,
        notificationService: notification.Object,
        settingsService: EnabledSyncSettings(),
        ttuSyncService: sync);

    await sut.SyncNovelCommand.ExecuteAsync(BookItem("book-1"));

    library.LoadCount.Should().Be(1);
    notification.Verify(service => service.ShowSuccess(
        It.Is<string>(message => message.Contains("321", StringComparison.Ordinal)),
        It.IsAny<string>()), Times.Once);
}

[Fact]
public async Task SyncNovelCommand_WhenSkipped_DoesNotShowSuccess()
{
    var notification = new Mock<INotificationService>();
    var sync = new RecordingTtuSyncService
    {
        Handler = (book, _, _) => Task.FromResult(new TtuSyncResult(
            TtuSyncResultKind.Skipped,
            book.Title)),
    };
    var sut = CreateSut(
        notificationService: notification.Object,
        settingsService: EnabledSyncSettings(),
        ttuSyncService: sync);

    await sut.SyncNovelCommand.ExecuteAsync(BookItem("book-1"));

    notification.Verify(service => service.ShowSuccess(
        It.IsAny<string>(),
        It.IsAny<string>()), Times.Never);
}
```

Add failure and cancellation coverage:

```csharp
[Fact]
public async Task SyncNovelCommand_WhenServiceFails_ShowsLocalizedError()
{
    var notification = new Mock<INotificationService>();
    var sync = new RecordingTtuSyncService
    {
        Handler = (_, _, _) => Task.FromException<TtuSyncResult>(
            new InvalidOperationException("network down")),
    };
    var sut = CreateSut(
        notificationService: notification.Object,
        settingsService: EnabledSyncSettings(),
        ttuSyncService: sync);

    await sut.SyncNovelCommand.ExecuteAsync(BookItem("book-1"));

    notification.Verify(service => service.ShowError(
        It.Is<string>(message => message.Contains("network down", StringComparison.Ordinal)),
        It.IsAny<string>()), Times.Once);
}

[Fact]
public async Task SyncNovelCommand_WhenCancelled_ShowsNoError()
{
    var notification = new Mock<INotificationService>();
    var sync = new RecordingTtuSyncService
    {
        Handler = (_, _, _) => Task.FromCanceled<TtuSyncResult>(
            new CancellationToken(canceled: true)),
    };
    var sut = CreateSut(
        notificationService: notification.Object,
        settingsService: EnabledSyncSettings(),
        ttuSyncService: sync);

    await sut.SyncNovelCommand.ExecuteAsync(BookItem("book-1"));

    notification.Verify(service => service.ShowError(
        It.IsAny<string>(),
        It.IsAny<string>()), Times.Never);
}
```

Add per-book single-flight coverage:

```csharp
[Fact]
public async Task SyncNovelCommand_DeduplicatesSameBookButAllowsDifferentBooks()
{
    var gates = new ConcurrentDictionary<string, TaskCompletionSource<TtuSyncResult>>();
    var started = new SemaphoreSlim(0);
    var sync = new RecordingTtuSyncService
    {
        Handler = (book, _, _) =>
        {
            var gate = gates.GetOrAdd(
                book.Id,
                _ => new TaskCompletionSource<TtuSyncResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously));
            started.Release();
            return gate.Task;
        },
    };
    var sut = CreateSut(
        settingsService: EnabledSyncSettings(),
        ttuSyncService: sync);

    var first = sut.SyncNovelCommand.ExecuteAsync(BookItem("book-1"));
    (await started.WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
    var duplicate = sut.SyncNovelCommand.ExecuteAsync(BookItem("book-1"));
    await duplicate;
    var secondBook = sut.SyncNovelCommand.ExecuteAsync(BookItem("book-2"));
    (await started.WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();

    sync.Calls.Select(call => call.Book.Id).Should().Equal("book-1", "book-2");
    gates["book-1"].SetResult(new TtuSyncResult(TtuSyncResultKind.Synced, "book-1"));
    gates["book-2"].SetResult(new TtuSyncResult(TtuSyncResultKind.Synced, "book-2"));
    await Task.WhenAll(first, secondBook);
}

private static NovelBookItemViewModel BookItem(string id) => new(new NovelBook
{
    Id = id,
    Title = id,
    ExtractedPath = $"D:\\Books\\{id}",
});

private static ISettingsService EnabledSyncSettings() =>
    Mock.Of<ISettingsService>(service => service.Current == new AppSettings
    {
        TtuSyncSettings = new TtuSyncSettings { EnableSync = true },
    });
```

- [ ] **Step 2: Run the NovelLibrary ViewModel tests and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageViewModelTests"
```

Expected: compilation/test failures identify the missing service dependency, menu-state properties, commands, option mapping, guard, and result handling.

- [ ] **Step 3: Inject the service and implement menu-state properties**

Add `using System.Collections.Concurrent;`, store `ITtuSyncService`, and add:

```csharp
private readonly ConcurrentDictionary<string, byte> _activeNovelSyncs = new();

public bool ShowAutomaticBookSyncAction =>
    _settingsService.Current.TtuSyncSettings.EnableSync
    && _settingsService.Current.TtuSyncSettings.SyncMode == TtuSettingsSyncMode.Auto;

public bool ShowManualBookSyncAction =>
    _settingsService.Current.TtuSyncSettings.EnableSync
    && _settingsService.Current.TtuSyncSettings.SyncMode == TtuSettingsSyncMode.Manual;
```

Add `ITtuSyncService ttuSyncService` to the constructor and assign it. DI already registers `ITtuSyncService` in `App.xaml.cs`.

- [ ] **Step 4: Implement three command wrappers and one guarded core**

```csharp
[RelayCommand(AllowConcurrentExecutions = true)]
private Task SyncNovelAsync(NovelBookItemViewModel item) =>
    SyncNovelCoreAsync(item, TtuSyncDirection.Auto);

[RelayCommand(AllowConcurrentExecutions = true)]
private Task ImportNovelAsync(NovelBookItemViewModel item) =>
    SyncNovelCoreAsync(item, TtuSyncDirection.ImportFromTtu);

[RelayCommand(AllowConcurrentExecutions = true)]
private Task ExportNovelAsync(NovelBookItemViewModel item) =>
    SyncNovelCoreAsync(item, TtuSyncDirection.ExportToTtu);

private async Task SyncNovelCoreAsync(
    NovelBookItemViewModel item,
    TtuSyncDirection direction)
{
    var current = _settingsService.Current;
    var global = current.TtuSyncSettings;
    if (!global.EnableSync || !_googleDriveAuthService.HasCredentials)
    {
        _notificationService.ShowError(
            ResourceStringHelper.GetString(
                "NovelBookSyncUnavailableMessage",
                "Enable ッツ Sync and connect Google Drive before syncing a book."),
            ResourceStringHelper.GetString(
                "NovelBookSyncUnavailableTitle",
                "Sync unavailable"));
        return;
    }

    if (!_activeNovelSyncs.TryAdd(item.Book.Id, 0))
        return;

    var options = new TtuSyncOptions(
        Direction: direction,
        SyncBookData: global.UploadBooks,
        SyncStatistics: current.StatisticsSettings.EnableSync,
        StatisticsSyncMode: current.StatisticsSettings.SyncMode,
        SyncAudioBook: current.SasayakiSettings.EnableSasayaki
            && current.SasayakiSettings.EnableSync);

    try
    {
        var result = await _ttuSyncService.SyncBookAsync(
            item.Book,
            options,
            _pageCts.Token);
        switch (result.Kind)
        {
            case TtuSyncResultKind.Synced:
                _notificationService.ShowSuccess(
                    ResourceStringHelper.FormatString(
                        "NovelBookAlreadySyncedFormat",
                        "{0} is already synced.",
                        result.Title));
                break;
            case TtuSyncResultKind.Imported:
                await LoadNovelsAsync();
                _notificationService.ShowSuccess(
                    ResourceStringHelper.FormatString(
                        "NovelBookSyncedFromTtuFormat",
                        "Synced {0} from ッツ ({1} characters).",
                        result.Title,
                        result.CharacterCount));
                break;
            case TtuSyncResultKind.Exported:
                _notificationService.ShowSuccess(
                    ResourceStringHelper.FormatString(
                        "NovelBookSyncedToTtuFormat",
                        "Synced {0} to ッツ ({1} characters).",
                        result.Title,
                        result.CharacterCount));
                break;
            case TtuSyncResultKind.Skipped:
                break;
        }
    }
    catch (OperationCanceledException)
    {
    }
    catch (Exception ex)
    {
        _notificationService.ShowError(
            ResourceStringHelper.FormatString(
                "NovelBookSyncFailedFormat",
                "Sync failed: {0}",
                ex.Message),
            ResourceStringHelper.GetString(
                "NovelBookSyncFailedTitle",
                "Sync failed"));
    }
    finally
    {
        _activeNovelSyncs.TryRemove(item.Book.Id, out _);
    }
}
```

- [ ] **Step 5: Run the ViewModel tests and verify GREEN**

Run the Step 2 command. Expected: all `NovelLibraryPageViewModelTests` pass, including independent-book concurrency and duplicate suppression.

- [ ] **Step 6: Commit the per-book behavior**

```powershell
git add -- Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs
git commit -m "feat(sync): add per-book bookshelf sync commands"
```

---

### Task 5: Bookshelf Context Menu, Bilingual Resources, and Documentation

**Files:**
- Modify: `Hoshi.Tests/Services/Sync/NovelLibraryTtuSyncAssetTests.cs`
- Modify: `Hoshi/Views/Pages/NovelLibraryPage.xaml`
- Modify: `Hoshi/Strings/en-US/Resources.resw`
- Modify: `Hoshi/Strings/zh-CN/Resources.resw`
- Modify: `docs/VERIFICATION.md`
- Modify: `docs/CHANGELOG.md`

**Interfaces:**
- Consumes: Task 4 commands and menu-state properties.
- Produces: keyboard-accessible automatic Sync and manual Import/Export flyout surfaces.

- [ ] **Step 1: Add failing asset/localization assertions**

Extend `NovelLibraryTtuSyncAssetTests`:

```csharp
[Fact]
public void NovelLibraryPage_ExposesNiratanPerBookSyncMenu()
{
    var xaml = File.ReadAllText(Path.Combine(
        ProjectRoot,
        "Views",
        "Pages",
        "NovelLibraryPage.xaml"));
    var en = File.ReadAllText(Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw"));
    var zh = File.ReadAllText(Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw"));

    xaml.Should().Contain("AutomationProperties.AutomationId=\"NovelBookSyncMenuItem\"");
    xaml.Should().Contain("AutomationProperties.AutomationId=\"NovelBookSyncSubmenu\"");
    xaml.Should().Contain("AutomationProperties.AutomationId=\"NovelBookSyncImportMenuItem\"");
    xaml.Should().Contain("AutomationProperties.AutomationId=\"NovelBookSyncExportMenuItem\"");
    xaml.Should().Contain("ViewModel.ShowAutomaticBookSyncAction");
    xaml.Should().Contain("ViewModel.ShowManualBookSyncAction");
    xaml.Should().Contain("ViewModel.SyncNovelCommand");
    xaml.Should().Contain("ViewModel.ImportNovelCommand");
    xaml.Should().Contain("ViewModel.ExportNovelCommand");

    foreach (var key in new[]
    {
        "NovelBookSyncMenuItem.Text",
        "NovelBookSyncSubmenu.Text",
        "NovelBookSyncImportMenuItem.Text",
        "NovelBookSyncExportMenuItem.Text",
    })
    {
        en.Should().Contain(key);
        zh.Should().Contain(key);
    }
}
```

- [ ] **Step 2: Run the asset test and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryTtuSyncAssetTests.NovelLibraryPage_ExposesNiratanPerBookSyncMenu"
```

Expected: the menu IDs, bindings, commands, and resource keys are missing.

- [ ] **Step 3: Add the native WinUI context-menu surfaces**

Inside the local `NovelBookTemplate` flyout, place these items after Move to shelf and before the destructive separator/Delete action:

```xml
<MenuFlyoutItem x:Uid="NovelBookSyncMenuItem"
                AutomationProperties.AutomationId="NovelBookSyncMenuItem"
                Visibility="{Binding ViewModel.ShowAutomaticBookSyncAction, ElementName=ThisPage, Converter={StaticResource BooleanToVisibilityConverter}}"
                Command="{Binding ViewModel.SyncNovelCommand, ElementName=ThisPage}"
                CommandParameter="{x:Bind}">
    <MenuFlyoutItem.Icon>
        <FontIcon Glyph="&#xE895;" />
    </MenuFlyoutItem.Icon>
</MenuFlyoutItem>
<MenuFlyoutSubItem x:Uid="NovelBookSyncSubmenu"
                   AutomationProperties.AutomationId="NovelBookSyncSubmenu"
                   Visibility="{Binding ViewModel.ShowManualBookSyncAction, ElementName=ThisPage, Converter={StaticResource BooleanToVisibilityConverter}}">
    <MenuFlyoutSubItem.Icon>
        <FontIcon Glyph="&#xE895;" />
    </MenuFlyoutSubItem.Icon>
    <MenuFlyoutItem x:Uid="NovelBookSyncImportMenuItem"
                    AutomationProperties.AutomationId="NovelBookSyncImportMenuItem"
                    Command="{Binding ViewModel.ImportNovelCommand, ElementName=ThisPage}"
                    CommandParameter="{x:Bind}">
        <MenuFlyoutItem.Icon>
            <FontIcon Glyph="&#xE896;" />
        </MenuFlyoutItem.Icon>
    </MenuFlyoutItem>
    <MenuFlyoutItem x:Uid="NovelBookSyncExportMenuItem"
                    AutomationProperties.AutomationId="NovelBookSyncExportMenuItem"
                    Command="{Binding ViewModel.ExportNovelCommand, ElementName=ThisPage}"
                    CommandParameter="{x:Bind}">
        <MenuFlyoutItem.Icon>
            <FontIcon Glyph="&#xE898;" />
        </MenuFlyoutItem.Icon>
    </MenuFlyoutItem>
</MenuFlyoutSubItem>
```

Use standard WinUI flyout items so Shift+F10, menu key, pointer, touch, and pen share the same commands.

- [ ] **Step 4: Add menu and result resources in both languages**

Add to `en-US/Resources.resw`:

```xml
<data name="NovelBookSyncMenuItem.Text" xml:space="preserve"><value>Sync</value></data>
<data name="NovelBookSyncSubmenu.Text" xml:space="preserve"><value>Sync</value></data>
<data name="NovelBookSyncImportMenuItem.Text" xml:space="preserve"><value>Import</value></data>
<data name="NovelBookSyncExportMenuItem.Text" xml:space="preserve"><value>Export</value></data>
<data name="NovelBookSyncUnavailableTitle" xml:space="preserve"><value>Sync unavailable</value></data>
<data name="NovelBookSyncUnavailableMessage" xml:space="preserve"><value>Enable ッツ Sync and connect Google Drive before syncing a book.</value></data>
<data name="NovelBookAlreadySyncedFormat" xml:space="preserve"><value>{0} is already synced.</value></data>
<data name="NovelBookSyncedFromTtuFormat" xml:space="preserve"><value>Synced {0} from ッツ ({1} characters).</value></data>
<data name="NovelBookSyncedToTtuFormat" xml:space="preserve"><value>Synced {0} to ッツ ({1} characters).</value></data>
<data name="NovelBookSyncFailedTitle" xml:space="preserve"><value>Sync failed</value></data>
<data name="NovelBookSyncFailedFormat" xml:space="preserve"><value>Sync failed: {0}</value></data>
```

Add to `zh-CN/Resources.resw`:

```xml
<data name="NovelBookSyncMenuItem.Text" xml:space="preserve"><value>同步</value></data>
<data name="NovelBookSyncSubmenu.Text" xml:space="preserve"><value>同步</value></data>
<data name="NovelBookSyncImportMenuItem.Text" xml:space="preserve"><value>导入</value></data>
<data name="NovelBookSyncExportMenuItem.Text" xml:space="preserve"><value>导出</value></data>
<data name="NovelBookSyncUnavailableTitle" xml:space="preserve"><value>无法同步</value></data>
<data name="NovelBookSyncUnavailableMessage" xml:space="preserve"><value>请先启用 ッツ Sync 并连接 Google Drive。</value></data>
<data name="NovelBookAlreadySyncedFormat" xml:space="preserve"><value>{0} 已是同步状态。</value></data>
<data name="NovelBookSyncedFromTtuFormat" xml:space="preserve"><value>已从 ッツ 同步 {0}（{1} 字符）。</value></data>
<data name="NovelBookSyncedToTtuFormat" xml:space="preserve"><value>已将 {0} 同步到 ッツ（{1} 字符）。</value></data>
<data name="NovelBookSyncFailedTitle" xml:space="preserve"><value>同步失败</value></data>
<data name="NovelBookSyncFailedFormat" xml:space="preserve"><value>同步失败：{0}</value></data>
```

- [ ] **Step 5: Update verification and changelog documentation**

In `docs/VERIFICATION.md` section 1.10.3, add checks for:

```markdown
7. 同步设置页：关闭全局同步时只保留 Syncing 区；重新开启后恢复 Client、Connection、Behaviour、Data 及原偏好。连接后 Client Secret 继续以 PasswordBox 掩码显示，离开/返回页面和重启应用后从 Windows Credential Manager 恢复；清缓存不清凭据，退出登录清凭据。
8. 书籍右键同步：全局同步关闭时不显示 Sync；Auto 模式显示单个 Sync；Manual 模式显示 Import/Export 子菜单。使用鼠标、Shift+F10 和菜单键逐项验证，mock 断言方向和 book/statistics/audio payload 与设置快照一致。
```

At the top of `docs/CHANGELOG.md`, add:

```markdown
## 同步设置与书架右键未对齐 Niratan

**原因**：
- ッツ Sync 设置页只暴露全局、方向和上传书籍选项；连接成功后还会主动清空 Client Secret，重新进入页面也不会从 Windows 凭据管理器回读。
- 清缓存只清除了 Drive 文件夹 ID，未清理封面文件；本地书籍右键菜单没有 Niratan 的自动 Sync 或手动 Import/Export 入口。

**解决**：
- 按 Niratan 重组 Syncing、客户端凭据、Connection、Behaviour、Data 分组，统一投影书籍、统计与 Sasayaki 同步偏好；Client Secret 仅保存在 Windows 凭据管理器，并在页面进入时安全回读。
- 清缓存同时删除文件夹 ID 与封面缓存；书架右键按 Auto/Manual 模式显示 Sync 或 Import/Export，并由 ViewModel 调用现有 `ITtuSyncService`。

---
```

- [ ] **Step 6: Run focused tests and verify GREEN**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryTtuSyncAssetTests|FullyQualifiedName~NovelLibraryPageViewModelTests|FullyQualifiedName~TtuSyncSettings|FullyQualifiedName~GoogleDriveCoverCacheServiceTests"
```

Expected: all focused tests pass with no warnings or errors.

- [ ] **Step 7: Commit the UI and documentation**

```powershell
git add -- Hoshi/Views/Pages/NovelLibraryPage.xaml Hoshi/Strings/en-US/Resources.resw Hoshi/Strings/zh-CN/Resources.resw Hoshi.Tests/Services/Sync/NovelLibraryTtuSyncAssetTests.cs docs/VERIFICATION.md docs/CHANGELOG.md
git commit -m "feat(sync): expose Niratan bookshelf sync menu"
```

---

### Task 6: Full Verification and WinUI Launch Check

**Files:**
- Verify only; change files only if a failing check identifies a defect in this feature.

**Interfaces:**
- Verifies: complete x64 build/test state and real WinUI startup.
- Excludes: real Google Drive mutations without explicit authorization.

- [ ] **Step 1: Run formatting/diff hygiene checks**

```powershell
git diff --check
git status --short
```

Expected: no whitespace errors. Confirm unrelated pre-existing dirty files remain untouched and unstaged.

- [ ] **Step 2: Run the complete x64 test suite**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
```

Expected: all tests pass; no test is skipped because of this feature.

- [ ] **Step 3: Run the complete x64 build**

```powershell
dotnet build -p:Platform=x64
```

Expected: build succeeds with zero errors and no new warnings.

- [ ] **Step 4: Launch and verify objective WinUI startup**

Run:

```powershell
.\build-and-run.ps1
```

Confirm the actual Hoshi top-level window is visible, responsive, and belongs to this workspace build. If another Hoshi instance causes single-instance redirection, stop and report that runtime UI boundaries were not verified rather than operating the wrong process.

- [ ] **Step 5: Verify the settings and menu states without remote mutation**

Use the running app to verify:

1. Global sync off/on conditional sections and retained toggles.
2. Disconnected Connect-only and connected Clear Cache/Sign out-only action sets.
3. Masked Client Secret remains after successful connection and page re-entry; restart restoration is checked only against credentials already authorized by the user.
4. Auto mode has one Sync menu item; Manual mode has Import and Export; disabled sync has none.
5. Shift+F10/menu key and pointer can reach all context-menu commands.
6. Light, dark, high contrast, narrow width, and 200% text scaling do not clip the affected controls.

Do not activate Import, Export, Clear Cache, or Sign out against the user's real account merely for smoke testing. Automated tests are the evidence for their side effects unless the user explicitly authorizes them.

- [ ] **Step 6: Leave the verified app running and report evidence**

Leave the final verified Hoshi instance running. Report focused test count/result, full test result, build result, startup evidence, which UI states were exercised, and that real Drive mutation was not performed.


