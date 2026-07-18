namespace Niratan.Models.Novel;

public sealed record ReaderGalleryImage(
    string RelativePath,
    string FilePath,
    int SpineIndex,
    double ChapterProgress);
