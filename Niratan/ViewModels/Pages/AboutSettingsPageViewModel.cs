using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Helpers;
using Niratan.Services.Sync;
using Niratan.Services.Updates;

namespace Niratan.ViewModels.Pages;

public partial class AboutSettingsPageViewModel : ObservableObject
{
    private readonly IAppUpdateService _updateService;
    private readonly IBrowserLauncher _browserLauncher;
    private Uri? _releasePageUri;

    public AboutSettingsPageViewModel(
        IAppUpdateService updateService,
        IBrowserLauncher browserLauncher
    )
    {
        _updateService = updateService;
        _browserLauncher = browserLauncher;
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync);
        OpenUpdateCommand = new AsyncRelayCommand(OpenUpdateAsync, () => HasAvailableUpdate);
    }

    public string NiratanVersion { get; } = AppInfoHelper.Version;

    public IAsyncRelayCommand CheckForUpdatesCommand { get; }

    public IAsyncRelayCommand OpenUpdateCommand { get; }

    [ObservableProperty]
    public partial bool IsCheckingForUpdates { get; set; }

    [ObservableProperty]
    public partial bool HasAvailableUpdate { get; set; }

    [ObservableProperty]
    public partial string UpdateStatusText { get; set; } =
        ResourceStringHelper.GetString(
            "AppUpdateInitialStatus",
            "Check GitHub for the latest release."
        );

    partial void OnHasAvailableUpdateChanged(bool value) =>
        OpenUpdateCommand.NotifyCanExecuteChanged();

    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        HasAvailableUpdate = false;
        _releasePageUri = null;
        UpdateStatusText = ResourceStringHelper.GetString(
            "AppUpdateCheckingStatus",
            "Checking for updates…"
        );

        try
        {
            var result = await _updateService.CheckForUpdateAsync(NiratanVersion);
            if (result.IsUpdateAvailable)
            {
                _releasePageUri = result.ReleasePageUri;
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

    private Task OpenUpdateAsync() =>
        _releasePageUri is null
            ? Task.CompletedTask
            : _browserLauncher.LaunchAsync(_releasePageUri);
}
