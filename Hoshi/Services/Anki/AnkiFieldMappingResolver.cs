using System.Collections.Generic;
using System.Linq;
using Hoshi.Models.Anki;
using Hoshi.Models.Settings;

namespace Hoshi.Services.Anki;

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

    private static bool IsVideoContext(AnkiMiningContext context) =>
        !string.IsNullOrWhiteSpace(context.VideoFileName)
        || !string.IsNullOrWhiteSpace(context.VideoTimestamp)
        || !string.IsNullOrWhiteSpace(context.VideoSubtitle)
        || !string.IsNullOrWhiteSpace(context.VideoScreenshotPath)
        || !string.IsNullOrWhiteSpace(context.VideoScreenshotTag)
        || !string.IsNullOrWhiteSpace(context.VideoAudioClipPath)
        || !string.IsNullOrWhiteSpace(context.VideoAudioClipTag);
}
