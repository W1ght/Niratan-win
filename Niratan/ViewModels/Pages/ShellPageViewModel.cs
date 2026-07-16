using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Niratan.Helpers;
using Niratan.Messages;
using Niratan.Models;
using Niratan.Services.Sync;
using Niratan.Services.Updates;
using Serilog;

namespace Niratan.ViewModels.Pages;

public partial class ShellPageViewModel : ObservableRecipient, IRecipient<ShowNotificationMessage>
{
    private readonly IAppUpdateService _updateService;
    private readonly IBrowserLauncher _browserLauncher;
    private Uri? _availableUpdateUri;

    public ObservableCollection<NotificationModel> Notifications { get; } = new();

    public ShellPageViewModel(
        IMessenger messenger,
        IAppUpdateService updateService,
        IBrowserLauncher browserLauncher
    )
        : base(messenger)
    {
        _updateService = updateService;
        _browserLauncher = browserLauncher;
        OpenAvailableUpdateCommand = new AsyncRelayCommand(OpenAvailableUpdateAsync);
        IsActive = true;
        _ = CheckForUpdatesInBackgroundAsync();
    }

    public IAsyncRelayCommand OpenAvailableUpdateCommand { get; }

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

            _availableUpdateUri = result.ReleasePageUri;
            UpdateBannerTitle = ResourceStringHelper.GetString(
                "AppUpdateBannerTitle",
                "Update available"
            );
            UpdateBannerMessage = ResourceStringHelper.FormatString(
                "AppUpdateBannerMessage",
                "Niratan {0} is available on GitHub.",
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

    private Task OpenAvailableUpdateAsync() =>
        _availableUpdateUri is null
            ? Task.CompletedTask
            : _browserLauncher.LaunchAsync(_availableUpdateUri);
}
