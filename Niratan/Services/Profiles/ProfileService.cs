using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Helpers;
using Niratan.Models.Profiles;

namespace Niratan.Services.Profiles;

public sealed class ProfileService : IProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _indexPath;
    private ProfileIndex _index = ProfileIndex.CreateDefault();

    public ProfileService(string? profilesRoot = null)
    {
        ProfilesRoot = profilesRoot ?? Path.Combine(AppDataHelper.GetAppDataPath(), "Profiles");
        _indexPath = Path.Combine(ProfilesRoot, "profiles.json");
    }

    public IReadOnlyList<NiratanProfile> Profiles => _index.Profiles;

    public string ProfilesRoot { get; }

    public static async Task<ProfileService> CreateForTestsAsync(string profilesRoot)
    {
        var service = new ProfileService(profilesRoot);
        await service.LoadAsync();
        return service;
    }

    public async Task LoadAsync()
    {
        Directory.CreateDirectory(ProfilesRoot);
        if (File.Exists(_indexPath))
        {
            try
            {
                await using var stream = File.OpenRead(_indexPath);
                _index = await JsonSerializer.DeserializeAsync<ProfileIndex>(stream, JsonOptions)
                         ?? ProfileIndex.CreateDefault();
            }
            catch
            {
                _index = ProfileIndex.CreateDefault();
            }
        }
        else
        {
            _index = ProfileIndex.CreateDefault();
            await SaveAsync();
        }

        EnsureBuiltInProfiles();
        await SaveAsync();
    }

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(ProfilesRoot);
        await using var stream = File.Create(_indexPath);
        await JsonSerializer.SerializeAsync(stream, _index, JsonOptions);
    }

    public async Task<NiratanProfile> CreateProfileAsync(
        string name,
        string languageId,
        string? profileId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var language = ContentLanguageProfile.Normalize(languageId);
        var safeName = string.IsNullOrWhiteSpace(name) ? language.DisplayName : name.Trim();
        var id = string.IsNullOrWhiteSpace(profileId)
            ? CreateProfileId(safeName, language.Id)
            : profileId.Trim();
        ValidateProfileId(id);

        if (_index.FindProfile(id) is not null)
            throw new InvalidOperationException($"Profile '{id}' already exists.");

        var profile = new NiratanProfile(id, safeName, language.Id);
        _index.Profiles.Add(profile);
        Directory.CreateDirectory(GetProfileDirectory(id));
        await SaveAsync();
        return profile;
    }

    public async Task SetPrimaryProfileForLanguageAsync(
        string languageId,
        string profileId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var language = ContentLanguageProfile.Normalize(languageId);
        RequireProfile(profileId);
        _index.PrimaryProfileIdsByLanguage[language.Id] = profileId;
        await SaveAsync();
    }

    public async Task SetGlobalActiveProfileAsync(string profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        RequireProfile(profileId);
        _index.GlobalActiveProfileId = profileId;
        await SaveAsync();
    }

    public string? GetPrimaryProfileIdForLanguage(string languageId)
    {
        var language = ContentLanguageProfile.Normalize(languageId);
        return _index.PrimaryProfileIdsByLanguage.TryGetValue(language.Id, out var profileId)
            && _index.FindProfile(profileId) is not null
                ? profileId
                : null;
    }

    public ProfileResolution Resolve(ProfileContext context)
    {
        var profile = context.Kind switch
        {
            ProfileContextKind.Book => ResolveBook(context),
            ProfileContextKind.Video => ResolveExplicitOrFallback(context.ProfileId),
            _ => ResolveExplicitOrFallback(context.ProfileId ?? _index.GlobalActiveProfileId),
        };

        return new ProfileResolution(profile, profile.Language, context);
    }

    public string GetProfileDirectory(string profileId)
    {
        ValidateProfileId(profileId);
        return Path.Combine(ProfilesRoot, profileId);
    }

    private NiratanProfile ResolveBook(ProfileContext context)
    {
        var explicitProfile = _index.FindProfile(context.ProfileId);
        if (explicitProfile is not null)
            return explicitProfile;

        var language = ContentLanguageProfile.TryNormalize(context.BookLanguage);
        if (language is not null
            && _index.PrimaryProfileIdsByLanguage.TryGetValue(language.Id, out var primaryId)
            && _index.FindProfile(primaryId) is { } primaryProfile)
        {
            return primaryProfile;
        }

        return ResolveExplicitOrFallback(_index.GlobalActiveProfileId);
    }

    private NiratanProfile ResolveExplicitOrFallback(string? profileId) =>
        _index.FindProfile(profileId)
        ?? _index.FindProfile(_index.DefaultProfileId)
        ?? _index.Profiles.First(profile => profile.DictionaryLanguageId == ContentLanguageProfile.Japanese.Id);

    private void RequireProfile(string profileId)
    {
        ValidateProfileId(profileId);
        if (_index.FindProfile(profileId) is null)
            throw new InvalidOperationException($"Profile '{profileId}' does not exist.");
    }

    private void EnsureBuiltInProfiles()
    {
        var changed = false;
        if (_index.FindProfile(ProfileConstants.DefaultJapaneseProfileId) is null)
        {
            _index.Profiles.Add(new NiratanProfile(
                ProfileConstants.DefaultJapaneseProfileId,
                "Japanese EPUB",
                ContentLanguageProfile.Japanese.Id,
                IsDefault: true));
            changed = true;
        }

        if (_index.FindProfile(ProfileConstants.DefaultJapaneseVideoProfileId) is null)
        {
            _index.Profiles.Add(new NiratanProfile(
                ProfileConstants.DefaultJapaneseVideoProfileId,
                "Japanese Video",
                ContentLanguageProfile.Japanese.Id,
                IsDefault: true));
            changed = true;
        }

        if (_index.FindProfile(ProfileConstants.DefaultEnglishProfileId) is null)
        {
            _index.Profiles.Add(new NiratanProfile(
                ProfileConstants.DefaultEnglishProfileId,
                "English EPUB",
                ContentLanguageProfile.English.Id,
                IsDefault: true));
            changed = true;
        }

        if (_index.FindProfile(ProfileConstants.DefaultEnglishVideoProfileId) is null)
        {
            _index.Profiles.Add(new NiratanProfile(
                ProfileConstants.DefaultEnglishVideoProfileId,
                "English Video",
                ContentLanguageProfile.English.Id,
                IsDefault: true));
            changed = true;
        }

        if (!_index.PrimaryProfileIdsByLanguage.ContainsKey(ContentLanguageProfile.Japanese.Id))
        {
            _index.PrimaryProfileIdsByLanguage[ContentLanguageProfile.Japanese.Id] =
                ProfileConstants.DefaultJapaneseProfileId;
            changed = true;
        }

        if (!_index.PrimaryProfileIdsByLanguage.ContainsKey(ContentLanguageProfile.English.Id))
        {
            _index.PrimaryProfileIdsByLanguage[ContentLanguageProfile.English.Id] =
                ProfileConstants.DefaultEnglishProfileId;
            changed = true;
        }

        if (_index.FindProfile(_index.DefaultProfileId) is null)
        {
            _index.DefaultProfileId = ProfileConstants.DefaultJapaneseProfileId;
            changed = true;
        }

        if (_index.FindProfile(_index.GlobalActiveProfileId) is null)
        {
            _index.GlobalActiveProfileId = _index.DefaultProfileId;
            changed = true;
        }

        if (changed)
        {
            foreach (var profile in _index.Profiles)
                Directory.CreateDirectory(GetProfileDirectory(profile.Id));
        }
    }

    private static string CreateProfileId(string name, string languageId)
    {
        var baseId = new string(name
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());
        baseId = string.Join('-', baseId.Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(baseId))
            baseId = languageId;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{languageId}:{name}")))[..8]
            .ToLowerInvariant();
        return $"{languageId}-{baseId}-{hash}";
    }

    private static void ValidateProfileId(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)
            || profileId is "." or ".."
            || profileId.Contains('/')
            || profileId.Contains('\\')
            || profileId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"Unsafe profile id '{profileId}'.", nameof(profileId));
        }
    }
}
