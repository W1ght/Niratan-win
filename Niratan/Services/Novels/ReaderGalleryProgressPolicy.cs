using System;

namespace Niratan.Services.Novels;

public static class ReaderGalleryProgressPolicy
{
    public static bool IsRead(
        int currentSpineIndex,
        double currentChapterProgress,
        int imageSpineIndex,
        double imageChapterProgress) =>
        imageSpineIndex < 0
        || imageSpineIndex < currentSpineIndex
        || imageSpineIndex == currentSpineIndex
            && Math.Clamp(currentChapterProgress, 0, 1) + 0.000_001
                >= Math.Clamp(imageChapterProgress, 0, 1);
}
