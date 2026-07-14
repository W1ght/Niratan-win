using System;
using System.Collections.Generic;

namespace Niratan.Models.Novel;

public readonly record struct ReaderStatisticsPosition(int RawCharacterCount);

public enum ReaderStatisticsCheckpointReason
{
    ReadingMovement,
    AdjacentChapter,
    ProgrammaticDeparture,
    Pause,
    Stop,
    Close,
    Background,
}

public sealed record ReaderStatisticsSessionState(
    bool IsTracking,
    bool IsPaused,
    NovelReadingStatistic Session,
    NovelReadingStatistic Today,
    NovelReadingStatistic AllTime,
    IReadOnlyList<NovelReadingStatistic> History);
