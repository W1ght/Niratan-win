using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoshi.Models.Sync;
using Hoshi.Services.Settings;
using Hoshi.Services.Sync;

namespace Hoshi.ViewModels.Pages;

public partial class TtuSyncSettingsPageViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IGoogleDriveAuthService _googleDriveAuthService;
    private readonly IGoogleDriveSyncCache _googleDriveSyncCache;
    private bool _isInitializing = true;

    public TtuSettingsSyncMode[] AvailableSyncModes { get; } =
        Enum.GetValues<TtuSettingsSyncMode>();

    [ObservableProperty]
    public partial bool EnableSync { get; set; }

    [ObservableProperty]
    public partial TtuSettingsSyncMode SelectedSyncMode { get; set; }

    [ObservableProperty]
    public partial bool EnableAutoSync { get; set; }

    [ObservableProperty]
    public partial string GoogleClientId { get; set; } = "";

    [ObservableProperty]
    public partial string GoogleClientSecret { get; set; } = "";

    [ObservableProperty]
    public partial bool UploadBooks { get; set; }

    [ObservableProperty]
    public partial string GoogleDriveConnectionStatus { get; set; } = "";

    [ObservableProperty]
    public partial bool IsGoogleDriveBusy { get; set; }

    public TtuSyncSettingsPageViewModel(
        ISettingsService settingsService,
        IGoogleDriveAuthService googleDriveAuthService,
        IGoogleDriveSyncCache googleDriveSyncCache)
    {
        _settingsService = settingsService;
        _googleDriveAuthService = googleDriveAuthService;
        _googleDriveSyncCache = googleDriveSyncCache;
        LoadSettings();
        UpdateConnectionStatus();
        _isInitializing = false;
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Current.TtuSyncSettings;

        EnableSync = settings.EnableSync;
        SelectedSyncMode = settings.SyncMode;
        EnableAutoSync = settings.EnableAutoSync;
        GoogleClientId = settings.GoogleClientId;
        UploadBooks = settings.UploadBooks;
    }

    private void SaveSettings()
    {
        if (_isInitializing) return;

        _settingsService.Set(
            s => s.TtuSyncSettings,
            new TtuSyncSettings
            {
                EnableSync = EnableSync,
                SyncMode = SelectedSyncMode,
                EnableAutoSync = EnableAutoSync,
                GoogleClientId = GoogleClientId.Trim(),
                UploadBooks = UploadBooks,
            });
        _ = _settingsService.SaveAsync();
    }

    partial void OnEnableSyncChanged(bool value) => SaveSettings();
    partial void OnSelectedSyncModeChanged(TtuSettingsSyncMode value) => SaveSettings();
    partial void OnEnableAutoSyncChanged(bool value) => SaveSettings();
    partial void OnGoogleClientIdChanged(string value) => SaveSettings();
    partial void OnUploadBooksChanged(bool value) => SaveSettings();

    [RelayCommand]
    private async Task ConnectGoogleDriveAsync()
    {
        var clientId = GoogleClientId.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            GoogleDriveConnectionStatus = "Enter a client ID first.";
            return;
        }

        var clientSecret = GoogleClientSecret.Trim();
        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            GoogleDriveConnectionStatus = "Enter a client secret first.";
            return;
        }

        IsGoogleDriveBusy = true;
        GoogleDriveConnectionStatus = "Connecting...";
        try
        {
            await _googleDriveAuthService.AuthenticateAsync(clientId, clientSecret);
            GoogleClientSecret = "";
            UpdateConnectionStatus();
        }
        catch (Exception ex)
        {
            GoogleDriveConnectionStatus = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsGoogleDriveBusy = false;
        }
    }

    [RelayCommand]
    private async Task SignOutGoogleDriveAsync()
    {
        IsGoogleDriveBusy = true;
        try
        {
            await _googleDriveAuthService.SignOutAsync();
            _googleDriveSyncCache.Clear();
            UpdateConnectionStatus();
        }
        catch (Exception ex)
        {
            GoogleDriveConnectionStatus = $"Sign out failed: {ex.Message}";
        }
        finally
        {
            IsGoogleDriveBusy = false;
        }
    }

    [RelayCommand]
    private void ClearGoogleDriveCache()
    {
        _googleDriveSyncCache.Clear();
        GoogleDriveConnectionStatus = _googleDriveAuthService.HasCredentials
            ? "Cache cleared. Connected"
            : "Cache cleared. Not connected";
    }

    private void UpdateConnectionStatus()
    {
        GoogleDriveConnectionStatus = _googleDriveAuthService.HasCredentials
            ? "Connected"
            : "Not connected";
    }

    public void OnNavigatedFrom() => SaveSettings();
}
