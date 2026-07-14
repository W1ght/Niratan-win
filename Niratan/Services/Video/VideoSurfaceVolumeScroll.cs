using System;
using System.Collections.Generic;
using System.Linq;

namespace Niratan.Services.Video;

public static class VideoSurfaceVolumeScroll
{
    private const double WheelStep = 5;
    private const double PreciseScale = 0.5;
    private const double MaximumPreciseStep = 5;
    private const double MinimumPreciseStep = 0.1;

    public readonly record struct ExcludedRect(double X, double Y, double Width, double Height)
    {
        public bool Contains(double x, double y) =>
            Width > 0
            && Height > 0
            && x >= X
            && x <= X + Width
            && y >= Y
            && y <= Y + Height;
    }

    public static double? TryGetAdjustment(
        double deltaX,
        double deltaY,
        bool hasPreciseScrollingDeltas,
        double pointerX,
        double pointerY,
        double surfaceWidth,
        double surfaceHeight,
        bool isEnabled,
        bool hasActivePopup,
        IEnumerable<ExcludedRect>? excludedRects)
    {
        if (!isEnabled
            || hasActivePopup
            || surfaceWidth <= 0
            || surfaceHeight <= 0
            || pointerX < 0
            || pointerY < 0
            || pointerX > surfaceWidth
            || pointerY > surfaceHeight)
        {
            return null;
        }

        if (excludedRects?.Any(rect => rect.Contains(pointerX, pointerY)) == true)
            return null;

        return GetAdjustment(deltaX, deltaY, hasPreciseScrollingDeltas);
    }

    public static double? GetAdjustment(
        double deltaX,
        double deltaY,
        bool hasPreciseScrollingDeltas)
    {
        if (!double.IsFinite(deltaY)
            || Math.Abs(deltaY) < 0.01
            || Math.Abs(deltaY) < Math.Abs(deltaX))
        {
            return null;
        }

        if (!hasPreciseScrollingDeltas)
            return deltaY > 0 ? WheelStep : -WheelStep;

        var preciseDelta = deltaY * PreciseScale;
        if (Math.Abs(preciseDelta) < MinimumPreciseStep)
            return null;

        return Math.Clamp(preciseDelta, -MaximumPreciseStep, MaximumPreciseStep);
    }
}
