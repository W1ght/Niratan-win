using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Niratan.Models.Anki;

namespace Niratan.Services.Anki;

public static partial class AnkiHandlebarRenderer
{
    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex HandlebarPattern();

    public static string Render(string template, AnkiMiningPayload payload, AnkiMiningContext context)
    {
        if (string.IsNullOrEmpty(template))
            return "";

        return HandlebarPattern().Replace(template, match =>
        {
            var handlebar = match.Value;
            return ResolveHandlebar(handlebar, payload, context);
        });
    }

    private static string ResolveHandlebar(string handlebar, AnkiMiningPayload payload, AnkiMiningContext context)
    {
        return handlebar switch
        {
            "{expression}" => payload.Expression,
            "{reading}" => payload.Reading,
            "{furigana-plain}" => payload.FuriganaPlain,
            "{audio}" => payload.Audio,
            "{glossary}" => payload.Glossary,
            "{glossary-first}" => payload.GlossaryFirst,
            "{selected-glossary}" => GetSingleGlossary(payload.SingleGlossaries, payload.SelectedDictionary),
            "{popup-selection-text}" => payload.PopupSelectionText,
            "{sentence}" => RenderSentence(payload.Matched, context.Sentence, context.SentenceOffset),
            "{frequencies}" => payload.FrequenciesHtml,
            "{frequency-harmonic-rank}" => payload.FreqHarmonicRank,
            "{pitch-accent-positions}" => payload.PitchPositions,
            "{pitch-accent-categories}" => payload.PitchCategories,
            "{document-title}" => context.DocumentTitle ?? "",
            "{book-cover}" => context.CoverTag ?? "",
            "{sasayaki-audio}" => context.SasayakiAudioTag ?? context.SasayakiAudioPath ?? "",
            "{video-file-name}" => context.VideoFileName ?? "",
            "{video-timestamp}" => context.VideoTimestamp ?? "",
            "{video-cue-start}" => context.VideoCueStart ?? "",
            "{video-cue-end}" => context.VideoCueEnd ?? "",
            "{video-subtitle}" => context.VideoSubtitle ?? context.Sentence,
            "{video-previous-subtitle}" => context.VideoPreviousSubtitle ?? "",
            "{video-next-subtitle}" => context.VideoNextSubtitle ?? "",
            "{video-screenshot}" => context.VideoScreenshotTag ?? context.VideoScreenshotPath ?? "",
            "{video-audio-clip}" => context.VideoAudioClipTag ?? context.VideoAudioClipPath ?? "",
            _ => ResolveDynamicHandlebar(handlebar, payload),
        };
    }

    private static string ResolveDynamicHandlebar(string handlebar, AnkiMiningPayload payload)
    {
        // {single-glossary-<dict>} pattern
        if (handlebar.StartsWith("{single-glossary-") && handlebar.EndsWith("}"))
        {
            var dictName = handlebar["{single-glossary-".Length..^1];
            return GetSingleGlossary(payload.SingleGlossaries, dictName);
        }

        return "";
    }

    private static string GetSingleGlossary(Dictionary<string, string> singleGlossaries, string dictionaryName)
    {
        if (string.IsNullOrWhiteSpace(dictionaryName))
            return "";

        if (singleGlossaries.TryGetValue(dictionaryName, out var glossary))
            return glossary;

        // Normalize: strip bracket annotations like "JMdict [2026-04-27]" -> "JMdict"
        var normalized = Regex.Replace(dictionaryName, @"\s*\[[^]]+]\s*$", "").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        var match = singleGlossaries
            .FirstOrDefault(kv =>
                string.Equals(
                    Regex.Replace(kv.Key, @"\s*\[[^]]+]\s*$", "").Trim(),
                    normalized,
                    System.StringComparison.OrdinalIgnoreCase));

        return match.Value ?? "";
    }

    private static string RenderSentence(string matched, string sentence, int? sentenceOffset)
    {
        if (string.IsNullOrEmpty(matched))
            return sentence;

        if (sentenceOffset.HasValue
            && sentenceOffset.Value >= 0
            && sentenceOffset.Value + matched.Length <= sentence.Length
            && sentence.Substring(sentenceOffset.Value, matched.Length) == matched)
        {
            return sentence[..sentenceOffset.Value]
                   + "<b>" + matched + "</b>"
                   + sentence[(sentenceOffset.Value + matched.Length)..];
        }

        // Fallback: bold first occurrence
        var idx = sentence.IndexOf(matched, System.StringComparison.Ordinal);
        if (idx < 0)
            return sentence;

        return sentence[..idx] + "<b>" + matched + "</b>" + sentence[(idx + matched.Length)..];
    }

    public static List<string> GetHandlebarOptions(List<string>? dictionaryNames = null)
    {
        var options = new List<string>
        {
            "-",
            "{expression}",
            "{reading}",
            "{furigana-plain}",
            "{audio}",
            "{glossary}",
            "{glossary-first}",
            "{selected-glossary}",
            "{popup-selection-text}",
            "{sentence}",
            "{frequencies}",
            "{frequency-harmonic-rank}",
            "{pitch-accent-positions}",
            "{pitch-accent-categories}",
            "{document-title}",
            "{book-cover}",
            "{sasayaki-audio}",
            "{video-file-name}",
            "{video-timestamp}",
            "{video-cue-start}",
            "{video-cue-end}",
            "{video-subtitle}",
            "{video-previous-subtitle}",
            "{video-next-subtitle}",
            "{video-screenshot}",
            "{video-audio-clip}",
        };

        if (dictionaryNames != null)
        {
            foreach (var name in dictionaryNames)
                options.Add($"{{single-glossary-{name}}}");
        }

        return options;
    }
}
