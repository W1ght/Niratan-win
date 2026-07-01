using System.Collections.Generic;

namespace Hoshi.Models.Dictionary;

public enum DictionaryType
{
    Term,
    Frequency,
    Pitch,
}

public sealed record InstalledDictionary(
    string Name,
    DictionaryType Type = DictionaryType.Term,
    bool IsEnabled = true,
    int Order = 0,
    string Revision = "",
    string DisplayTitle = ""
);

public sealed record DictionaryConfig(
    List<DictionaryConfigEntry> TermDictionaries,
    List<DictionaryConfigEntry> FrequencyDictionaries,
    List<DictionaryConfigEntry> PitchDictionaries
)
{
    public static DictionaryConfig Empty { get; } = new([], [], []);
}

public sealed record DictionaryConfigEntry(string FileName, bool IsEnabled, int Order);
