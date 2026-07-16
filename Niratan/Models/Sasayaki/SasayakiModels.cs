using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Niratan.Models.Sasayaki;

public sealed class SasayakiCue
{
    public string Id { get; set; } = "";
    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public string Text { get; set; } = "";
}

public sealed class SasayakiMatch
{
    public string Id { get; set; } = "";
    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public string Text { get; set; } = "";
    public int ChapterIndex { get; set; }
    public int Start { get; set; }
    public int Length { get; set; }
}

public sealed class SasayakiMatchData
{
    public List<SasayakiMatch> Matches { get; set; } = [];
    public int Unmatched { get; set; }

    [JsonIgnore]
    public int TotalCueCount => Matches.Count + Math.Max(0, Unmatched);

    [JsonIgnore]
    public bool IsValid => Matches.Count > 0;

    [JsonIgnore]
    public bool RequiresMatcherRefresh => Matches.Any(match =>
        match.Text.StartsWith('＊')
        && match.Length < 5);
}

public sealed class SasayakiSourceData
{
    public string AudiobookPath { get; set; } = "";
    public string SrtPath { get; set; } = "";
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
