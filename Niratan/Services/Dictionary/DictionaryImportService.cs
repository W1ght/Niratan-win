using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Niratan.Helpers;
using Niratan.Models.Dictionary;

namespace Niratan.Services.Dictionary;

public sealed class DictionaryImportService : IDictionaryImportService, IDisposable
{
    private readonly ILogger<DictionaryImportService> _logger;
    private readonly IDictionaryLookupService _lookupService;
    private readonly string _dictionaryStorageDir;
    private readonly IDictionaryProfileContext? _profileContext;
    private readonly SemaphoreSlim _fsLock = new(1, 1);

    public DictionaryImportService(
        ILogger<DictionaryImportService> logger,
        IDictionaryLookupService lookupService,
        string? dictionaryStorageDir = null,
        IDictionaryProfileContext? profileContext = null)
    {
        _logger = logger;
        _lookupService = lookupService;
        _dictionaryStorageDir = dictionaryStorageDir ?? GetDictionaryStorageDir();
        _profileContext = profileContext;
    }

    public async Task<DictionaryImportResult> ImportAsync(string zipPath)
    {
        await _fsLock.WaitAsync();
        try
        {
            var result = await Task.Run(() =>
            {
                var dictDir = _dictionaryStorageDir;
                Directory.CreateDirectory(dictDir);
                EnsureTypeDirectories(dictDir);

                var importId = Guid.NewGuid().ToString("N");
                var stagingRoot = Path.Combine(dictDir, $".dictionary-import-{importId}");
                Directory.CreateDirectory(stagingRoot);

                // Copy ZIP to a local temp file first, then import from the local
                // copy. This preserves hoshidicts import compatibility by copying the
                // InputStream to a temp ZIP before calling the native bridge.
                // It also avoids potential path encoding issues with CreateFileA
                // on Windows (which expects ANSI, not UTF-8).
                var localZip = Path.Combine(stagingRoot, $".import-{importId}.zip");
                try
                {
                    File.Copy(zipPath, localZip, overwrite: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Import] Failed to copy ZIP from {Source} to {Dest}: {ExType}: {Message}",
                        zipPath, localZip, ex.GetType().Name, ex.Message);
                    return new DictionaryImportResult(false, "", 0, 0, 0, 0, 0,
                        [$"Cannot access the selected file. Try moving the dictionary ZIP to a local folder first. ({ex.Message})"]);
                }
                var timedOut = false;
                try
                {
                    var nativeResult = RunNativeImport(localZip, stagingRoot);
                    string? restoredTitle = null;

                    if (!nativeResult.Success && IsCompatibilityRetryCandidate(nativeResult))
                    {
                        _logger.LogWarning(
                            "[Import] Native import failed for '{Zip}'. Retrying with Niratan-compatible ASCII import zip. Errors: {Errors}",
                            localZip,
                            string.Join("; ", nativeResult.Errors ?? []));
                        ClearStagingImportedDictionaries(stagingRoot);
                        var compatZipPath = Path.Combine(stagingRoot, $".import-{importId}.compat.zip");
                        var compatZip = CreateCompatibilityImportZip(localZip, compatZipPath, importId);
                        restoredTitle = compatZip.OriginalTitle;
                        nativeResult = RunNativeImport(compatZip.Path, stagingRoot);
                    }

                    if (nativeResult.TimedOut)
                    {
                        timedOut = true;
                        _logger.LogWarning("[Import] Native import timed out (likely importer crash) for '{Zip}'", localZip);
                        return new DictionaryImportResult(
                            false, "", 0, 0, 0, 0, 0,
                            nativeResult.Errors ?? ["Dictionary import timed out — this dictionary format may not be compatible"]);
                    }

                    if (!nativeResult.Success)
                        return new DictionaryImportResult(
                            false, nativeResult.Title,
                            nativeResult.TermCount, nativeResult.MetaCount,
                            nativeResult.FreqCount, nativeResult.PitchCount,
                            nativeResult.MediaCount,
                            nativeResult.Errors ?? []);

                    var resultTitle = !string.IsNullOrWhiteSpace(restoredTitle)
                        ? restoredTitle!
                        : nativeResult.Title;
                    var importedDir = ResolveImportedDictionaryDirectory(stagingRoot, nativeResult.Title);
                    if (importedDir == null)
                        return new DictionaryImportResult(
                            false, resultTitle,
                            nativeResult.TermCount, nativeResult.MetaCount,
                            nativeResult.FreqCount, nativeResult.PitchCount,
                            nativeResult.MediaCount,
                            ["Native import did not create a dictionary directory"]);

                    if (!string.IsNullOrWhiteSpace(restoredTitle))
                        RewriteImportedIndexTitle(importedDir, restoredTitle!);

                    var dictName = Path.GetFileName(importedDir);
                    foreach (var type in ImportTypes(nativeResult.TermCount, nativeResult.FreqCount, nativeResult.PitchCount))
                    {
                        var typeDir = GetDictionaryTypeStorageDir(dictDir, type);
                        Directory.CreateDirectory(typeDir);
                        var targetDir = Path.Combine(typeDir, dictName);
                        if (!Directory.Exists(targetDir))
                            CopyDirectory(importedDir, targetDir);
                        EnableImportedDictionaryInActiveConfig(type, dictName);
                    }

                    _logger.LogInformation(
                        "Imported dictionary '{Name}': {TermCount} terms, {FreqCount} freq, {PitchCount} pitch, {MediaCount} media",
                        resultTitle, nativeResult.TermCount, nativeResult.FreqCount, nativeResult.PitchCount, nativeResult.MediaCount);

                    return new DictionaryImportResult(
                        true, resultTitle,
                        nativeResult.TermCount, nativeResult.MetaCount,
                        nativeResult.FreqCount, nativeResult.PitchCount,
                        nativeResult.MediaCount,
                        []);
                }
                finally
                {
                    // Skip cleanup on timeout — import threads may still be running
                    if (!timedOut && Directory.Exists(stagingRoot))
                        Directory.Delete(stagingRoot, recursive: true);
                }
            });

            if (result.Success)
            {
                try
                    {
                        NormalizeActiveConfig(_dictionaryStorageDir);
                        await _lookupService.RebuildQueryAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Import] Rebuild failed (type={ExType}) after import of '{Title}'",
                            ex.GetType().Name, result.Title);
                        return new DictionaryImportResult(true, result.Title, result.TermCount, result.MetaCount,
                            result.FreqCount, result.PitchCount, result.MediaCount,
                            [$"Dictionary imported but lookup index rebuild failed [{ex.GetType().Name}]: {ex.Message}. Please restart the app."]);
                    }
            }

            return result;
        }
        finally
        {
            _fsLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string dictName)
    {
        await _fsLock.WaitAsync();
        try
        {
            var deleted = false;
            foreach (var type in Enum.GetValues<DictionaryType>())
                deleted |= await DeleteTypeInternalAsync(type, dictName);

            if (deleted)
                await _lookupService.RebuildQueryAsync();

            return deleted;
        }
        finally
        {
            _fsLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(DictionaryType type, string dictName)
    {
        await _fsLock.WaitAsync();
        try
        {
            var deleted = await DeleteTypeInternalAsync(type, dictName);
            if (deleted)
                await _lookupService.RebuildQueryAsync();

            return deleted;
        }
        finally
        {
            _fsLock.Release();
        }
    }

    private Task<bool> DeleteTypeInternalAsync(DictionaryType type, string dictName)
    {
        return Task.Run(() =>
        {
            var dictDir = _dictionaryStorageDir;
            var dictPath = Path.Combine(GetDictionaryTypeStorageDir(dictDir, type), dictName);
            if (!Directory.Exists(dictPath))
                return false;

            Directory.Delete(dictPath, recursive: true);
            RemoveDictionaryConfigFromAllProfiles(type, dictName);
            return true;
        });
    }

    public async Task<List<InstalledDictionary>> GetInstalledDictionariesAsync(DictionaryType? type = null)
    {
        await _fsLock.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                var dictDir = _dictionaryStorageDir;
                if (!Directory.Exists(dictDir))
                    return new List<InstalledDictionary>();

                var config = NormalizeActiveConfig(dictDir);
                var types = type.HasValue ? [type.Value] : Enum.GetValues<DictionaryType>();

                var dictionaries = types
                    .SelectMany(t => DictionaryConfigurationStore.GetEntries(config, t)
                        .Select(entry => ToInstalledDictionary(t, entry, dictDir)))
                    .ToList();

                return dictionaries;
            });
        }
        finally
        {
            _fsLock.Release();
        }
    }

    public async Task SaveDictionaryOrderAsync(DictionaryType type, IReadOnlyList<string> orderedNames)
    {
        await _fsLock.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                var dictDir = _dictionaryStorageDir;
                Directory.CreateDirectory(dictDir);
                var configRoot = GetActiveConfigRoot();
                var config = NormalizeActiveConfig(dictDir);
                var existing = DictionaryConfigurationStore.GetEntries(config, type)
                    .ToDictionary(e => e.FileName, StringComparer.Ordinal);
                var entries = orderedNames
                    .Where(existing.ContainsKey)
                    .Select((name, index) => existing[name] with { Order = index })
                    .ToList();
                config = DictionaryConfigurationStore.WithEntries(config, type, entries);
                DictionaryConfigurationStore.Save(configRoot, config);
            });

            await _lookupService.RebuildQueryAsync();
        }
        finally
        {
            _fsLock.Release();
        }
    }

    public async Task SetDictionaryEnabledAsync(DictionaryType type, string dictName, bool enabled)
    {
        await _fsLock.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                var dictDir = _dictionaryStorageDir;
                Directory.CreateDirectory(dictDir);
                var configRoot = GetActiveConfigRoot();
                var config = NormalizeActiveConfig(dictDir);
                var entries = DictionaryConfigurationStore.GetEntries(config, type)
                    .Select(entry => entry.FileName == dictName ? entry with { IsEnabled = enabled } : entry)
                    .ToList();
                config = DictionaryConfigurationStore.WithEntries(config, type, entries);
                DictionaryConfigurationStore.Save(configRoot, config);
            });

            await _lookupService.RebuildQueryAsync();
        }
        finally
        {
            _fsLock.Release();
        }
    }

    internal static string GetDictionaryStorageDir()
    {
        return Path.Combine(AppDataHelper.GetAppDataPath(), "dictionaries");
    }

    internal static string GetDictionaryTypeStorageDir(string dictDir, DictionaryType type) =>
        Path.Combine(dictDir, type.ToString());

    private static NativeImportResultJson RunNativeImport(string zipPath, string outputDir)
    {
        string? json;
        try
        {
            var jsonPtr = HoshiDictsNative.hoshi_import(zipPath, outputDir);
            json = HoshiDictsNative.ReadStringAndFree(jsonPtr);
        }
        catch (Exception ex)
        {
            return new NativeImportResultJson(
                Success: false,
                Errors: [$"Dictionary backend error [{ex.GetType().Name}]: {ex.Message}"]);
        }

        if (string.IsNullOrWhiteSpace(json))
            return new NativeImportResultJson(false, Errors: ["Native import returned no result"]);

        try
        {
            return JsonSerializer.Deserialize<NativeImportResultJson>(
                       json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new NativeImportResultJson(false, Errors: ["Failed to parse import result"]);
        }
        catch (JsonException ex)
        {
            return new NativeImportResultJson(false, Errors: [$"Failed to parse import result: {ex.Message}"]);
        }
    }

    private static bool IsCompatibilityRetryCandidate(NativeImportResultJson result)
    {
        if (!OperatingSystem.IsWindows() || result.Success)
            return false;

        var errors = result.Errors ?? [];
        if (errors.Count == 0)
            return true;

        return errors.Any(error =>
            error.Contains("SEHException", StringComparison.OrdinalIgnoreCase)
            || error.Contains("External component has thrown an exception", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Unicode character", StringComparison.OrdinalIgnoreCase)
            || error.Contains("multi-byte code page", StringComparison.OrdinalIgnoreCase)
            || error.Contains("code page", StringComparison.OrdinalIgnoreCase));
    }

    internal sealed record CompatibilityImportZip(string Path, string? OriginalTitle);

    internal static CompatibilityImportZip CreateCompatibilityImportZip(
        string sourceZipPath,
        string outputZipPath,
        string importId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputZipPath)!);
        if (File.Exists(outputZipPath))
            File.Delete(outputZipPath);

        string? originalTitle = null;
        using var sourceStream = File.OpenRead(sourceZipPath);
        using var sourceArchive = new ZipArchive(sourceStream, ZipArchiveMode.Read, leaveOpen: false);
        using var outputStream = File.Create(outputZipPath);
        using var outputArchive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);

        foreach (var entry in sourceArchive.Entries)
        {
            var name = ValidateCompatibilityEntryName(entry.FullName);
            if (string.IsNullOrWhiteSpace(name) || name.EndsWith("/", StringComparison.Ordinal))
                continue;
            if (!IsLookupCompatibilityCoreEntry(name))
                continue;

            byte[] bytes;
            using (var entryStream = entry.Open())
            using (var ms = new MemoryStream())
            {
                entryStream.CopyTo(ms);
                bytes = ms.ToArray();
            }

            if (string.Equals(name, "index.json", StringComparison.Ordinal))
            {
                var indexJson = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
                originalTitle = ReadIndexTitle(indexJson);
                bytes = RewriteIndexTitleForCompatibilityImport(indexJson, importId);
            }

            var outputEntry = outputArchive.CreateEntry(name, CompressionLevel.NoCompression);
            using var outputEntryStream = outputEntry.Open();
            outputEntryStream.Write(bytes);
        }

        if (originalTitle == null)
            throw new InvalidDataException("Dictionary zip does not contain index.json.");

        return new CompatibilityImportZip(outputZipPath, originalTitle);
    }

    private static string ValidateCompatibilityEntryName(string entryName)
    {
        var normalized = entryName.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains('\0'))
            throw new InvalidDataException("Dictionary zip contains an invalid empty entry path.");
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Contains(':', StringComparison.Ordinal))
            throw new InvalidDataException($"Dictionary zip entry path must be relative: {normalized}");

        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
                throw new InvalidDataException($"Dictionary zip entry path must stay inside the archive: {normalized}");
        }

        return normalized;
    }

    private static bool IsLookupCompatibilityCoreEntry(string name) =>
        string.Equals(name, "index.json", StringComparison.Ordinal)
        || string.Equals(name, "styles.css", StringComparison.Ordinal)
        || IsNumberedYomitanBank(name, "term_bank_", ".json")
        || IsNumberedYomitanBank(name, "term_meta_bank_", ".json")
        || IsNumberedYomitanBank(name, "tag_bank_", ".json");

    private static bool IsNumberedYomitanBank(string name, string prefix, string suffix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || !name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var number = name[prefix.Length..^suffix.Length];
        return number.Length > 0 && number.All(char.IsAsciiDigit);
    }

    private static string? ReadIndexTitle(string indexJson)
    {
        try
        {
            var node = JsonNode.Parse(indexJson);
            return node?["title"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsCompatibilityImportDirectoryName(string name) =>
        name.StartsWith("niratan-import-", StringComparison.Ordinal);

    private static byte[] RewriteIndexTitleForCompatibilityImport(string indexJson, string importId)
    {
        var node = JsonNode.Parse(indexJson)
                   ?? throw new InvalidDataException("Cannot parse dictionary index.json.");
        node["title"] = $"niratan-import-{importId}";
        return JsonSerializer.SerializeToUtf8Bytes(node);
    }

    private static void RewriteImportedIndexTitle(string importedDir, string title)
    {
        var indexPath = Path.Combine(importedDir, "index.json");
        if (!File.Exists(indexPath))
            return;

        var node = JsonNode.Parse(File.ReadAllText(indexPath, Encoding.UTF8));
        if (node == null)
            return;

        node["title"] = title;
        File.WriteAllBytes(indexPath, JsonSerializer.SerializeToUtf8Bytes(node));
    }

    private static void ClearStagingImportedDictionaries(string stagingRoot)
    {
        foreach (var dir in Directory.EnumerateDirectories(stagingRoot).ToList())
            Directory.Delete(dir, recursive: true);
    }

    internal static DictionaryConfig NormalizeConfig(string dictDir)
    {
        return NormalizeConfig(dictDir, dictDir, enableUnconfigured: true);
    }

    internal static DictionaryConfig NormalizeConfig(
        string dictDir,
        string configRoot,
        bool enableUnconfigured)
    {
        Directory.CreateDirectory(dictDir);
        Directory.CreateDirectory(configRoot);
        EnsureTypeDirectories(dictDir);
        CleanupAbandonedImportStagingDirs(dictDir);
        MigrateLegacyFlatDictionaries(dictDir);
        FixCorrectlyNamedDirectoriesForNative(dictDir);

        var installedByType = Enum.GetValues<DictionaryType>()
            .ToDictionary(type => type, _ => new List<string>());

        foreach (var type in Enum.GetValues<DictionaryType>())
        {
            var typeDir = GetDictionaryTypeStorageDir(dictDir, type);
            if (!Directory.Exists(typeDir))
                continue;

            installedByType[type].AddRange(Directory
                .EnumerateDirectories(typeDir)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!));
        }

        var config = DictionaryConfigurationStore.Load(configRoot);
        var normalized = DictionaryConfigurationStore.NormalizeForInstalled(
            config,
            Enum.GetValues<DictionaryType>(),
            type => installedByType[type],
            enableUnconfigured);

        DictionaryConfigurationStore.Save(configRoot, normalized);
        return normalized;
    }

    private static InstalledDictionary ToInstalledDictionary(
        DictionaryType type,
        DictionaryConfigEntry entry,
        string dictDir)
    {
        var revision = "";
        var displayTitle = entry.FileName;
        var indexPath = Path.Combine(GetDictionaryTypeStorageDir(dictDir, type), entry.FileName, "index.json");
        if (File.Exists(indexPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
                revision = doc.RootElement.TryGetProperty("revision", out var revisionElement)
                    ? revisionElement.GetString() ?? ""
                    : "";
                displayTitle = doc.RootElement.TryGetProperty("title", out var titleElement)
                    ? titleElement.GetString() ?? entry.FileName
                    : entry.FileName;
            }
            catch
            {
                revision = "";
            }
        }

        return new InstalledDictionary(entry.FileName, type, entry.IsEnabled, entry.Order, revision, displayTitle);
    }

    private static string? ResolveImportedDictionaryDirectory(string stagingRoot, string title)
    {
        // Aligns with Android commitStagedDictionariesByType: iterate all
        // subdirectories and find the one that contains index.json (a real
        // dictionary), preferring a title match.
        var dictDirs = Directory.EnumerateDirectories(stagingRoot)
            .Where(dir =>
            {
                var name = Path.GetFileName(dir);
                return !name.StartsWith(".", StringComparison.Ordinal)
                       && File.Exists(Path.Combine(dir, "index.json"));
            })
            .ToList();

        if (dictDirs.Count == 0)
        {
            // Fallback: staging root itself might be the dictionary directory
            // (if the ZIP's root contains index.json).
            if (File.Exists(Path.Combine(stagingRoot, "index.json")))
                return stagingRoot;
            return null;
        }

        // Prefer the directory whose name matches the reported title.
        if (!string.IsNullOrWhiteSpace(title))
        {
            var match = dictDirs.FirstOrDefault(
                dir => string.Equals(Path.GetFileName(dir), title, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        // If exactly one dictionary directory was found, use it.
        if (dictDirs.Count == 1)
            return dictDirs[0];

        // Multiple dictionary directories — this is unexpected for a single-ZIP
        // import. Return the first one and log a warning.
        return dictDirs[0];
    }

    private static void EnsureTypeDirectories(string dictDir)
    {
        foreach (var type in Enum.GetValues<DictionaryType>())
            Directory.CreateDirectory(GetDictionaryTypeStorageDir(dictDir, type));
    }

    private static void MigrateLegacyFlatDictionaries(string dictDir)
    {
        var config = DictionaryConfigurationStore.Load(dictDir);
        var typeNames = Enum.GetValues<DictionaryType>()
            .Select(type => type.ToString())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var dir in Directory.EnumerateDirectories(dictDir).ToList())
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(name)
                || name.StartsWith(".", StringComparison.Ordinal)
                || typeNames.Contains(name))
            {
                continue;
            }

            var targetTypes = Enum.GetValues<DictionaryType>()
                .Where(type => DictionaryConfigurationStore
                    .GetEntries(config, type)
                    .Any(entry => entry.FileName == name))
                .ToList();

            if (targetTypes.Count == 0)
                targetTypes = DetectDictionaryTypes(dir).DefaultIfEmpty(DictionaryType.Term).ToList();

            // Only delete the original flat directory after ALL copies succeed.
            var copied = new List<string>();
            try
            {
                foreach (var type in targetTypes)
                {
                    var targetDir = Path.Combine(GetDictionaryTypeStorageDir(dictDir, type), name);
                    if (!Directory.Exists(targetDir))
                    {
                        CopyDirectory(dir, targetDir);
                        copied.Add(targetDir);
                    }
                }

                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Clean up partial copies so the next NormalizeConfig can retry.
                foreach (var copiedDir in copied)
                {
                    try { Directory.Delete(copiedDir, recursive: true); }
#pragma warning disable S108
                    catch { /* best effort */ }
#pragma warning restore S108
                }
            }
        }
    }

    // Cleans up abandoned staging directories from previous import timeouts.
    private static void CleanupAbandonedImportStagingDirs(string dictDir)
    {
        foreach (var dir in Directory.EnumerateDirectories(dictDir, ".dictionary-import-*"))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    // Renames dictionary directories to match their actual title from index.json.
    // Previously, the native library interpreted UTF-8 paths as the system ANSI
    // code page (GBK), creating garbled directory names. With the UTF-8
    // activeCodePage manifest, directory names should match the JSON title.
    private static void FixCorrectlyNamedDirectoriesForNative(string dictDir)
    {
        foreach (var type in Enum.GetValues<DictionaryType>())
        {
            var typeDir = GetDictionaryTypeStorageDir(dictDir, type);
            if (!Directory.Exists(typeDir))
                continue;

            foreach (var dir in Directory.EnumerateDirectories(typeDir).ToList())
            {
                var indexPath = Path.Combine(dir, "index.json");
                if (!File.Exists(indexPath))
                    continue;

                string? titleFromJson;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
                    titleFromJson = doc.RootElement.TryGetProperty("title", out var titleElement)
                        ? titleElement.GetString()
                        : null;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(titleFromJson))
                    continue;

                var currentName = Path.GetFileName(dir);
                if (IsCompatibilityImportDirectoryName(currentName))
                    continue;
                if (string.Equals(currentName, titleFromJson, StringComparison.Ordinal))
                    continue;

                // Rename garbled directory to its proper title
                var properDir = Path.Combine(typeDir, titleFromJson);
                if (Directory.Exists(properDir))
                {
                    try { Directory.Delete(dir, recursive: true); }
                    catch { /* best effort */ }
                }
                else
                {
                    try { Directory.Move(dir, properDir); }
                    catch { /* best effort */ }
                }
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, directory);
            Directory.CreateDirectory(Path.Combine(targetDir, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var targetFile = Path.Combine(targetDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, overwrite: false);
        }
    }

    private static IEnumerable<DictionaryType> DetectDictionaryTypes(string dir)
    {
        var indexPath = Path.Combine(dir, "index.json");
        if (!File.Exists(indexPath))
            yield break;

        // hoshidicts native binary format: type comes from config
        if (File.Exists(Path.Combine(dir, ".hoshidicts_1"))
            || File.Exists(Path.Combine(dir, ".hoshidicts_2")))
        {
            var dictName = Path.GetFileName(dir);
            var config = DictionaryConfigurationStore.Load(Path.GetDirectoryName(dir) ?? GetDictionaryStorageDir());
            var found = false;
            foreach (var type in Enum.GetValues<DictionaryType>())
            {
                var entries = DictionaryConfigurationStore.GetEntries(config, type);
                if (entries.Any(e => e.FileName == dictName))
                {
                    yield return type;
                    found = true;
                }
            }
            if (!found)
                yield return DictionaryType.Term;
            yield break;
        }

        // Legacy JSON-format dictionary detection
        var kinds = Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories)
            .Select(path => DetectBankKind(Path.GetFileName(path), path))
            .ToHashSet();

        if (kinds.Contains(DictionaryBankKind.Term))
            yield return DictionaryType.Term;
        if (kinds.Contains(DictionaryBankKind.Frequency))
            yield return DictionaryType.Frequency;
        if (kinds.Contains(DictionaryBankKind.Pitch))
            yield return DictionaryType.Pitch;
    }

    private void RemoveDictionaryConfigFromAllProfiles(DictionaryType type, string dictName)
    {
        foreach (var configRoot in GetConfigRootsForCleanup())
            RemoveDictionaryConfig(configRoot, type, dictName);
    }

    private IEnumerable<string> GetConfigRootsForCleanup()
    {
        yield return _dictionaryStorageDir;

        if (_profileContext == null)
            yield break;

        foreach (var profileId in _profileContext.ProfileIds)
            yield return _profileContext.GetDictionaryConfigRoot(profileId);
    }

    private DictionaryConfig NormalizeActiveConfig(string dictDir)
    {
        var profileId = _profileContext?.ActiveProfileId;
        return NormalizeConfig(
            dictDir,
            GetActiveConfigRoot(),
            profileId is null || _profileContext!.EnableUnconfiguredDictionariesForProfile(profileId));
    }

    private void EnableImportedDictionaryInActiveConfig(DictionaryType type, string dictName)
    {
        var configRoot = GetActiveConfigRoot();
        var config = DictionaryConfigurationStore.Load(configRoot);
        var entries = DictionaryConfigurationStore.GetEntries(config, type).ToList();
        var existingIndex = entries.FindIndex(entry => string.Equals(entry.FileName, dictName, StringComparison.Ordinal));
        if (existingIndex >= 0)
        {
            entries[existingIndex] = entries[existingIndex] with { IsEnabled = true };
        }
        else
        {
            var nextOrder = entries.Count == 0 ? 0 : entries.Max(entry => entry.Order) + 1;
            entries.Add(new DictionaryConfigEntry(dictName, true, nextOrder));
        }

        config = DictionaryConfigurationStore.WithEntries(config, type, entries);
        DictionaryConfigurationStore.Save(configRoot, config);
    }

    private string GetActiveConfigRoot() =>
        _profileContext == null
            ? _dictionaryStorageDir
            : _profileContext.GetDictionaryConfigRoot(_profileContext.ActiveProfileId);

    private static void RemoveDictionaryConfig(string configRoot, DictionaryType type, string dictName)
    {
        var config = DictionaryConfigurationStore.Load(configRoot);
        var entries = DictionaryConfigurationStore.GetEntries(config, type)
            .Where(e => !string.Equals(e.FileName, dictName, StringComparison.Ordinal))
            .Select((entry, index) => entry with { Order = index })
            .ToList();
        config = DictionaryConfigurationStore.WithEntries(config, type, entries);
        DictionaryConfigurationStore.Save(configRoot, config);
    }

    private static IEnumerable<DictionaryType> ImportTypes(long termCount, long freqCount, long pitchCount)
    {
        if (termCount > 0)
            yield return DictionaryType.Term;
        if (freqCount > 0)
            yield return DictionaryType.Frequency;
        if (pitchCount > 0)
            yield return DictionaryType.Pitch;
    }

    internal enum DictionaryBankKind
    {
        Unknown,
        Term,
        Meta,
        Frequency,
        Pitch,
    }

    internal static DictionaryBankKind DetectBankKind(string fileName, string? extractedPath = null)
    {
        if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return DictionaryBankKind.Unknown;

        if (fileName.StartsWith("term_bank_", StringComparison.OrdinalIgnoreCase))
            return DictionaryBankKind.Term;
        if (fileName.StartsWith("freq_bank_", StringComparison.OrdinalIgnoreCase))
            return DictionaryBankKind.Frequency;
        if (fileName.StartsWith("pitch_bank_", StringComparison.OrdinalIgnoreCase))
            return DictionaryBankKind.Pitch;

        if (!fileName.StartsWith("meta_bank_", StringComparison.OrdinalIgnoreCase)
            && !fileName.StartsWith("term_meta_bank_", StringComparison.OrdinalIgnoreCase))
            return DictionaryBankKind.Unknown;

        var content = "";
        if (!string.IsNullOrWhiteSpace(extractedPath) && File.Exists(extractedPath))
        {
            try
            {
                content = File.ReadAllText(extractedPath);
            }
            catch
            {
                content = "";
            }
        }

        return DetectMetadataBankKind(content);
    }

    internal static DictionaryBankKind DetectMetadataBankKind(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return DictionaryBankKind.Meta;

        if (content.Contains("\"pitch\"", StringComparison.OrdinalIgnoreCase)
            || content.Contains("\"ipa\"", StringComparison.OrdinalIgnoreCase)
            || content.Contains("\"transcriptions\"", StringComparison.OrdinalIgnoreCase))
            return DictionaryBankKind.Pitch;
        if (content.Contains("\"freq\"", StringComparison.OrdinalIgnoreCase)
            || content.Contains("\"frequency\"", StringComparison.OrdinalIgnoreCase))
            return DictionaryBankKind.Frequency;

        return DictionaryBankKind.Meta;
    }

    private static bool IsLikelyMediaEntry(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return false;

        var extension = Path.GetExtension(fullName).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".avif" or ".heic" or ".svg";
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized.Trim();
    }

    public void Dispose()
    {
        _fsLock.Dispose();
    }
}
