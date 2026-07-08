using System;
using System.Collections.Generic;
using System.Linq;

namespace Hoshi.Models.Profiles;

public sealed record ContentLanguageProfile(string Id, string DisplayName)
{
    public static readonly ContentLanguageProfile Japanese = new("ja", "Japanese");
    public static readonly ContentLanguageProfile English = new("en", "English");

    public static IReadOnlyList<ContentLanguageProfile> All { get; } =
    [
        Japanese,
        English,
    ];

    public static ContentLanguageProfile Normalize(string? language) =>
        TryNormalize(language) ?? Japanese;

    public static ContentLanguageProfile? TryNormalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        var normalized = language.Trim().Replace('_', '-').ToLowerInvariant();
        var primary = normalized.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return primary switch
        {
            "ja" or "jpn" => Japanese,
            "en" or "eng" => English,
            _ => null,
        };
    }

    public int DisplayUnitsFromRawCharacters(int rawCharacters)
    {
        rawCharacters = Math.Max(0, rawCharacters);
        if (Id != English.Id)
            return rawCharacters;

        return rawCharacters == 0 ? 0 : (int)Math.Ceiling(rawCharacters / 5d);
    }

    public int RawCharactersFromDisplayUnits(int displayUnits)
    {
        displayUnits = Math.Max(0, displayUnits);
        return Id == English.Id ? displayUnits * 5 : displayUnits;
    }

    public static ContentLanguageProfile FromId(string? id) =>
        All.FirstOrDefault(language => string.Equals(language.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? Japanese;
}
