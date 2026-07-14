using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Niratan.Models.Anki;

public sealed class DictionaryMedia
{
    [JsonPropertyName("dictionary")]
    public string Dictionary { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";
}

public sealed class AnkiMiningPayload
{
    [JsonPropertyName("expression")]
    public string Expression { get; set; } = "";

    [JsonPropertyName("reading")]
    public string Reading { get; set; } = "";

    [JsonPropertyName("matched")]
    public string Matched { get; set; } = "";

    [JsonPropertyName("furiganaPlain")]
    public string FuriganaPlain { get; set; } = "";

    [JsonPropertyName("frequenciesHtml")]
    public string FrequenciesHtml { get; set; } = "";

    [JsonPropertyName("freqHarmonicRank")]
    public string FreqHarmonicRank { get; set; } = "";

    [JsonPropertyName("glossary")]
    public string Glossary { get; set; } = "";

    [JsonPropertyName("glossaryFirst")]
    public string GlossaryFirst { get; set; } = "";

    [JsonPropertyName("singleGlossaries")]
    public string SingleGlossariesJson { get; set; } = "";

    [JsonPropertyName("pitchPositions")]
    public string PitchPositions { get; set; } = "";

    [JsonPropertyName("pitchCategories")]
    public string PitchCategories { get; set; } = "";

    [JsonPropertyName("popupSelectionText")]
    public string PopupSelectionText { get; set; } = "";

    [JsonPropertyName("audio")]
    public string Audio { get; set; } = "";

    [JsonPropertyName("selectedDictionary")]
    public string SelectedDictionary { get; set; } = "";

    [JsonPropertyName("dictionaryMedia")]
    public string DictionaryMediaJson { get; set; } = "";

    [JsonIgnore]
    public Dictionary<string, string> SingleGlossaries
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SingleGlossariesJson))
                return new Dictionary<string, string>();
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(SingleGlossariesJson)
                       ?? new Dictionary<string, string>();
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }
    }

    [JsonIgnore]
    public List<DictionaryMedia> DictionaryMediaList
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DictionaryMediaJson))
                return [];
            try
            {
                return JsonSerializer.Deserialize<List<DictionaryMedia>>(DictionaryMediaJson) ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    public static AnkiMiningPayload FromJson(string rawJson)
    {
        var payload = JsonSerializer.Deserialize<AnkiMiningPayload>(rawJson)
                      ?? new AnkiMiningPayload();
        return payload;
    }
}

public sealed class AnkiMiningContext
{
    public string Sentence { get; set; } = "";
    public string? DocumentTitle { get; set; }
    public string? CoverPath { get; set; }
    public string? SasayakiAudioPath { get; set; }
    public string? SasayakiAudioTag { get; set; }
    public int? SentenceOffset { get; set; }
    public string? VideoFileName { get; set; }
    public string? VideoTimestamp { get; set; }
    public string? VideoCueStart { get; set; }
    public string? VideoCueEnd { get; set; }
    public string? VideoSubtitle { get; set; }
    public string? VideoPreviousSubtitle { get; set; }
    public string? VideoNextSubtitle { get; set; }
    public string? VideoScreenshotPath { get; set; }
    public string? VideoAudioClipPath { get; set; }
    public string? VideoScreenshotTag { get; set; }
    public string? VideoAudioClipTag { get; set; }

    [JsonIgnore]
    public VideoMiningMediaProvider? VideoMediaProvider { get; set; }

    [JsonIgnore]
    public SasayakiMiningAudioProvider? SasayakiAudioProvider { get; set; }

    [JsonIgnore]
    public SasayakiPopupControls? SasayakiPopupControls { get; set; }
}

public sealed record SasayakiPopupControls(
    Func<Task> TogglePlaybackAsync,
    Func<Task> ReplayCueAsync,
    Func<Task> JumpToCueAsync,
    Func<bool>? IsPlaying = null,
    Func<bool>? CanControl = null);
