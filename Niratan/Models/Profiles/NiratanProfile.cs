using System;
using System.Collections.Generic;
using System.Linq;

namespace Niratan.Models.Profiles;

public enum ProfileContextKind
{
    Global,
    Book,
    Video,
}

public sealed record NiratanProfile(
    string Id,
    string Name,
    string DictionaryLanguageId,
    bool IsDefault = false)
{
    public ContentLanguageProfile Language => ContentLanguageProfile.FromId(DictionaryLanguageId);
}

public sealed record ProfileContext(
    ProfileContextKind Kind,
    string? ProfileId = null,
    string? BookLanguage = null)
{
    public static ProfileContext Global() => new(ProfileContextKind.Global);

    public static ProfileContext Book(string? profileId, string? bookLanguage) =>
        new(ProfileContextKind.Book, profileId, bookLanguage);

    public static ProfileContext Video(string? profileId) =>
        new(ProfileContextKind.Video, profileId);
}

public sealed record ProfileResolution(
    NiratanProfile Profile,
    ContentLanguageProfile Language,
    ProfileContext Context);

public sealed class ProfileIndex
{
    public List<NiratanProfile> Profiles { get; set; } = [];
    public string DefaultProfileId { get; set; } = ProfileConstants.DefaultJapaneseProfileId;
    public string GlobalActiveProfileId { get; set; } = ProfileConstants.DefaultJapaneseProfileId;
    public Dictionary<string, string> PrimaryProfileIdsByLanguage { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static ProfileIndex CreateDefault() => new()
    {
        Profiles =
        [
            new NiratanProfile(
                ProfileConstants.DefaultJapaneseProfileId,
                "Japanese",
                ContentLanguageProfile.Japanese.Id,
                IsDefault: true),
        ],
        DefaultProfileId = ProfileConstants.DefaultJapaneseProfileId,
        GlobalActiveProfileId = ProfileConstants.DefaultJapaneseProfileId,
        PrimaryProfileIdsByLanguage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ContentLanguageProfile.Japanese.Id] = ProfileConstants.DefaultJapaneseProfileId,
        },
    };

    public NiratanProfile? FindProfile(string? profileId) =>
        string.IsNullOrWhiteSpace(profileId)
            ? null
            : Profiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal));
}

public static class ProfileConstants
{
    public const string DefaultJapaneseProfileId = "default-ja";
    // Retained only to migrate profile indexes written before Niratan v1.4.1.
    public const string DefaultJapaneseVideoProfileId = "default-ja-video";
    public const string DefaultEnglishProfileId = "default-en";
    public const string DefaultEnglishVideoProfileId = "default-en-video";
}
