using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Helpers;
using Niratan.Models.Settings;
using Niratan.Services.Audio;
using Niratan.Services.Settings;
using Niratan.Services.UI;
using Serilog;
using Windows.Storage;

namespace Niratan.ViewModels.Pages;

public partial class AudioSettingsPageViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private bool _isInitializing = true;

    public IReadOnlyList<AudioPlaybackModeOption> AvailablePlaybackModes { get; } =
    [
        new(
            AudioPlaybackMode.Interrupt,
            ResourceStringHelper.GetString("AudioPlaybackModeInterrupt", "Interrupt other audio")
        ),
        new(
            AudioPlaybackMode.Duck,
            ResourceStringHelper.GetString("AudioPlaybackModeDuck", "Duck other audio")
        ),
        new(
            AudioPlaybackMode.Mix,
            ResourceStringHelper.GetString("AudioPlaybackModeMix", "Mix with other audio")
        ),
    ];

    public ObservableCollection<AudioSourceViewModel> AudioSources { get; } = [];

    [ObservableProperty]
    public partial string NewSourceName { get; set; } = "";

    [ObservableProperty]
    public partial string NewSourceUrl { get; set; } = "";

    [ObservableProperty]
    public partial bool EnableAutoplay { get; set; }

    [ObservableProperty]
    public partial AudioPlaybackMode SelectedPlaybackMode { get; set; } = AudioPlaybackMode.Interrupt;

    [ObservableProperty]
    public partial bool EnableLocalAudio { get; set; }

    [ObservableProperty]
    public partial bool IsLocalAudioImported { get; set; }

    [ObservableProperty]
    public partial bool IsImporting { get; set; }

    [ObservableProperty]
    public partial string LocalAudioStatusText { get; set; } = "";

    public IRelayCommand AddSourceCommand { get; }
    public IRelayCommand<AudioSourceViewModel> DeleteSourceCommand { get; }
    public IRelayCommand<AudioSourceViewModel> MoveSourceUpCommand { get; }
    public IRelayCommand<AudioSourceViewModel> MoveSourceDownCommand { get; }
    public IAsyncRelayCommand ImportLocalAudioCommand { get; }
    public IRelayCommand DeleteLocalAudioCommand { get; }

    public AudioSettingsPageViewModel()
    {
        _settingsService = App.GetService<ISettingsService>();
        AddSourceCommand = new RelayCommand(AddSource);
        DeleteSourceCommand = new RelayCommand<AudioSourceViewModel>(DeleteSource);
        MoveSourceUpCommand = new RelayCommand<AudioSourceViewModel>(MoveSourceUp);
        MoveSourceDownCommand = new RelayCommand<AudioSourceViewModel>(MoveSourceDown);
        ImportLocalAudioCommand = new AsyncRelayCommand(ImportLocalAudioAsync);
        DeleteLocalAudioCommand = new RelayCommand(DeleteLocalAudio);

        LoadSettings();
        _isInitializing = false;
    }

    private void LoadSettings()
    {
        var audio = _settingsService.Current.AudioSettings;

        AudioSources.Clear();
        foreach (var source in audio.NormalizedSources())
        {
            AudioSources.Add(new AudioSourceViewModel
            {
                Name = source.Name,
                Url = source.Url,
                IsEnabled = source.IsEnabled,
                IsDefault = source.IsDefault,
            });
        }

        RefreshPositionFlags();
        EnableAutoplay = audio.EnableAutoplay;
        SelectedPlaybackMode = audio.PlaybackMode;
        EnableLocalAudio = audio.EnableLocalAudio;
        RefreshLocalAudioStatus();
    }

    private void SaveSettings()
    {
        if (_isInitializing) return;

        var audio = _settingsService.Current.AudioSettings;
        audio.AudioSources = AudioSources.Select(vm => new AudioSource
        {
            Name = vm.Name,
            Url = vm.Url,
            IsEnabled = vm.IsEnabled,
            IsDefault = vm.IsDefault,
        }).ToList();
        audio.EnableAutoplay = EnableAutoplay;
        audio.PlaybackMode = SelectedPlaybackMode;
        audio.EnableLocalAudio = EnableLocalAudio;

        _settingsService.Set(s => s.AudioSettings, audio);
        _ = _settingsService.SaveAsync();

        var audioService = App.GetService<IAudioService>();
        audioService.UpdateSettings(audio);
    }

    partial void OnEnableAutoplayChanged(bool value) => SaveSettings();
    partial void OnSelectedPlaybackModeChanged(AudioPlaybackMode value) => SaveSettings();
    partial void OnEnableLocalAudioChanged(bool value)
    {
        SaveSettings();
        RefreshLocalAudioStatus();
    }

    private void RefreshPositionFlags()
    {
        for (var i = 0; i < AudioSources.Count; i++)
        {
            AudioSources[i].CanMoveUp = i > 0;
            AudioSources[i].CanMoveDown = i < AudioSources.Count - 1;
        }
    }

    private void AddSource()
    {
        var name = NewSourceName.Trim();
        var url = NewSourceUrl.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) return;
        if (AudioSources.Any(s => s.Url == url)) return;

        AudioSources.Add(new AudioSourceViewModel
        {
            Name = name,
            Url = url,
            IsEnabled = true,
            IsDefault = false,
        });

        NewSourceName = "";
        NewSourceUrl = "";
        RefreshPositionFlags();
        SaveSettings();
    }

    private void DeleteSource(AudioSourceViewModel? source)
    {
        if (source == null) return;
        if (source.IsDefault || source.Url == AudioSettings.LocalAudioUrl) return;
        AudioSources.Remove(source);
        RefreshPositionFlags();
        SaveSettings();
    }

    private void MoveSourceUp(AudioSourceViewModel? source)
    {
        if (source == null) return;
        var idx = AudioSources.IndexOf(source);
        if (idx <= 0) return;
        AudioSources.Move(idx, idx - 1);
        RefreshPositionFlags();
        SaveSettings();
    }

    private void MoveSourceDown(AudioSourceViewModel? source)
    {
        if (source == null) return;
        var idx = AudioSources.IndexOf(source);
        if (idx < 0 || idx >= AudioSources.Count - 1) return;
        AudioSources.Move(idx, idx + 1);
        RefreshPositionFlags();
        SaveSettings();
    }

    private async Task ImportLocalAudioAsync()
    {
        try
        {
            var window = App.MainWindow;
            if (window == null) return;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".db");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            IsImporting = true;
            LocalAudioStatusText = ResourceStringHelper.GetString(
                "AudioLocalAudioImportingStatus",
                "Importing audio database..."
            );

            var dataPath = Helpers.AppDataHelper.GetDataPath();
            var destPath = System.IO.Path.Combine(dataPath, "Audio", "android.db");
            var destDir = System.IO.Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir) && !System.IO.Directory.Exists(destDir))
                System.IO.Directory.CreateDirectory(destDir);

            var buffer = await FileIO.ReadBufferAsync(file);
            using var srcStream = buffer.AsStream();
            using var destStream = new System.IO.FileStream(destPath, System.IO.FileMode.Create, System.IO.FileAccess.Write);
            await srcStream.CopyToAsync(destStream);

            RefreshLocalAudioStatus();
            Log.Information("[AudioSettings] Local audio database imported to {Path}", destPath);
            App.GetService<INotificationService>().ShowSuccess(
                ResourceStringHelper.GetString(
                    "AudioLocalAudioImportedNotification",
                    "Local audio database imported"
                ),
                ResourceStringHelper.GetString("AudioNotificationTitle", "Audio")
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[AudioSettings] Failed to import local audio database");
            App.GetService<INotificationService>().ShowError(
                ex.Message,
                ResourceStringHelper.GetString("AudioImportErrorTitle", "Import Error")
            );
        }
        finally
        {
            IsImporting = false;
        }
    }

    private void DeleteLocalAudio()
    {
        try
        {
            var dataPath = Helpers.AppDataHelper.GetDataPath();
            var dbPath = System.IO.Path.Combine(dataPath, "Audio", "android.db");
            if (System.IO.File.Exists(dbPath))
                System.IO.File.Delete(dbPath);

            RefreshLocalAudioStatus();
            Log.Information("[AudioSettings] Local audio database deleted");
            App.GetService<INotificationService>().ShowSuccess(
                ResourceStringHelper.GetString(
                    "AudioLocalAudioDeletedNotification",
                    "Local audio database deleted"
                ),
                ResourceStringHelper.GetString("AudioNotificationTitle", "Audio")
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[AudioSettings] Failed to delete local audio database");
            App.GetService<INotificationService>().ShowError(
                ex.Message,
                ResourceStringHelper.GetString("AudioDeleteErrorTitle", "Delete Error")
            );
        }
    }

    private void RefreshLocalAudioStatus()
    {
        var dataPath = Helpers.AppDataHelper.GetDataPath();
        var dbPath = System.IO.Path.Combine(dataPath, "Audio", "android.db");
        IsLocalAudioImported = System.IO.File.Exists(dbPath);

        if (IsLocalAudioImported)
        {
            var info = new System.IO.FileInfo(dbPath);
            LocalAudioStatusText = ResourceStringHelper.FormatString(
                "AudioLocalAudioImportedStatus",
                "Database: {0}",
                FormatFileSize(info.Length)
            );
        }
        else
        {
            LocalAudioStatusText = ResourceStringHelper.GetString(
                "AudioLocalAudioMissingStatus",
                "No audio database imported. Import an Android audio .db file to use local audio."
            );
        }
    }

    private static string FormatFileSize(long bytes) =>
        bytes switch
        {
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} B",
        };

    public void OnNavigatedFrom()
    {
        SaveSettings();
    }
}

public partial class AudioSourceViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Url { get; set; } = "";

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsDefault { get; set; }

    [ObservableProperty]
    public partial bool CanMoveUp { get; set; }

    [ObservableProperty]
    public partial bool CanMoveDown { get; set; }

    public string DisplayName
    {
        get
        {
            if (IsDefault)
            {
                return ResourceStringHelper.GetString("AudioDefaultSourceName", "Default");
            }

            if (Url == AudioSettings.LocalAudioUrl || Url == AudioSettings.LegacyLocalAudioUrl)
            {
                return ResourceStringHelper.GetString("AudioLocalSourceName", "Local");
            }

            return Name;
        }
    }

    public bool CanDelete => !IsDefault && Url != AudioSettings.LocalAudioUrl;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayName));

    partial void OnUrlChanged(string value) => OnPropertyChanged(nameof(DisplayName));

    partial void OnIsDefaultChanged(bool value) => OnPropertyChanged(nameof(DisplayName));
}

public sealed record AudioPlaybackModeOption(AudioPlaybackMode Mode, string Label);
