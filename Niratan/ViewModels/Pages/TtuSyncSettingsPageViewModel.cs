using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Niratan.Helpers;
using Niratan.Messages;
using Niratan.Models.Sasayaki;
using Niratan.Models.Settings;
using Niratan.Models.Sync;
using Niratan.Services.Settings;
using Niratan.Services.Sync;
using Niratan.Services.UI;

namespace Niratan.ViewModels.Pages;

public partial class TtuSyncSettingsPageViewModel : ObservableObject,
    IRecipient<GoogleDriveConnectionStateChangedMessage>
{
    private readonly ISettingsService _settingsService;
    private readonly IGoogleDriveAuthService _googleDriveAuthService;
    private readonly IGoogleDriveCredentialStore _credentialStore;
    private readonly IGoogleDriveSyncCache _googleDriveSyncCache;
    private readonly IGoogleDriveCoverCacheService _googleDriveCoverCacheService;
    private readonly IDialogService _dialogService;
    private SynchronizationContext? _uiContext;
    private bool _isInitializing = true;

    public IReadOnlyList<TtuSyncModeItem> AvailableSyncModes { get; } =
    [
        new(
            TtuSettingsSyncMode.Auto,
            ResourceStringHelper.GetString("TtuSyncModeAuto", "Auto")),
        new(
            TtuSettingsSyncMode.Manual,
            ResourceStringHelper.GetString("TtuSyncModeManual", "Manual")),
    ];

    [ObservableProperty]
    public partial bool EnableSync { get; set; }

    [ObservableProperty]
    public partial TtuSettingsSyncMode SelectedSyncMode { get; set; }

    [ObservableProperty]
    public partial TtuSyncModeItem? SelectedSyncModeItem { get; set; }

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

    public bool IsGoogleDriveDisconnected => !IsGoogleDriveConnected;

    public bool CanEditGoogleDriveCredentials =>
        !IsGoogleDriveConnected && !IsGoogleDriveBusy;

    public bool CanRunGoogleDriveActions => !IsGoogleDriveBusy;

    public TtuSyncSettingsPageViewModel(
        ISettingsService settingsService,
        IGoogleDriveAuthService googleDriveAuthService,
        IGoogleDriveCredentialStore credentialStore,
        IGoogleDriveSyncCache googleDriveSyncCache,
        IGoogleDriveCoverCacheService googleDriveCoverCacheService,
        IDialogService dialogService,
        IMessenger messenger)
    {
        _settingsService = settingsService;
        _googleDriveAuthService = googleDriveAuthService;
        _credentialStore = credentialStore;
        _googleDriveSyncCache = googleDriveSyncCache;
        _googleDriveCoverCacheService = googleDriveCoverCacheService;
        _dialogService = dialogService;
        _uiContext = SynchronizationContext.Current;
        messenger.Register<TtuSyncSettingsPageViewModel, GoogleDriveConnectionStateChangedMessage>(
            this,
            static (recipient, message) => recipient.Receive(message));
        LoadSettings();
        RefreshConnectionState();
        _isInitializing = false;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _uiContext ??= SynchronizationContext.Current;
        _isInitializing = true;
        var loadFailed = false;
        var authorizationExpired = false;
        var connectionCheckFailed = false;
        try
        {
            var credentials = await _credentialStore.LoadAsync(ct);
            if (credentials != null)
            {
                GoogleClientId = credentials.ClientId;
                GoogleClientSecret = credentials.ClientSecret;
                try
                {
                    await _googleDriveAuthService.GetAccessTokenAsync(ct);
                }
                catch (GoogleDriveReauthenticationRequiredException)
                {
                    authorizationExpired = true;
                    ApplyConnectionState(new GoogleDriveConnectionStateChangedMessage(
                        IsConnected: false,
                        RequiresReconnect: true));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    connectionCheckFailed = true;
                    GoogleDriveConnectionStatus = ResourceStringHelper.FormatString(
                        "TtuSyncStatusConnectionCheckFailedFormat",
                        "Unable to verify the Google Drive connection: {0}",
                        ex.Message);
                }
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
            if (!loadFailed && !authorizationExpired && !connectionCheckFailed)
                RefreshConnectionState();
        }
    }

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

    private void SaveSettings()
    {
        if (_isInitializing)
            return;

        _settingsService.Set(
            settings => settings.TtuSyncSettings,
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

    partial void OnEnableAutoSyncChanged(bool value) => SaveSettings();

    partial void OnGoogleClientIdChanged(string value) => SaveSettings();

    partial void OnUploadBooksChanged(bool value) => SaveSettings();

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
                ResetTimeMinutes = current.ResetTimeMinutes,
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
            GoogleClientId = clientId;
            GoogleClientSecret = clientSecret;
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
        {
            return;
        }

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
        {
            return;
        }

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

    private void RefreshConnectionState()
    {
        IsGoogleDriveConnected = _googleDriveAuthService.HasCredentials;
        GoogleDriveConnectionStatus = IsGoogleDriveConnected
            ? ResourceStringHelper.GetString("TtuSyncStatusConnected", "Connected")
            : ResourceStringHelper.GetString("TtuSyncStatusNotConnected", "Not connected");
    }

    public void Receive(GoogleDriveConnectionStateChangedMessage message)
    {
        var uiContext = _uiContext;
        if (uiContext != null && SynchronizationContext.Current != uiContext)
        {
            uiContext.Post(_ => ApplyConnectionState(message), null);
            return;
        }

        ApplyConnectionState(message);
    }

    private void ApplyConnectionState(GoogleDriveConnectionStateChangedMessage message)
    {
        IsGoogleDriveConnected = message.IsConnected;
        if (!message.IsConnected)
            GoogleClientSecret = "";

        GoogleDriveConnectionStatus = message.RequiresReconnect
            ? ResourceStringHelper.GetString(
                "TtuSyncStatusAuthorizationExpired",
                "Authorization expired or was revoked. Connect Google Drive again.")
            : message.IsConnected
                ? ResourceStringHelper.GetString("TtuSyncStatusConnected", "Connected")
                : ResourceStringHelper.GetString("TtuSyncStatusNotConnected", "Not connected");
    }

    public void OnNavigatedFrom() => SaveSettings();
}

public sealed record TtuSyncModeItem(
    TtuSettingsSyncMode Value,
    string DisplayName);
