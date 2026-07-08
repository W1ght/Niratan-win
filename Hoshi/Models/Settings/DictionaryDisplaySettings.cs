using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Hoshi.Models.Settings;

public enum DictionaryCollapseMode
{
    ExpandAll,
    CollapseAll,
    Custom,
}

public sealed record DictionaryDisplaySettings(
    bool DictionaryTabDefault = false,
    bool CompactGlossaries = true,
    bool ExpandFirstDictionary = false,
    DictionaryCollapseMode CollapseMode = DictionaryCollapseMode.ExpandAll,
    HashSet<string>? CollapsedDictionaries = null,
    bool CompactPitchAccents = true,
    bool DeduplicatePitchAccents = false,
    bool HarmonicFrequency = false,
    bool ShowExpressionTags = false,
    string CustomCSS = "",
    bool ScanNonJapaneseText = true,
    int MaxResults = 16,
    int ScanLength = 16,
    int PopupMaxWidth = 560,
    int PopupMaxHeight = 420
)
{
    [JsonIgnore]
    public HashSet<string> CollapsedDictionariesOrDefault =>
        CollapsedDictionaries ?? new HashSet<string>();

    public string CollapseModeText => CollapseMode switch
    {
        DictionaryCollapseMode.ExpandAll => "Expand All",
        DictionaryCollapseMode.CollapseAll => "Collapse All",
        DictionaryCollapseMode.Custom => "Custom",
        _ => "Expand All",
    };
}
