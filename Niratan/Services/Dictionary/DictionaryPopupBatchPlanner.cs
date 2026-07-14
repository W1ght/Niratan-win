using System;
using System.Collections.Generic;

namespace Niratan.Services.Dictionary;

internal readonly record struct DictionaryPopupBatchRange(int Offset, int Count);

internal static class DictionaryPopupBatchPlanner
{
    internal const int InitialBatchSize = 1;
    internal const int DeferredBatchSize = 3;

    public static IReadOnlyList<DictionaryPopupBatchRange> Create(int resultCount)
    {
        if (resultCount <= 0)
            return [];

        var ranges = new List<DictionaryPopupBatchRange>
        {
            new(0, Math.Min(InitialBatchSize, resultCount)),
        };

        for (var offset = InitialBatchSize; offset < resultCount; offset += DeferredBatchSize)
        {
            ranges.Add(new DictionaryPopupBatchRange(
                offset,
                Math.Min(DeferredBatchSize, resultCount - offset)));
        }

        return ranges;
    }
}
