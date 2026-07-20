using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Helpers;
using Niratan.Models.Settings;
using Niratan.Services.Settings;
using Niratan.Services.UI;
using Niratan.Services.Updates;

namespace Niratan.ViewModels.Pages;

public partial class AboutSettingsPageViewModel : ObservableObject
{
    private readonly IAppUpdateService _updateService;
    private readonly IAppUpdateInstallerLauncher _installerLauncher;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;
    private AppUpdateCheckResult? _availableUpdate;

    public AboutSettingsPageViewModel(
        IAppUpdateService updateService,
        IAppUpdateInstallerLauncher installerLauncher,
        ISettingsService settingsService,
        IDialogService dialogService
    )
    {
        _updateService = updateService;
        _installerLauncher = installerLauncher;
        _settingsService = settingsService;
        _dialogService = dialogService;
        UpdateDownloadDirectory = ResolveUpdateSettings().ResolveDownloadDirectory();
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync);
        InstallUpdateCommand = new AsyncRelayCommand(
            InstallUpdateAsync,
            () => HasAvailableUpdate && !IsDownloadingUpdate
        );
        ChooseUpdateDownloadDirectoryCommand = new AsyncRelayCommand(
            ChooseUpdateDownloadDirectoryAsync
        );
    }

    public string NiratanVersion { get; } = AppInfoHelper.Version;

    public IAsyncRelayCommand CheckForUpdatesCommand { get; }

    public IAsyncRelayCommand InstallUpdateCommand { get; }

    public IAsyncRelayCommand ChooseUpdateDownloadDirectoryCommand { get; }

    [ObservableProperty]
    public partial bool IsCheckingForUpdates { get; set; }

    [ObservableProperty]
    public partial bool HasAvailableUpdate { get; set; }

    [ObservableProperty]
    public partial bool IsDownloadingUpdate { get; set; }

    [ObservableProperty]
    public partial double UpdateDownloadProgress { get; set; }

    [ObservableProperty]
    public partial string UpdateDownloadDirectory { get; set; }

    [ObservableProperty]
    public partial string UpdateStatusText { get; set; } =
        ResourceStringHelper.GetString(
            "AppUpdateInitialStatus",
            "Check for the latest release."
        );

    partial void OnHasAvailableUpdateChanged(bool value) =>
        InstallUpdateCommand.NotifyCanExecuteChanged();

    partial void OnIsDownloadingUpdateChanged(bool value) =>
        InstallUpdateCommand.NotifyCanExecuteChanged();

    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        HasAvailableUpdate = false;
        _availableUpdate = null;
        UpdateStatusText = ResourceStringHelper.GetString(
            "AppUpdateCheckingStatus",
            "Checking for updates…"
        );

        try
        {
            var result = await _updateService.CheckForUpdateAsync(NiratanVersion);
            if (result.IsUpdateAvailable)
            {
                _availableUpdate = result;
                HasAvailableUpdate = true;
                UpdateStatusText = ResourceStringHelper.FormatString(
                    "AppUpdateAvailableStatus",
                    "Version {0} is available.",
                    result.LatestVersion
                );
            }
            else
            {
                UpdateStatusText = ResourceStringHelper.FormatString(
                    "AppUpdateCurrentStatus",
                    "You're up to date (version {0}).",
                    NiratanVersion
                );
            }
        }
        catch (Exception)
        {
            UpdateStatusText = ResourceStringHelper.GetString(
                "AppUpdateFailedStatus",
                "Unable to check for updates. Please try again later."
            );
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_availableUpdate is null)
            return;

        IsDownloadingUpdate = true;
        UpdateDownloadProgress = 0;
        try
        {
            var progress = new Progress<AppUpdateDownloadProgress>(value =>
            {
                UpdateDownloadProgress = value.Percentage;
                UpdateStatusText = ResourceStringHelper.FormatString(
                    "AppUpdateDownloadingStatus",
                    "Downloading version {0}… {1:F0}%",
                    _availableUpdate.LatestVersion,
                    value.Percentage
                );
            });
            var package = await _updateService.DownloadUpdateAsync(
                _availableUpdate,
                UpdateDownloadDirectory,
                progress
            );
            _installerLauncher.Launch(package.InstallerPath);
            UpdateStatusText = ResourceStringHelper.FormatString(
                "AppUpdateInstallerStartedStatus",
                "Version {0} was downloaded. Continue in Setup to finish updating.",
                package.Version
            );
        }
        catch (Exception)
        {
            UpdateStatusText = ResourceStringHelper.GetString(
                "AppUpdateDownloadFailedStatus",
                "Unable to download or start the update. Please try again later."
            );
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    private async Task ChooseUpdateDownloadDirectoryAsync()
    {
        var selected = await _dialogService.OpenFolderPickerAsync();
        if (string.IsNullOrWhiteSpace(selected))
            return;

        var updateSettings = new AppUpdateSettings { DownloadDirectory = selected };
        UpdateDownloadDirectory = updateSettings.ResolveDownloadDirectory();
        _settingsService.Set(settings => settings.AppUpdateSettings, updateSettings);
        await _settingsService.SaveAsync();
    }

    private AppUpdateSettings ResolveUpdateSettings() =>
        _settingsService.Current.AppUpdateSettings ?? new AppUpdateSettings();
}
