using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Helpers;
using Niratan.Models;
using Niratan.Models.Sasayaki;
using Niratan.Services.Sasayaki;
using Niratan.Services.Settings;
using Niratan.Services.UI;

namespace Niratan.ViewModels.Components;

public partial class SasayakiResourcesViewModel : ObservableObject
{
    private const double MinimumSearchWindow = 100;
    private const double MaximumSearchWindow = 10000;

    private readonly IDialogService _dialogService;
    private readonly ISasayakiMatchService _matchService;
    private readonly ISasayakiSidecarService _sidecarService;
    private readonly ISettingsService _settingsService;
    private readonly CancellationTokenSource _cts = new();
    private NovelBook? _book;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudiobookDisplayName))]
    [NotifyPropertyChangedFor(nameof(CanMatch))]
    [NotifyCanExecuteChangedFor(nameof(MatchCommand))]
    public partial string? AudiobookPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleDisplayName))]
    [NotifyPropertyChangedFor(nameof(CanMatch))]
    [NotifyCanExecuteChangedFor(nameof(MatchCommand))]
    public partial string? SubtitlePath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentMatchSummary))]
    public partial SasayakiMatchData? CurrentMatch { get; set; }

    [ObservableProperty]
    public partial double SearchWindowSizeValue { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMatch))]
    [NotifyPropertyChangedFor(nameof(CanPickFiles))]
    [NotifyPropertyChangedFor(nameof(MatchButtonText))]
    [NotifyCanExecuteChangedFor(nameof(MatchCommand))]
    public partial bool IsMatching { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public string AudiobookDisplayName => FileDisplayName(
        AudiobookPath,
        ResourceStringHelper.GetString(
            "SasayakiMatchNoAudiobookSelected",
            "No audiobook selected"));

    public string SubtitleDisplayName => FileDisplayName(
        SubtitlePath,
        ResourceStringHelper.GetString(
            "SasayakiMatchNoFileSelected",
            "No file selected"));

    public string CurrentMatchSummary
    {
        get
        {
            if (CurrentMatch == null)
            {
                return ResourceStringHelper.GetString(
                    "SasayakiMatchNoSubtitleMatch",
                    "No subtitle match");
            }

            var matched = CurrentMatch.Matches.Count;
            var total = CurrentMatch.TotalCueCount;
            var percentage = total > 0 ? matched * 100d / total : 0;
            return ResourceStringHelper.FormatString(
                "SasayakiMatchSummary",
                "{0}/{1} cues matched ({2:F1}%)",
                matched,
                total,
                percentage);
        }
    }

    public string MatchButtonText => IsMatching
        ? ResourceStringHelper.GetString("SasayakiMatchMatching", "Matching…")
        : ResourceStringHelper.GetString("SasayakiMatchAction", "Match");

    public bool CanMatch =>
        !IsMatching
        && !string.IsNullOrWhiteSpace(AudiobookPath)
        && !string.IsNullOrWhiteSpace(SubtitlePath);

    public bool CanPickFiles => !IsMatching;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public event EventHandler<SasayakiMatchData>? MatchCompleted;

    public SasayakiResourcesViewModel(
        IDialogService dialogService,
        ISasayakiMatchService matchService,
        ISasayakiSidecarService sidecarService,
        ISettingsService settingsService)
    {
        _dialogService = dialogService;
        _matchService = matchService;
        _sidecarService = sidecarService;
        _settingsService = settingsService;
    }

    public async Task InitializeAsync(NovelBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        _book = book;
        SearchWindowSizeValue = Math.Clamp(
            _settingsService.Current.SasayakiSettings.SearchWindowSize,
            MinimumSearchWindow,
            MaximumSearchWindow);

        var bookRootPath = ResolveBookRootPath(book);
        var matchTask = _sidecarService.LoadMatchAsync(bookRootPath, _cts.Token);
        var sourceTask = _sidecarService.LoadSourceAsync(bookRootPath, _cts.Token);
        await Task.WhenAll(matchTask, sourceTask);

        CurrentMatch = await matchTask;
        var source = await sourceTask;
        AudiobookPath = source?.AudiobookPath;
        SubtitlePath = source?.SrtPath;
        ErrorMessage = null;
    }

    public void Cancel() => _cts.Cancel();

    [RelayCommand]
    private async Task PickAudiobookAsync()
    {
        var path = await _dialogService.OpenFilePickerAsync(
            ".mp3",
            ".m4b",
            ".m4a",
            ".wav",
            ".flac",
            ".ogg");
        if (path != null)
        {
            AudiobookPath = path;
            ErrorMessage = null;
        }
    }

    [RelayCommand]
    private async Task PickSubtitleAsync()
    {
        var path = await _dialogService.OpenFilePickerAsync(".srt", ".vtt");
        if (path != null)
        {
            SubtitlePath = path;
            ErrorMessage = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunMatch))]
    private async Task MatchAsync()
    {
        if (_book == null || AudiobookPath == null || SubtitlePath == null)
            return;

        IsMatching = true;
        ErrorMessage = null;
        try
        {
            CurrentMatch = await _matchService.MatchAsync(
                _book,
                AudiobookPath,
                SubtitlePath,
                (int)Math.Round(SearchWindowSizeValue),
                _cts.Token);
            MatchCompleted?.Invoke(this, CurrentMatch);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsMatching = false;
        }
    }

    private bool CanRunMatch() => CanMatch;

    private static string FileDisplayName(string? path, string fallback) =>
        string.IsNullOrWhiteSpace(path)
            ? fallback
            : Path.GetFileName(path);

    private static string ResolveBookRootPath(NovelBook book) =>
        string.IsNullOrWhiteSpace(book.ExtractedPath)
            ? AppDataHelper.GetNovelBookPath(book.Id)
            : book.ExtractedPath;
}
