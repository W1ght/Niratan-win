using System;
using Windows.Graphics;

namespace Hoshi.Services.Dictionary;

internal static class GlobalLookupPopupWindowPlacement
{
    private const int CursorOffset = 16;
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
        PointInt32 cursorPoint,
        RectInt32 workArea,
        SizeInt32 popupSize)
    {
        var width = Math.Max(1, popupSize.Width);
        var height = Math.Max(1, popupSize.Height);
        var right = workArea.X + workArea.Width;
        var bottom = workArea.Y + workArea.Height;

        var x = cursorPoint.X + CursorOffset;
        if (x + width > right)
            x = cursorPoint.X - width - CursorOffset;

        var y = cursorPoint.Y + CursorOffset;
        if (y + height > bottom)
            y = cursorPoint.Y - height - CursorOffset;

        var maxX = Math.Max(workArea.X, right - width);
        var maxY = Math.Max(workArea.Y, bottom - height);
        x = Math.Clamp(x, workArea.X, maxX);
        y = Math.Clamp(y, workArea.Y, maxY);

        return new RectInt32(x, y, width, height);
    }
}
