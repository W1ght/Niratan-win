using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Settings;
using Niratan.Services.Settings;

namespace Niratan.Services.Profiles;

public sealed class ProfileSettingsStore
{
    private const string DictionarySettingsFileName = "dictionary-settings.json";
    private const string ReaderSettingsFileName = "reader-settings.json";
    private const string AnkiSettingsFileName = "anki-settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IProfileService _profiles;
    private readonly ISettingsService _settings;
    private readonly IReaderSettingsService _readerSettings;
    private string? _activeProfileId;

    public ProfileSettingsStore(
        IProfileService profiles,
        ISettingsService settings,
        IReaderSettingsService readerSettings)
    {
        _profiles = profiles;
        _settings = settings;
        _readerSettings = readerSettings;
    }

    public string? ActiveProfileId => _activeProfileId;

    public async Task SaveActiveAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_activeProfileId is null)
            return;

        await SaveProfileOwnedSettingsAsync(_activeProfileId, ct);
    }

    public async Task ActivateAsync(string profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.Equals(_activeProfileId, profileId, StringComparison.Ordinal))
            return;

        if (_activeProfileId is not null
            && _profiles.Profiles.Any(profile => profile.Id == _activeProfileId))
            await SaveProfileOwnedSettingsAsync(_activeProfileId, ct);

        await LoadProfileOwnedSettingsAsync(profileId, ct);
        _activeProfileId = profileId;
    }

    public Task ReloadActiveAsync(CancellationToken ct = default) =>
        _activeProfileId is null
            ? Task.CompletedTask
            : LoadProfileOwnedSettingsAsync(_activeProfileId, ct);

    private async Task SaveProfileOwnedSettingsAsync(string profileId, CancellationToken ct)
    {
        var profileDir = _profiles.GetProfileDirectory(profileId);
        Directory.CreateDirectory(profileDir);

        await WriteJsonAsync(
            Path.Combine(profileDir, DictionarySettingsFileName),
            CloneDictionaryDisplaySettings(_settings.Current.DictionaryDisplaySettings),
            ct);
        await WriteJsonAsync(
            Path.Combine(profileDir, ReaderSettingsFileName),
            Clone(_readerSettings.Current),
            ct);
        await WriteJsonAsync(
            Path.Combine(profileDir, AnkiSettingsFileName),
            AnkiSettings.Clone(_settings.Current.AnkiSettings),
            ct);
    }

    private async Task LoadProfileOwnedSettingsAsync(string profileId, CancellationToken ct)
    {
        var profileDir = _profiles.GetProfileDirectory(profileId);
        Directory.CreateDirectory(profileDir);

        var dictionarySettings = await ReadJsonAsync(
            Path.Combine(profileDir, DictionarySettingsFileName),
            new DictionaryDisplaySettings(),
            ct);
        var readerSettings = await ReadJsonAsync(
            Path.Combine(profileDir, ReaderSettingsFileName),
            new ReaderSettings(),
            ct);
        var ankiSettings = await ReadJsonAsync(
            Path.Combine(profileDir, AnkiSettingsFileName),
            new AnkiSettings(),
            ct);

        var appSettings = Clone(_settings.Current);
        appSettings.DictionaryDisplaySettings = CloneDictionaryDisplaySettings(dictionarySettings);
        appSettings.AnkiSettings = AnkiSettings.WithGlobalTransportFrom(
            _settings.Current.AnkiSettings,
            ankiSettings);

        _settings.ReplaceCurrent(appSettings);
        _readerSettings.ReplaceCurrent(Clone(readerSettings));
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        var tmpPath = path + ".tmp";
        await using (var stream = File.Create(tmpPath))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct);
        }

        File.Move(tmpPath, path, overwrite: true);
    }

    private static async Task<T> ReadJsonAsync<T>(string path, T fallback, CancellationToken ct)
    {
        if (!File.Exists(path))
            return Clone(fallback);

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct) ?? Clone(fallback);
        }
        catch
        {
            return Clone(fallback);
        }
    }

    private static DictionaryDisplaySettings CloneDictionaryDisplaySettings(DictionaryDisplaySettings settings) =>
        settings with
        {
            CollapsedDictionaries = settings.CollapsedDictionaries is null
                ? null
                : new(settings.CollapsedDictionaries),
        };

    private static T Clone<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
               ?? throw new InvalidOperationException($"Failed to clone {typeof(T).Name}.");
    }
}
