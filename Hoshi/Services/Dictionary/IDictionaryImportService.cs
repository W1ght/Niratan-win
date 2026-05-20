using System.Collections.Generic;
using System.Threading.Tasks;
using Hoshi.Models.Dictionary;

namespace Hoshi.Services.Dictionary;

public interface IDictionaryImportService
{
    Task<DictionaryImportResult> ImportAsync(string zipPath);
    Task<bool> DeleteAsync(string dictName);
    Task<List<InstalledDictionary>> GetInstalledDictionariesAsync(DictionaryType? type = null);
    Task SaveDictionaryOrderAsync(DictionaryType type, IReadOnlyList<string> orderedNames);
    Task SetDictionaryEnabledAsync(DictionaryType type, string dictName, bool enabled);
}
