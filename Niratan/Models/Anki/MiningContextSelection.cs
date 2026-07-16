using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Niratan.Models.Anki;

public sealed record MiningContextMediaRange(TimeSpan Start, TimeSpan End);

public sealed record MiningContextSentence(
    string Id,
    string Text,
    int? TargetUtf16Location = null,
    MiningContextMediaRange? MediaRange = null);

public sealed class MiningContextSelection
{
    public IReadOnlyList<MiningContextSentence> Sentences { get; }
    public int CurrentIndex { get; }

    public MiningContextSelection(
        IEnumerable<MiningContextSentence> sentences,
        int currentIndex)
    {
        Sentences = sentences
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence.Text))
            .ToArray();
        if (Sentences.Count == 0)
            throw new ArgumentException("Mining context requires at least one sentence.", nameof(sentences));

        CurrentIndex = Math.Clamp(currentIndex, 0, Sentences.Count - 1);
    }

    public static MiningContextSelection? FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("sentences", out var rawSentences)
            || rawSentences.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var sentences = new List<MiningContextSentence>();
        var sourceIndex = 0;
        foreach (var raw in rawSentences.EnumerateArray())
        {
            if (raw.ValueKind != JsonValueKind.Object
                || !raw.TryGetProperty("text", out var textElement))
            {
                sourceIndex++;
                continue;
            }

            var text = textElement.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                sourceIndex++;
                continue;
            }

            var id = raw.TryGetProperty("id", out var idElement)
                ? idElement.GetString() ?? sourceIndex.ToString()
                : sourceIndex.ToString();
            var targetLocation = TryGetNonNegativeInt32(raw, "targetLocation");
            sentences.Add(new MiningContextSentence(id, text, targetLocation));
            sourceIndex++;
        }

        if (sentences.Count == 0)
            return null;

        var currentIndex = TryGetNonNegativeInt32(element, "currentIndex") ?? 0;
        return new MiningContextSelection(sentences, currentIndex);
    }

    private static int? TryGetNonNegativeInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var parsed))
        {
            return null;
        }

        return parsed >= 0 ? parsed : null;
    }
}

public sealed record MiningContextSelectionRange(int LowerBound, int UpperBound);
