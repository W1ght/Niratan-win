using System.Linq.Expressions;
using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using Hoshi.Models.Settings;
using Hoshi.Models.Sync;
using Hoshi.Services.Settings;
using Hoshi.Services.Sync;
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
        settings.UploadBooks.Should().BeFalse();
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
            new GoogleDriveSyncCache())
        {
            EnableSync = true,
            SelectedSyncMode = TtuSettingsSyncMode.Manual,
            EnableAutoSync = true,
            GoogleClientId = "1234567890-abcdef.apps.googleusercontent.com",
            UploadBooks = true,
        };

        saved.Should().NotBeNull();
        saved!.EnableSync.Should().BeTrue();
        saved.SyncMode.Should().Be(TtuSettingsSyncMode.Manual);
        saved.EnableAutoSync.Should().BeTrue();
        saved.GoogleClientId.Should().Be("1234567890-abcdef.apps.googleusercontent.com");
        saved.UploadBooks.Should().BeTrue();
        settingsService.Verify(s => s.SaveAsync(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ConnectGoogleDriveCommand_AuthenticatesWithTrimmedClientIdAndUpdatesStatus()
    {
        var auth = new FakeGoogleDriveAuthService();
        var viewModel = CreateViewModel(auth);
        viewModel.GoogleClientId = "  1234567890-abcdef.apps.googleusercontent.com  ";

        await viewModel.ConnectGoogleDriveCommand.ExecuteAsync(null);

        auth.AuthenticatedClientId.Should().Be("1234567890-abcdef.apps.googleusercontent.com");
        auth.HasCredentials.Should().BeTrue();
        viewModel.GoogleDriveConnectionStatus.Should().Be("Connected");
    }

    [Fact]
    public async Task ConnectGoogleDriveCommand_RequiresClientId()
    {
        var auth = new FakeGoogleDriveAuthService();
        var viewModel = CreateViewModel(auth);
        viewModel.GoogleClientId = " ";

        await viewModel.ConnectGoogleDriveCommand.ExecuteAsync(null);

        auth.AuthenticatedClientId.Should().BeNull();
        viewModel.GoogleDriveConnectionStatus.Should().Contain("client ID");
    }

    [Fact]
    public async Task SignOutGoogleDriveCommand_ClearsCredentialsAndStatus()
    {
        var auth = new FakeGoogleDriveAuthService { HasCredentials = true };
        var viewModel = CreateViewModel(auth);

        await viewModel.SignOutGoogleDriveCommand.ExecuteAsync(null);

        auth.HasCredentials.Should().BeFalse();
        viewModel.GoogleDriveConnectionStatus.Should().Be("Not connected");
    }

    [Fact]
    public void ClearGoogleDriveCacheCommand_ClearsFolderCache()
    {
        var cache = new GoogleDriveSyncCache();
        cache.SetBookFolder("星を読む", "folder-id");
        var viewModel = CreateViewModel(new FakeGoogleDriveAuthService(), cache);

        viewModel.ClearGoogleDriveCacheCommand.Execute(null);

        cache.TryGetBookFolder("星を読む", out _).Should().BeFalse();
        viewModel.GoogleDriveConnectionStatus.Should().Contain("Cache cleared");
    }

    private static TtuSyncSettingsPageViewModel CreateViewModel(
        IGoogleDriveAuthService authService,
        IGoogleDriveSyncCache? cache = null)
    {
        var appSettings = new AppSettings();
        var settingsService = new Mock<ISettingsService>();
        settingsService.SetupGet(s => s.Current).Returns(appSettings);
        settingsService
            .Setup(s => s.Set(
                It.IsAny<Expression<Func<AppSettings, TtuSyncSettings>>>(),
                It.IsAny<TtuSyncSettings>()))
            .Callback<Expression<Func<AppSettings, TtuSyncSettings>>, TtuSyncSettings>(
                (_, value) => appSettings.TtuSyncSettings = value);
        settingsService.Setup(s => s.SaveAsync()).Returns(Task.CompletedTask);

        return new TtuSyncSettingsPageViewModel(
            settingsService.Object,
            authService,
            cache ?? new GoogleDriveSyncCache());
    }

    private sealed class FakeGoogleDriveAuthService : IGoogleDriveAuthService
    {
        public bool HasCredentials { get; set; }
        public string? AuthenticatedClientId { get; private set; }

        public Task AuthenticateAsync(string clientId, CancellationToken ct = default)
        {
            AuthenticatedClientId = clientId;
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
