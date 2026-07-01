using System.Collections.Generic;
using System.Linq;
using Hoshi.Models.Settings;

namespace Hoshi.Services.Anki;

public static class LapisPreset
{
    private static readonly Dictionary<string, string> Defaults = new()
    {
        ["Expression"] = "{expression}",
        ["ExpressionFurigana"] = "{furigana-plain}",
        ["ExpressionReading"] = "{reading}",
        ["ExpressionAudio"] = "{audio}",
        ["SelectionText"] = "{popup-selection-text}",
        ["MainDefinition"] = "{glossary-first}",
        ["Sentence"] = "{sentence}",
        ["SentenceAudio"] = "{sasayaki-audio}",
        ["Picture"] = "{book-cover}",
        ["Glossary"] = "{glossary}",
        ["PitchPosition"] = "{pitch-accent-positions}",
        ["PitchCategories"] = "{pitch-accent-categories}",
        ["Frequency"] = "{frequencies}",
        ["FreqSort"] = "{frequency-harmonic-rank}",
        ["MiscInfo"] = "{document-title}",
        ["IsWordAndSentenceCard"] = "x",
    };

    public static bool Matches(AnkiNoteType noteType)
    {
        if (noteType.Name.Contains("lapis", System.StringComparison.OrdinalIgnoreCase))
            return true;

        var fields = noteType.Fields.ToHashSet();
        return fields.Contains("Expression")
               && fields.Contains("MainDefinition")
               && fields.Contains("Sentence");
    }

    public static Dictionary<string, string> DefaultMappings(AnkiNoteType noteType)
    {
        var fields = noteType.Fields.ToHashSet();
        return Defaults
            .Where(kv => fields.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public static Dictionary<string, string> ApplyDefaults(
        AnkiNoteType noteType,
        Dictionary<string, string> currentMappings)
    {
        if (!Matches(noteType))
            return new Dictionary<string, string>(currentMappings);

        var merged = new Dictionary<string, string>(DefaultMappings(noteType));
        foreach (var (key, value) in currentMappings)
            merged[key] = value;

        return merged;
    }
}
