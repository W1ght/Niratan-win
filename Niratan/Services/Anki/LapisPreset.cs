using System.Collections.Generic;
using System.Linq;
using Niratan.Models.Settings;

namespace Niratan.Services.Anki;

public static class LapisPreset
{
    private sealed record FieldTemplate(string NoteType, Dictionary<string, string> Mappings);

    private const string LapisNoteType = "Lapis";

    private static readonly FieldTemplate[] Templates =
    [
        new(LapisNoteType, new Dictionary<string, string>
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
        }),
        new("Kiku", new Dictionary<string, string>
        {
            ["Expression"] = "{expression}",
            ["ExpressionFurigana"] = "{furigana-plain}",
            ["ExpressionReading"] = "{reading}",
            ["ExpressionAudio"] = "{audio}",
            ["SelectionText"] = "{popup-selection-text}",
            ["MainDefinition"] = "{glossary-first}",
            ["Sentence"] = "{sentence}",
            ["Picture"] = "{book-cover}",
            ["Glossary"] = "{glossary}",
            ["PitchPosition"] = "{pitch-accent-positions}",
            ["PitchCategories"] = "{pitch-accent-categories}",
            ["Frequency"] = "{frequencies}",
            ["FreqSort"] = "{frequency-harmonic-rank}",
            ["MiscInfo"] = "{document-title}",
        }),
        new("Senren", new Dictionary<string, string>
        {
            ["word"] = "{expression}",
            ["reading"] = "{reading}",
            ["sentence"] = "{sentence}",
            ["selectionText"] = "{popup-selection-text}",
            ["definition"] = "{glossary-first}",
            ["wordAudio"] = "{audio}",
            ["picture"] = "{book-cover}",
            ["glossary"] = "{glossary}",
            ["pitchPositions"] = "{pitch-accent-positions}",
            ["pitchCategories"] = "{pitch-accent-categories}",
            ["frequencies"] = "{frequencies}",
            ["freqSort"] = "{frequency-harmonic-rank}",
            ["miscInfo"] = "{document-title}",
        }),
    ];

    public static bool Matches(AnkiNoteType noteType) => HasDefaults(noteType);

    public static bool HasDefaults(AnkiNoteType noteType) => ResolveTemplate(noteType) != null;

    public static Dictionary<string, string> DefaultMappings(AnkiNoteType noteType)
    {
        var template = ResolveTemplate(noteType);
        if (template == null)
            return [];

        var fields = noteType.Fields.ToHashSet();
        var mappings = new Dictionary<string, string>();
        foreach (var field in noteType.Fields)
        {
            if (fields.Contains(field)
                && TryDefaultMapping(field, template, out var value))
            {
                mappings[field] = value;
            }
        }

        foreach (var field in noteType.Fields.Where(field => ClearsMapping(noteType, field)))
            mappings.Remove(field);

        return mappings;
    }

    public static Dictionary<string, string> AutofillDefaults(
        AnkiNoteType noteType,
        Dictionary<string, string> currentMappings)
    {
        var template = ResolveTemplate(noteType);
        if (template == null)
            return new Dictionary<string, string>(currentMappings);

        var available = noteType.Fields.ToHashSet();
        var merged = currentMappings
            .Where(kv => available.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        foreach (var field in noteType.Fields)
        {
            if (ClearsMapping(noteType, field))
            {
                merged.Remove(field);
                continue;
            }

            if (!TryDefaultMapping(field, template, out var value))
                continue;

            if (!merged.TryGetValue(field, out var existing)
                || string.IsNullOrWhiteSpace(existing))
            {
                merged[field] = value;
            }
        }

        return merged;
    }

    public static Dictionary<string, string> ApplyDefaults(
        AnkiNoteType noteType,
        Dictionary<string, string> currentMappings)
    {
        var template = ResolveTemplate(noteType);
        if (template == null)
            return new Dictionary<string, string>(currentMappings);

        var available = noteType.Fields.ToHashSet();
        var merged = currentMappings
            .Where(kv => available.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        foreach (var field in noteType.Fields)
        {
            if (ClearsMapping(noteType, field))
            {
                merged.Remove(field);
                continue;
            }

            if (TryDefaultMapping(field, template, out var value))
                merged[field] = value;
        }

        return merged;
    }

    public static bool ClearsMapping(AnkiNoteType noteType, string field) =>
        string.Equals(ResolveTemplate(noteType)?.NoteType, LapisNoteType, System.StringComparison.Ordinal)
        && string.Equals(field, "DefinitionPicture", System.StringComparison.Ordinal);

    private static FieldTemplate? ResolveTemplate(AnkiNoteType noteType)
    {
        var template = Templates.FirstOrDefault(template =>
            string.Equals(noteType.Name, template.NoteType, System.StringComparison.OrdinalIgnoreCase));
        if (template != null)
            return template;

        if (noteType.Name.Contains(LapisNoteType, System.StringComparison.OrdinalIgnoreCase))
            return Templates.First(template => template.NoteType == LapisNoteType);

        var fields = noteType.Fields.ToHashSet();
        if (fields.Contains("Expression")
            && fields.Contains("MainDefinition")
            && fields.Contains("Sentence"))
        {
            return Templates.First(template => template.NoteType == LapisNoteType);
        }

        return null;
    }

    private static bool TryDefaultMapping(
        string field,
        FieldTemplate template,
        out string value)
    {
        switch (field.ToLowerInvariant())
        {
            case "sentenceaudio":
                value = "{sasayaki-audio}";
                return true;
            case "picture":
                value = "{book-cover}";
                return true;
            default:
                return template.Mappings.TryGetValue(field, out value!);
        }
    }
}
