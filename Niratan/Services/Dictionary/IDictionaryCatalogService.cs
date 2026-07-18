using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Dictionary;

namespace Niratan.Services.Dictionary;

public interface IDictionaryCatalogService
{
    IReadOnlyList<DictionaryRecommendation> GetRecommendations();

    Task<DictionaryUpdateCheckResult> CheckForUpdatesAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    Task<DictionaryBatchOperationResult> DownloadRecommendationsAsync(
        IReadOnlyList<DictionaryRecommendation> recommendations,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    Task<DictionaryBatchOperationResult> UpdateDictionariesAsync(
        IReadOnlyList<DictionaryUpdateCandidate> updates,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    Task TryAutoUpdateAsync(CancellationToken ct = default);
}
