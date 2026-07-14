using System.Collections.Generic;

namespace Niratan.Models.Dictionary;

public sealed record DictionaryImportResult(
    bool Success,
    string Title,
    long TermCount,
    long MetaCount,
    long FreqCount,
    long PitchCount,
    long MediaCount,
    List<string> Errors
);
