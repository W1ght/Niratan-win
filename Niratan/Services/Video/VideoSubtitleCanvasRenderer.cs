using System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;

namespace Niratan.Services.Video;

public readonly record struct VideoSubtitleCanvasRenderOptions(
    string Text,
    string FontFamily,
    double FontSize,
    int FontWeight,
    Color Foreground,
    double ShadowRadius,
    double MaskBlurRadius,
    int SelectionStart,
    int SelectionLength,
    Color SelectionBackground,
    Color SelectionForeground);

public readonly record struct VideoSubtitleCanvasHitTestResult(
    int CharacterIndex,
    Rect Bounds);

public static class VideoSubtitleCanvasRenderer
{
    private const double HorizontalInset = 28;
    private const double MaximumTextWidth = 1680;

    public static Rect Draw(
        CanvasDrawingSession drawingSession,
        Size size,
        VideoSubtitleCanvasRenderOptions options)
    {
        drawingSession.Clear(Colors.Transparent);
        if (string.IsNullOrEmpty(options.Text) || size.Width <= 0 || size.Height <= 0)
            return default;

        var layoutBounds = CalculateLayoutBounds(size);
        using var format = CreateTextFormat(options);
        using var layout = CreateTextLayout(drawingSession, layoutBounds, options.Text, format);
        var drawBounds = layout.DrawBounds;
        var visibleBounds = new Rect(
            drawBounds.X + layoutBounds.X,
            drawBounds.Y + layoutBounds.Y,
            drawBounds.Width,
            drawBounds.Height);
        using var composite = new CanvasCommandList(drawingSession);
        using (var compositeSession = composite.CreateDrawingSession())
        {
            DrawShadow(compositeSession, layout, layoutBounds, options.ShadowRadius);
            DrawSelection(compositeSession, layout, layoutBounds, options);
            compositeSession.DrawTextLayout(
                layout,
                (float)layoutBounds.X,
                (float)layoutBounds.Y,
                options.Foreground);
        }

        var maskBlurRadius = (float)Math.Clamp(options.MaskBlurRadius, 0, 20);
        if (maskBlurRadius <= 0)
        {
            drawingSession.DrawImage(composite);
            return visibleBounds;
        }

        using var maskBlur = new GaussianBlurEffect
        {
            Source = composite,
            BlurAmount = maskBlurRadius,
            BorderMode = EffectBorderMode.Soft,
        };
        drawingSession.DrawImage(maskBlur);
        return visibleBounds;
    }

    public static bool TryHitTestCharacter(
        ICanvasResourceCreator resourceCreator,
        Size size,
        VideoSubtitleCanvasRenderOptions options,
        Point point,
        out VideoSubtitleCanvasHitTestResult result)
    {
        result = default;
        if (string.IsNullOrEmpty(options.Text)
            || size.Width <= 0
            || size.Height <= 0
            || !double.IsFinite(point.X)
            || !double.IsFinite(point.Y))
        {
            return false;
        }

        var layoutBounds = CalculateLayoutBounds(size);
        using var format = CreateTextFormat(options);
        using var layout = CreateTextLayout(resourceCreator, layoutBounds, options.Text, format);
        var isHit = layout.HitTest(
            (float)(point.X - layoutBounds.X),
            (float)(point.Y - layoutBounds.Y),
            out var region);
        if (!isHit || region.CharacterCount <= 0)
            return false;

        var bounds = region.LayoutBounds;
        result = new VideoSubtitleCanvasHitTestResult(
            region.CharacterIndex,
            new Rect(
                bounds.X + layoutBounds.X,
                bounds.Y + layoutBounds.Y,
                bounds.Width,
                bounds.Height));
        return true;
    }

    public static Rect CalculateLayoutBounds(Size size)
    {
        var width = Math.Min(
            MaximumTextWidth,
            Math.Max(1, size.Width - (HorizontalInset * 2)));
        return new Rect(
            Math.Max(0, (size.Width - width) / 2),
            0,
            width,
            Math.Max(1, size.Height));
    }

    private static CanvasTextLayout CreateTextLayout(
        ICanvasResourceCreator resourceCreator,
        Rect bounds,
        string text,
        CanvasTextFormat format) =>
        new(
            resourceCreator,
            text,
            format,
            (float)bounds.Width,
            (float)bounds.Height);

    private static CanvasTextFormat CreateTextFormat(VideoSubtitleCanvasRenderOptions options)
    {
        var fontSize = (float)Math.Clamp(options.FontSize, 12, 160);
        return new CanvasTextFormat
        {
            FontFamily = string.IsNullOrWhiteSpace(options.FontFamily)
                ? "Segoe UI, Yu Gothic UI, Meiryo"
                : options.FontFamily,
            FontSize = fontSize,
            FontWeight = new FontWeight
            {
                Weight = (ushort)Math.Clamp(options.FontWeight, 100, 900),
            },
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.Wrap,
            LineSpacing = fontSize * 1.25f,
            LineSpacingBaseline = fontSize,
        };
    }

    private static void DrawShadow(
        CanvasDrawingSession drawingSession,
        CanvasTextLayout layout,
        Rect layoutBounds,
        double shadowRadius)
    {
        var style = VideoSubtitleShadowLayout.Create(shadowRadius, 1);
        if (style.BlurRadius <= 0 || style.Opacity <= 0)
            return;

        using var shadowSource = new CanvasCommandList(drawingSession);
        using (var shadowSession = shadowSource.CreateDrawingSession())
        {
            shadowSession.DrawTextLayout(
                layout,
                (float)layoutBounds.X,
                (float)layoutBounds.Y,
                Color.FromArgb((byte)Math.Round(style.Opacity * 255), 0, 0, 0));
        }

        using var shadowBlur = new GaussianBlurEffect
        {
            Source = shadowSource,
            BlurAmount = style.BlurRadius,
            BorderMode = EffectBorderMode.Soft,
        };
        drawingSession.DrawImage(shadowBlur, style.OffsetX, style.OffsetY);
    }

    private static void DrawSelection(
        CanvasDrawingSession drawingSession,
        CanvasTextLayout layout,
        Rect layoutBounds,
        VideoSubtitleCanvasRenderOptions options)
    {
        var start = Math.Clamp(options.SelectionStart, 0, options.Text.Length);
        var length = Math.Clamp(options.SelectionLength, 0, options.Text.Length - start);
        if (length <= 0)
            return;

        foreach (var region in layout.GetCharacterRegions(start, length))
        {
            var bounds = region.LayoutBounds;
            drawingSession.FillRectangle(
                new Rect(
                    bounds.X + layoutBounds.X,
                    bounds.Y + layoutBounds.Y,
                    bounds.Width,
                    bounds.Height),
                options.SelectionBackground);
        }

        layout.SetColor(start, length, options.SelectionForeground);
    }
}
