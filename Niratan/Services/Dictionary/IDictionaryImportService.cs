using System.Collections.Generic;
using System.Threading.Tasks;
using Niratan.Models.Dictionary;

namespace Niratan.Services.Dictionary;

public interface IDictionaryImportService
{
    Task<DictionaryImportResult> ImportAsync(string zipPath);
    Task<bool> DeleteAsync(string dictName);
    Task<bool> DeleteAsync(DictionaryType type, string dictName);
    Task<List<InstalledDictionary>> GetInstalledDictionariesAsync(DictionaryType? type = null);
    Task SaveDictionaryOrderAsync(DictionaryType type, IReadOnlyList<string> orderedNames);
    Task SetDictionaryEnabledAsync(DictionaryType type, string dictName, bool enabled);
    Task MigrateDictionaryNameAsync(DictionaryType type, string oldName, string newName);
}
