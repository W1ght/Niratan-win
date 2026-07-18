using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Helpers;
using Niratan.Models.Profiles;

namespace Niratan.Services.Profiles;

public sealed class ProfileService : IProfileService
{
    private static readonly string[] ProfileOwnedRelativePaths =
    [
        "dictionary-settings.json",
        "reader-settings.json",
        "anki-settings.json",
        Path.Combine("dictionaries", "dictionary-config.json"),
    ];

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

        NormalizeAndMigrateProfiles();
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
        CancellationToken ct = default,
        string? copyFromProfileId = null)
    {
        ct.ThrowIfCancellationRequested();

        var language = ContentLanguageProfile.Normalize(languageId);
        var safeName = name.Trim();
        if (safeName.Length == 0)
            throw new ArgumentException("Profile name cannot be empty.", nameof(name));
        var id = string.IsNullOrWhiteSpace(profileId)
            ? $"profile-{Guid.NewGuid():D}"
            : profileId.Trim();
        ValidateProfileId(id);

        if (_index.FindProfile(id) is not null)
            throw new InvalidOperationException($"Profile '{id}' already exists.");

        if (!string.IsNullOrWhiteSpace(copyFromProfileId))
            RequireProfile(copyFromProfileId);

        var profile = new NiratanProfile(id, safeName, language.Id);
        _index.Profiles.Add(profile);
        var profileDirectory = GetProfileDirectory(id);
        Directory.CreateDirectory(profileDirectory);
        if (!string.IsNullOrWhiteSpace(copyFromProfileId))
            CopyProfileOwnedFiles(GetProfileDirectory(copyFromProfileId), profileDirectory);
        await SaveAsync();
        return profile;
    }

    public async Task RenameProfileAsync(
        string profileId,
        string name,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var profile = RequireProfile(profileId);
        var trimmedName = name.Trim();
        if (trimmedName.Length == 0)
            throw new ArgumentException("Profile name cannot be empty.", nameof(name));

        var index = _index.Profiles.FindIndex(item => item.Id == profile.Id);
        _index.Profiles[index] = profile with { Name = trimmedName };
        await SaveAsync();
    }

    public async Task DeleteProfileAsync(string profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var profile = RequireProfile(profileId);
        if (profile.IsDefault || string.Equals(profile.Id, _index.DefaultProfileId, StringComparison.Ordinal))
            throw new InvalidOperationException("The default profile cannot be deleted.");

        _index.Profiles.RemoveAll(item => string.Equals(item.Id, profile.Id, StringComparison.Ordinal));
        if (string.Equals(_index.GlobalActiveProfileId, profile.Id, StringComparison.Ordinal))
            _index.GlobalActiveProfileId = _index.DefaultProfileId;
        _index.PrimaryProfileIdsByLanguage = _index.PrimaryProfileIdsByLanguage
            .Where(pair => !string.Equals(pair.Value, profile.Id, StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        var profileDirectory = GetProfileDirectory(profile.Id);
        if (Directory.Exists(profileDirectory))
            Directory.Delete(profileDirectory, recursive: true);
        await SaveAsync();
    }

    public async Task SetPrimaryProfileForLanguageAsync(
        string languageId,
        string profileId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var language = ContentLanguageProfile.Normalize(languageId);
        var profile = RequireProfile(profileId);
        if (profile.Language.Id != language.Id)
            throw new InvalidOperationException("The profile language does not match.");
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
        // v1.4.1 keeps legacy book/video contexts serializable, but runtime
        // selection is controlled globally from Settings > Profiles.
        var profile = ResolveExplicitOrFallback(_index.GlobalActiveProfileId);

        return new ProfileResolution(profile, profile.Language, context);
    }

    public string GetProfileDirectory(string profileId)
    {
        ValidateProfileId(profileId);
        return Path.Combine(ProfilesRoot, profileId);
    }

    private NiratanProfile ResolveExplicitOrFallback(string? profileId) =>
        _index.FindProfile(profileId)
        ?? _index.FindProfile(_index.DefaultProfileId)
        ?? _index.Profiles.First(profile => profile.DictionaryLanguageId == ContentLanguageProfile.Japanese.Id);

    private NiratanProfile RequireProfile(string profileId)
    {
        ValidateProfileId(profileId);
        return _index.FindProfile(profileId)
               ?? throw new InvalidOperationException($"Profile '{profileId}' does not exist.");
    }

    private static void CopyProfileOwnedFiles(string sourceDirectory, string destinationDirectory)
    {
        foreach (var fileName in new[]
                 {
                     "dictionary-settings.json",
                     "reader-settings.json",
                     "anki-settings.json",
                 })
        {
            var sourcePath = Path.Combine(sourceDirectory, fileName);
            if (File.Exists(sourcePath))
                File.Copy(sourcePath, Path.Combine(destinationDirectory, fileName), overwrite: false);
        }

        var sourceDictionaryDirectory = Path.Combine(sourceDirectory, "dictionaries");
        var sourceDictionaryConfig = Path.Combine(sourceDictionaryDirectory, "dictionary-config.json");
        if (!File.Exists(sourceDictionaryConfig))
            return;

        var destinationDictionaryDirectory = Path.Combine(destinationDirectory, "dictionaries");
        Directory.CreateDirectory(destinationDictionaryDirectory);
        File.Copy(
            sourceDictionaryConfig,
            Path.Combine(destinationDictionaryDirectory, "dictionary-config.json"),
            overwrite: false);
    }

    private void NormalizeAndMigrateProfiles()
    {
        _index.Profiles = _index.Profiles
            .Where(profile => IsSafeProfileId(profile.Id))
            .GroupBy(profile => profile.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        var defaultJapanese = _index.FindProfile(ProfileConstants.DefaultJapaneseProfileId);
        if (defaultJapanese is null)
        {
            _index.Profiles.Insert(0, new NiratanProfile(
                ProfileConstants.DefaultJapaneseProfileId,
                "Japanese",
                ContentLanguageProfile.Japanese.Id,
                IsDefault: true));
        }
        else
        {
            ReplaceProfile(defaultJapanese with
            {
                Name = defaultJapanese.Name is "Japanese" or "Japanese EPUB"
                    ? "Japanese"
                    : defaultJapanese.Name,
                IsDefault = true,
            });
        }

        MigrateLegacyJapaneseVideoProfile();
        MigrateWindowsOnlyBuiltInProfile(ProfileConstants.DefaultEnglishProfileId);
        MigrateWindowsOnlyBuiltInProfile(ProfileConstants.DefaultEnglishVideoProfileId);

        if (_index.DefaultProfileId == ProfileConstants.DefaultJapaneseVideoProfileId
            || _index.FindProfile(_index.DefaultProfileId) is null)
            _index.DefaultProfileId = ProfileConstants.DefaultJapaneseProfileId;

        if (_index.FindProfile(_index.GlobalActiveProfileId) is null)
            _index.GlobalActiveProfileId = _index.DefaultProfileId;

        var validIds = _index.Profiles.Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal);
        _index.PrimaryProfileIdsByLanguage = _index.PrimaryProfileIdsByLanguage
            .Where(pair => ContentLanguageProfile.All.Any(language => language.Id == pair.Key)
                           && validIds.Contains(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (!_index.PrimaryProfileIdsByLanguage.ContainsKey(ContentLanguageProfile.Japanese.Id))
            _index.PrimaryProfileIdsByLanguage[ContentLanguageProfile.Japanese.Id] = _index.DefaultProfileId;

        foreach (var profile in _index.Profiles)
            Directory.CreateDirectory(GetProfileDirectory(profile.Id));
    }

    private void MigrateLegacyJapaneseVideoProfile()
    {
        var legacy = _index.FindProfile(ProfileConstants.DefaultJapaneseVideoProfileId);
        if (legacy is null)
            return;

        var source = GetProfileDirectory(legacy.Id);
        var destination = GetProfileDirectory(ProfileConstants.DefaultJapaneseProfileId);
        var equivalent = ProfileOwnedRelativePaths.All(relativePath =>
            ProfileFilesAreEquivalent(
                Path.Combine(source, relativePath),
                Path.Combine(destination, relativePath)));
        if (equivalent)
        {
            _index.Profiles.RemoveAll(profile => profile.Id == legacy.Id);
            return;
        }

        ReplaceProfile(legacy with { IsDefault = false });
    }

    private void MigrateWindowsOnlyBuiltInProfile(string profileId)
    {
        var legacy = _index.FindProfile(profileId);
        if (legacy is null)
            return;

        var directory = GetProfileDirectory(profileId);
        var ownsSettings = ProfileOwnedRelativePaths.Any(relativePath =>
            File.Exists(Path.Combine(directory, relativePath)));
        if (!ownsSettings)
        {
            _index.Profiles.RemoveAll(profile => profile.Id == profileId);
            return;
        }

        ReplaceProfile(legacy with { IsDefault = false });
    }

    private void ReplaceProfile(NiratanProfile replacement)
    {
        var index = _index.Profiles.FindIndex(profile => profile.Id == replacement.Id);
        if (index >= 0)
            _index.Profiles[index] = replacement;
    }

    private static bool ProfileFilesAreEquivalent(string lhs, string rhs)
    {
        var lhsExists = File.Exists(lhs);
        var rhsExists = File.Exists(rhs);
        if (lhsExists != rhsExists)
            return false;
        if (!lhsExists)
            return true;

        var lhsBytes = File.ReadAllBytes(lhs);
        var rhsBytes = File.ReadAllBytes(rhs);
        if (lhsBytes.AsSpan().SequenceEqual(rhsBytes))
            return true;

        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(lhsBytes), JsonNode.Parse(rhsBytes));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSafeProfileId(string profileId) =>
        !string.IsNullOrWhiteSpace(profileId)
        && profileId is not "." and not ".."
        && !profileId.Contains('/')
        && !profileId.Contains('\\')
        && profileId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

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
