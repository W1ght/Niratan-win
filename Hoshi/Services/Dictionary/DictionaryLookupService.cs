using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Hoshi.Helpers;
using Hoshi.Models.Dictionary;
using Hoshi.Models.Profiles;

namespace Hoshi.Services.Dictionary;

public sealed class DictionaryLookupService : IDictionaryLookupService, IDisposable
{
    private readonly ILogger<DictionaryLookupService> _logger;
    private readonly string _dictionaryStorageDir;
    private readonly IDictionaryProfileContext? _profileContext;
    private readonly Dictionary<string, string> _loadedDictDirs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _displayNameByNativeName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _nativeNameByDisplayName = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);
    private DictionaryStyle[]? _cachedStyles;
    private IntPtr _session = IntPtr.Zero;
    private string _activeLanguageId = ContentLanguageProfile.Japanese.Id;
    private bool _indexReady;
    private bool _nativeAvailable = true;

    public DictionaryLookupService(
        ILogger<DictionaryLookupService> logger,
        string? dictionaryStorageDir = null,
        IDictionaryProfileContext? profileContext = null)
    {
        _logger = logger;
        _dictionaryStorageDir = dictionaryStorageDir ?? GetDictionaryStorageDir();
        _profileContext = profileContext;
        CreateSession();
    }

    private void CreateSession()
    {
        try
        {
            _session = HoshiDictsNative.hoshi_session_create();
            _logger.LogInformation("[Lookup] Native session created: {SessionPtr}, DLL loaded successfully", _session);
        }
        catch (DllNotFoundException ex)
        {
            _logger.LogWarning(ex, "[Lookup] hoshidicts native library NOT FOUND; dictionary lookup unavailable");
            _nativeAvailable = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Lookup] Failed to create native session (type={ExType}): {Message}",
                ex.GetType().Name, ex.Message);
            _nativeAvailable = false;
        }
    }

    public async Task<List<DictionaryLookupResult>> LookupAsync(
        string text, int maxResults = 16, int scanLength = 16, string? traceId = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        if (!_nativeAvailable)
        {
            _logger.LogWarning("[Lookup] Native DLL not available, returning empty for '{Text}'", text);
            return [];
        }

        var totalSw = Stopwatch.StartNew();
        var phaseSw = Stopwatch.StartNew();
        await EnsureIndexAsync().ConfigureAwait(false);
        _logger.LogInformation(
            "[LookupTrace] trace={TraceId} ensure-index completed in {Ms}ms total={TotalMs}ms indexReady={IndexReady}",
            traceId ?? "-", phaseSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds, _indexReady);

        _logger.LogInformation(
            "[Lookup] Session={SessionPtr}, indexReady={IndexReady}, text='{Text}', max={Max}, scan={Scan}",
            _session, _indexReady, text, maxResults, scanLength);

        phaseSw.Restart();
        return await DictionaryNativeExecutor.RunAsync(_rebuildLock, () =>
        {
            _logger.LogInformation(
                "[LookupTrace] trace={TraceId} rebuild-lock acquired in {Ms}ms total={TotalMs}ms",
                traceId ?? "-", phaseSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds);
            phaseSw.Restart();
            var jsonPtr = HoshiDictsNative.hoshi_lookup(_session, text, maxResults, scanLength);
            var json = HoshiDictsNative.ReadStringAndFree(jsonPtr);
            _logger.LogInformation(
                "[LookupTrace] trace={TraceId} native lookup completed in {Ms}ms total={TotalMs}ms jsonBytes={JsonLength}",
                traceId ?? "-", phaseSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds, json?.Length ?? 0);
            _logger.LogInformation("[Lookup] Native returned JSON length={Len}, preview='{Preview}'",
                json?.Length ?? 0, json?.Length > 200 ? json[..200] : (json ?? "null"));
            phaseSw.Restart();
            var results = ApplyDictionaryDisplayTitles(HoshiDictsNative.DeserializeLookupResults(json));
            _logger.LogInformation(
                "[LookupTrace] trace={TraceId} deserialize/display completed in {Ms}ms total={TotalMs}ms results={Count}",
                traceId ?? "-", phaseSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds, results.Count);
            return results;
        }).ConfigureAwait(false);
    }

    public static IEnumerable<string> EnumerateLookupCandidates(string text, int scanLength = 16)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        var runes = text.EnumerateRunes()
            .Take(Math.Max(0, scanLength))
            .ToArray();

        for (var length = runes.Length; length > 0; length--)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < length; i++)
                builder.Append(runes[i]);
            yield return builder.ToString();
        }
    }

    public async Task<List<DictionaryStyle>> GetStylesAsync()
    {
        if (!_nativeAvailable)
            return [];

        var totalSw = Stopwatch.StartNew();
        await EnsureIndexAsync().ConfigureAwait(false);
        var waitSw = Stopwatch.StartNew();
        return await DictionaryNativeExecutor.RunAsync(_rebuildLock, () =>
        {
            _logger.LogInformation(
                "[LookupTrace] trace={TraceId} styles rebuild-lock acquired in {Ms}ms total={TotalMs}ms",
                "-", waitSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds);

            if (_cachedStyles is { } cachedStyles)
            {
                _logger.LogInformation(
                    "[LookupTrace] trace={TraceId} styles cache hit total={TotalMs}ms styles={StyleCount}",
                    "-", totalSw.ElapsedMilliseconds, cachedStyles.Length);
                return cachedStyles.ToList();
            }

            var nativeSw = Stopwatch.StartNew();
            var jsonPtr = HoshiDictsNative.hoshi_get_styles(_session);
            var json = HoshiDictsNative.ReadStringAndFree(jsonPtr);
            var styles = HoshiDictsNative.DeserializeStyles(json)
                .Select(style => style with { DictName = ToDisplayDictionaryName(style.DictName) })
                .ToArray();
            _cachedStyles = styles;
            _logger.LogInformation(
                "[LookupTrace] trace={TraceId} styles native+deserialize completed in {Ms}ms total={TotalMs}ms styles={StyleCount} jsonBytes={JsonLength}",
                "-", nativeSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds, styles.Length, json?.Length ?? 0);
            return styles.ToList();
        }).ConfigureAwait(false);
    }

    public async Task<byte[]?> GetMediaFileAsync(string dictName, string mediaPath)
    {
        if (!_nativeAvailable)
            return null;

        await EnsureIndexAsync().ConfigureAwait(false);
        return await DictionaryNativeExecutor.RunAsync(_rebuildLock, () =>
        {
            var nativeDictName = ToNativeDictionaryName(dictName);
            var dataPtr = HoshiDictsNative.hoshi_get_media_file(
                _session, nativeDictName, mediaPath, out var size);
            return HoshiDictsNative.ReadBufferAndFree(dataPtr, size);
        }).ConfigureAwait(false);
    }

    public async Task RebuildQueryAsync()
    {
        if (!_nativeAvailable)
        {
            _logger.LogDebug("[Rebuild] Skipped: native not available");
            return;
        }

        var dictDir = _dictionaryStorageDir;
        if (!Directory.Exists(dictDir))
        {
            _logger.LogDebug("[Rebuild] Skipped: dict dir not found '{Dir}'", dictDir);
            await DictionaryNativeExecutor.RunAsync(_rebuildLock, () =>
            {
                _cachedStyles = null;
                _indexReady = true;
                return true;
            }).ConfigureAwait(false);
            return;
        }

        // Sync config with filesystem before acquiring the rebuild lock
        // so that file I/O does not block lookups.
        DictionaryImportService.NormalizeConfig(
            dictDir,
            GetActiveConfigRoot(),
            EnableUnconfiguredDictionariesForActiveProfile());

        var termPaths = GetOrderedDictionaryDirectories(
            dictDir,
            DictionaryType.Term,
            GetActiveConfigRoot(),
            EnableUnconfiguredDictionariesForActiveProfile());
        var freqPaths = GetOrderedDictionaryDirectories(
            dictDir,
            DictionaryType.Frequency,
            GetActiveConfigRoot(),
            EnableUnconfiguredDictionariesForActiveProfile());
        var pitchPaths = GetOrderedDictionaryDirectories(
            dictDir,
            DictionaryType.Pitch,
            GetActiveConfigRoot(),
            EnableUnconfiguredDictionariesForActiveProfile());

        try
        {
            await DictionaryNativeExecutor.RunAsync(_rebuildLock, () =>
            {
                _cachedStyles = null;
                _logger.LogInformation(
                    "[Rebuild] Passing paths to native: Term=[{TermPaths}], Freq=[{FreqPaths}], Pitch=[{PitchPaths}], Session={SessionPtr}",
                    string.Join(", ", termPaths), string.Join(", ", freqPaths), string.Join(", ", pitchPaths), _session);

                HoshiDictsNative.HoshiSessionRebuild(
                    _session,
                    termPaths,
                    freqPaths,
                    pitchPaths,
                    _activeLanguageId);

                _loadedDictDirs.Clear();
                _displayNameByNativeName.Clear();
                _nativeNameByDisplayName.Clear();
                foreach (var path in termPaths.Concat(freqPaths).Concat(pitchPaths))
                {
                    var name = Path.GetFileName(path);
                    if (string.IsNullOrEmpty(name))
                        continue;

                    var displayName = ReadDisplayTitle(path) ?? name;
                    _loadedDictDirs[name] = path;
                    _displayNameByNativeName[name] = displayName;
                    _nativeNameByDisplayName.TryAdd(displayName, name);
                }

                _indexReady = true;
                _logger.LogInformation(
                    "[Rebuild] Success: {TermCount} term, {FreqCount} freq, {PitchCount} pitch dictionaries loaded",
                    termPaths.Count, freqPaths.Count, pitchPaths.Count);
                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rebuild dictionary query");
        }
    }

    private async Task EnsureIndexAsync()
    {
        if (_indexReady) return;
        await RebuildQueryAsync();
    }

    private static string GetDictionaryStorageDir()
    {
        return Path.Combine(AppDataHelper.GetAppDataPath(), "dictionaries");
    }

    internal static IReadOnlyList<string> GetOrderedDictionaryDirectories(string dictDir, DictionaryType type) =>
        GetOrderedDictionaryDirectories(dictDir, type, dictDir, enableUnconfigured: true);

    internal static IReadOnlyList<string> GetOrderedDictionaryDirectories(
        string dictDir,
        DictionaryType type,
        string configRoot,
        bool enableUnconfigured)
    {
        var typeDir = DictionaryImportService.GetDictionaryTypeStorageDir(dictDir, type);
        var directories = Directory.Exists(typeDir)
            ? Directory.EnumerateDirectories(typeDir).ToList()
            : [];

        var byName = directories
            .Select(path => (Name: Path.GetFileName(path), Path: path))
            .Where(item => !string.IsNullOrEmpty(item.Name))
            .ToDictionary(item => item.Name!, item => item.Path, StringComparer.Ordinal);

        var config = DictionaryConfigurationStore.Load(configRoot);
        var installedForType = directories
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToList();

        var enabledEntries = DictionaryConfigurationStore
            .MergeWithInstalled(
                DictionaryConfigurationStore.GetEntries(config, type),
                installedForType,
                enableUnconfigured)
            .Where(entry => entry.IsEnabled)
            .ToList();

        return enabledEntries
            .Select(entry => byName.TryGetValue(entry.FileName, out var path) ? path : null)
            .Where(path => path != null)
            .Select(path => path!)
            .ToList();
    }

    public async Task SetActiveLanguageAsync(string languageId)
    {
        _activeLanguageId = ContentLanguageProfile.Normalize(languageId).Id;
        _indexReady = false;
        await RebuildQueryAsync();
    }

    internal Task SetActiveLanguageForTestsAsync(string languageId) =>
        SetActiveLanguageAsync(languageId);

    private string GetActiveConfigRoot() =>
        _profileContext == null
            ? _dictionaryStorageDir
            : _profileContext.GetDictionaryConfigRoot(_profileContext.ActiveProfileId);

    private bool EnableUnconfiguredDictionariesForActiveProfile()
    {
        var profileId = _profileContext?.ActiveProfileId;
        return profileId is null || _profileContext!.EnableUnconfiguredDictionariesForProfile(profileId);
    }

    private List<DictionaryLookupResult> ApplyDictionaryDisplayTitles(List<DictionaryLookupResult> results)
    {
        if (_displayNameByNativeName.Count == 0 || results.Count == 0)
            return results;

        return results
            .Select(result => result with
            {
                Term = result.Term with
                {
                    Glossaries = result.Term.Glossaries
                        .Select(glossary => glossary with
                        {
                            DictName = ToDisplayDictionaryName(glossary.DictName),
                        })
                        .ToList(),
                    Frequencies = result.Term.Frequencies
                        .Select(frequency => frequency with
                        {
                            DictName = ToDisplayDictionaryName(frequency.DictName),
                        })
                        .ToList(),
                    Pitches = result.Term.Pitches
                        .Select(pitch => pitch with
                        {
                            DictName = ToDisplayDictionaryName(pitch.DictName),
                        })
                        .ToList(),
                },
            })
            .ToList();
    }

    private string ToDisplayDictionaryName(string dictName) =>
        _displayNameByNativeName.TryGetValue(dictName, out var displayName)
            ? displayName
            : dictName;

    private string ToNativeDictionaryName(string dictName) =>
        _nativeNameByDisplayName.TryGetValue(dictName, out var nativeName)
            ? nativeName
            : dictName;

    private static string? ReadDisplayTitle(string dictionaryDir)
    {
        var indexPath = Path.Combine(dictionaryDir, "index.json");
        if (!File.Exists(indexPath))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(indexPath));
            return doc.RootElement.TryGetProperty("title", out var titleElement)
                ? titleElement.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_session != IntPtr.Zero)
        {
            HoshiDictsNative.hoshi_session_destroy(_session);
            _session = IntPtr.Zero;
        }
        _rebuildLock.Dispose();
    }
}
