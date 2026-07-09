using System;

namespace Hoshi.Services.Video;

public sealed class VideoBottomChromeAutoHideState
{
    public static TimeSpan DefaultHideDelay { get; } = TimeSpan.FromSeconds(2);

    public bool IsVisible { get; private set; } = true;

    public void ShowForPointerActivity()
    {
        IsVisible = true;
    }

    public void HideForInactivity()
    {
        IsVisible = false;
    }

    public void HideForPointerLeave()
    {
        IsVisible = false;
    }
}
