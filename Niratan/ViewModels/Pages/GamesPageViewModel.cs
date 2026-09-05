using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Niratan.Helpers;
using Niratan.Models.Anki;
using Niratan.Models.Games;
using Niratan.Models.Settings;
using Niratan.Services.Games;
using Niratan.Services.Settings;

namespace Niratan.ViewModels.Pages;

public partial class GamesPageViewModel : ObservableObject, IDisposable
{
    private readonly IGalGameRepository _repository;
    private readonly IGalGameSessionService _session;
    private readonly GalGameMediaCapture _mediaCapture;
    private readonly GalGameIngameLookupController _ingameLookup;
    private readonly ISettingsService _settings;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;
    private string? _lastDiagnosticEventKey;
    private readonly Dictionary<ulong, List<string>> _threadPreviewHistory = [];
    private readonly Dictionary<ulong, GalGameThreadPreview> _discoveredThreads = [];
    private bool _loadingAppearance;
    private CancellationTokenSource? _appearanceSaveCts;

    [ObservableProperty]
    public partial string GamePath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string SessionStatusText { get; set; } =
        ResourceStringHelper.GetString("GamesSessionIdle", "No capture session is running.");

    [ObservableProperty]
    public partial string AttachProcessId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial GalGameEntry? SelectedGame { get; set; }

    [ObservableProperty]
    public partial int SelectedSectionIndex { get; set; }

    [ObservableProperty]
    public partial string LibrarySearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int LibrarySortIndex { get; set; }

    [ObservableProperty]
    public partial int LibraryStatusFilterIndex { get; set; }

    [ObservableProperty]
    public partial bool IsPolling { get; set; }

    [ObservableProperty]
    public partial ulong? SelectedThreadId { get; set; }

    [ObservableProperty]
    public partial string DiagnosticSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DiagnosticIdentity { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DiagnosticCounters { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DiagnosticFlags { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string VoiceStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OverlayFontFamily { get; set; } = "Yu Gothic UI";

    [ObservableProperty]
    public partial double OverlayFontSize { get; set; } = 30;

    [ObservableProperty]
    public partial double OverlayLetterSpacing { get; set; }

    [ObservableProperty]
    public partial double OverlayLineHeight { get; set; } = 1;

    [ObservableProperty]
    public partial bool OverlayBold { get; set; } = true;

    [ObservableProperty]
    public partial int OverlayHorizontalAlignmentIndex { get; set; }

    [ObservableProperty]
    public partial int OverlayVerticalAlignmentIndex { get; set; }

    [ObservableProperty]
    public partial string OverlayTextColor { get; set; } = "#FFFFFFFF";

    [ObservableProperty]
    public partial string OverlayBackgroundColor { get; set; } = "#FF000000";

    [ObservableProperty]
    public partial double OverlayBackgroundOpacity { get; set; }

    [ObservableProperty]
    public partial string OverlayOutlineColor { get; set; } = "#E0000000";

    [ObservableProperty]
    public partial double OverlayOutlineWidth { get; set; } = 1.6;

    [ObservableProperty]
    public partial double OverlayPadding { get; set; } = 20;

    [ObservableProperty]
    public partial double OverlayCornerRadius { get; set; } = 14;

    public ObservableCollection<GalGameEntry> Games { get; } = [];
    public ObservableCollection<GalGameEntry> VisibleGames { get; } = [];
    public ObservableCollection<GalGameTextLine> CapturedLines { get; } = [];
    public ObservableCollection<GalGameThreadPreview> ThreadPreviews { get; } = [];
    public ObservableCollection<GalGameDiagnosticDisplayItem> Diagnostics { get; } = [];
    public ObservableCollection<GalGameDiagnosticEventDisplayItem> DiagnosticEvents { get; } = [];

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool CanStop => _session.State.IsActive || _session.State.Phase == GalHookSessionPhase.Error;
    public bool IsCaptureActive => _session.State.IsActive;
    public bool HasLibraryGames => Games.Count > 0;
    public bool HasVisibleGames => VisibleGames.Count > 0;

    public IReadOnlyList<string> LibrarySortOptions { get; } =
    [
        ResourceStringHelper.GetString("GamesSortName", "Name"),
        ResourceStringHelper.GetString("GamesSortAdded", "Recently added"),
        ResourceStringHelper.GetString("GamesSortPlayed", "Recently played"),
        ResourceStringHelper.GetString("GamesSortStatus", "Play status"),
        ResourceStringHelper.GetString("GamesSortDuration", "Play time"),
    ];

    public IReadOnlyList<string> LibraryStatusFilterOptions { get; } =
    [
        ResourceStringHelper.GetString("GamesStatusAll", "All statuses"),
        ResourceStringHelper.GetString("GamesStatusWant", "Want to play"),
        ResourceStringHelper.GetString("GamesStatusPlaying", "Playing"),
        ResourceStringHelper.GetString("GamesStatusPlayed", "Played"),
        ResourceStringHelper.GetString("GamesStatusOnHold", "On hold"),
        ResourceStringHelper.GetString("GamesStatusDropped", "Dropped"),
        ResourceStringHelper.GetString("GamesStatusUnset", "Not set"),
    ];

    public IReadOnlyList<string> OverlayHorizontalAlignmentOptions { get; } =
    [
        ResourceStringHelper.GetString("GamesOverlayAlignCenter", "Center"),
        ResourceStringHelper.GetString("GamesOverlayAlignLeft", "Left"),
    ];

    public IReadOnlyList<string> OverlayVerticalAlignmentOptions { get; } =
    [
        ResourceStringHelper.GetString("GamesOverlayAlignMiddle", "Middle"),
        ResourceStringHelper.GetString("GamesOverlayAlignTop", "Top"),
    ];

    public GamesPageViewModel(
        IGalGameRepository repository,
        IGalGameSessionService session,
        GalGameMediaCapture mediaCapture,
        GalGameIngameLookupController ingameLookup,
        ISettingsService settings)
    {
        _repository = repository;
        _session = session;
        _mediaCapture = mediaCapture;
        _ingameLookup = ingameLookup;
        _settings = settings;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _session.StateChanged += Session_StateChanged;
        LoadOverlayAppearance(_settings.Current.GalGameSettings?.OverlayAppearance);
        ApplySessionState(_session.State);
    }

    public async Task InitializeAsync()
    {
        if (_initialized || _disposed)
            return;
        _initialized = true;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_disposed)
            return;
        try
        {
            var result = await _repository.LoadAsync();
            Games.Clear();
            foreach (var game in result.Games.OrderBy(game => game.SortOrder).ThenBy(game => game.DisplayName))
                Games.Add(game);
            RefreshLibraryView();
            ErrorMessage = result.Error;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task AddGameAsync(string? path)
    {
        if (_disposed || IsBusy)
            return;
        path = path?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path)
            || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
        {
            ErrorMessage = ResourceStringHelper.GetString(
                "GamesInvalidExecutable",
                "Choose an existing .exe file.");
            return;
        }

        if (GalGameLibraryFunctions.FilterNewExes(Games, [path]).Count == 0)
        {
            ErrorMessage = ResourceStringHelper.GetString(
                "GamesDuplicateExecutable",
                "This executable is already in the game library.");
            return;
        }

        try
        {
            IsBusy = true;
            ErrorMessage = null;
            await ImportPathsAsync([path]);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<int> ImportPathsAsync(IEnumerable<string> paths)
    {
        var candidates = GalGameLibraryFunctions.FilterNewExes(
                Games,
                paths.Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => path.Trim().Trim('"')))
            .Where(File.Exists)
            .ToArray();
        var imported = 0;
        foreach (var path in candidates)
        {
            var entry = GalGameLibraryFunctions.NewFromExe(path);
            await _repository.AddAsync(entry);
            Games.Add(entry);
            imported++;
        }

        if (imported > 0)
        {
            GamePath = string.Empty;
            SelectedGame = Games.LastOrDefault();
            RefreshLibraryView();
            OnPropertyChanged(nameof(HasLibraryGames));
        }
        else if (candidates.Length == 0)
        {
            ErrorMessage = ResourceStringHelper.GetString(
                "GamesNoNewExecutables",
                "No new .exe files were found.");
        }
        return imported;
    }

    [RelayCommand]
    private async Task LaunchAsync(GalGameEntry? game)
    {
        if (_disposed || IsBusy || game is null)
            return;
        try
        {
            IsBusy = true;
            ErrorMessage = null;
            var result = await _session.LaunchAsync(game);
            if (!result.Success)
                ErrorMessage = result.Detail;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanStop));
        }
    }

    [RelayCommand]
    private async Task AttachAsync()
    {
        if (_disposed || IsBusy || !int.TryParse(AttachProcessId, out var pid) || pid <= 0)
        {
            ErrorMessage = ResourceStringHelper.GetString(
                "GamesInvalidProcessId",
                "Enter a valid running process id.");
            return;
        }

        try
        {
            IsBusy = true;
            ErrorMessage = null;
            var result = await _session.AttachAsync(pid);
            if (!result.Success)
                ErrorMessage = result.Detail;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanStop));
        }
    }

    public Task AttachToProcessAsync(int processId)
    {
        AttachProcessId = processId.ToString();
        return AttachAsync();
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        await StopCaptureAsync();
    }

    public async Task StopCaptureAsync()
    {
        if (_disposed)
            return;

        await _ingameLookup.StopAsync();
        await _session.StopAsync();
        ClearCapturedLines();
        OnPropertyChanged(nameof(CanStop));
    }

    [RelayCommand]
    private void ClearCapture()
    {
        ClearCapturedLines();
    }

    public void ClearCapturedLines()
    {
        CapturedLines.Clear();
        ThreadPreviews.Clear();
        _threadPreviewHistory.Clear();
        _discoveredThreads.Clear();
        SelectedThreadId = null;
        UpdateDiagnostics();
    }

    public async Task PollCaptureAsync(CancellationToken ct = default)
    {
        if (_disposed || !_session.State.IsActive || !_pollGate.Wait(0))
            return;

        try
        {
            IsPolling = true;
            var linesTask = Task.Run(_session.PollText, ct);
            var previewsTask = Task.Run(_session.ReadThreadPreviews, ct);
            await Task.WhenAll(linesTask, previewsTask);

            foreach (var line in linesTask.Result)
            {
                if (line.IsThreadDiscovered)
                {
                    _discoveredThreads[line.ThreadId] = new GalGameThreadPreview
                    {
                        ThreadId = line.ThreadId,
                        Sequence = line.Sequence,
                        TimestampMs = line.TimestampMs,
                        LineCount = 0,
                        ArtifactCount = 0,
                        EventFlags = 0,
                        Text = string.Join(" · ", new[] { line.HookName, line.HookCode }
                            .Where(value => !string.IsNullOrWhiteSpace(value))),
                    };
                    continue;
                }
                if (CapturedLines.Any(existing => existing.Id == line.Id))
                    continue;
                CapturedLines.Add(line);
            }

            while (CapturedLines.Count > 250)
                CapturedLines.RemoveAt(0);

            var previews = previewsTask.Result.ToList();
            foreach (var discovered in _discoveredThreads.Values)
            {
                if (previews.All(preview => preview.ThreadId != discovered.ThreadId))
                    previews.Add(discovered);
            }
            foreach (var fallback in BuildFallbackThreadPreviews(CapturedLines))
            {
                if (previews.All(preview => preview.ThreadId != fallback.ThreadId))
                    previews.Add(fallback);
            }

            var liveThreadIds = previews.Select(preview => preview.ThreadId).ToHashSet();
            foreach (var staleId in _threadPreviewHistory.Keys
                         .Where(id => !liveThreadIds.Contains(id))
                         .ToArray())
            {
                _threadPreviewHistory.Remove(staleId);
            }
            foreach (var preview in previews)
            {
                var text = preview.Text.Trim();
                if (!_threadPreviewHistory.TryGetValue(preview.ThreadId, out var history))
                {
                    history = [];
                    _threadPreviewHistory[preview.ThreadId] = history;
                }
                if (text.Length > 0
                    && (history.Count == 0
                        || !string.Equals(history[0], text, StringComparison.Ordinal)))
                {
                    history.Insert(0, text);
                    if (history.Count > 3)
                        history.RemoveRange(3, history.Count - 3);
                }
                preview.PreviewText = string.Join(Environment.NewLine, history);
            }

            // Match Fushi/Luna's selection ergonomics: useful, clean, recent
            // lanes appear before empty or redraw-artifact lanes. Every native
            // preview remains available; this is ordering, not filtering.
            var orderedPreviews = previews
                .OrderByDescending(preview => preview.LineCount > 0)
                .ThenBy(preview => preview.IsArtifact)
                .ThenByDescending(preview => preview.LineCount)
                .ThenByDescending(preview => preview.TimestampMs)
                .ThenBy(preview => preview.ThreadId)
                .ToList();
            var samePreviews = ThreadPreviews.Count == orderedPreviews.Count
                && ThreadPreviews.Zip(orderedPreviews).All(pair =>
                    pair.First.ThreadId == pair.Second.ThreadId
                    && pair.First.Sequence == pair.Second.Sequence
                    && pair.First.LineCount == pair.Second.LineCount
                    && string.Equals(pair.First.PreviewText, pair.Second.PreviewText, StringComparison.Ordinal)
                    && string.Equals(pair.First.Text, pair.Second.Text, StringComparison.Ordinal));
            if (!samePreviews)
            {
                ThreadPreviews.Clear();
                foreach (var preview in orderedPreviews)
                    ThreadPreviews.Add(preview);
            }

            await _ingameLookup.PollAsync(CreateDeferredMiningContext, ct);
            UpdateDiagnostics();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            IsPolling = false;
            _pollGate.Release();
        }
    }

    [RelayCommand]
    private async Task SelectThreadAsync(GalGameThreadPreview? preview)
    {
        if (preview is null || _disposed)
            return;
        var selected = await Task.Run(() => _session.SelectTextThread(preview.ThreadId));
        if (!selected)
            ErrorMessage = "The capture channel is no longer available.";
        else
        {
            SelectedThreadId = preview.ThreadId;
            // The session resets its IPC cursor, so the next poll repopulates
            // this list with the selected hook lane's recent text only.
            CapturedLines.Clear();
            UpdateDiagnostics();
        }
    }

    public Task SelectThreadFromOverlayAsync(GalGameThreadPreview preview) =>
        SelectThreadAsync(preview);

    public Task<AnkiMiningContext?> CreateMiningContextAsync(
        GalGameTextLine line,
        CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult(CreateDeferredMiningContext(line));
    }

    /// <summary>
    /// Creates the game mining context without making the lookup click wait for
    /// PrintWindow or the hook's resource-audio pairing window. The popup keeps
    /// this context instance and asks the provider for media only when the user
    /// actually mines an Anki card.
    /// </summary>
    public AnkiMiningContext? CreateDeferredMiningContext(GalGameTextLine line)
    {
        if (_disposed || line is null || _session.State.GamePid is not > 0)
            return null;

        var pid = _session.State.GamePid.Value;
        var mediaTask = new Lazy<Task<GalGameMiningMedia>>(
            () => CaptureMiningMediaAsync(pid, line),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var context = new AnkiMiningContext
        {
            Sentence = line.Text,
            SentenceOffset = 0,
            DocumentTitle = SelectedGame?.DisplayName ?? _session.State.LaunchExe,
            // Mark this as a media-backed game context so the existing Anki
            // video-media provider path can defer both screenshot and voice.
            VideoFileName = SelectedGame?.DisplayName ?? _session.State.LaunchExe,
            VideoSubtitle = line.Text,
            // Fushi's default/full policy treats a genuinely unvoiced line as
            // a valid screenshot-and-text card. Missing voice is evidence to
            // surface, not a reason to abort the entire mining job.
            AllowMissingVideoAudio = true,
        };

        context.VideoMediaProvider = async (request, providerCt) =>
        {
            try
            {
                var media = await mediaTask.Value.WaitAsync(providerCt);
                return new VideoMiningMediaResult(
                    request.CaptureScreenshot ? media.ScreenshotPath : null,
                    request.CaptureAudioClip ? media.AudioPath : null,
                    ScreenshotErrorMessage: request.CaptureScreenshot
                        && string.IsNullOrWhiteSpace(media.ScreenshotPath)
                        ? "Unable to capture the game window."
                        : null,
                    AudioClipErrorMessage: request.CaptureAudioClip
                        && string.IsNullOrWhiteSpace(media.AudioPath)
                        ? "No voice clip was paired with this line."
                        : null);
            }
            catch (OperationCanceledException) when (providerCt.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new VideoMiningMediaResult(
                    ScreenshotErrorMessage: request.CaptureScreenshot ? ex.Message : null,
                    AudioClipErrorMessage: request.CaptureAudioClip ? ex.Message : null);
            }
        };

        return context;
    }

    private async Task<GalGameMiningMedia> CaptureMiningMediaAsync(
        int processId,
        GalGameTextLine line)
    {
        return await _mediaCapture.PrepareAsync(
            processId,
            line,
            async ct =>
            {
                try
                {
                    const int postRollMs = 4000;
                    var target = line.TimestampMs + postRollMs;
                    var now = (ulong)Math.Max(0, Environment.TickCount64);
                    if (target > now)
                        await Task.Delay(TimeSpan.FromMilliseconds(target - now), ct);
                    return await Task.Run(() => _session.CaptureAudio(line), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Screenshot-only cards remain valid when no voice exists.
                    return null;
                }
            },
            CancellationToken.None);
    }

    [RelayCommand]
    private async Task RemoveAsync(GalGameEntry? game)
    {
        if (_disposed || IsBusy || game is null)
            return;
        try
        {
            await _repository.RemoveAsync(game.Id);
            Games.Remove(game);
            RefreshLibraryView();
            OnPropertyChanged(nameof(HasLibraryGames));
            if (ReferenceEquals(SelectedGame, game))
                SelectedGame = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void ResetOverlayAppearance()
    {
        LoadOverlayAppearance(new GalGameOverlayAppearanceSettings());
        PersistOverlayAppearance();
    }

    partial void OnLibrarySearchTextChanged(string value) => RefreshLibraryView();
    partial void OnLibrarySortIndexChanged(int value) => RefreshLibraryView();
    partial void OnLibraryStatusFilterIndexChanged(int value) => RefreshLibraryView();

    partial void OnOverlayFontFamilyChanged(string value) => PersistOverlayAppearance();
    partial void OnOverlayFontSizeChanged(double value) => PersistOverlayAppearance();
    partial void OnOverlayLetterSpacingChanged(double value) => PersistOverlayAppearance();
    partial void OnOverlayLineHeightChanged(double value) => PersistOverlayAppearance();
    partial void OnOverlayBoldChanged(bool value) => PersistOverlayAppearance();
    partial void OnOverlayHorizontalAlignmentIndexChanged(int value) => PersistOverlayAppearance();
    partial void OnOverlayVerticalAlignmentIndexChanged(int value) => PersistOverlayAppearance();
    partial void OnOverlayTextColorChanged(string value) => PersistOverlayAppearance();
    partial void OnOverlayBackgroundColorChanged(string value) => PersistOverlayAppearance();
    partial void OnOverlayBackgroundOpacityChanged(double value) => PersistOverlayAppearance();
    partial void OnOverlayOutlineColorChanged(string value) => PersistOverlayAppearance();
    partial void OnOverlayOutlineWidthChanged(double value) => PersistOverlayAppearance();
    partial void OnOverlayPaddingChanged(double value) => PersistOverlayAppearance();
    partial void OnOverlayCornerRadiusChanged(double value) => PersistOverlayAppearance();

    private void RefreshLibraryView()
    {
        IEnumerable<GalGameEntry> query = Games;
        var search = LibrarySearchText?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(game =>
                game.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || game.ExePath.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var status = LibraryStatusFilterIndex switch
        {
            1 => GalGamePlayStatus.WantToPlay,
            2 => GalGamePlayStatus.Playing,
            3 => GalGamePlayStatus.Played,
            4 => GalGamePlayStatus.OnHold,
            5 => GalGamePlayStatus.Dropped,
            6 => GalGamePlayStatus.Unset,
            _ => (GalGamePlayStatus?)null,
        };
        if (status is not null)
            query = query.Where(game => game.PlayStatus == status.Value);

        query = LibrarySortIndex switch
        {
            1 => query.OrderByDescending(game => game.AddedAt).ThenBy(game => game.DisplayName),
            2 => query.OrderByDescending(game => game.LastPlayedMs).ThenBy(game => game.DisplayName),
            3 => query.OrderBy(game => game.PlayStatus).ThenBy(game => game.DisplayName),
            4 => query.OrderByDescending(game => game.TotalPlaySeconds).ThenBy(game => game.DisplayName),
            _ => query.OrderBy(game => game.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        };

        VisibleGames.Clear();
        foreach (var game in query)
            VisibleGames.Add(game);
        OnPropertyChanged(nameof(HasVisibleGames));
    }

    private void LoadOverlayAppearance(GalGameOverlayAppearanceSettings? source)
    {
        var value = (source ?? new GalGameOverlayAppearanceSettings()).Normalize();
        _loadingAppearance = true;
        try
        {
            OverlayFontFamily = value.FontFamily;
            OverlayFontSize = value.FontSize;
            OverlayLetterSpacing = value.LetterSpacing;
            OverlayLineHeight = value.LineHeight;
            OverlayBold = value.Bold;
            OverlayHorizontalAlignmentIndex = value.HorizontalAlignment ==
                GalGameOverlayHorizontalAlignment.Left ? 1 : 0;
            OverlayVerticalAlignmentIndex = value.VerticalAlignment ==
                GalGameOverlayVerticalAlignment.Top ? 1 : 0;
            OverlayTextColor = value.TextColor;
            OverlayBackgroundColor = value.BackgroundColor;
            OverlayBackgroundOpacity = value.BackgroundOpacity;
            OverlayOutlineColor = value.OutlineColor;
            OverlayOutlineWidth = value.OutlineWidth;
            OverlayPadding = value.Padding;
            OverlayCornerRadius = value.CornerRadius;
        }
        finally
        {
            _loadingAppearance = false;
        }
    }

    private void PersistOverlayAppearance()
    {
        if (_loadingAppearance || _disposed)
            return;
        var appearance = new GalGameOverlayAppearanceSettings
        {
            FontFamily = OverlayFontFamily,
            FontSize = OverlayFontSize,
            LetterSpacing = OverlayLetterSpacing,
            LineHeight = OverlayLineHeight,
            Bold = OverlayBold,
            HorizontalAlignment = OverlayHorizontalAlignmentIndex == 1
                ? GalGameOverlayHorizontalAlignment.Left
                : GalGameOverlayHorizontalAlignment.Center,
            VerticalAlignment = OverlayVerticalAlignmentIndex == 1
                ? GalGameOverlayVerticalAlignment.Top
                : GalGameOverlayVerticalAlignment.Center,
            TextColor = OverlayTextColor,
            BackgroundColor = OverlayBackgroundColor,
            BackgroundOpacity = OverlayBackgroundOpacity,
            OutlineColor = OverlayOutlineColor,
            OutlineWidth = OverlayOutlineWidth,
            Padding = OverlayPadding,
            CornerRadius = OverlayCornerRadius,
        }.Normalize();
        _settings.Set(settings => settings.GalGameSettings, new GalGameSettings
        {
            OverlayAppearance = appearance,
        });
        _appearanceSaveCts?.Cancel();
        _appearanceSaveCts?.Dispose();
        _appearanceSaveCts = new CancellationTokenSource();
        _ = SaveOverlayAppearanceAsync(_appearanceSaveCts.Token);
    }

    private async Task SaveOverlayAppearanceAsync(CancellationToken ct)
    {
        try
        {
            // Sliders update the live overlay immediately. Debounce only the
            // persistent write so dragging a thumb does not enqueue one JSON
            // save for every pointer-move event.
            await Task.Delay(250, ct);
            ct.ThrowIfCancellationRequested();
            await _settings.SaveAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _appearanceSaveCts?.Cancel();
        _appearanceSaveCts?.Dispose();
        _appearanceSaveCts = null;
        _session.StateChanged -= Session_StateChanged;
        _ingameLookup.Dispose();
        _pollGate.Dispose();
    }

    private void Session_StateChanged(object? sender, GalHookSessionState state)
    {
        _ = sender;
        if (_disposed)
            return;

        if (_dispatcherQueue is { HasThreadAccess: false } dispatcher)
        {
            dispatcher.TryEnqueue(() =>
            {
                if (!_disposed)
                    ApplySessionState(state);
            });
            return;
        }

        ApplySessionState(state);
    }

    private void ApplySessionState(GalHookSessionState state)
    {
        var phase = state.Phase switch
        {
            GalHookSessionPhase.Idle => ResourceStringHelper.GetString("GamesSessionIdle", "No capture session is running."),
            GalHookSessionPhase.Resolving => ResourceStringHelper.GetString("GamesSessionResolving", "Resolving game and helper runtime…"),
            GalHookSessionPhase.Launching => ResourceStringHelper.GetString("GamesSessionLaunching", "Launching game…"),
            GalHookSessionPhase.Attaching => ResourceStringHelper.GetString("GamesSessionAttaching", "Attaching to the running game…"),
            GalHookSessionPhase.Injecting => ResourceStringHelper.GetString("GamesSessionInjecting", "Injecting the isolated voice helper…"),
            GalHookSessionPhase.OpeningIpc => ResourceStringHelper.GetString("GamesSessionOpeningIpc", "Opening the read-only capture channel…"),
            GalHookSessionPhase.WaitingSignals => ResourceStringHelper.GetString("GamesSessionWaiting", "Waiting for text and voice signals…"),
            GalHookSessionPhase.Running => ResourceStringHelper.GetString("GamesSessionRunning", "Capture session is running."),
            GalHookSessionPhase.Degraded => ResourceStringHelper.GetString("GamesSessionDegraded", "Game is running, but hook signals are incomplete."),
            GalHookSessionPhase.Stopping => ResourceStringHelper.GetString("GamesSessionStopping", "Stopping capture…"),
            GalHookSessionPhase.Error => state.LastError ?? ResourceStringHelper.GetString("GamesSessionError", "Capture session failed."),
            _ => state.Phase.ToString(),
        };
        SessionStatusText = phase;
        AddDiagnosticEvent(state, phase);
        UpdateDiagnostics(state);
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(IsCaptureActive));
    }

    private void UpdateDiagnostics(GalHookSessionState? state = null)
    {
        state ??= _session.State;
        VoiceStatusText = state.Ipc switch
        {
            null => ResourceStringHelper.GetString(
                "GamesVoiceWaiting",
                "Waiting for a live session"),
            { } voiceIpc when voiceIpc.ClipWriteCount > 0
                || voiceIpc.TotalWritten > 0
                || voiceIpc.HasLoopbackAudio
                || GalHookDiagnosticBits.HasResourceEvidence(
                    voiceIpc.HookDiagnostics,
                    voiceIpc.ReservedLunaDiagnostics,
                    voiceIpc.ReservedHookDiagnostics,
                    voiceIpc.XAudioDiagnostics) => ResourceStringHelper.GetString(
                        "GamesVoiceObserved",
                        "Voice observed"),
            _ => ResourceStringHelper.GetString(
                "GamesVoiceNotObserved",
                "No sentence audio observed"),
        };
        var evaluated = GalHookDiagnosticFunctions.Evaluate(
            state,
            CapturedLines.Count,
            SelectedThreadId);

        Diagnostics.Clear();
        foreach (var result in evaluated)
        {
            Diagnostics.Add(new GalGameDiagnosticDisplayItem
            {
                Boundary = result.Boundary,
                Label = DiagnosticBoundaryLabel(result.Boundary),
                Status = DiagnosticOutcomeLabel(result.Outcome),
                Evidence = DiagnosticEvidence(result),
                Glyph = result.Outcome switch
                {
                    GalHookDiagnosticOutcome.Passed => "\uE73E",
                    GalHookDiagnosticOutcome.Failed => "\uEA39",
                    GalHookDiagnosticOutcome.Pending => "\uE823",
                    GalHookDiagnosticOutcome.NotApplicable => "\uE73A",
                    GalHookDiagnosticOutcome.Unavailable => "\uE946",
                    _ => "\uE73C",
                },
            });
        }

        var firstOpen = evaluated.FirstOrDefault(result => result.Outcome is
            GalHookDiagnosticOutcome.Pending
            or GalHookDiagnosticOutcome.Failed
            or GalHookDiagnosticOutcome.Unavailable);
        DiagnosticSummary = firstOpen is null
            ? ResourceStringHelper.GetString(
                "GamesDiagnosticsAllObserved",
                "All observable runtime gates passed.")
            : string.Format(
                ResourceStringHelper.GetString(
                    "GamesDiagnosticsNextGateFormat",
                    "Next gate: {0}."),
                DiagnosticBoundaryLabel(firstOpen.Boundary));

        var ipc = state.Ipc;
        var exeName = Path.GetFileName(SelectedGame?.ExePath ?? state.LaunchExe) ?? string.Empty;
        DiagnosticIdentity = string.Join(" · ", new[]
        {
            string.IsNullOrWhiteSpace(exeName)
                ? ResourceStringHelper.GetString("GamesDiagnosticsNoTarget", "No target selected")
                : exeName,
            state.GamePid is > 0 ? $"PID {state.GamePid}" : null,
            state.Architecture,
            string.IsNullOrWhiteSpace(state.InjectorPath)
                ? null
                : Path.GetFileName(state.InjectorPath),
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        DiagnosticCounters = ipc is null
            ? ResourceStringHelper.GetString(
                "GamesDiagnosticsNoSnapshot",
                "No compatible IPC snapshot has been observed.")
            : string.Format(
                ResourceStringHelper.GetString(
                    "GamesDiagnosticsCountersFormat",
                    "HVH1 v{0} / IPC v{1} · text={2} · clips={3} · PCM={4} B"),
                ipc.Version,
                ipc.IpcProtocolVersion,
                ipc.TextWriteCount,
                ipc.ClipWriteCount,
                ipc.TotalWritten);
        IReadOnlyList<string> flags = ipc is null
            ? Array.Empty<string>()
            : GalHookDiagnosticBits.Explain(
                ipc.HookDiagnostics,
                ipc.ReservedLunaDiagnostics,
                ipc.ReservedHookDiagnostics,
                ipc.XAudioDiagnostics,
                ipc.XAudioDiagnostics2);
        DiagnosticFlags = ipc is null
            ? "hookdiag=not_observed"
            : $"hookdiag=0x{ipc.HookDiagnostics:x8} · luna=0x{ipc.ReservedLunaDiagnostics:x8}"
                + $" · hookio=0x{ipc.ReservedHookDiagnostics:x8}"
                + $" · xaudio=0x{ipc.XAudioDiagnostics:x8}"
                + $" · xaudio2=0x{ipc.XAudioDiagnostics2:x8}"
                + (flags.Count == 0 ? string.Empty : $"\n{string.Join(" · ", flags)}");
    }

    private void AddDiagnosticEvent(GalHookSessionState state, string phase)
    {
        var key = $"{state.Phase}|{state.GamePid}|{state.LastError}|{state.Detail}";
        if (string.Equals(_lastDiagnosticEventKey, key, StringComparison.Ordinal))
            return;
        _lastDiagnosticEventKey = key;
        DiagnosticEvents.Insert(0, new GalGameDiagnosticEventDisplayItem
        {
            Time = DateTimeOffset.Now.ToString("HH:mm:ss"),
            Phase = phase,
            Detail = state.LastError ?? state.Detail ?? string.Empty,
        });
        while (DiagnosticEvents.Count > 50)
            DiagnosticEvents.RemoveAt(DiagnosticEvents.Count - 1);
    }

    private static string DiagnosticBoundaryLabel(GalHookDiagnosticBoundary boundary) => boundary switch
    {
        GalHookDiagnosticBoundary.ProcessFound => ResourceStringHelper.GetString("GamesDiagProcess", "Target process"),
        GalHookDiagnosticBoundary.HelperReady => ResourceStringHelper.GetString("GamesDiagHelper", "Helper ready"),
        GalHookDiagnosticBoundary.IpcReady => ResourceStringHelper.GetString("GamesDiagIpc", "IPC compatible"),
        GalHookDiagnosticBoundary.TextObserved => ResourceStringHelper.GetString("GamesDiagText", "Text observed"),
        GalHookDiagnosticBoundary.TextThreadSelected => ResourceStringHelper.GetString("GamesDiagThread", "Text thread selected"),
        GalHookDiagnosticBoundary.ResourceObserved => ResourceStringHelper.GetString("GamesDiagResource", "Voice resource observed"),
        GalHookDiagnosticBoundary.PcmObserved => ResourceStringHelper.GetString("GamesDiagPcm", "PCM observed"),
        GalHookDiagnosticBoundary.Paired => ResourceStringHelper.GetString("GamesDiagPaired", "Text and voice paired"),
        GalHookDiagnosticBoundary.LoopbackObserved => ResourceStringHelper.GetString("GamesDiagLoopback", "Loopback fallback observed"),
        GalHookDiagnosticBoundary.CardE2e => ResourceStringHelper.GetString("GamesDiagE2e", "Card end-to-end verified"),
        _ => boundary.ToString(),
    };

    private static string DiagnosticOutcomeLabel(GalHookDiagnosticOutcome outcome) => outcome switch
    {
        GalHookDiagnosticOutcome.Passed => ResourceStringHelper.GetString("GamesDiagPassed", "Observed"),
        GalHookDiagnosticOutcome.Pending => ResourceStringHelper.GetString("GamesDiagPending", "Waiting"),
        GalHookDiagnosticOutcome.Failed => ResourceStringHelper.GetString("GamesDiagFailed", "Failed"),
        GalHookDiagnosticOutcome.NotApplicable => ResourceStringHelper.GetString("GamesDiagNotApplicable", "Not applicable"),
        GalHookDiagnosticOutcome.Unavailable => ResourceStringHelper.GetString("GamesDiagUnavailable", "Not exposed"),
        _ => ResourceStringHelper.GetString("GamesDiagNotRun", "Not run"),
    };

    private static string DiagnosticEvidence(GalHookDiagnosticResult result)
    {
        if (result.Outcome == GalHookDiagnosticOutcome.Passed)
        {
            if (result.Boundary == GalHookDiagnosticBoundary.HelperReady)
                return ResourceStringHelper.GetString(
                    "GamesDiagHelperEvidence",
                    "The helper published a shared capture header.");
            if (result.Boundary == GalHookDiagnosticBoundary.TextObserved
                && ulong.TryParse(result.Evidence, out var textEvents))
            {
                return string.Format(
                    ResourceStringHelper.GetString("GamesDiagTextEvidenceFormat", "{0} text events observed."),
                    textEvents);
            }
            if (result.Boundary == GalHookDiagnosticBoundary.ResourceObserved)
                return ResourceStringHelper.GetString(
                    "GamesDiagResourceEvidence",
                    "A symbolic resource or engine clip flag was observed.");
            if (result.Boundary == GalHookDiagnosticBoundary.PcmObserved)
            {
                var values = result.Evidence.Split('|');
                if (values.Length == 2)
                {
                    return string.Format(
                        ResourceStringHelper.GetString("GamesDiagPcmEvidenceFormat", "{0} PCM bytes · {1} clips"),
                        values[0],
                        values[1]);
                }
            }
        }

        if (result.Boundary == GalHookDiagnosticBoundary.ResourceObserved
            && result.Outcome == GalHookDiagnosticOutcome.NotApplicable)
        {
            return ResourceStringHelper.GetString(
                "GamesDiagResourceNotApplicableEvidence",
                "PCM was observed without a resource-level flag.");
        }
        if (result.Boundary == GalHookDiagnosticBoundary.Paired
            && result.Outcome == GalHookDiagnosticOutcome.Unavailable)
        {
            return ResourceStringHelper.GetString(
                "GamesDiagPairUnavailableEvidence",
                "The current IPC does not expose a stable text/audio pair event.");
        }
        if (!string.IsNullOrWhiteSpace(result.Evidence))
            return result.Evidence;
        return result.Outcome switch
        {
            GalHookDiagnosticOutcome.Pending => ResourceStringHelper.GetString(
                "GamesDiagWaitingEvidence",
                "No direct evidence has been observed yet."),
            GalHookDiagnosticOutcome.Failed => ResourceStringHelper.GetString(
                "GamesDiagFailedEvidence",
                "The session stopped at this boundary."),
            GalHookDiagnosticOutcome.Unavailable => ResourceStringHelper.GetString(
                "GamesDiagUnavailableEvidence",
                "The current IPC contract does not expose this proof."),
            _ => string.Empty,
        };
    }

    private static IReadOnlyList<GalGameThreadPreview> BuildFallbackThreadPreviews(
        IEnumerable<GalGameTextLine> lines)
    {
        return lines
            .Where(line => line.HasText)
            .GroupBy(line => line.ThreadId)
            .Select(group =>
            {
                var latest = group.OrderByDescending(line => line.Sequence).First();
                return new GalGameThreadPreview
                {
                    ThreadId = group.Key,
                    Sequence = latest.Sequence,
                    TimestampMs = latest.TimestampMs,
                    LineCount = (ulong)group.LongCount(),
                    ArtifactCount = 0,
                    EventFlags = 0,
                    Text = latest.Text,
                };
            })
            .ToList();
    }
}

public sealed record GalGameDiagnosticDisplayItem
{
    public required GalHookDiagnosticBoundary Boundary { get; init; }
    public required string Label { get; init; }
    public required string Status { get; init; }
    public required string Evidence { get; init; }
    public required string Glyph { get; init; }
}

public sealed record GalGameDiagnosticEventDisplayItem
{
    public required string Time { get; init; }
    public required string Phase { get; init; }
    public required string Detail { get; init; }
}
