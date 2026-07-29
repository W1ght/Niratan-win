using System;

namespace Niratan.Services.Video;

public sealed class VideoBottomChromeAutoHideState
{
    private double? _lastPointerX;
    private double? _lastPointerY;

    public static TimeSpan DefaultHideDelay { get; } = TimeSpan.FromSeconds(1);

    public bool IsVisible { get; private set; } = true;

    public void ShowForPointerActivity()
    {
        IsVisible = true;
    }

    public bool ShowForPointerMovement(double pointerX, double pointerY)
    {
        if (_lastPointerX == pointerX && _lastPointerY == pointerY)
            return false;

        _lastPointerX = pointerX;
        _lastPointerY = pointerY;
        IsVisible = true;
        return true;
    }

    public void HideForInactivity()
    {
        IsVisible = false;
    }

    public void HideForPointerLeave()
    {
        _lastPointerX = null;
        _lastPointerY = null;
        IsVisible = false;
    }
}
