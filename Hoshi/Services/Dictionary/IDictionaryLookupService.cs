using System.Collections.Generic;
using System.Threading.Tasks;
using Hoshi.Models.Dictionary;

namespace Hoshi.Services.Dictionary;

public interface IDictionaryLookupService
{
    Task<List<DictionaryLookupResult>> LookupAsync(
        string text,
        int maxResults = 16,
        int scanLength = 16,
        string? traceId = null
    );

    Task<List<DictionaryStyle>> GetStylesAsync();
    Task<byte[]?> GetMediaFileAsync(string dictName, string mediaPath);
    Task RebuildQueryAsync();
    Task SetActiveLanguageAsync(string languageId);
}
