using System;
using Windows.Graphics;

namespace Niratan.Services.Dictionary;

internal static class GlobalLookupPopupWindowPlacement
{
    public const int PopupGap = 12;
    private const int HiddenStagingOffset = 10000;

    public static RectInt32 ResolveStagingRect(RectInt32 workArea, SizeInt32 stagingSize)
    {
        var width = Math.Max(1, stagingSize.Width);
        var height = Math.Max(1, stagingSize.Height);
        return new RectInt32(
            workArea.X - width - HiddenStagingOffset,
            workArea.Y - height - HiddenStagingOffset,
            width,
            height);
    }

    public static RectInt32 ResolveFinalRect(
        RectInt32 anchorRect,
        RectInt32 workArea,
        SizeInt32 popupSize,
        int gap = PopupGap)
    {
        gap = Math.Max(0, gap);
        var width = Math.Clamp(Math.Max(1, popupSize.Width), 1, Math.Max(1, workArea.Width));
        var requestedHeight = Math.Clamp(
            Math.Max(1, popupSize.Height),
            1,
            Math.Max(1, workArea.Height));
        var right = workArea.X + workArea.Width;
        var bottom = workArea.Y + workArea.Height;

        var anchorLeft = Math.Clamp(anchorRect.X, workArea.X, right);
        var anchorTop = Math.Clamp(anchorRect.Y, workArea.Y, bottom);
        var anchorRight = Math.Clamp(anchorRect.X + Math.Max(1, anchorRect.Width), workArea.X, right);
        var anchorBottom = Math.Clamp(anchorRect.Y + Math.Max(1, anchorRect.Height), workArea.Y, bottom);
        var availableBelow = Math.Max(0, bottom - anchorBottom - gap);
        var availableAbove = Math.Max(0, anchorTop - workArea.Y - gap);
        var fitsBelow = requestedHeight <= availableBelow;
        var fitsAbove = requestedHeight <= availableAbove;
        var placeBelow = fitsBelow || (!fitsAbove && availableBelow >= availableAbove);
        var availableHeight = placeBelow ? availableBelow : availableAbove;
        var height = Math.Max(1, Math.Min(requestedHeight, Math.Max(1, availableHeight)));

        var anchorCenterX = anchorLeft + (anchorRight - anchorLeft) / 2;
        var x = anchorCenterX - width / 2;
        var y = placeBelow
            ? anchorBottom + gap
            : anchorTop - gap - height;

        var maxX = Math.Max(workArea.X, right - width);
        var maxY = Math.Max(workArea.Y, bottom - height);
        x = Math.Clamp(x, workArea.X, maxX);
        y = Math.Clamp(y, workArea.Y, maxY);

        return new RectInt32(x, y, width, height);
    }
}
