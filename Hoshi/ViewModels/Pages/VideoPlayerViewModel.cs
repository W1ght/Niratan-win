using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Hoshi.Models;
using Hoshi.Models.Anki;
using Hoshi.Models.Dictionary;
using Hoshi.Models.Settings;
using Hoshi.Services.Dictionary;
using Hoshi.Services.Settings;
using Hoshi.Services.Video;

namespace Hoshi.ViewModels.Pages;

public partial class VideoPlayerViewModel : ObservableObject
{
    private readonly SubtitleParserService _subtitleParserService;
    private readonly IDictionaryPopupRequestService _popupRequestService;

    private VideoSubtitleDocument _subtitleDocument = new([]);
    private VideoSubtitleCue? _embeddedSubtitleCue;
    private IReadOnlyList<VideoSubtitleCue> _currentExternalCues = [];
    private IReadOnlyList<VideoSubtitleCue> _currentEmbeddedTranscriptCues = [];
    private int? _lastSelectedSubtitleTrackId;
    private string? _primarySubtitlePath;
    private bool _subtitleSelectionOff;
    private bool _hasCompleteEmbeddedTranscript;
    private readonly VideoTranscriptWindow _transcriptWindow = new();
    private int _lastTranscriptCurrentIndex = -1;
    private IReadOnlyList<VideoChapter> _chapters = [];

    [ObservableProperty]
    public partial string Title { get; set; } = "Video";

    [ObservableProperty]
    public partial string CurrentSubtitleText { get; set; } = "";

    [ObservableProperty]
    public partial string PrimarySubtitleName { get; set; } = "";

    [ObservableProperty]
    public partial string EmbeddedSubtitleName { get; set; } = "";

    [ObservableProperty]
    public partial string PreviousSubtitleText { get; set; } = "";

    [ObservableProperty]
    public partial string NextSubtitleText { get; set; } = "";

    [ObservableProperty]
    public partial string PositionText { get; set; } = "00:00:00.000";

    [ObservableProperty]
    public partial string DurationText { get; set; } = "00:00:00.000";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    [ObservableProperty]
    public partial double ProgressMaximum { get; set; } = 1;

    [ObservableProperty]
    public partial bool HasCurrentSubtitle { get; set; }

    [ObservableProperty]
    public partial bool AreSubtitlesVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool HasSubtitleTracks { get; set; }

    [ObservableProperty]
    public partial bool HasAudioTracks { get; set; }

    [ObservableProperty]
    public partial bool HasVideoTracks { get; set; }

    [ObservableProperty]
    public partial bool HasTranscriptRows { get; set; }

    [ObservableProperty]
    public partial bool IsTranscriptLoading { get; set; }

    [ObservableProperty]
    public partial string TranscriptErrorMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool HasEpisodes { get; set; }

    [ObservableProperty]
    public partial bool HasChapters { get; set; }

    [ObservableProperty]
    public partial bool HasMiningHistory { get; set; }

    [ObservableProperty]
    public partial bool AutoPlayNextEpisode { get; set; } = true;

    [ObservableProperty]
    public partial bool RememberPlaybackState { get; set; } = true;

    [ObservableProperty]
    public partial int SeekIntervalSeconds { get; set; } = 5;

    [ObservableProperty]
    public partial int MiningHistoryLimit { get; set; } = 25;

    [ObservableProperty]
    public partial int? SelectedSubtitleTrackId { get; set; }

    [ObservableProperty]
    public partial int? SelectedAudioTrackId { get; set; }

    [ObservableProperty]
    public partial int? SelectedVideoTrackId { get; set; }

    [ObservableProperty]
    public partial double SubtitlePanelHeight { get; set; }

    [ObservableProperty]
    public partial double SubtitleFontSize { get; set; } = 52;

    [ObservableProperty]
    public partial int SubtitleFontWeight { get; set; } = 700;

    [ObservableProperty]
    public partial string SubtitleFontWeightText { get; set; } = "700";

    [ObservableProperty]
    public partial string SubtitleFontFamily { get; set; } = JapaneseFontCatalog.DefaultSubtitleFontFamily;

    [ObservableProperty]
    public partial string SubtitleFontFamilyText { get; set; } = JapaneseFontCatalog.DefaultFont.Name;

    public IReadOnlyList<JapaneseFontOption> AvailableSubtitleFonts { get; } = JapaneseFontCatalog.Fonts;

    [ObservableProperty]
    public partial double SubtitleShadowRadius { get; set; } = 10;

    [ObservableProperty]
    public partial string SubtitleShadowRadiusText { get; set; } = "10.0";

    [ObservableProperty]
    public partial double SubtitleBackgroundOpacity { get; set; }

    [ObservableProperty]
    public partial string SubtitleBackgroundOpacityText { get; set; } = "0%";

    [ObservableProperty]
    public partial bool SubtitleBackgroundDisabled { get; set; } = true;

    [ObservableProperty]
    public partial string SubtitleColorHex { get; set; } = "#FFFFFFFF";

    [ObservableProperty]
    public partial string SubtitleLookupHighlightColorHex { get; set; } = "#3EB5C1CB";

    [ObservableProperty]
    public partial string SubtitleLookupHighlightTextColorHex { get; set; } = "#FFFFFFFF";

    [ObservableProperty]
    public partial double Volume { get; set; } = 100;

    [ObservableProperty]
    public partial double PlaybackSpeed { get; set; } = 1;

    [ObservableProperty]
    public partial string PlaybackSpeedText { get; set; } = "1x";

    [ObservableProperty]
    public partial double AudioDelaySeconds { get; set; }

    [ObservableProperty]
    public partial string AudioDelayText { get; set; } = "+0.0 s";

    [ObservableProperty]
    public partial int SubtitleDelayMilliseconds { get; set; }

    [ObservableProperty]
    public partial string SubtitleDelayText { get; set; } = "+0 ms";

    [ObservableProperty]
    public partial bool LoopFileEnabled { get; set; }

    [ObservableProperty]
    public partial TimeSpan? PendingABLoopStart { get; set; }

    [ObservableProperty]
    public partial VideoABLoop? ABLoop { get; set; }

    [ObservableProperty]
    public partial bool CanSetABLoopEnd { get; set; }

    [ObservableProperty]
    public partial bool CanClearABLoop { get; set; }

    [ObservableProperty]
    public partial string ABLoopText { get; set; } = "Off";

    [ObservableProperty]
    public partial bool HardwareDecodingEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool DeinterlaceEnabled { get; set; }

    [ObservableProperty]
    public partial bool HdrEnhancementEnabled { get; set; }

    [ObservableProperty]
    public partial double VideoBrightness { get; set; }

    [ObservableProperty]
    public partial string VideoBrightnessText { get; set; } = "0";

    [ObservableProperty]
    public partial double VideoContrast { get; set; }

    [ObservableProperty]
    public partial string VideoContrastText { get; set; } = "0";

    [ObservableProperty]
    public partial double VideoSaturation { get; set; }

    [ObservableProperty]
    public partial string VideoSaturationText { get; set; } = "0";

    [ObservableProperty]
    public partial double VideoGamma { get; set; }

    [ObservableProperty]
    public partial string VideoGammaText { get; set; } = "0";

    [ObservableProperty]
    public partial double VideoHue { get; set; }

    [ObservableProperty]
    public partial string VideoHueText { get; set; } = "0";

    [ObservableProperty]
    public partial string AspectRatioValue { get; set; } = "-1";

    [ObservableProperty]
    public partial string AspectRatioText { get; set; } = "Automatic";

    [ObservableProperty]
    public partial int VideoRotationDegrees { get; set; }

    [ObservableProperty]
    public partial string VideoRotationText { get; set; } = "0°";

    [ObservableProperty]
    public partial double SubtitleVerticalPosition { get; set; } = -51;

    [ObservableProperty]
    public partial string SubtitleVerticalPositionText { get; set; } = "-51";

    [ObservableProperty]
    public partial bool SubtitleMaskEnabled { get; set; }

    [ObservableProperty]
    public partial string SubtitleMaskMode { get; set; } = "Blur";

    [ObservableProperty]
    public partial double SubtitleMaskBlurRadius { get; set; } = 10;

    [ObservableProperty]
    public partial string SubtitleMaskBlurRadiusText { get; set; } = "10 px";

    [ObservableProperty]
    public partial double SubtitleMaskHiddenOpacity { get; set; }

    [ObservableProperty]
    public partial string SubtitleMaskHiddenOpacityText { get; set; } = "0%";

    [ObservableProperty]
    public partial string SubtitleFontSizeText { get; set; } = "52 px";

    public VideoItem? CurrentVideo { get; private set; }
    public VideoSubtitleCue? CurrentCue { get; private set; }
    public TimeSpan CurrentPosition { get; private set; }
    public TimeSpan Duration { get; private set; }
    public ObservableCollection<VideoTrackInfo> VideoTracks { get; } = [];
    public ObservableCollection<VideoTrackInfo> AudioTracks { get; } = [];
    public ObservableCollection<VideoTrackInfo> SubtitleTracks { get; } = [];
    public ObservableCollection<VideoTranscriptRow> TranscriptRows { get; } = [];
    public ObservableCollection<VideoTranscriptRow> TranscriptVisibleRows { get; } = [];
    public ObservableCollection<VideoEpisodeRow> EpisodeRows { get; } = [];
    public ObservableCollection<VideoChapterRow> ChapterRows { get; } = [];
    public ObservableCollection<VideoMiningHistoryRow> MiningHistoryRows { get; } = [];
    public bool IsEmbeddedSubtitleActive => SelectedSubtitleTrackId.HasValue
        && string.IsNullOrWhiteSpace(PrimarySubtitleName);
    public bool HasCompleteEmbeddedTranscript => _hasCompleteEmbeddedTranscript;

    public VideoPlayerViewModel(
        SubtitleParserService subtitleParserService,
        IDictionaryPopupRequestService popupRequestService,
        ISettingsService? settingsService = null)
    {
        _subtitleParserService = subtitleParserService;
        _popupRequestService = popupRequestService;
        if (settingsService != null)
            ApplySettings(settingsService.Current.VideoSettings);
    }

    public async Task LoadVideoAsync(VideoItem video, CancellationToken ct = default)
    {
        CurrentVideo = video;
        Title = video.Title;
        ReplaceEpisodes([video], video);
        CurrentCue = null;
        CurrentSubtitleText = "";
        PreviousSubtitleText = "";
        NextSubtitleText = "";
        HasCurrentSubtitle = false;
        SubtitlePanelHeight = 0;
        ProgressValue = 0;
        ProgressMaximum = 1;
        DurationText = "00:00:00.000";
        CurrentPosition = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        _subtitleDocument = new VideoSubtitleDocument([]);
        _currentExternalCues = [];
        _currentEmbeddedTranscriptCues = [];
        PrimarySubtitleName = "";
        _primarySubtitlePath = null;
        EmbeddedSubtitleName = "";
        SelectedSubtitleTrackId = null;
        SelectedAudioTrackId = null;
        SelectedVideoTrackId = null;
        _embeddedSubtitleCue = null;
        _lastSelectedSubtitleTrackId = null;
        _subtitleSelectionOff = false;
        SetHasCompleteEmbeddedTranscript(false);
        ClearABLoop();
        ReplaceTranscriptRows([]);
        ReplaceChapters([]);

        if (!string.IsNullOrWhiteSpace(video.SubtitlePath) && File.Exists(video.SubtitlePath))
        {
            try
            {
                await LoadSubtitleAsync(video.SubtitlePath, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _subtitleDocument = new VideoSubtitleDocument([]);
                _currentExternalCues = [];
                _currentEmbeddedTranscriptCues = [];
                PrimarySubtitleName = "";
                _primarySubtitlePath = null;
                EmbeddedSubtitleName = "";
                SelectedSubtitleTrackId = null;
                _embeddedSubtitleCue = null;
                _lastSelectedSubtitleTrackId = null;
                _subtitleSelectionOff = false;
                SetHasCompleteEmbeddedTranscript(false);
                ReplaceTranscriptRows([]);
                StatusText = $"Failed to load subtitles: {ex.Message}";
            }
        }
        else
        {
            StatusText = "No interactive subtitles loaded";
        }
    }

    public void ReplaceEpisodes(IEnumerable<VideoItem> episodes, VideoItem current)
    {
        var rows = NormalizeEpisodes(episodes, current)
            .Select(video => new VideoEpisodeRow(video, IsSameVideo(video, current)))
            .ToList();

        EpisodeRows.Clear();
        foreach (var row in rows)
            EpisodeRows.Add(row);

        HasEpisodes = EpisodeRows.Count > 0;
    }

    public void ReplaceChapters(IEnumerable<VideoChapter> chapters)
    {
        _chapters = chapters
            .OrderBy(chapter => chapter.StartTime)
            .ThenBy(chapter => chapter.Id)
            .ToList();
        HasChapters = _chapters.Count > 0;
        RefreshChapterRows(CurrentPosition);
    }

    public void ReplaceMiningHistoryItems(IEnumerable<VideoMiningHistoryItem> items)
    {
        MiningHistoryRows.Clear();
        string? previousSource = null;
        foreach (var item in items)
        {
            var showHeader = !string.Equals(
                previousSource,
                item.SubtitleSourceName,
                StringComparison.Ordinal);
            MiningHistoryRows.Add(new VideoMiningHistoryRow(item, showHeader));
            previousSource = item.SubtitleSourceName;
        }

        HasMiningHistory = MiningHistoryRows.Count > 0;
    }

    public async Task LoadSubtitleAsync(string subtitlePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subtitlePath) || !File.Exists(subtitlePath))
            return;

        _subtitleDocument = await _subtitleParserService.ParseFileAsync(subtitlePath, ct);
        _currentExternalCues = [];
        _currentEmbeddedTranscriptCues = [];
        _primarySubtitlePath = subtitlePath;
        PrimarySubtitleName = Path.GetFileName(subtitlePath);
        EmbeddedSubtitleName = "";
        SelectedSubtitleTrackId = null;
        _embeddedSubtitleCue = null;
        _lastSelectedSubtitleTrackId = null;
        _subtitleSelectionOff = false;
        SetHasCompleteEmbeddedTranscript(false);
        AreSubtitlesVisible = true;
        ReplaceTranscriptRows(_subtitleDocument.Cues);
        StatusText = $"{_subtitleDocument.Cues.Count} subtitle cues";
        UpdatePosition(CurrentPosition, Duration);
    }

    public void ClearSubtitles()
    {
        _subtitleDocument = new VideoSubtitleDocument([]);
        _currentExternalCues = [];
        _currentEmbeddedTranscriptCues = [];
        _primarySubtitlePath = null;
        PrimarySubtitleName = "";
        EmbeddedSubtitleName = "";
        SelectedSubtitleTrackId = null;
        _embeddedSubtitleCue = null;
        CurrentCue = null;
        _subtitleSelectionOff = true;
        SetHasCompleteEmbeddedTranscript(false);
        AreSubtitlesVisible = true;
        HideSubtitleDisplay();
        ReplaceTranscriptRows([]);
        StatusText = "Subtitles cleared";
    }

    public void ReplaceTracks(IEnumerable<VideoTrackInfo> tracks)
    {
        var videoTracks = tracks
            .Where(track => track.Type == VideoTrackType.Video)
            .OrderBy(track => track.Id)
            .ToList();
        var audioTracks = tracks
            .Where(track => track.Type == VideoTrackType.Audio)
            .OrderBy(track => track.Id)
            .ToList();
        var subtitleTracks = tracks
            .Where(track => track.Type == VideoTrackType.Subtitle)
            .OrderBy(track => track.Id)
            .ToList();

        VideoTracks.Clear();
        foreach (var track in videoTracks)
            VideoTracks.Add(track);

        AudioTracks.Clear();
        foreach (var track in audioTracks)
            AudioTracks.Add(track);

        SubtitleTracks.Clear();
        foreach (var track in subtitleTracks)
            SubtitleTracks.Add(track);

        HasVideoTracks = VideoTracks.Count > 0;
        HasAudioTracks = AudioTracks.Count > 0;
        HasSubtitleTracks = SubtitleTracks.Count > 0;

        SelectedVideoTrackId = videoTracks.FirstOrDefault(track => track.IsSelected)?.Id;
        SelectedAudioTrackId = audioTracks.FirstOrDefault(track => track.IsSelected)?.Id;

        var selected = subtitleTracks.FirstOrDefault(track => track.IsSelected);
        SelectedSubtitleTrackId = selected?.Id;
        if (selected != null && string.IsNullOrWhiteSpace(PrimarySubtitleName))
            EmbeddedSubtitleName = selected.DisplayName;
        else if (selected == null && string.IsNullOrWhiteSpace(PrimarySubtitleName))
            EmbeddedSubtitleName = "";
    }

    public void SelectAudioTrack(VideoTrackInfo? track)
    {
        SelectedAudioTrackId = track?.Id;
        StatusText = track == null
            ? "Audio off"
            : $"Audio track: {track.DisplayName}";
    }

    public void SelectVideoTrack(VideoTrackInfo track)
    {
        SelectedVideoTrackId = track.Id;
        StatusText = $"Video track: {track.DisplayName}";
    }

    public void SelectEmbeddedSubtitleTrack(VideoTrackInfo track)
    {
        _subtitleDocument = new VideoSubtitleDocument([]);
        _currentExternalCues = [];
        _currentEmbeddedTranscriptCues = [];
        _embeddedSubtitleCue = null;
        _primarySubtitlePath = null;
        PrimarySubtitleName = "";
        EmbeddedSubtitleName = track.DisplayName;
        SelectedSubtitleTrackId = track.Id;
        _lastSelectedSubtitleTrackId = track.Id;
        _subtitleSelectionOff = false;
        CurrentCue = null;
        SetHasCompleteEmbeddedTranscript(false);
        AreSubtitlesVisible = true;
        HideSubtitleDisplay();
        ReplaceTranscriptRows([]);
        StatusText = $"Subtitle track: {track.DisplayName}";
        OnPropertyChanged(nameof(IsEmbeddedSubtitleActive));
    }

    public bool ReplaceEmbeddedSubtitleCues(
        VideoTrackInfo track,
        IEnumerable<VideoSubtitleCue> cues)
    {
        if (!IsEmbeddedSubtitleActive || SelectedSubtitleTrackId != track.Id)
            return false;

        _subtitleDocument = new VideoSubtitleDocument(cues);
        _currentExternalCues = [];
        _currentEmbeddedTranscriptCues = [];
        _embeddedSubtitleCue = null;
        CurrentCue = null;
        SetHasCompleteEmbeddedTranscript(_subtitleDocument.Cues.Count > 0);
        ReplaceTranscriptRows(_subtitleDocument.Cues);
        UpdateEmbeddedTranscriptAtCurrentPosition();
        StatusText = _hasCompleteEmbeddedTranscript
            ? $"{_subtitleDocument.Cues.Count} subtitle cues"
            : "No text subtitle cues";
        return true;
    }

    public void BeginEmbeddedTranscriptLoad(VideoTrackInfo track)
    {
        IsTranscriptLoading = true;
        TranscriptErrorMessage = "";
        StatusText = $"Loading subtitles: {track.DisplayName}";
    }

    public bool CompleteEmbeddedTranscriptLoad(
        VideoTrackInfo track,
        IReadOnlyList<VideoSubtitleCue> cues)
    {
        IsTranscriptLoading = false;
        TranscriptErrorMessage = "";

        if (cues.Count == 0)
        {
            if (SelectedSubtitleTrackId == track.Id)
                StatusText = "Embedded transcript unavailable; using live subtitle cues";

            return false;
        }

        return ReplaceEmbeddedSubtitleCues(track, cues);
    }

    public void FailEmbeddedTranscriptLoad(string message)
    {
        IsTranscriptLoading = false;
        TranscriptErrorMessage = message;
        StatusText = message;
    }

    public void CancelEmbeddedTranscriptLoadStatus()
    {
        IsTranscriptLoading = false;
    }

    public void ToggleSubtitlesVisible()
    {
        AreSubtitlesVisible = !AreSubtitlesVisible;
        if (!AreSubtitlesVisible)
        {
            if (SelectedSubtitleTrackId.HasValue)
                _lastSelectedSubtitleTrackId = SelectedSubtitleTrackId;

            HideSubtitleDisplay();
            StatusText = "Subtitles hidden";
            return;
        }

        if (IsEmbeddedSubtitleActive)
        {
            if (_hasCompleteEmbeddedTranscript)
                UpdateEmbeddedTranscriptAtCurrentPosition();
            else
                DisplayEmbeddedCue(_embeddedSubtitleCue);
        }
        else
        {
            DisplayExternalCues(_currentExternalCues);
        }

        StatusText = "Subtitles visible";
    }

    public VideoTrackInfo? GetNextSubtitleTrackForCycle()
    {
        if (SubtitleTracks.Count == 0)
            return null;

        if (!AreSubtitlesVisible)
        {
            return GetSubtitleTrackById(_lastSelectedSubtitleTrackId)
                ?? SubtitleTracks.FirstOrDefault();
        }

        var selectedId = SelectedSubtitleTrackId;
        if (!selectedId.HasValue)
            return SubtitleTracks.FirstOrDefault();

        var selectedIndex = -1;
        for (var i = 0; i < SubtitleTracks.Count; i++)
        {
            if (SubtitleTracks[i].Id == selectedId.Value)
            {
                selectedIndex = i;
                break;
            }
        }

        if (selectedIndex < 0 || selectedIndex >= SubtitleTracks.Count - 1)
        {
            _lastSelectedSubtitleTrackId = selectedId;
            return null;
        }

        return SubtitleTracks[selectedIndex + 1];
    }

    private VideoTrackInfo? GetSubtitleTrackById(int? id) =>
        id.HasValue
            ? SubtitleTracks.FirstOrDefault(track => track.Id == id.Value)
            : null;

    public void UpdateEmbeddedSubtitleCue(VideoSubtitleCue? cue)
    {
        if (!IsEmbeddedSubtitleActive)
            return;

        if (_hasCompleteEmbeddedTranscript)
        {
            UpdateEmbeddedTranscriptAtCurrentPosition();
            return;
        }

        _embeddedSubtitleCue = cue;
        CurrentCue = cue;
        AddEmbeddedTranscriptCue(cue);
        UpdateTranscriptCurrentCue(cue);
        DisplayEmbeddedCue(cue);
    }

    public void UpdatePosition(TimeSpan position, TimeSpan duration)
    {
        CurrentPosition = position;
        Duration = duration;
        PositionText = VideoMiningContextFactory.FormatTimestamp(position);
        DurationText = VideoMiningContextFactory.FormatTimestamp(duration);
        ProgressMaximum = Math.Max(1, duration.TotalSeconds);
        ProgressValue = Math.Clamp(position.TotalSeconds, 0, ProgressMaximum);
        RefreshChapterRows(position);

        if (IsEmbeddedSubtitleActive)
        {
            UpdateEmbeddedTranscriptAtCurrentPosition();
            return;
        }

        var cues = _subtitleDocument.FindCuesAt(GetSubtitleTimelinePosition(position));
        if (AreSameCues(cues, _currentExternalCues))
            return;

        _currentExternalCues = cues;
        CurrentCue = cues.FirstOrDefault();
        UpdateTranscriptCurrentCues(cues);
        DisplayExternalCues(cues);
    }

    private void UpdateEmbeddedTranscriptAtCurrentPosition()
    {
        if (!_hasCompleteEmbeddedTranscript)
            return;

        var cues = _subtitleDocument.FindCuesAt(GetSubtitleTimelinePosition(CurrentPosition));
        if (!AreSameCues(cues, _currentEmbeddedTranscriptCues))
        {
            _currentEmbeddedTranscriptCues = cues;
            _embeddedSubtitleCue = cues.FirstOrDefault();
            CurrentCue = _embeddedSubtitleCue;
            UpdateTranscriptCurrentCues(cues);
        }

        DisplayEmbeddedTranscriptCues(cues);
    }

    private void SetHasCompleteEmbeddedTranscript(bool value)
    {
        if (_hasCompleteEmbeddedTranscript == value)
            return;

        _hasCompleteEmbeddedTranscript = value;
        OnPropertyChanged(nameof(HasCompleteEmbeddedTranscript));
    }

    private TimeSpan GetSubtitleTimelinePosition(TimeSpan position)
    {
        var subtitleDelay = TimeSpan.FromMilliseconds(SubtitleDelayMilliseconds);
        var subtitlePosition = position - subtitleDelay;
        return subtitlePosition < TimeSpan.Zero ? TimeSpan.Zero : subtitlePosition;
    }

    private void RefreshChapterRows(TimeSpan position)
    {
        ChapterRows.Clear();
        if (_chapters.Count == 0)
            return;

        var currentId = _chapters
            .Where(chapter => chapter.StartTime <= position)
            .OrderByDescending(chapter => chapter.StartTime)
            .ThenByDescending(chapter => chapter.Id)
            .FirstOrDefault()
            ?.Id;
        foreach (var chapter in _chapters)
            ChapterRows.Add(new VideoChapterRow(chapter, chapter.Id == currentId));
    }

    private void DisplayEmbeddedCue(VideoSubtitleCue? cue)
    {
        if (!AreSubtitlesVisible || cue == null || string.IsNullOrWhiteSpace(cue.Text))
        {
            HideSubtitleDisplay();
            return;
        }

        CurrentSubtitleText = cue.Text;
        PreviousSubtitleText = "";
        NextSubtitleText = "";
        HasCurrentSubtitle = true;
        RefreshSubtitlePanelHeight();
    }

    private void DisplayExternalCue(VideoSubtitleCue? cue)
    {
        if (!AreSubtitlesVisible || cue == null)
        {
            HideSubtitleDisplay();
            return;
        }

        DisplayExternalCues([cue]);
    }

    private void DisplayExternalCues(IReadOnlyList<VideoSubtitleCue> cues)
    {
        if (!AreSubtitlesVisible || cues.Count == 0)
        {
            HideSubtitleDisplay();
            return;
        }

        var firstContext = _subtitleDocument.GetContext(cues[0]);
        var lastContext = _subtitleDocument.GetContext(cues[^1]);
        CurrentSubtitleText = string.Join(
            '\n',
            cues.Select(cue => cue.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
        PreviousSubtitleText = firstContext.PreviousText;
        NextSubtitleText = lastContext.NextText;
        HasCurrentSubtitle = !string.IsNullOrWhiteSpace(CurrentSubtitleText);
        RefreshSubtitlePanelHeight();
    }

    private void DisplayEmbeddedTranscriptCues(IReadOnlyList<VideoSubtitleCue> cues)
    {
        if (!AreSubtitlesVisible || cues.Count == 0)
        {
            HideSubtitleDisplay();
            return;
        }

        var firstContext = _subtitleDocument.GetContext(cues[0]);
        var lastContext = _subtitleDocument.GetContext(cues[^1]);
        CurrentSubtitleText = string.Join(
            '\n',
            cues.Select(cue => cue.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
        PreviousSubtitleText = firstContext.PreviousText;
        NextSubtitleText = lastContext.NextText;
        HasCurrentSubtitle = !string.IsNullOrWhiteSpace(CurrentSubtitleText);
        RefreshSubtitlePanelHeight();
    }

    private void HideSubtitleDisplay()
    {
        CurrentSubtitleText = "";
        PreviousSubtitleText = "";
        NextSubtitleText = "";
        HasCurrentSubtitle = false;
        SubtitlePanelHeight = 0;
    }

    private void ReplaceTranscriptRows(IEnumerable<VideoSubtitleCue> cues)
    {
        _lastTranscriptCurrentIndex = -1;
        TranscriptRows.Clear();
        foreach (var cue in cues)
        {
            TranscriptRows.Add(new VideoTranscriptRow(
                cue.Index,
                cue.Start,
                cue.End,
                cue.Text,
                VideoMiningContextFactory.FormatTimestamp(cue.Start)));
        }

        HasTranscriptRows = TranscriptRows.Count > 0;
        _transcriptWindow.Reset(TranscriptRows.Count, FindCurrentTranscriptRowIndex());
        RefreshTranscriptVisibleRows();
        UpdateTranscriptCurrentCue(CurrentCue);
        UpdateTranscriptABLoopMarkers();
    }

    private void AddEmbeddedTranscriptCue(VideoSubtitleCue? cue)
    {
        if (cue == null || string.IsNullOrWhiteSpace(cue.Text))
            return;

        if (TranscriptRows.Any(row => IsSameTranscriptCue(row.Start, row.End, row.Text, cue)))
            return;

        var row = new VideoTranscriptRow(
            TranscriptRows.Count,
            cue.Start,
            cue.End,
            cue.Text,
            VideoMiningContextFactory.FormatTimestamp(cue.Start));
        var insertIndex = 0;
        while (insertIndex < TranscriptRows.Count && TranscriptRows[insertIndex].Start <= row.Start)
            insertIndex++;

        TranscriptRows.Insert(insertIndex, row);
        HasTranscriptRows = true;
        if (_transcriptWindow.Count == 0)
            _transcriptWindow.Reset(TranscriptRows.Count);
        else
            _transcriptWindow.EnsureContains(TranscriptRows.Count, Math.Min(insertIndex, TranscriptRows.Count - 1));

        RefreshTranscriptVisibleRows();
        UpdateTranscriptABLoopMarkers();
    }

    private void UpdateTranscriptCurrentCue(VideoSubtitleCue? cue)
    {
        UpdateTranscriptCurrentCues(cue == null ? [] : [cue]);
    }

    private void UpdateTranscriptCurrentCues(IReadOnlyList<VideoSubtitleCue> cues)
    {
        var previousCurrentIndex = _lastTranscriptCurrentIndex;
        var currentIndex = -1;
        for (var index = 0; index < TranscriptRows.Count; index++)
        {
            var row = TranscriptRows[index];
            var isCurrent = cues.Any(cue => IsSameTranscriptCue(row.Start, row.End, row.Text, cue));
            row.IsCurrent = isCurrent;
            if (isCurrent && currentIndex < 0)
                currentIndex = index;
        }

        if (currentIndex >= 0)
        {
            var currentRow = TranscriptRows[currentIndex];
            if (ShouldRecenterTranscriptWindow(previousCurrentIndex, currentIndex))
            {
                _transcriptWindow.Reset(TranscriptRows.Count, currentIndex);
                RefreshTranscriptVisibleRows();
            }
            else if (_transcriptWindow.EnsureContains(TranscriptRows.Count, currentIndex)
                || !TranscriptVisibleRows.Contains(currentRow))
            {
                RefreshTranscriptVisibleRows();
            }

            _lastTranscriptCurrentIndex = currentIndex;
        }
    }

    private static bool ShouldRecenterTranscriptWindow(int previousCurrentIndex, int currentIndex) =>
        previousCurrentIndex < 0 || Math.Abs(currentIndex - previousCurrentIndex) > 1;

    public void RefreshTranscriptWindowForCurrentRow()
    {
        if (TranscriptRows.Count == 0)
        {
            _transcriptWindow.Reset(0);
            RefreshTranscriptVisibleRows();
            return;
        }

        var currentIndex = FindCurrentTranscriptRowIndex();
        if (currentIndex < 0)
            currentIndex = FindNearestTranscriptRowIndex(CurrentPosition);

        _transcriptWindow.Reset(TranscriptRows.Count, currentIndex);
        RefreshTranscriptVisibleRows();
    }

    public void ExpandTranscriptWindowTowardStart()
    {
        if (_transcriptWindow.ExtendTowardStart(TranscriptRows.Count))
            RefreshTranscriptVisibleRows();
    }

    public void ExpandTranscriptWindowTowardEnd()
    {
        if (_transcriptWindow.ExtendTowardEnd(TranscriptRows.Count))
            RefreshTranscriptVisibleRows();
    }

    private void RefreshTranscriptVisibleRows()
    {
        TranscriptVisibleRows.Clear();
        if (TranscriptRows.Count == 0 || _transcriptWindow.Count == 0)
            return;

        var start = Math.Clamp(_transcriptWindow.StartIndex, 0, Math.Max(0, TranscriptRows.Count - 1));
        var endExclusive = Math.Min(TranscriptRows.Count, start + _transcriptWindow.Count);
        for (var index = start; index < endExclusive; index++)
            TranscriptVisibleRows.Add(TranscriptRows[index]);
    }

    private int? FindCurrentTranscriptRowIndex()
    {
        for (var index = 0; index < TranscriptRows.Count; index++)
        {
            if (TranscriptRows[index].IsCurrent)
                return index;
        }

        if (CurrentCue == null)
            return null;

        for (var index = 0; index < TranscriptRows.Count; index++)
        {
            var row = TranscriptRows[index];
            if (IsSameTranscriptCue(row.Start, row.End, row.Text, CurrentCue))
                return index;
        }

        return null;
    }

    public VideoSubtitleSelection GetCurrentSubtitleSelection()
    {
        if (_subtitleSelectionOff)
            return VideoSubtitleSelection.Off();

        if (!string.IsNullOrWhiteSpace(_primarySubtitlePath))
            return VideoSubtitleSelection.ExternalFile(_primarySubtitlePath);

        if (SelectedSubtitleTrackId.HasValue)
        {
            var track = GetSubtitleTrackById(SelectedSubtitleTrackId);
            return VideoSubtitleSelection.EmbeddedTrack(
                SelectedSubtitleTrackId.Value,
                track?.DisplayName ?? EmbeddedSubtitleName);
        }

        return VideoSubtitleSelection.None();
    }

    public VideoMiningHistoryCapture? CreateMiningHistoryCapture()
    {
        if (CurrentVideo == null)
            return null;

        var cues = GetCurrentMiningHistoryCues();
        if (cues.Count == 0)
            return null;

        var text = string.Join("\n", cues.Select(cue => cue.Text).Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var selection = GetCurrentSubtitleSelection();
        var sourceName = !string.IsNullOrWhiteSpace(PrimarySubtitleName)
            ? PrimarySubtitleName
            : !string.IsNullOrWhiteSpace(EmbeddedSubtitleName)
                ? EmbeddedSubtitleName
                : Path.GetFileName(CurrentVideo.FilePath);
        return new VideoMiningHistoryCapture(
            text,
            CurrentVideo.FilePath,
            sourceName,
            selection.Kind == VideoSubtitleSelectionKind.ExternalFile ? selection.ExternalPath : null,
            selection.Kind,
            selection.Kind == VideoSubtitleSelectionKind.EmbeddedTrack ? selection.TrackId : null,
            cues.Min(cue => cue.Start),
            cues.Max(cue => cue.End));
    }

    private IReadOnlyList<VideoSubtitleCue> GetCurrentMiningHistoryCues()
    {
        if (IsEmbeddedSubtitleActive)
        {
            if (_currentEmbeddedTranscriptCues.Count > 0)
                return _currentEmbeddedTranscriptCues;

            return CurrentCue == null ? [] : [CurrentCue];
        }

        if (_currentExternalCues.Count > 0)
            return _currentExternalCues;

        return CurrentCue == null ? [] : [CurrentCue];
    }

    private int? FindNearestTranscriptRowIndex(TimeSpan position)
    {
        if (TranscriptRows.Count == 0)
            return null;

        var bestIndex = 0;
        for (var index = 0; index < TranscriptRows.Count; index++)
        {
            if (TranscriptRows[index].Start > position)
                break;

            bestIndex = index;
        }

        return bestIndex;
    }

    public void SetABLoopStart(TimeSpan time)
    {
        PendingABLoopStart = ClampPlaybackTime(time);
        ABLoop = null;
        UpdateABLoopState();
    }

    public VideoABLoop? TrySetABLoopEnd(TimeSpan time)
    {
        var start = PendingABLoopStart ?? ABLoop?.Start;
        if (start == null)
            return null;

        var end = ClampPlaybackTime(time);
        if (end <= start.Value)
            return null;

        var loop = new VideoABLoop(start.Value, end);
        ABLoop = loop;
        PendingABLoopStart = null;
        UpdateABLoopState();
        return loop;
    }

    public void ClearABLoop()
    {
        PendingABLoopStart = null;
        ABLoop = null;
        UpdateABLoopState();
    }

    public bool ShouldRestartABLoopPlayback(VideoABLoop loop) =>
        CurrentPosition >= loop.End;

    public void ApplySettings(VideoSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        AutoPlayNextEpisode = settings.AutoPlayNextEpisode;
        RememberPlaybackState = settings.RememberPlaybackState;
        SeekIntervalSeconds = settings.SeekIntervalSeconds;
        MiningHistoryLimit = settings.MiningHistoryLimit;
        HardwareDecodingEnabled = settings.HardwareDecodingEnabled;
        DeinterlaceEnabled = settings.DeinterlacingEnabled;
        HdrEnhancementEnabled = settings.HdrEnhancementEnabled;
        SetVideoEqualizer("brightness", settings.VideoBrightness);
        SetVideoEqualizer("contrast", settings.VideoContrast);
        SetVideoEqualizer("saturation", settings.VideoSaturation);
        SetVideoEqualizer("gamma", settings.VideoGamma);
        SetVideoEqualizer("hue", settings.VideoHue);
        SetSubtitleFontFamily(settings.SubtitleFontFamily);
        SubtitleFontSize = settings.SubtitleFontSize;
        SubtitleFontWeight = settings.SubtitleFontWeight;
        SetSubtitleShadowRadius(settings.SubtitleShadowRadius);
        SubtitleBackgroundOpacity = settings.SubtitleBackgroundOpacity;
        SubtitleBackgroundDisabled = settings.SubtitleBackgroundDisabled;
        SubtitleVerticalPosition = settings.SubtitleVerticalPosition;
        SetSubtitleColor(settings.SubtitleColorHex);
        SetSubtitleLookupHighlightColor(settings.SubtitleLookupHighlightColorHex);
        SetSubtitleLookupHighlightTextColor(settings.SubtitleLookupHighlightTextColorHex);
        SubtitleMaskEnabled = settings.SubtitleMaskEnabled;
        SetSubtitleMaskMode(settings.SubtitleMaskMode == VideoSubtitleMaskMode.Transparent
            ? "Transparent"
            : "Blur");
        SetSubtitleMaskBlurRadius(settings.SubtitleMaskBlurRadius);
        SubtitleMaskHiddenOpacity = settings.SubtitleMaskHiddenOpacity;
    }

    public void SetHDREnhancementEnabled(bool enabled)
    {
        HdrEnhancementEnabled = enabled;
    }

    public void SetVideoEqualizer(string adjustment, double value)
    {
        var normalized = NormalizeVideoEqualizerValue(value);
        switch (NormalizeVideoEqualizerAdjustment(adjustment))
        {
            case "brightness":
                VideoBrightness = normalized;
                VideoBrightnessText = FormatVideoEqualizerValue(normalized);
                break;
            case "contrast":
                VideoContrast = normalized;
                VideoContrastText = FormatVideoEqualizerValue(normalized);
                break;
            case "saturation":
                VideoSaturation = normalized;
                VideoSaturationText = FormatVideoEqualizerValue(normalized);
                break;
            case "gamma":
                VideoGamma = normalized;
                VideoGammaText = FormatVideoEqualizerValue(normalized);
                break;
            case "hue":
                VideoHue = normalized;
                VideoHueText = FormatVideoEqualizerValue(normalized);
                break;
        }
    }

    public void ResetVideoEqualizer(string adjustment)
    {
        SetVideoEqualizer(adjustment, 0);
    }

    public void SetSubtitleShadowRadius(double value)
    {
        SubtitleShadowRadius = NormalizeSubtitleShadowRadius(value);
    }

    public void SetSubtitleFontFamily(string value)
    {
        SubtitleFontFamily = NormalizeSubtitleFontFamily(value);
    }

    public void SetSubtitleColor(string value)
    {
        SubtitleColorHex = NormalizeColorHex(value, "#FFFFFFFF");
    }

    public void SetSubtitleLookupHighlightColor(string value)
    {
        SubtitleLookupHighlightColorHex = NormalizeColorHex(value, "#3EB5C1CB");
    }

    public void SetSubtitleLookupHighlightTextColor(string value)
    {
        SubtitleLookupHighlightTextColorHex = NormalizeColorHex(value, "#FFFFFFFF");
    }

    public void SetSubtitleMaskMode(string value)
    {
        SubtitleMaskMode = NormalizeSubtitleMaskMode(value);
    }

    public void SetSubtitleMaskBlurRadius(double value)
    {
        SubtitleMaskBlurRadius = NormalizeSubtitleMaskBlurRadius(value);
    }

    public bool IsSubtitleMaskRevealed(
        bool isHovering,
        bool isLookupPopupVisible,
        bool isPlaybackPaused) =>
        isHovering || isLookupPopupVisible || isPlaybackPaused;

    public double CalculateSubtitleMaskOpacity(
        bool isHovering,
        bool isLookupPopupVisible,
        bool isPlaybackPaused)
    {
        if (!SubtitleMaskEnabled
            || IsSubtitleMaskRevealed(isHovering, isLookupPopupVisible, isPlaybackPaused)
            || !string.Equals(SubtitleMaskMode, "Transparent", StringComparison.Ordinal))
        {
            return 1;
        }

        return Math.Clamp(SubtitleMaskHiddenOpacity, 0, 1);
    }

    public double CalculateSubtitleMaskBlurRadius(
        bool isHovering,
        bool isLookupPopupVisible,
        bool isPlaybackPaused)
    {
        if (!SubtitleMaskEnabled
            || IsSubtitleMaskRevealed(isHovering, isLookupPopupVisible, isPlaybackPaused)
            || !string.Equals(SubtitleMaskMode, "Blur", StringComparison.Ordinal))
        {
            return 0;
        }

        return NormalizeSubtitleMaskBlurRadius(SubtitleMaskBlurRadius);
    }

    public void ResetSubtitleAppearance()
    {
        SubtitleFontSize = 52;
        SubtitleFontWeight = 700;
        SubtitleFontFamily = JapaneseFontCatalog.DefaultSubtitleFontFamily;
        SubtitleShadowRadius = 10;
        SubtitleBackgroundOpacity = 0;
        SubtitleBackgroundDisabled = true;
        SubtitleVerticalPosition = -51;
        SubtitleColorHex = "#FFFFFFFF";
        SubtitleLookupHighlightColorHex = "#3EB5C1CB";
        SubtitleLookupHighlightTextColorHex = "#FFFFFFFF";
        SubtitleMaskEnabled = false;
        SetSubtitleMaskMode("Blur");
        SetSubtitleMaskBlurRadius(10);
        SubtitleMaskHiddenOpacity = 0;
    }

    public void SetAspectRatio(string value)
    {
        AspectRatioValue = NormalizeAspectRatioValue(value);
        AspectRatioText = AspectRatioValue == "-1"
            ? "Automatic"
            : AspectRatioValue;
    }

    public void RotateClockwise()
    {
        VideoRotationDegrees = (VideoRotationDegrees + 90) % 360;
        VideoRotationText = $"{VideoRotationDegrees}°";
    }

    private static string NormalizeAspectRatioValue(string value) =>
        value.Trim() switch
        {
            "16:9" => "16:9",
            "4:3" => "4:3",
            "1:1" => "1:1",
            "21:9" => "21:9",
            _ => "-1",
        };

    private static string NormalizeVideoEqualizerAdjustment(string adjustment) =>
        adjustment.Trim().ToLowerInvariant() switch
        {
            "brightness" => "brightness",
            "contrast" => "contrast",
            "saturation" => "saturation",
            "gamma" => "gamma",
            "hue" => "hue",
            _ => "",
        };

    private static double NormalizeVideoEqualizerValue(double value)
    {
        if (!double.IsFinite(value))
            return 0;

        return Math.Clamp(Math.Round(value), -100, 100);
    }

    private static string FormatVideoEqualizerValue(double value) =>
        value.ToString("0", CultureInfo.InvariantCulture);

    private static double NormalizeSubtitleShadowRadius(double value)
    {
        if (!double.IsFinite(value))
            return 10;

        return Math.Clamp(Math.Round(value * 2, MidpointRounding.AwayFromZero) / 2, 0, 10);
    }

    private static string NormalizeSubtitleFontFamily(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? ""
            : value.Trim();

    private static string NormalizeColorHex(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var hex = value.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];

        if (hex.Length == 6)
            hex = "FF" + hex;

        if (hex.Length != 8 || hex.Any(character => !Uri.IsHexDigit(character)))
            return fallback;

        return "#" + hex.ToUpperInvariant();
    }

    private static string NormalizeSubtitleMaskMode(string value) =>
        value.Trim().Equals("Transparent", StringComparison.OrdinalIgnoreCase)
            ? "Transparent"
            : "Blur";

    private static double NormalizeSubtitleMaskBlurRadius(double value)
    {
        if (!double.IsFinite(value))
            return 10;

        return Math.Clamp(Math.Round(value), 0, 20);
    }

    private static IReadOnlyList<VideoItem> NormalizeEpisodes(IEnumerable<VideoItem> episodes, VideoItem current)
    {
        var rows = new List<VideoItem>();
        foreach (var episode in episodes)
        {
            if (rows.Any(existing => IsSameVideo(existing, episode)))
                continue;

            rows.Add(episode);
        }

        if (!rows.Any(episode => IsSameVideo(episode, current)))
            rows.Add(current);

        return rows;
    }

    private static bool IsSameVideo(VideoItem left, VideoItem right)
    {
        if (!string.IsNullOrWhiteSpace(left.FilePath) || !string.IsNullOrWhiteSpace(right.FilePath))
        {
            return string.Equals(
                NormalizeVideoPath(left.FilePath),
                NormalizeVideoPath(right.FilePath),
                StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(left.Id, right.Id, StringComparison.Ordinal);
    }

    private static string NormalizeVideoPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private TimeSpan ClampPlaybackTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
            return TimeSpan.Zero;

        return Duration > TimeSpan.Zero && time > Duration
            ? Duration
            : time;
    }

    private void UpdateABLoopState()
    {
        CanSetABLoopEnd = PendingABLoopStart != null || ABLoop != null;
        CanClearABLoop = PendingABLoopStart != null || ABLoop != null;
        ABLoopText = ABLoop != null
            ? $"{VideoMiningContextFactory.FormatTimestamp(ABLoop.Start)} - {VideoMiningContextFactory.FormatTimestamp(ABLoop.End)}"
            : PendingABLoopStart != null
                ? $"A {VideoMiningContextFactory.FormatTimestamp(PendingABLoopStart.Value)}"
                : "Off";
        UpdateTranscriptABLoopMarkers();
    }

    private void UpdateTranscriptABLoopMarkers()
    {
        var start = PendingABLoopStart ?? ABLoop?.Start;
        var end = PendingABLoopStart == null ? ABLoop?.End : null;
        foreach (var row in TranscriptRows)
        {
            row.SetABLoopMarkers(
                start != null && ContainsTime(row, start.Value),
                end != null && ContainsTime(row, end.Value));
        }
    }

    private static bool ContainsTime(VideoTranscriptRow row, TimeSpan time) =>
        row.Start <= time && time <= row.End;

    private static bool IsSameTranscriptCue(
        TimeSpan start,
        TimeSpan end,
        string text,
        VideoSubtitleCue cue) =>
        start == cue.Start
        && end == cue.End
        && string.Equals(text, cue.Text, StringComparison.Ordinal);

    private static bool AreSameCues(
        IReadOnlyList<VideoSubtitleCue> left,
        IReadOnlyList<VideoSubtitleCue> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
                return false;
        }

        return true;
    }

    public void RefreshSubtitlePanelHeight()
    {
        if (!HasCurrentSubtitle)
        {
            SubtitlePanelHeight = 0;
            return;
        }

        var fontSize = Math.Clamp(SubtitleFontSize, 12, 72);
        var normalizedText = (CurrentSubtitleText ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var explicitLineCount = Math.Max(1, normalizedText.Split('\n').Length);
        var lineHeight = Math.Max(fontSize * 1.25, fontSize + 10);
        var textHeight = Math.Max(fontSize * 3.2, explicitLineCount * lineHeight);
        var effectPadding = Math.Max(
            NormalizeSubtitleShadowRadius(SubtitleShadowRadius),
            NormalizeSubtitleMaskBlurRadius(SubtitleMaskBlurRadius)) * 2;

        SubtitlePanelHeight = Math.Clamp(textHeight + effectPadding + 16, 48, 320);
    }

    public TimeSpan? GetPreviousSubtitleStart() =>
        IsEmbeddedSubtitleActive
            ? FindPreviousEmbeddedSubtitleStart()
            : _subtitleDocument.FindPreviousCue(CurrentPosition)?.Start;

    public TimeSpan? GetNextSubtitleStart() =>
        IsEmbeddedSubtitleActive
            ? FindNextEmbeddedSubtitleStart()
            : _subtitleDocument.FindNextCue(CurrentPosition)?.Start;

    private TimeSpan? FindPreviousEmbeddedSubtitleStart()
    {
        var anchor = _embeddedSubtitleCue?.Start ?? CurrentPosition;
        return TranscriptRows
            .Where(row => row.Start < anchor)
            .OrderByDescending(row => row.Start)
            .FirstOrDefault()
            ?.Start;
    }

    private TimeSpan? FindNextEmbeddedSubtitleStart()
    {
        if (!_hasCompleteEmbeddedTranscript)
            return _embeddedSubtitleCue?.End;

        var anchor = _embeddedSubtitleCue?.Start ?? CurrentPosition;
        return TranscriptRows
            .Where(row => row.Start > anchor)
            .OrderBy(row => row.Start)
            .FirstOrDefault()
            ?.Start;
    }

    public async Task<DictionaryPopupRequest?> CreateLookupRequestAsync(
        string query,
        int? sentenceOffset,
        VideoMiningMediaProvider? mediaProvider = null,
        CancellationToken ct = default)
    {
        query = query.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var cueContext = BuildCueContext(query);
        var context = VideoMiningContextFactory.Create(
            CurrentVideo?.FilePath ?? "",
            CurrentPosition,
            cueContext,
            null,
            null,
            sentenceOffset);
        context.VideoMediaProvider = mediaProvider;

        return await _popupRequestService.CreateAsync(query, context, ct: ct);
    }

    private VideoSubtitleCueContext BuildCueContext(string fallbackText)
    {
        if (CurrentCue != null)
            return _subtitleDocument.GetContext(CurrentCue);

        var cue = new VideoSubtitleCue(0, CurrentPosition, CurrentPosition, fallbackText);
        return new VideoSubtitleCueContext(cue, "", "");
    }

    partial void OnPlaybackSpeedChanged(double value)
    {
        PlaybackSpeedText = $"{Math.Clamp(value, 0.25, 5):0.##}x";
    }

    partial void OnAudioDelaySecondsChanged(double value)
    {
        AudioDelayText = $"{Math.Clamp(value, -30, 30):+0.0;-0.0;0.0} s";
    }

    partial void OnSubtitleDelayMillisecondsChanged(int value)
    {
        SubtitleDelayText = $"{Math.Clamp(value, -10_000, 10_000):+#;-#;0} ms";
    }

    partial void OnSubtitleVerticalPositionChanged(double value)
    {
        SubtitleVerticalPositionText = $"{Math.Clamp(value, -200, 200):0}";
    }

    partial void OnSubtitleMaskHiddenOpacityChanged(double value)
    {
        SubtitleMaskHiddenOpacityText = $"{Math.Clamp(value, 0, 1) * 100:0}%";
    }

    partial void OnSubtitleBackgroundOpacityChanged(double value)
    {
        SubtitleBackgroundOpacityText = $"{Math.Clamp(value, 0, 1) * 100:0}%";
    }

    partial void OnSubtitleMaskBlurRadiusChanged(double value)
    {
        SubtitleMaskBlurRadiusText = $"{NormalizeSubtitleMaskBlurRadius(value):0} px";
        RefreshSubtitlePanelHeight();
    }

    partial void OnSubtitleShadowRadiusChanged(double value)
    {
        SubtitleShadowRadiusText = $"{NormalizeSubtitleShadowRadius(value):0.0}";
        RefreshSubtitlePanelHeight();
    }

    partial void OnSubtitleFontSizeChanged(double value)
    {
        SubtitleFontSizeText = $"{Math.Clamp(value, 12, 72):0} px";
        RefreshSubtitlePanelHeight();
    }

    partial void OnSubtitleFontWeightChanged(int value)
    {
        SubtitleFontWeightText = $"{Math.Clamp(value, 100, 900)}";
    }

    partial void OnSubtitleFontFamilyChanged(string value)
    {
        SubtitleFontFamilyText = JapaneseFontCatalog.FindBySubtitleFontFamily(value)?.Name
            ?? (string.IsNullOrWhiteSpace(value) ? "System Default" : value.Trim());
    }

    partial void OnPrimarySubtitleNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsEmbeddedSubtitleActive));
    }

    partial void OnSelectedSubtitleTrackIdChanged(int? value)
    {
        OnPropertyChanged(nameof(IsEmbeddedSubtitleActive));
    }
}
