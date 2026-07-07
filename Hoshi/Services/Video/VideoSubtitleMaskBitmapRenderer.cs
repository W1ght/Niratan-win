using System;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Text;

namespace Hoshi.Services.Video;

public static class VideoSubtitleMaskBitmapRenderer
{
    private const float Dpi = 96;
    private const double HorizontalInset = 28;

    public static async Task<byte[]> RenderPngAsync(
        string text,
        double width,
        double height,
        string fontFamily,
        double fontSize,
        int fontWeight,
        Color foreground,
        double shadowRadius,
        double blurRadius)
    {
        var pixelWidth = (float)Math.Clamp(Math.Ceiling(width), 1, 4096);
        var pixelHeight = (float)Math.Clamp(Math.Ceiling(height), 1, 2048);
        var device = CanvasDevice.GetSharedDevice();

        using var textTarget = new CanvasRenderTarget(device, pixelWidth, pixelHeight, Dpi);
        using (var drawingSession = textTarget.CreateDrawingSession())
        {
            drawingSession.Clear(Colors.Transparent);
            using var format = CreateTextFormat(fontFamily, fontSize, fontWeight);
            var bounds = new Rect(
                HorizontalInset,
                0,
                Math.Max(1, pixelWidth - (HorizontalInset * 2)),
                pixelHeight);

            DrawShadow(drawingSession, text, bounds, format, shadowRadius);
            drawingSession.DrawText(text, bounds, foreground, format);
        }

        using var blurTarget = new CanvasRenderTarget(device, pixelWidth, pixelHeight, Dpi);
        using (var drawingSession = blurTarget.CreateDrawingSession())
        {
            drawingSession.Clear(Colors.Transparent);
            using var blur = new GaussianBlurEffect
            {
                Source = textTarget,
                BlurAmount = (float)Math.Clamp(blurRadius, 0, 20),
                BorderMode = EffectBorderMode.Soft,
            };
            drawingSession.DrawImage(blur);
        }

        using var stream = new InMemoryRandomAccessStream();
        await blurTarget.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        return await ReadAllBytesAsync(stream);
    }

    private static CanvasTextFormat CreateTextFormat(string fontFamily, double fontSize, int fontWeight)
    {
        return new CanvasTextFormat
        {
            FontFamily = string.IsNullOrWhiteSpace(fontFamily)
                ? "Segoe UI, Yu Gothic UI, Meiryo"
                : fontFamily,
            FontSize = (float)Math.Clamp(fontSize, 12, 160),
            FontWeight = new FontWeight
            {
                Weight = (ushort)Math.Clamp(fontWeight, 100, 900),
            },
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.Wrap,
        };
    }

    private static void DrawShadow(
        CanvasDrawingSession drawingSession,
        string text,
        Rect bounds,
        CanvasTextFormat format,
        double shadowRadius)
    {
        var offsets = VideoSubtitleShadowLayout.CreateOffsets(shadowRadius, 1);
        foreach (var offset in offsets)
        {
            if (offset.Opacity <= 0)
                continue;

            var shadowBounds = new Rect(
                bounds.X + offset.X,
                bounds.Y + offset.Y,
                bounds.Width,
                bounds.Height);
            drawingSession.DrawText(
                text,
                shadowBounds,
                Color.FromArgb((byte)Math.Round(offset.Opacity * 255), 0, 0, 0),
                format);
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(IRandomAccessStream stream)
    {
        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var size = (uint)stream.Size;
        await reader.LoadAsync(size);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
