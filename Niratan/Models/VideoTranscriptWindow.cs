using System;

namespace Niratan.Models;

public sealed class VideoTranscriptWindow
{
    public const int DefaultWindowSize = 80;
    public const int DefaultExtensionSize = 40;

    public int StartIndex { get; private set; }
    public int Count { get; private set; }
    public int EndExclusive => StartIndex + Count;

    public void Reset(int totalCount, int? anchorIndex = null)
    {
        if (totalCount <= 0)
        {
            StartIndex = 0;
            Count = 0;
            return;
        }

        Count = Math.Min(DefaultWindowSize, totalCount);
        if (anchorIndex == null)
        {
            StartIndex = 0;
            return;
        }

        var centeredStart = anchorIndex.Value - (Count / 2);
        StartIndex = Math.Clamp(centeredStart, 0, Math.Max(0, totalCount - Count));
    }

    public bool EnsureContains(int totalCount, int anchorIndex)
    {
        if (totalCount <= 0)
        {
            var changed = Count != 0 || StartIndex != 0;
            StartIndex = 0;
            Count = 0;
            return changed;
        }

        if (Count == 0 || anchorIndex < StartIndex || anchorIndex >= EndExclusive)
        {
            var previousStart = StartIndex;
            var previousCount = Count;
            Reset(totalCount, anchorIndex);
            return StartIndex != previousStart || Count != previousCount;
        }

        return false;
    }

    public bool ExtendTowardStart(int totalCount)
    {
        if (totalCount <= 0 || StartIndex == 0)
            return false;

        var previousStart = StartIndex;
        var previousCount = Count;
        var extension = Math.Min(DefaultExtensionSize, StartIndex);
        StartIndex -= extension;
        Count = Math.Min(totalCount - StartIndex, Count + extension);
        return StartIndex != previousStart || Count != previousCount;
    }

    public bool ExtendTowardEnd(int totalCount)
    {
        if (totalCount <= 0 || EndExclusive >= totalCount)
            return false;

        var previousCount = Count;
        var extension = Math.Min(DefaultExtensionSize, totalCount - EndExclusive);
        Count += extension;
        return Count != previousCount;
    }
}
