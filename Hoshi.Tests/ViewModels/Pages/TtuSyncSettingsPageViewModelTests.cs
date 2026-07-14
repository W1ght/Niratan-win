using System.Linq.Expressions;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using Hoshi.Models.Sasayaki;
using Hoshi.Models.Settings;
using Hoshi.Models.Sync;
using Hoshi.Services.Settings;
using Hoshi.Services.Sync;
using Hoshi.Services.UI;
using Hoshi.ViewModels.Pages;
using Moq;

namespace Hoshi.Tests.ViewModels.Pages;

public sealed class TtuSyncSettingsPageViewModelTests
{
    [Fact]
    public void Defaults_AreAlignedWithNiratanTtuSyncSettings()
    {
        var settings = new TtuSyncSettings();

        settings.EnableSync.Should().BeFalse();
        settings.SyncMode.Should().Be(TtuSettingsSyncMode.Auto);
        settings.EnableAutoSync.Should().BeFalse();
        settings.GoogleClientId.Should().BeEmpty();
        settings.UploadBooks.Should().BeTrue();
    }

    [Fact]
    public void Deserialization_RespectsExplicitUploadBooksFalse()
    {
        var settings = JsonSerializer.Deserialize<TtuSyncSettings>(
            """{"UploadBooks":false}""");

        settings.Should().NotBeNull();
        settings!.UploadBooks.Should().BeFalse();
    }

    [Fact]
    public void UpdatingSettings_SavesTtuSyncSettings()
    {
        var appSettings = new AppSettings();
        TtuSyncSettings? saved = null;
        var settingsService = new Mock<ISettingsService>();
        settingsService.SetupGet(s => s.Current).Returns(appSettings);
        settingsService
            .Setup(s => s.Set(
                It.IsAny<Expression<Func<AppSettings, TtuSyncSettings>>>(),
                It.IsAny<TtuSyncSettings>()))
            .Callback<Expression<Func<AppSettings, TtuSyncSettings>>, TtuSyncSettings>(
                (_, value) =>
                {
                    saved = value;
                    appSettings.TtuSyncSettings = value;
                });
        settingsService.Setup(s => s.SaveAsync()).Returns(Task.CompletedTask);

        var viewModel = new TtuSyncSettingsPageViewModel(
            settingsService.Object,
            new FakeGoogleDriveAuthService(),
            new FakeCredentialStore(),
            new GoogleDriveSyncCache(),
            new RecordingCoverCache(),
            Mock.Of<IDialogService>())
        {
            EnableSync = true,
            SelectedSyncMode = TtuSettingsSyncMode.Manual,
            EnableAutoSync = true,
            GoogleClientId = "1234567890-abcdef.apps.googleusercontent.com",
            GoogleClientSecret = "must-not-enter-app-settings",
            UploadBooks = true,
        };

        saved.Should().NotBeNull();
        saved!.EnableSync.Should().BeTrue();
        saved.SyncMode.Should().Be(TtuSettingsSyncMode.Manual);
        saved.EnableAutoSync.Should().BeTrue();
        saved.GoogleClientId.Should().Be("1234567890-abcdef.apps.googleusercontent.com");
        saved.UploadBooks.Should().BeTrue();
        JsonSerializer.Serialize(saved).Should().NotContain("ClientSecret");
        JsonSerializer.Serialize(saved).Should().NotContain("must-not-enter-app-settings");
        settingsService.Verify(s => s.SaveAsync(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ConnectGoogleDriveCommand_AuthenticatesWithTrimmedClientCredentialsAndRetainsSecret()
    {
        var auth = new FakeGoogleDriveAuthService();
        var viewModel = CreateViewModel(auth);
        viewModel.GoogleClientId = "  1234567890-abcdef.apps.googleusercontent.com  ";
        viewModel.GoogleClientSecret = "  desktop-client-secret  ";

        await viewModel.ConnectGoogleDriveCommand.ExecuteAsync(null);

        auth.AuthenticatedClientId.Should().Be("1234567890-abcdef.apps.googleusercontent.com");
        auth.AuthenticatedClientSecret.Should().Be("desktop-client-secret");
        auth.HasCredentials.Should().BeTrue();
        viewModel.GoogleClientSecret.Should().Be("desktop-client-secret");
        viewModel.IsGoogleDriveConnected.Should().BeTrue();
        viewModel.GoogleDriveConnectionStatus.Should().NotBeNullOrWhiteSpace();
    }

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

        viewModel.ShowStatisticsSync.Should().BeTrue();
        viewModel.ShowSasayakiSync.Should().BeTrue();
        settings.StatisticsSettings.EnableSync.Should().BeTrue();
        settings.StatisticsSettings.DailyCharacterTarget.Should().Be(12345);
        settings.StatisticsSettings.SyncMode.Should().Be(StatisticsSyncMode.Replace);
        settings.SasayakiSettings.EnableSync.Should().BeTrue();
        settings.SasayakiSettings.SearchWindowSize.Should().Be(4321);
        settings.SasayakiSettings.PlaybackRate.Should().Be(1.5);
    }

    [Fact]
    public async Task ConnectGoogleDriveCommand_RequiresClientId()
    {
        var auth = new FakeGoogleDriveAuthService();
        var viewModel = CreateViewModel(auth);
        viewModel.GoogleClientId = " ";
        viewModel.GoogleClientSecret = "desktop-client-secret";

        await viewModel.ConnectGoogleDriveCommand.ExecuteAsync(null);

        auth.AuthenticatedClientId.Should().BeNull();
        viewModel.GoogleDriveConnectionStatus.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ConnectGoogleDriveCommand_RequiresClientSecret()
    {
        var auth = new FakeGoogleDriveAuthService();
        var viewModel = CreateViewModel(auth);
        viewModel.GoogleClientId = "1234567890-abcdef.apps.googleusercontent.com";
        viewModel.GoogleClientSecret = " ";

        await viewModel.ConnectGoogleDriveCommand.ExecuteAsync(null);

        auth.AuthenticatedClientId.Should().BeNull();
        viewModel.GoogleDriveConnectionStatus.Should().NotBeNullOrWhiteSpace();
    }

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

    [Fact]
    public async Task SignOutGoogleDriveCommand_ClearsCredentialsAndStatus()
    {
        var auth = new FakeGoogleDriveAuthService { HasCredentials = true };
        var covers = new RecordingCoverCache();
        var viewModel = CreateViewModel(auth, coverCache: covers);
        viewModel.GoogleClientSecret = "remove-me";

        await viewModel.SignOutGoogleDriveCommand.ExecuteAsync(null);

        auth.HasCredentials.Should().BeFalse();
        viewModel.GoogleClientSecret.Should().BeEmpty();
        covers.ClearCount.Should().Be(1);
        viewModel.IsGoogleDriveConnected.Should().BeFalse();
        viewModel.GoogleDriveConnectionStatus.Should().NotBeNullOrWhiteSpace();
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

        dialog.Verify(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        cache.TryGetBookFolder("星を読む", out _).Should().BeFalse();
        covers.ClearCount.Should().Be(1);
        viewModel.GoogleClientSecret.Should().Be("keep-me");
        viewModel.GoogleDriveConnectionStatus.Should().NotBeNullOrWhiteSpace();
    }

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
        settingsService.SetupGet(s => s.Current).Returns(settings);
        settingsService
            .Setup(s => s.Set(
                It.IsAny<Expression<Func<AppSettings, TtuSyncSettings>>>(),
                It.IsAny<TtuSyncSettings>()))
            .Callback<Expression<Func<AppSettings, TtuSyncSettings>>, TtuSyncSettings>(
                (_, value) => settings.TtuSyncSettings = value);
        settingsService
            .Setup(s => s.Set(
                It.IsAny<Expression<Func<AppSettings, NovelStatisticsSettings>>>(),
                It.IsAny<NovelStatisticsSettings>()))
            .Callback<Expression<Func<AppSettings, NovelStatisticsSettings>>, NovelStatisticsSettings>(
                (_, value) => settings.StatisticsSettings = value);
        settingsService
            .Setup(s => s.Set(
                It.IsAny<Expression<Func<AppSettings, SasayakiSettings>>>(),
                It.IsAny<SasayakiSettings>()))
            .Callback<Expression<Func<AppSettings, SasayakiSettings>>, SasayakiSettings>(
                (_, value) => settings.SasayakiSettings = value);
        settingsService.Setup(s => s.SaveAsync()).Returns(Task.CompletedTask);

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

        public Task SaveAsync(
            GoogleDriveCredentials credentials,
            CancellationToken ct = default)
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
            CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task ClearAsync(CancellationToken ct = default)
        {
            ClearCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGoogleDriveAuthService : IGoogleDriveAuthService
    {
        public bool HasCredentials { get; set; }
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

        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) =>
            Task.FromResult("token");

        public Task SignOutAsync(CancellationToken ct = default)
        {
            HasCredentials = false;
            return Task.CompletedTask;
        }
    }
}
