using System;
using System.Collections.Generic;

namespace Niratan.Models.Novel;

public sealed record NovelBookmark(
    int ChapterIndex,
    double Progress,
    int CharacterCount,
    DateTimeOffset? LastModified);

public sealed record NovelBookInfo(
    int CharacterCount,
    Dictionary<string, NovelBookInfoChapter> ChapterInfo);

public sealed record NovelBookInfoChapter(
    int? SpineIndex,
    int CurrentTotal,
    int ChapterCount);
