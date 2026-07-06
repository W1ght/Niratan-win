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

namespace Hoshi.Services.Dictionary;

public sealed class DictionaryLookupService : IDictionaryLookupService, IDisposable
{
    private readonly ILogger<DictionaryLookupService> _logger;
    private readonly string _dictionaryStorageDir;
    private readonly Dictionary<string, string> _loadedDictDirs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _displayNameByNativeName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _nativeNameByDisplayName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _styles = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);
    private IntPtr _session = IntPtr.Zero;
    private bool _indexReady;
    private bool _nativeAvailable = true;

    public DictionaryLookupService(ILogger<DictionaryLookupService> logger, string? dictionaryStorageDir = null)
    {
        _logger = logger;
        _dictionaryStorageDir = dictionaryStorageDir ?? GetDictionaryStorageDir();
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
        await EnsureIndexAsync();
        _logger.LogInformation(
            "[LookupTrace] trace={TraceId} ensure-index completed in {Ms}ms total={TotalMs}ms indexReady={IndexReady}",
            traceId ?? "-", phaseSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds, _indexReady);

        _logger.LogInformation(
            "[Lookup] Session={SessionPtr}, indexReady={IndexReady}, text='{Text}', max={Max}, scan={Scan}",
            _session, _indexReady, text, maxResults, scanLength);

        phaseSw.Restart();
        await _rebuildLock.WaitAsync();
        _logger.LogInformation(
            "[LookupTrace] trace={TraceId} rebuild-lock acquired in {Ms}ms total={TotalMs}ms",
            traceId ?? "-", phaseSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds);
        try
        {
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
        }
        finally
        {
            _rebuildLock.Release();
        }
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

        await EnsureIndexAsync();
        await _rebuildLock.WaitAsync();
        try
        {
            var jsonPtr = HoshiDictsNative.hoshi_get_styles(_session);
            var json = HoshiDictsNative.ReadStringAndFree(jsonPtr);
            return HoshiDictsNative.DeserializeStyles(json)
                .Select(style => style with { DictName = ToDisplayDictionaryName(style.DictName) })
                .ToList();
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    public async Task<byte[]?> GetMediaFileAsync(string dictName, string mediaPath)
    {
        if (!_nativeAvailable)
            return null;

        await EnsureIndexAsync();
        await _rebuildLock.WaitAsync();
        try
        {
            var nativeDictName = ToNativeDictionaryName(dictName);
            var dataPtr = HoshiDictsNative.hoshi_get_media_file(
                _session, nativeDictName, mediaPath, out var size);
            return HoshiDictsNative.ReadBufferAndFree(dataPtr, size);
        }
        finally
        {
            _rebuildLock.Release();
        }
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
            _indexReady = true;
            return;
        }

        // Sync config with filesystem before acquiring the rebuild lock
        // so that file I/O does not block lookups.
        DictionaryImportService.NormalizeConfig(dictDir);

        await _rebuildLock.WaitAsync();
        try
        {
            var termPaths = GetOrderedDictionaryDirectories(dictDir, DictionaryType.Term);
            var freqPaths = GetOrderedDictionaryDirectories(dictDir, DictionaryType.Frequency);
            var pitchPaths = GetOrderedDictionaryDirectories(dictDir, DictionaryType.Pitch);

            _logger.LogInformation(
                "[Rebuild] Passing paths to native: Term=[{TermPaths}], Freq=[{FreqPaths}], Pitch=[{PitchPaths}], Session={SessionPtr}",
                string.Join(", ", termPaths), string.Join(", ", freqPaths), string.Join(", ", pitchPaths), _session);

            HoshiDictsNative.HoshiSessionRebuild(
                _session,
                termPaths,
                freqPaths,
                pitchPaths);

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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rebuild dictionary query");
        }
        finally
        {
            _rebuildLock.Release();
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

    internal static IReadOnlyList<string> GetOrderedDictionaryDirectories(string dictDir, DictionaryType type)
    {
        var typeDir = DictionaryImportService.GetDictionaryTypeStorageDir(dictDir, type);
        var directories = Directory.Exists(typeDir)
            ? Directory.EnumerateDirectories(typeDir).ToList()
            : [];

        var byName = directories
            .Select(path => (Name: Path.GetFileName(path), Path: path))
            .Where(item => !string.IsNullOrEmpty(item.Name))
            .ToDictionary(item => item.Name!, item => item.Path, StringComparer.Ordinal);

        var config = DictionaryConfigurationStore.Load(dictDir);
        var installedForType = directories
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToList();

        var enabledEntries = DictionaryConfigurationStore
            .MergeWithInstalled(
                DictionaryConfigurationStore.GetEntries(config, type),
                installedForType)
            .Where(entry => entry.IsEnabled)
            .ToList();

        return enabledEntries
            .Select(entry => byName.TryGetValue(entry.FileName, out var path) ? path : null)
            .Where(path => path != null)
            .Select(path => path!)
            .ToList();
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
