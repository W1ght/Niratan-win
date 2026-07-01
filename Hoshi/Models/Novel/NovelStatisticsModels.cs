namespace Hoshi.Models.Novel;

public sealed record NovelReadingStatistic(
    string Title,
    string DateKey,
    int CharactersRead,
    double ReadingTime,
    int MinReadingSpeed,
    int AltMinReadingSpeed,
    int LastReadingSpeed,
    int MaxReadingSpeed,
    long LastStatisticModified);
