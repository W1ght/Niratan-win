using System;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Enums;
using Microsoft.UI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;

namespace Hoshi.Views.Dictionary;

internal static class DictionaryPopupSnapshotAcrylicRenderer
{
    private const float Dpi = 96;

    public static async Task<byte[]?> RenderPngAsync(
        string imagePath,
        double width,
        double height,
        ThemeMode themeMode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return null;

        ct.ThrowIfCancellationRequested();
        var pixelWidth = (float)Math.Clamp(Math.Ceiling(width), 1, 1200);
        var pixelHeight = (float)Math.Clamp(Math.Ceiling(height), 1, 1000);
        var device = CanvasDevice.GetSharedDevice();

        using var source = await CanvasBitmap.LoadAsync(device, imagePath);
        ct.ThrowIfCancellationRequested();

        var sourceRect = CreateCenterCropRect(source.Size, pixelWidth, pixelHeight);
        using var fitted = new CanvasRenderTarget(device, pixelWidth, pixelHeight, Dpi);
        using (var drawingSession = fitted.CreateDrawingSession())
        {
            drawingSession.Clear(Colors.Transparent);
            drawingSession.DrawImage(
                source,
                new Rect(0, 0, pixelWidth, pixelHeight),
                sourceRect);
        }

        using var target = new CanvasRenderTarget(device, pixelWidth, pixelHeight, Dpi);
        using (var drawingSession = target.CreateDrawingSession())
        {
            drawingSession.Clear(Colors.Transparent);
            using var blur = new GaussianBlurEffect
            {
                Source = fitted,
                BlurAmount = 34,
                BorderMode = EffectBorderMode.Hard,
                Optimization = EffectOptimization.Balanced,
            };
            drawingSession.DrawImage(blur);
            drawingSession.FillRectangle(
                0,
                0,
                pixelWidth,
                pixelHeight,
                CreateAcrylicThinTint(themeMode));
        }

        using var stream = new InMemoryRandomAccessStream();
        await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        return await ReadAllBytesAsync(stream);
    }

    private static Rect CreateCenterCropRect(Size sourceSize, double targetWidth, double targetHeight)
    {
        var sourceRatio = sourceSize.Width / Math.Max(1, sourceSize.Height);
        var targetRatio = targetWidth / Math.Max(1, targetHeight);

        if (sourceRatio > targetRatio)
        {
            var width = sourceSize.Height * targetRatio;
            return new Rect((sourceSize.Width - width) / 2, 0, width, sourceSize.Height);
        }

        var height = sourceSize.Width / targetRatio;
        return new Rect(0, (sourceSize.Height - height) / 2, sourceSize.Width, height);
    }

    private static Color CreateAcrylicThinTint(ThemeMode themeMode)
    {
        var isDark = DictionaryPopupMaterial.IsThemeDark(themeMode);
        return isDark
            ? Color.FromArgb(0x78, 0x18, 0x18, 0x18)
            : Color.FromArgb(0x70, 0xF8, 0xF8, 0xF8);
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
