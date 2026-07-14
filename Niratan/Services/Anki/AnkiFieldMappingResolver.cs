using System.Collections.Generic;
using System.Linq;
using Niratan.Models.Anki;
using Niratan.Models.Settings;

namespace Niratan.Services.Anki;

internal static class AnkiFieldMappingResolver
{
    public static Dictionary<string, string> ResolveForMining(
        AnkiNoteType noteType,
        Dictionary<string, string> savedMappings,
        AnkiMiningContext context)
    {
        var preset = IsVideoContext(context)
            ? AnkiFieldMappingPreset.Anime
            : AnkiFieldMappingPreset.Novel;

        return LapisPreset.AutofillDefaults(noteType, savedMappings, preset);
    }

    public static AnkiMiningMediaNeeds ResolveMediaNeedsForMining(
        AnkiNoteType noteType,
        Dictionary<string, string> savedMappings,
        AnkiMiningContext context)
    {
        var mappings = ResolveForMining(noteType, savedMappings, context);
        return new AnkiMiningMediaNeeds(
            mappings.Values.Any(UsesVideoScreenshot),
            mappings.Values.Any(UsesVideoAudioClip),
            mappings.Values.Any(UsesSasayakiAudio));
    }

    private static bool UsesVideoScreenshot(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains("{video-screenshot}", System.StringComparison.Ordinal);

    private static bool UsesVideoAudioClip(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains("{video-audio-clip}", System.StringComparison.Ordinal);

    private static bool UsesSasayakiAudio(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains("{sasayaki-audio}", System.StringComparison.Ordinal);

    private static bool IsVideoContext(AnkiMiningContext context) =>
        !string.IsNullOrWhiteSpace(context.VideoFileName)
        || !string.IsNullOrWhiteSpace(context.VideoTimestamp)
        || !string.IsNullOrWhiteSpace(context.VideoSubtitle)
        || !string.IsNullOrWhiteSpace(context.VideoScreenshotPath)
        || !string.IsNullOrWhiteSpace(context.VideoScreenshotTag)
        || !string.IsNullOrWhiteSpace(context.VideoAudioClipPath)
        || !string.IsNullOrWhiteSpace(context.VideoAudioClipTag);
}
