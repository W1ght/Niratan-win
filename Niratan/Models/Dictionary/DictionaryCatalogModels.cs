using System.Collections.Generic;

namespace Niratan.Models.Dictionary;

public sealed record DictionaryRecommendation(
    string Id,
    string Name,
    DictionaryType Type,
    string? IndexUrl,
    string? DownloadUrl,
    string LanguageId);

public sealed record DictionaryUpdateCandidate(
    InstalledDictionary Dictionary,
    string IndexUrl,
    string RemoteRevision,
    string DownloadUrl);

public sealed record DictionaryBatchOperationResult(
    IReadOnlyList<string> Succeeded,
    IReadOnlyList<string> Failures)
{
    public bool IsSuccess => Failures.Count == 0;
}

public sealed record DictionaryUpdateCheckResult(
    IReadOnlyList<DictionaryUpdateCandidate> Updates,
    IReadOnlyList<string> Failures,
    int CheckedCount = 0);
