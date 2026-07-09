using System;

namespace Hoshi.Services.Video;

public static class VideoProgressSaveGuard
{
    private static readonly TimeSpan RestoreFloorTolerance = TimeSpan.FromSeconds(2);

    public static TimeSpan CreateProtectedRestoreFloor(TimeSpan restoreTarget) =>
        restoreTarget <= RestoreFloorTolerance
            ? TimeSpan.Zero
            : restoreTarget - RestoreFloorTolerance;

    public static bool ShouldSaveProgress(
        TimeSpan position,
        TimeSpan? protectedRestoreFloor) =>
        protectedRestoreFloor == null || position >= protectedRestoreFloor.Value;
}
