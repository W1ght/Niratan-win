using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Hoshi.Models.Sasayaki;

public sealed class SasayakiCue
{
    public int Id { get; set; }
    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public string Text { get; set; } = "";
}

public sealed class SasayakiMatch
{
    public int CueIndex { get; set; }
    public int ChapterIndex { get; set; }
    public int StartCodePoint { get; set; }
    public int Length { get; set; }
}

public sealed class SasayakiMatchData
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string BookId { get; set; } = "";
    public string AudiobookPath { get; set; } = "";
    public string SrtPath { get; set; } = "";
    public List<SasayakiCue> Cues { get; set; } = [];
    public List<SasayakiMatch> Matches { get; set; } = [];
    public int TotalChapters { get; set; }
    public int UnmatchedCount { get; set; }

    [JsonIgnore]
    public bool IsCurrentSchemaVersion => SchemaVersion == CurrentSchemaVersion;

    [JsonIgnore]
    public bool IsValid => IsCurrentSchemaVersion && Cues.Count > 0 && Matches.Count > 0;

    [JsonIgnore]
    public bool RequiresMatcherRefresh => Matches.Any(match =>
        match.CueIndex >= 0
        && match.CueIndex < Cues.Count
        && Cues[match.CueIndex].Text.StartsWith('＊')
        && match.Length < 5);
}

public sealed class SasayakiPlaybackData
{
    public double LastPosition { get; set; }
    public double Delay { get; set; }
    public double Rate { get; set; } = 1.0;
    public int AudioBookmark { get; set; } = -1;

    [JsonIgnore]
    public string BookId { get; set; } = "";

    [JsonIgnore]
    public double PositionSeconds
    {
        get => LastPosition;
        set => LastPosition = value;
    }

    [JsonIgnore]
    public double PlaybackRate
    {
        get => Rate;
        set => Rate = value;
    }

    [JsonIgnore]
    public int CurrentCueIndex
    {
        get => AudioBookmark;
        set => AudioBookmark = value;
    }
}

public sealed class SasayakiSettings
{
    public const int DefaultSearchWindow = 2000;

    public bool EnableSasayaki { get; set; }
    public bool ReaderShowSasayakiToggle { get; set; }
    public int SearchWindowSize { get; set; } = DefaultSearchWindow;
    public double PlaybackRate { get; set; } = 1.0;
    public bool AutoScroll { get; set; } = true;
    public bool AutoPauseOnLookup { get; set; } = true;
    public bool ShowSkipControls { get; set; }
    public bool EnableSync { get; set; }
    public string LightTextColor { get; set; } = "#FF000000";
    public string LightBackgroundColor { get; set; } = "#6652C7FA";
    public string DarkTextColor { get; set; } = "#FFFFFFFF";
    public string DarkBackgroundColor { get; set; } = "#6652C7FA";
}

public sealed class ChapterCodePointRange
{
    public int ChapterIndex { get; set; }
    public int StartCodePoint { get; set; }
    public int Length { get; set; }
}
