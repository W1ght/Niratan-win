using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Hoshi.Helpers;
using Hoshi.Models.Dictionary;

namespace Hoshi.Services.Dictionary;

public sealed class DictionaryImportService : IDictionaryImportService
{
    private readonly ILogger<DictionaryImportService> _logger;
    private readonly IDictionaryLookupService _lookupService;

    public DictionaryImportService(
        ILogger<DictionaryImportService> logger,
        IDictionaryLookupService lookupService)
    {
        _logger = logger;
        _lookupService = lookupService;
    }

    public async Task<DictionaryImportResult> ImportAsync(string zipPath)
    {
        var result = await Task.Run(() => ImportCore(zipPath));
        if (result.Success)
            await _lookupService.RebuildQueryAsync();

        return result;
    }

    private DictionaryImportResult ImportCore(string zipPath)
    {
        var dictDir = GetDictionaryStorageDir();
        Directory.CreateDirectory(dictDir);

        using var archive = ZipFile.OpenRead(zipPath);

        var indexEntry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, "index.json", StringComparison.OrdinalIgnoreCase));
        if (indexEntry == null)
            return new DictionaryImportResult(false, "", 0, 0, 0, 0, 0, ["Missing index.json"]);

        string dictName;
        using (var stream = indexEntry.Open())
        using (var reader = new StreamReader(stream))
        {
            var indexJson = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(indexJson);
            dictName = SanitizeFileName(
                doc.RootElement.GetProperty("title").GetString() ?? "unknown");
        }

        var outputDir = Path.Combine(dictDir, dictName);

        // Remove existing
        if (Directory.Exists(outputDir))
            Directory.Delete(outputDir, recursive: true);
        Directory.CreateDirectory(outputDir);

        long termCount = 0, freqCount = 0, pitchCount = 0, mediaCount = 0;

        foreach (var entry in archive.Entries)
        {
            var destPath = Path.GetFullPath(Path.Combine(outputDir, entry.FullName));
            if (!destPath.StartsWith(outputDir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Zip slip prevented: {entry.FullName}");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);

            if (entry.Name.StartsWith("term_bank_", StringComparison.OrdinalIgnoreCase))
                termCount++;
            else if (entry.Name.StartsWith("freq_bank_", StringComparison.OrdinalIgnoreCase))
                freqCount++;
            else if (entry.Name.StartsWith("pitch_bank_", StringComparison.OrdinalIgnoreCase))
                pitchCount++;
            else if (entry.FullName.Contains("media/", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrEmpty(entry.Name))
                mediaCount++;
        }

        AppendDictionaryConfig(dictName, termCount, freqCount, pitchCount);

        _logger.LogInformation(
            "Imported dictionary '{Name}': {TermCount} term banks, {FreqCount} freq banks, {PitchCount} pitch banks, {MediaCount} media files",
            dictName, termCount, freqCount, pitchCount, mediaCount);

        return new DictionaryImportResult(true, dictName, termCount, 0, freqCount, pitchCount, mediaCount, []);
    }

    public async Task<bool> DeleteAsync(string dictName)
    {
        var deleted = await Task.Run(() =>
        {
            var dictDir = GetDictionaryStorageDir();
            var dictPath = Path.Combine(dictDir, dictName);
            if (!Directory.Exists(dictPath))
                return false;

            Directory.Delete(dictPath, recursive: true);
            RemoveDictionaryConfig(dictName);
            return true;
        });

        if (deleted)
            await _lookupService.RebuildQueryAsync();

        return deleted;
    }

    public Task<List<InstalledDictionary>> GetInstalledDictionariesAsync(DictionaryType? type = null)
    {
        return Task.Run(() =>
        {
            var dictDir = GetDictionaryStorageDir();
            if (!Directory.Exists(dictDir))
                return new List<InstalledDictionary>();

            var directories = Directory.EnumerateDirectories(dictDir).ToList();
            var config = NormalizeConfig(dictDir, directories);
            var types = type.HasValue ? [type.Value] : Enum.GetValues<DictionaryType>();

            var dictionaries = types
                .SelectMany(t => DictionaryConfigurationStore.GetEntries(config, t)
                    .Select(entry => ToInstalledDictionary(t, entry, dictDir)))
                .ToList();

            return dictionaries;
        });
    }

    public async Task SaveDictionaryOrderAsync(DictionaryType type, IReadOnlyList<string> orderedNames)
    {
        await Task.Run(() =>
        {
            var dictDir = GetDictionaryStorageDir();
            Directory.CreateDirectory(dictDir);
            var config = NormalizeConfig(dictDir, Directory.EnumerateDirectories(dictDir).ToList());
            var existing = DictionaryConfigurationStore.GetEntries(config, type)
                .ToDictionary(e => e.FileName, StringComparer.Ordinal);
            var entries = orderedNames
                .Where(existing.ContainsKey)
                .Select((name, index) => existing[name] with { Order = index })
                .ToList();
            config = DictionaryConfigurationStore.WithEntries(config, type, entries);
            DictionaryConfigurationStore.Save(dictDir, config);
        });

        await _lookupService.RebuildQueryAsync();
    }

    public async Task SetDictionaryEnabledAsync(DictionaryType type, string dictName, bool enabled)
    {
        await Task.Run(() =>
        {
            var dictDir = GetDictionaryStorageDir();
            Directory.CreateDirectory(dictDir);
            var config = NormalizeConfig(dictDir, Directory.EnumerateDirectories(dictDir).ToList());
            var entries = DictionaryConfigurationStore.GetEntries(config, type)
                .Select(entry => entry.FileName == dictName ? entry with { IsEnabled = enabled } : entry)
                .ToList();
            config = DictionaryConfigurationStore.WithEntries(config, type, entries);
            DictionaryConfigurationStore.Save(dictDir, config);
        });

        await _lookupService.RebuildQueryAsync();
    }

    private static string GetDictionaryStorageDir()
    {
        return Path.Combine(AppDataHelper.GetAppDataPath(), "dictionaries");
    }

    private static DictionaryConfig NormalizeConfig(string dictDir, IReadOnlyList<string> directories)
    {
        var installedByType = Enum.GetValues<DictionaryType>()
            .ToDictionary(type => type, _ => new List<string>());

        foreach (var dir in directories)
        {
            var name = Path.GetFileName(dir);
            foreach (var type in DetectDictionaryTypes(dir))
                installedByType[type].Add(name);
        }

        var config = DictionaryConfigurationStore.Load(dictDir);
        var normalized = config;
        foreach (var type in Enum.GetValues<DictionaryType>())
        {
            var entries = DictionaryConfigurationStore.MergeWithInstalled(
                DictionaryConfigurationStore.GetEntries(normalized, type),
                installedByType[type]);
            normalized = DictionaryConfigurationStore.WithEntries(normalized, type, entries);
        }

        DictionaryConfigurationStore.Save(dictDir, normalized);
        return normalized;
    }

    private static InstalledDictionary ToInstalledDictionary(
        DictionaryType type,
        DictionaryConfigEntry entry,
        string dictDir)
    {
        var revision = "";
        var indexPath = Path.Combine(dictDir, entry.FileName, "index.json");
        if (File.Exists(indexPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
                revision = doc.RootElement.TryGetProperty("revision", out var revisionElement)
                    ? revisionElement.GetString() ?? ""
                    : "";
            }
            catch
            {
                revision = "";
            }
        }

        return new InstalledDictionary(entry.FileName, type, entry.IsEnabled, entry.Order, revision);
    }

    private static IEnumerable<DictionaryType> DetectDictionaryTypes(string dir)
    {
        if (Directory.EnumerateFiles(dir, "term_bank_*.json").Any())
            yield return DictionaryType.Term;
        if (Directory.EnumerateFiles(dir, "freq_bank_*.json").Any())
            yield return DictionaryType.Frequency;
        if (Directory.EnumerateFiles(dir, "pitch_bank_*.json").Any())
            yield return DictionaryType.Pitch;
    }

    private static void AppendDictionaryConfig(string dictName, long termCount, long freqCount, long pitchCount)
    {
        var dictDir = GetDictionaryStorageDir();
        var config = NormalizeConfig(dictDir, Directory.EnumerateDirectories(dictDir).ToList());
        foreach (var type in ImportTypes(termCount, freqCount, pitchCount))
        {
            var entries = DictionaryConfigurationStore.GetEntries(config, type)
                .Where(e => !string.Equals(e.FileName, dictName, StringComparison.Ordinal))
                .ToList();
            entries.Add(new DictionaryConfigEntry(dictName, true, entries.Count));
            config = DictionaryConfigurationStore.WithEntries(config, type, entries);
        }
        DictionaryConfigurationStore.Save(dictDir, config);
    }

    private static void RemoveDictionaryConfig(string dictName)
    {
        var dictDir = GetDictionaryStorageDir();
        var config = DictionaryConfigurationStore.Load(dictDir);
        foreach (var type in Enum.GetValues<DictionaryType>())
        {
            var entries = DictionaryConfigurationStore.GetEntries(config, type)
                .Where(e => !string.Equals(e.FileName, dictName, StringComparison.Ordinal))
                .Select((entry, index) => entry with { Order = index })
                .ToList();
            config = DictionaryConfigurationStore.WithEntries(config, type, entries);
        }
        DictionaryConfigurationStore.Save(dictDir, config);
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

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized.Trim();
    }
}
