using System;
using System.Collections.Generic;
using System.Linq;

namespace Hoshi.Models.Profiles;

public enum ProfileContextKind
{
    Global,
    Book,
    Video,
}

public sealed record HoshiProfile(
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
    HoshiProfile Profile,
    ContentLanguageProfile Language,
    ProfileContext Context);

public sealed class ProfileIndex
{
    public List<HoshiProfile> Profiles { get; set; } = [];
    public string DefaultProfileId { get; set; } = ProfileConstants.DefaultJapaneseProfileId;
    public string GlobalActiveProfileId { get; set; } = ProfileConstants.DefaultJapaneseProfileId;
    public Dictionary<string, string> PrimaryProfileIdsByLanguage { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static ProfileIndex CreateDefault() => new()
    {
        Profiles =
        [
            new HoshiProfile(
                ProfileConstants.DefaultJapaneseProfileId,
                "Japanese EPUB",
                ContentLanguageProfile.Japanese.Id,
                IsDefault: true),
            new HoshiProfile(
                ProfileConstants.DefaultJapaneseVideoProfileId,
                "Japanese Video",
                ContentLanguageProfile.Japanese.Id,
                IsDefault: true),
            new HoshiProfile(
                ProfileConstants.DefaultEnglishProfileId,
                "English EPUB",
                ContentLanguageProfile.English.Id,
                IsDefault: true),
            new HoshiProfile(
                ProfileConstants.DefaultEnglishVideoProfileId,
                "English Video",
                ContentLanguageProfile.English.Id,
                IsDefault: true),
        ],
        DefaultProfileId = ProfileConstants.DefaultJapaneseProfileId,
        GlobalActiveProfileId = ProfileConstants.DefaultJapaneseProfileId,
        PrimaryProfileIdsByLanguage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ContentLanguageProfile.Japanese.Id] = ProfileConstants.DefaultJapaneseProfileId,
            [ContentLanguageProfile.English.Id] = ProfileConstants.DefaultEnglishProfileId,
        },
    };

    public HoshiProfile? FindProfile(string? profileId) =>
        string.IsNullOrWhiteSpace(profileId)
            ? null
            : Profiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal));
}

public static class ProfileConstants
{
    public const string DefaultJapaneseProfileId = "default-ja";
    public const string DefaultJapaneseVideoProfileId = "default-ja-video";
    public const string DefaultEnglishProfileId = "default-en";
    public const string DefaultEnglishVideoProfileId = "default-en-video";
}
