using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hoshi.Models.Dictionary;

namespace Hoshi.Services.Dictionary;

public static class DictionaryConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static DictionaryConfig Load(string dictionaryRoot)
    {
        var configPath = GetConfigPath(dictionaryRoot);
        if (File.Exists(configPath))
        {
            try
            {
                return JsonSerializer.Deserialize<DictionaryConfig>(
                    File.ReadAllText(configPath),
                    JsonOptions) ?? DictionaryConfig.Empty;
            }
            catch
            {
                return DictionaryConfig.Empty;
            }
        }

        return LoadLegacyOrder(dictionaryRoot);
    }

    public static void Save(string dictionaryRoot, DictionaryConfig config)
    {
        Directory.CreateDirectory(dictionaryRoot);
        File.WriteAllText(GetConfigPath(dictionaryRoot), JsonSerializer.Serialize(config, JsonOptions));
    }

    public static IReadOnlyList<DictionaryConfigEntry> GetEntries(
        DictionaryConfig config,
        DictionaryType type) =>
        type switch
        {
            DictionaryType.Term => config.TermDictionaries,
            DictionaryType.Frequency => config.FrequencyDictionaries,
            DictionaryType.Pitch => config.PitchDictionaries,
            _ => [],
        };

    public static DictionaryConfig WithEntries(
        DictionaryConfig config,
        DictionaryType type,
        List<DictionaryConfigEntry> entries) =>
        type switch
        {
            DictionaryType.Term => config with { TermDictionaries = entries },
            DictionaryType.Frequency => config with { FrequencyDictionaries = entries },
            DictionaryType.Pitch => config with { PitchDictionaries = entries },
            _ => config,
        };

    public static List<DictionaryConfigEntry> MergeWithInstalled(
        IReadOnlyList<DictionaryConfigEntry> configured,
        IReadOnlyList<string> installedNames)
    {
        var configuredByName = configured
            .GroupBy(e => e.FileName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var merged = new List<DictionaryConfigEntry>();
        foreach (var name in installedNames)
        {
            if (configuredByName.TryGetValue(name, out var entry))
                merged.Add(entry);
            else
                merged.Add(new DictionaryConfigEntry(name, true, int.MaxValue));
        }

        return merged
            .OrderBy(e => e.Order)
            .ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase)
            .Select((entry, index) => entry with { Order = index })
            .ToList();
    }

    private static DictionaryConfig LoadLegacyOrder(string dictionaryRoot)
    {
        var orderPath = Path.Combine(dictionaryRoot, "dictionary-order.json");
        if (!File.Exists(orderPath))
            return DictionaryConfig.Empty;

        try
        {
            var order = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(orderPath)) ?? [];
            return new DictionaryConfig(
                order.Select((name, index) => new DictionaryConfigEntry(name, true, index)).ToList(),
                [],
                []);
        }
        catch
        {
            return DictionaryConfig.Empty;
        }
    }

    private static string GetConfigPath(string dictionaryRoot) =>
        Path.Combine(dictionaryRoot, "dictionary-config.json");
}
