using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Hoshi.Enums;
using Hoshi.Models;
using Hoshi.Models.Anki;
using Hoshi.Models.Dictionary;
using Hoshi.Models.Settings;
using Hoshi.Services.Dictionary;
using Hoshi.Services.Settings;
using Hoshi.Services.Video;

namespace Hoshi.ViewModels.Pages;

public sealed record VideoLookupPopupRequest(
    List<DictionaryLookupResult> Results,
    Dictionary<string, string> Styles,
    DictionaryDisplaySettings DisplaySettings,
    ThemeMode Theme,
    AudioSettings AudioSettings,
    AnkiSettings AnkiSettings,
    AnkiMiningContext MiningContext);

public partial class VideoPlayerViewModel : ObservableObject
{
    private readonly SubtitleParserService _subtitleParserService;
    private readonly IDictionaryLookupService _dictionaryLookupService;
    private readonly ISettingsService _settingsService;

    private VideoSubtitleDocument _subtitleDocument = new([]);

    [ObservableProperty]
    public partial string Title { get; set; } = "Video";

    [ObservableProperty]
    public partial string CurrentSubtitleText { get; set; } = "";

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
    public partial double SubtitlePanelHeight { get; set; }

    [ObservableProperty]
    public partial double SubtitleFontSize { get; set; } = 20;

    [ObservableProperty]
    public partial double Volume { get; set; } = 100;

    [ObservableProperty]
    public partial bool HardwareDecodingEnabled { get; set; } = true;

    public VideoItem? CurrentVideo { get; private set; }
    public VideoSubtitleCue? CurrentCue { get; private set; }
    public TimeSpan CurrentPosition { get; private set; }
    public TimeSpan Duration { get; private set; }

    public VideoPlayerViewModel(
        SubtitleParserService subtitleParserService,
        IDictionaryLookupService dictionaryLookupService,
        ISettingsService settingsService)
    {
        _subtitleParserService = subtitleParserService;
        _dictionaryLookupService = dictionaryLookupService;
        _settingsService = settingsService;
    }

    public async Task LoadVideoAsync(VideoItem video, CancellationToken ct = default)
    {
        CurrentVideo = video;
        Title = video.Title;
        CurrentCue = null;
        CurrentSubtitleText = "";
        PreviousSubtitleText = "";
        NextSubtitleText = "";
        HasCurrentSubtitle = false;
        SubtitlePanelHeight = 0;
        ProgressValue = 0;
        ProgressMaximum = 1;
        DurationText = "00:00:00.000";
        _subtitleDocument = new VideoSubtitleDocument([]);

        if (!string.IsNullOrWhiteSpace(video.SubtitlePath) && File.Exists(video.SubtitlePath))
        {
            _subtitleDocument = await _subtitleParserService.ParseFileAsync(video.SubtitlePath, ct);
            StatusText = $"{_subtitleDocument.Cues.Count} subtitle cues";
        }
        else
        {
            StatusText = "No interactive subtitles loaded";
        }
    }

    public void UpdatePosition(TimeSpan position, TimeSpan duration)
    {
        CurrentPosition = position;
        Duration = duration;
        PositionText = VideoMiningContextFactory.FormatTimestamp(position);
        DurationText = VideoMiningContextFactory.FormatTimestamp(duration);
        ProgressMaximum = Math.Max(1, duration.TotalSeconds);
        ProgressValue = Math.Clamp(position.TotalSeconds, 0, ProgressMaximum);

        var cue = _subtitleDocument.FindCueAt(position);
        if (cue == CurrentCue)
            return;

        CurrentCue = cue;
        if (cue == null)
        {
            CurrentSubtitleText = "";
            PreviousSubtitleText = "";
            NextSubtitleText = "";
            HasCurrentSubtitle = false;
            SubtitlePanelHeight = 0;
            return;
        }

        var context = _subtitleDocument.GetContext(cue);
        CurrentSubtitleText = context.Cue.Text;
        PreviousSubtitleText = context.PreviousText;
        NextSubtitleText = context.NextText;
        HasCurrentSubtitle = !string.IsNullOrWhiteSpace(context.Cue.Text);
        RefreshSubtitlePanelHeight();
    }

    public void RefreshSubtitlePanelHeight()
    {
        SubtitlePanelHeight = HasCurrentSubtitle
            ? Math.Clamp(SubtitleFontSize * 2.2, 42, 84)
            : 0;
    }

    public TimeSpan? GetPreviousSubtitleStart() =>
        _subtitleDocument.FindPreviousCue(CurrentPosition)?.Start;

    public TimeSpan? GetNextSubtitleStart() =>
        _subtitleDocument.FindNextCue(CurrentPosition)?.Start;

    public async Task<VideoLookupPopupRequest?> CreateLookupRequestAsync(
        string query,
        string? screenshotPath,
        string? audioClipPath,
        int? sentenceOffset,
        CancellationToken ct = default)
    {
        query = query.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var displaySettings = _settingsService.Current.DictionaryDisplaySettings;
        var results = await _dictionaryLookupService.LookupAsync(
            query,
            displaySettings.MaxResults,
            displaySettings.ScanLength);
        if (results.Count == 0)
            return null;

        var styles = (await _dictionaryLookupService.GetStylesAsync())
            .ToDictionary(style => style.DictName, style => style.Styles);

        var cueContext = BuildCueContext(query);
        var context = VideoMiningContextFactory.Create(
            CurrentVideo?.FilePath ?? "",
            CurrentPosition,
            cueContext,
            screenshotPath,
            audioClipPath,
            sentenceOffset);

        return new VideoLookupPopupRequest(
            results,
            styles,
            displaySettings,
            _settingsService.Current.Theme,
            _settingsService.Current.AudioSettings,
            _settingsService.Current.AnkiSettings,
            context);
    }

    private VideoSubtitleCueContext BuildCueContext(string fallbackText)
    {
        if (CurrentCue != null)
            return _subtitleDocument.GetContext(CurrentCue);

        var cue = new VideoSubtitleCue(0, CurrentPosition, CurrentPosition, fallbackText);
        return new VideoSubtitleCueContext(cue, "", "");
    }
}
