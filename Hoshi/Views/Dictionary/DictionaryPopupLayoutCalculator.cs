using System;

namespace Hoshi.Views.Dictionary;

internal readonly record struct DictionaryPopupAnchorRect
{
    public DictionaryPopupAnchorRect(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
}

internal readonly record struct DictionaryPopupLayoutResult(double Left, double Top, double Width, double Height);

internal static class DictionaryPopupLayoutCalculator
{
    public const double PopupPadding = 4;
    public const double ScreenBorderPadding = 6;

    public static DictionaryPopupLayoutResult Resolve(
        DictionaryPopupAnchorRect selection,
        double screenWidth,
        double screenHeight,
        double maxWidth,
        double maxHeight,
        double minWidth,
        bool isVertical)
    {
        double width, height, centerX, centerY;

        if (isVertical)
        {
            var spaceToSide = Math.Max(
                SpaceLeft(selection.X),
                SpaceRight(selection.X, selection.Width, screenWidth));
            width = ClampDimension(spaceToSide - ScreenBorderPadding, minWidth, maxWidth);
            centerX = ShowOnRight(selection.X, selection.Width, screenWidth, maxWidth)
                ? selection.X + selection.Width + PopupPadding + width / 2
                : selection.X - PopupPadding - width / 2;
            centerX = Clamp(centerX, width / 2, screenWidth - width / 2);

            height = maxHeight;
            centerY = Clamp(
                selection.Y + height / 2,
                height / 2 + ScreenBorderPadding,
                screenHeight - height / 2 - ScreenBorderPadding);
        }
        else
        {
            width = ClampDimension(
                Math.Min(screenWidth - ScreenBorderPadding * 2, maxWidth),
                minWidth,
                maxWidth);
            centerX = Clamp(
                selection.X + selection.Width / 2,
                width / 2 + ScreenBorderPadding,
                screenWidth - width / 2 - ScreenBorderPadding);

            var spaceAbove = SpaceAbove(selection.Y);
            var spaceBelow = SpaceBelow(selection.Y, selection.Height, screenHeight);
            var showBelow = ShowBelow(selection.Y, selection.Height, screenHeight, maxHeight);
            var availableHeight = showBelow ? spaceBelow : spaceAbove;
            height = ClampPopupExtent(availableHeight - ScreenBorderPadding, maxHeight);

            centerY = showBelow
                ? selection.Y + selection.Height + PopupPadding + height / 2
                : selection.Y - PopupPadding - height / 2;
            centerY = Clamp(
                centerY,
                height / 2 + ScreenBorderPadding,
                screenHeight - height / 2 - ScreenBorderPadding);
        }

        return FromCenter(centerX, centerY, width, height, screenWidth, screenHeight);
    }

    private static DictionaryPopupLayoutResult FromCenter(
        double centerX,
        double centerY,
        double width,
        double height,
        double screenWidth,
        double screenHeight)
    {
        var left = centerX - width / 2;
        var top = centerY - height / 2;
        var maxLeft = Math.Max(ScreenBorderPadding, screenWidth - width - ScreenBorderPadding);
        var maxTop = Math.Max(ScreenBorderPadding, screenHeight - height - ScreenBorderPadding);
        return new DictionaryPopupLayoutResult(
            Clamp(left, ScreenBorderPadding, maxLeft),
            Clamp(top, ScreenBorderPadding, maxTop),
            width,
            height);
    }

    private static double SpaceLeft(double x) => x - PopupPadding;
    private static double SpaceRight(double x, double w, double screenWidth) => screenWidth - x - w - PopupPadding;
    private static double SpaceAbove(double y) => y - PopupPadding;
    private static double SpaceBelow(double y, double h, double screenHeight) => screenHeight - y - h - PopupPadding;
    private static bool ShowOnRight(double x, double w, double screenWidth, double popupWidth) => SpaceRight(x, w, screenWidth) >= SpaceLeft(x) || SpaceRight(x, w, screenWidth) >= popupWidth;
    private static bool ShowBelow(double y, double h, double screenHeight, double popupHeight) => SpaceBelow(y, h, screenHeight) >= SpaceAbove(y) || SpaceBelow(y, h, screenHeight) >= popupHeight;
    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(value, max));
    private static double ClampDimension(double value, double min, double max) => Math.Clamp(value, Math.Min(min, max), Math.Max(min, max));
    private static double ClampPopupExtent(double value, double max) => Math.Clamp(value, 0, Math.Max(0, max));
}
