using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Helpers;
using Niratan.Models.Video;
using Niratan.Services.UI;
using Niratan.Services.Video;
using Niratan.ViewModels.Components;

namespace Niratan.ViewModels.Pages;

public sealed record VideoDiscoverySubtitleDestinationOption(
    VideoDiscoverySubtitleDestination Value,
    string DisplayName);

public partial class VideoDiscoverySubtitleSearchPageViewModel : ObservableObject, IDisposable
{
    private readonly IJimakuSubtitleService _subtitles;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource _cts = new();
    private bool _disposed;
    private string? _videoPath;
    private string? _directoryPath;

    [ObservableProperty]
    public partial VideoDiscoveryNavigationTarget? Target { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial string SearchQuery { get; set; } = "";

    [ObservableProperty]
    public partial ObservableCollection<JimakuSubtitleItemViewModel> Results { get; set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedCommand))]
    public partial JimakuSubtitleItemViewModel? SelectedResult { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsTargetPicker))]
    [NotifyPropertyChangedFor(nameof(TargetPathText))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedCommand))]
    public partial VideoDiscoverySubtitleDestinationOption SelectedDestination { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveSelectedCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    public IReadOnlyList<VideoDiscoverySubtitleDestinationOption> Destinations { get; }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool NeedsTargetPicker =>
        SelectedDestination.Value != VideoDiscoverySubtitleDestination.SaveAs;
    public string TargetPathText => SelectedDestination.Value switch
    {
        VideoDiscoverySubtitleDestination.SaveAs => ResourceStringHelper.GetString(
            "DiscoverSubtitleSaveAsHint",
            "Choose a file name when you save."),
        VideoDiscoverySubtitleDestination.ExistingVideo => _videoPath
            ?? ResourceStringHelper.GetString(
                "DiscoverSubtitleChooseVideoHint",
                "Choose an existing video."),
        _ => _directoryPath
            ?? ResourceStringHelper.GetString(
                "DiscoverSubtitleChooseDirectoryHint",
                "Choose a destination folder."),
    };

    public VideoDiscoverySubtitleSearchPageViewModel(
        IJimakuSubtitleService subtitles,
        IDialogService dialogs)
    {
        _subtitles = subtitles;
        _dialogs = dialogs;
        Destinations =
        [
            new(
                VideoDiscoverySubtitleDestination.SaveAs,
                ResourceStringHelper.GetString("DiscoverSubtitleSaveAs", "Save as")),
            new(
                VideoDiscoverySubtitleDestination.ExistingVideo,
                ResourceStringHelper.GetString("DiscoverSubtitleNextToVideo", "Next to an existing video")),
            new(
                VideoDiscoverySubtitleDestination.Directory,
                ResourceStringHelper.GetString("DiscoverSubtitleSaveToDirectory", "Save to a folder")),
        ];
        SelectedDestination = Destinations[0];
    }

    public async Task InitializeAsync(VideoDiscoveryNavigationTarget target)
    {
        if (_disposed)
            return;
        ResetCancellation();
        Target = target;
        SearchQuery = target.Identity.Title;
        Results.Clear();
        SelectedResult = null;
        ErrorMessage = null;
        StatusText = "";
        await SearchAsync();
    }

    private bool CanSearch() => !IsBusy
        && Target is not null
        && !string.IsNullOrWhiteSpace(SearchQuery);

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        if (Target is null)
            return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await _subtitles.SearchAsync(
                new VideoSubtitleSearchRequest(Target.Identity, SearchQuery.Trim()),
                _cts.Token);
            if (result.IsCancelled)
                return;
            if (!result.IsSuccess || result.Value is null)
            {
                ErrorMessage = result.Error;
                Results.Clear();
                return;
            }
            Results = new ObservableCollection<JimakuSubtitleItemViewModel>(
                result.Value.Select(item => new JimakuSubtitleItemViewModel(item)));
            SelectedResult = null;
            StatusText = ResourceStringHelper.FormatString(
                "DiscoverSubtitleSummary",
                "Showing {0} subtitle results.",
                Results.Count);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task PickTargetAsync()
    {
        string? path;
        if (SelectedDestination.Value == VideoDiscoverySubtitleDestination.ExistingVideo)
        {
            path = await _dialogs.OpenFilePickerAsync(
                ".mkv", ".mp4", ".m4v", ".webm", ".avi", ".mov", ".wmv");
            if (path is not null)
                _videoPath = path;
        }
        else if (SelectedDestination.Value == VideoDiscoverySubtitleDestination.Directory)
        {
            path = await _dialogs.OpenFolderPickerAsync();
            if (path is not null)
                _directoryPath = path;
        }
        OnPropertyChanged(nameof(TargetPathText));
        SaveSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanSaveSelected() => !IsBusy
        && SelectedResult is not null
        && SelectedDestination.Value switch
        {
            VideoDiscoverySubtitleDestination.SaveAs => true,
            VideoDiscoverySubtitleDestination.ExistingVideo => !string.IsNullOrWhiteSpace(_videoPath),
            VideoDiscoverySubtitleDestination.Directory => !string.IsNullOrWhiteSpace(_directoryPath),
            _ => false,
        };

    [RelayCommand(CanExecute = nameof(CanSaveSelected))]
    private async Task SaveSelectedAsync()
    {
        if (SelectedResult is null)
            return;
        var row = SelectedResult;
        var extension = NormalizeExtension(Path.GetExtension(row.Item.FileName));
        string? destination;
        if (SelectedDestination.Value == VideoDiscoverySubtitleDestination.SaveAs)
        {
            destination = await _dialogs.SaveFilePickerAsync(
                SanitizeFileName(Path.GetFileNameWithoutExtension(row.Item.FileName)),
                ResourceStringHelper.GetString("DiscoverSubtitleFileType", "Subtitle file"),
                extension);
            if (destination is null)
                return;
        }
        else if (SelectedDestination.Value == VideoDiscoverySubtitleDestination.ExistingVideo)
        {
            if (!File.Exists(_videoPath))
            {
                ErrorMessage = ResourceStringHelper.GetString(
                    "DiscoverSubtitleVideoMissing",
                    "The selected video no longer exists.");
                return;
            }
            var language = string.IsNullOrWhiteSpace(row.Item.Language)
                ? "subtitle"
                : SanitizeFileName(row.Item.Language);
            var videoDirectory = Path.GetDirectoryName(Path.GetFullPath(_videoPath!))!;
            var videoName = SanitizeFileName(Path.GetFileNameWithoutExtension(_videoPath!));
            destination = Path.Combine(videoDirectory, $"{videoName}.{language}{extension}");
        }
        else
        {
            if (!Directory.Exists(_directoryPath))
            {
                ErrorMessage = ResourceStringHelper.GetString(
                    "DiscoverSubtitleDirectoryMissing",
                    "The selected subtitle folder no longer exists.");
                return;
            }
            destination = Path.Combine(
                Path.GetFullPath(_directoryPath!),
                SanitizeFileName(Path.GetFileNameWithoutExtension(row.Item.FileName)) + extension);
        }

        destination = FindUniqueDestination(destination);
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await _subtitles.DownloadAsync(row.Item, destination, _cts.Token);
            if (!result.IsSuccess)
            {
                if (!result.IsCancelled)
                    ErrorMessage = result.Error;
                row.Status = result.Error ?? "";
                return;
            }
            row.Status = ResourceStringHelper.GetString(
                "DiscoverSubtitleSaved",
                "Subtitle saved.");
            StatusText = result.Value ?? destination;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            row.Status = ex.Message;
            ErrorMessage = ex.Message;
        }
        finally { IsBusy = false; }
    }

    internal static string FindUniqueDestination(string requestedPath)
    {
        var fullPath = Path.GetFullPath(requestedPath);
        if (!File.Exists(fullPath))
            return fullPath;
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(ResourceStringHelper.GetString(
                "DiscoverSubtitleInvalidDestination",
                "The subtitle destination is invalid."));
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);
        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name} ({suffix}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
        throw new IOException(ResourceStringHelper.GetString(
            "DiscoverSubtitleNoUniqueFileName",
            "Could not find an unused subtitle file name."));
    }

    private static string NormalizeExtension(string extension) =>
        extension.ToLowerInvariant() is ".srt" or ".ass" or ".ssa" or ".vtt"
            ? extension.ToLowerInvariant()
            : ".srt";

    private static string SanitizeFileName(string value)
    {
        foreach (var character in Path.GetInvalidFileNameChars())
            value = value.Replace(character, '_');
        return string.IsNullOrWhiteSpace(value) ? "subtitle" : value.Trim();
    }

    private void ResetCancellation()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
    }

    public void OnNavigatedFrom() => _cts.Cancel();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
