using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Niratan.Helpers;
using Niratan.Messages;
using Niratan.Models;
using Niratan.Models.Settings;
using Niratan.Services.Settings;
using Niratan.Services.Updates;
using Serilog;

namespace Niratan.ViewModels.Pages;

public partial class ShellPageViewModel : ObservableRecipient, IRecipient<ShowNotificationMessage>
{
    private readonly IAppUpdateService _updateService;
    private readonly IAppUpdateInstallerLauncher _installerLauncher;
    private readonly ISettingsService _settingsService;
    private AppUpdateCheckResult? _availableUpdate;

    public ObservableCollection<NotificationModel> Notifications { get; } = new();

    public ShellPageViewModel(
        IMessenger messenger,
        IAppUpdateService updateService,
        IAppUpdateInstallerLauncher installerLauncher,
        ISettingsService settingsService
    )
        : base(messenger)
    {
        _updateService = updateService;
        _installerLauncher = installerLauncher;
        _settingsService = settingsService;
        InstallAvailableUpdateCommand = new AsyncRelayCommand(InstallAvailableUpdateAsync);
        IsActive = true;
        _ = CheckForUpdatesInBackgroundAsync();
    }

    public IAsyncRelayCommand InstallAvailableUpdateCommand { get; }

    [ObservableProperty]
    public partial bool IsUpdateBannerOpen { get; set; }

    [ObservableProperty]
    public partial string UpdateBannerTitle { get; set; } = "";

    [ObservableProperty]
    public partial string UpdateBannerMessage { get; set; } = "";

    public async void Receive(ShowNotificationMessage message)
    {
        var notification = message.Notification;

        Notifications.Add(notification);

        await Task.Delay(10000);
        Notifications.Remove(notification);
    }

    private async Task CheckForUpdatesInBackgroundAsync()
    {
        try
        {
            // Keep startup interactive; update discovery never participates in app initialization.
            await Task.Delay(TimeSpan.FromSeconds(3));
            var result = await _updateService.CheckForUpdateAsync(AppInfoHelper.Version);
            if (!result.IsUpdateAvailable)
                return;

            _availableUpdate = result;
            UpdateBannerTitle = ResourceStringHelper.GetString(
                "AppUpdateBannerTitle",
                "Update available"
            );
            UpdateBannerMessage = ResourceStringHelper.FormatString(
                "AppUpdateBannerMessage",
                "Niratan {0} is available.",
                result.LatestVersion
            );
            IsUpdateBannerOpen = true;
        }
        catch (Exception ex)
        {
            // Automatic checks are intentionally silent when offline or GitHub is unavailable.
            Log.Debug(ex, "Automatic update check skipped");
        }
    }

    private async Task InstallAvailableUpdateAsync()
    {
        if (_availableUpdate is null)
            return;

        try
        {
            UpdateBannerTitle = ResourceStringHelper.GetString(
                "AppUpdateBannerDownloadingTitle",
                "Downloading update"
            );
            var progress = new Progress<AppUpdateDownloadProgress>(value =>
            {
                UpdateBannerMessage = ResourceStringHelper.FormatString(
                    "AppUpdateBannerDownloadingMessage",
                    "Downloading Niratan {0}… {1:F0}%",
                    _availableUpdate.LatestVersion,
                    value.Percentage
                );
            });
            var settings = _settingsService.Current.AppUpdateSettings ?? new AppUpdateSettings();
            var package = await _updateService.DownloadUpdateAsync(
                _availableUpdate,
                settings.ResolveDownloadDirectory(),
                progress
            );
            _installerLauncher.Launch(package.InstallerPath);
            UpdateBannerTitle = ResourceStringHelper.GetString(
                "AppUpdateBannerReadyTitle",
                "Update ready"
            );
            UpdateBannerMessage = ResourceStringHelper.FormatString(
                "AppUpdateBannerReadyMessage",
                "Niratan {0} was downloaded. Continue in Setup to finish updating.",
                package.Version
            );
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unable to download or start app update");
            UpdateBannerTitle = ResourceStringHelper.GetString(
                "AppUpdateBannerFailedTitle",
                "Update failed"
            );
            UpdateBannerMessage = ResourceStringHelper.GetString(
                "AppUpdateDownloadFailedStatus",
                "Unable to download or start the update. Please try again later."
            );
        }
    }
}
