using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Niratan.Models.Sasayaki;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;

namespace Niratan.Services.Sasayaki;

public readonly record struct ReaderLyricsCanvasRenderOptions(
    IReadOnlyList<SasayakiMatch> Cues,
    int CurrentCueIndex,
    double PlaybackSeconds,
    double DelaySeconds,
    bool IsVertical,
    bool BlurLyrics,
    string? SelectedCueId,
    int SelectionStart,
    int SelectionLength,
    string EmptyText);

public readonly record struct ReaderLyricsCanvasHitTestResult(
    SasayakiMatch Cue,
    int CueIndex,
    int CharacterIndex,
    Rect Bounds);

public static class ReaderLyricsCanvasRenderer
{
    private const float HorizontalPadding = 18;
    private const float LineSpacing = 25;
    private const float MaskBlurRadius = 20;
    private static readonly Color FocusedPendingColor = Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF);
    private static readonly Color ContextColor = Color.FromArgb(0x84, 0xFF, 0xFF, 0xFF);
    private static readonly Color SelectionColor = Color.FromArgb(0x70, 0x36, 0xA8, 0xFF);

    public static void Draw(
        CanvasDrawingSession drawingSession,
        Size size,
        ReaderLyricsCanvasRenderOptions options)
    {
        drawingSession.Clear(Colors.Transparent);
        if (size.Width <= 0 || size.Height <= 0)
            return;

        if (options.Cues.Count == 0)
        {
            DrawEmptyState(drawingSession, size, options.EmptyText);
            return;
        }

        if (!options.BlurLyrics)
        {
            DrawCore(drawingSession, size, options);
            return;
        }

        using var commandList = new CanvasCommandList(drawingSession);
        using (var commandSession = commandList.CreateDrawingSession())
            DrawCore(commandSession, size, options);
        using var blur = new GaussianBlurEffect
        {
            Source = commandList,
            BlurAmount = MaskBlurRadius,
            BorderMode = EffectBorderMode.Soft,
        };
        drawingSession.DrawImage(blur);
    }

    public static bool TryHitTest(
        ICanvasResourceCreator resourceCreator,
        Size size,
        ReaderLyricsCanvasRenderOptions options,
        Point point,
        out ReaderLyricsCanvasHitTestResult result)
    {
        result = default;
        if (options.Cues.Count == 0
            || size.Width <= 0
            || size.Height <= 0
            || !double.IsFinite(point.X)
            || !double.IsFinite(point.Y))
        {
            return false;
        }

        return options.IsVertical
            ? TryHitTestVertical(size, options, point, out result)
            : TryHitTestHorizontal(resourceCreator, size, options, point, out result);
    }

    private static void DrawCore(
        CanvasDrawingSession drawingSession,
        Size size,
        ReaderLyricsCanvasRenderOptions options)
    {
        if (options.IsVertical)
            DrawVertical(drawingSession, size, options);
        else
            DrawHorizontal(drawingSession, size, options);
    }

    private static void DrawHorizontal(
        CanvasDrawingSession drawingSession,
        Size size,
        ReaderLyricsCanvasRenderOptions options)
    {
        using var layouts = CreateHorizontalLayouts(drawingSession, size, options);
        foreach (var item in layouts.Items)
        {
            DrawHorizontalSelection(drawingSession, item, options);
            drawingSession.DrawTextLayout(
                item.Layout,
                (float)item.Bounds.X,
                (float)item.Bounds.Y,
                item.IsFocused ? FocusedPendingColor : ContextColor);

            if (!item.IsFocused)
                continue;

            var progress = ReaderLyricsModeProjection.CueProgress(
                item.Cue,
                options.PlaybackSeconds,
                options.DelaySeconds);
            var completedLength = Math.Clamp(
                (int)Math.Ceiling(item.Cue.Text.Length * progress),
                0,
                item.Cue.Text.Length);
            if (completedLength <= 0)
                continue;

            item.Layout.SetColor(0, completedLength, Colors.White);
            drawingSession.DrawTextLayout(
                item.Layout,
                (float)item.Bounds.X,
                (float)item.Bounds.Y,
                FocusedPendingColor);
        }
    }

    private static bool TryHitTestHorizontal(
        ICanvasResourceCreator resourceCreator,
        Size size,
        ReaderLyricsCanvasRenderOptions options,
        Point point,
        out ReaderLyricsCanvasHitTestResult result)
    {
        result = default;
        using var layouts = CreateHorizontalLayouts(resourceCreator, size, options);
        foreach (var item in layouts.Items)
        {
            if (!item.Bounds.Contains(point))
                continue;

            if (!item.Layout.HitTest(
                    (float)(point.X - item.Bounds.X),
                    (float)(point.Y - item.Bounds.Y),
                    out var region)
                || region.CharacterCount <= 0)
            {
                continue;
            }

            var bounds = region.LayoutBounds;
            result = new ReaderLyricsCanvasHitTestResult(
                item.Cue,
                item.CueIndex,
                Math.Clamp(region.CharacterIndex, 0, Math.Max(0, item.Cue.Text.Length - 1)),
                new Rect(
                    bounds.X + item.Bounds.X,
                    bounds.Y + item.Bounds.Y,
                    Math.Max(1, bounds.Width),
                    Math.Max(1, bounds.Height)));
            return true;
        }

        return false;
    }

    private static HorizontalLayoutCollection CreateHorizontalLayouts(
        ICanvasResourceCreator resourceCreator,
        Size size,
        ReaderLyricsCanvasRenderOptions options)
    {
        var window = ReaderLyricsModeProjection.VisibleCueWindow(
            options.Cues.Count,
            options.CurrentCueIndex,
            size.Height);
        var safeCurrent = Math.Clamp(options.CurrentCueIndex, 0, options.Cues.Count - 1);
        var availableWidth = Math.Max(1, size.Width - HorizontalPadding * 2);
        var focusedFontSize = Math.Clamp(Math.Min(size.Height * 0.052, size.Width * 0.058), 28, 48);
        var contextFontSize = Math.Clamp(focusedFontSize * 0.76, 21, 34);
        var focusedHeight = Math.Ceiling(focusedFontSize * 1.58);
        var contextHeight = Math.Ceiling(contextFontSize * 1.52);
        var beforeHeight = 0d;
        for (var i = window.StartIndex; i < safeCurrent; i++)
            beforeHeight += contextHeight + LineSpacing;
        var currentHeight = safeCurrent >= window.StartIndex && safeCurrent < window.EndIndex
            ? focusedHeight
            : contextHeight;
        var y = Math.Clamp(size.Height * 0.46 - beforeHeight - currentHeight / 2, 0, size.Height);
        var items = new List<HorizontalCueLayout>();

        for (var index = window.StartIndex; index < window.EndIndex; index++)
        {
            var cue = options.Cues[index];
            var isFocused = index == safeCurrent;
            var baseFontSize = isFocused ? focusedFontSize : contextFontSize;
            var rowHeight = isFocused ? focusedHeight : contextHeight;
            var format = CreateHorizontalFormat(baseFontSize);
            var measured = new CanvasTextLayout(
                resourceCreator,
                NormalizeLine(cue.Text),
                format,
                10000,
                (float)rowHeight);
            var measuredWidth = Math.Max(1, measured.DrawBounds.Width);
            var fittedFontSize = ReaderLyricsAdaptiveFontPolicy.FitHorizontal(
                baseFontSize,
                measuredWidth,
                availableWidth);
            measured.Dispose();
            format.Dispose();
            format = CreateHorizontalFormat(fittedFontSize);
            var layout = new CanvasTextLayout(
                resourceCreator,
                NormalizeLine(cue.Text),
                format,
                (float)availableWidth,
                (float)rowHeight);
            format.Dispose();
            items.Add(new HorizontalCueLayout(
                cue,
                index,
                isFocused,
                layout,
                new Rect(HorizontalPadding, y, availableWidth, rowHeight)));
            y += rowHeight + LineSpacing;
        }

        return new HorizontalLayoutCollection(items);
    }

    private static CanvasTextFormat CreateHorizontalFormat(double fontSize) => new()
    {
        FontFamily = "Segoe UI, Yu Gothic UI, Meiryo",
        FontSize = (float)Math.Clamp(fontSize, 1, 72),
        FontWeight = new FontWeight { Weight = 700 },
        HorizontalAlignment = CanvasHorizontalAlignment.Left,
        VerticalAlignment = CanvasVerticalAlignment.Center,
        WordWrapping = CanvasWordWrapping.NoWrap,
    };

    private static void DrawHorizontalSelection(
        CanvasDrawingSession drawingSession,
        HorizontalCueLayout item,
        ReaderLyricsCanvasRenderOptions options)
    {
        if (!string.Equals(options.SelectedCueId, item.Cue.Id, StringComparison.Ordinal)
            || options.SelectionStart < 0
            || options.SelectionLength <= 0)
        {
            return;
        }

        var start = Math.Clamp(options.SelectionStart, 0, item.Cue.Text.Length);
        var length = Math.Clamp(options.SelectionLength, 0, item.Cue.Text.Length - start);
        foreach (var region in item.Layout.GetCharacterRegions(start, length))
        {
            var bounds = region.LayoutBounds;
            drawingSession.FillRectangle(
                new Rect(
                    bounds.X + item.Bounds.X,
                    bounds.Y + item.Bounds.Y,
                    bounds.Width,
                    bounds.Height),
                SelectionColor);
        }
    }

    private static void DrawVertical(
        CanvasDrawingSession drawingSession,
        Size size,
        ReaderLyricsCanvasRenderOptions options)
    {
        var glyphs = CreateVerticalGlyphs(size, options);
        foreach (var glyph in glyphs)
        {
            if (glyph.IsSelected)
                drawingSession.FillRoundedRectangle(glyph.Bounds, 4, 4, SelectionColor);
            drawingSession.DrawText(glyph.Text, glyph.Bounds, glyph.Color, glyph.Format);
        }
        foreach (var glyph in glyphs)
            glyph.Format.Dispose();
    }

    private static bool TryHitTestVertical(
        Size size,
        ReaderLyricsCanvasRenderOptions options,
        Point point,
        out ReaderLyricsCanvasHitTestResult result)
    {
        result = default;
        var glyphs = CreateVerticalGlyphs(size, options);
        try
        {
            foreach (var glyph in glyphs)
            {
                if (!glyph.Bounds.Contains(point))
                    continue;

                result = new ReaderLyricsCanvasHitTestResult(
                    glyph.Cue,
                    glyph.CueIndex,
                    glyph.Utf16Start,
                    glyph.Bounds);
                return true;
            }
        }
        finally
        {
            foreach (var glyph in glyphs)
                glyph.Format.Dispose();
        }

        return false;
    }

    private static List<VerticalGlyphLayout> CreateVerticalGlyphs(
        Size size,
        ReaderLyricsCanvasRenderOptions options)
    {
        var window = ReaderLyricsModeProjection.VisibleCueWindow(
            options.Cues.Count,
            options.CurrentCueIndex,
            size.Height);
        var indices = Enumerable.Range(window.StartIndex, window.Count).Reverse().ToArray();
        var safeCurrent = Math.Clamp(options.CurrentCueIndex, 0, options.Cues.Count - 1);
        var slotWidth = Math.Max(28, size.Width / Math.Max(indices.Length, 1));
        var glyphs = new List<VerticalGlyphLayout>();

        for (var slot = 0; slot < indices.Length; slot++)
        {
            var cueIndex = indices[slot];
            var cue = options.Cues[cueIndex];
            var focused = cueIndex == safeCurrent;
            var baseFont = focused ? Math.Clamp(size.Width * 0.052, 28, 44) : Math.Clamp(size.Width * 0.038, 20, 32);
            var textElements = TextElements(cue.Text);
            var fittedFont = ReaderLyricsAdaptiveFontPolicy.FitVertical(
                baseFont,
                textElements.Count,
                size.Height,
                slotWidth);
            var rowHeight = fittedFont * ReaderLyricsAdaptiveFontPolicy.VerticalRowHeightRatio;
            var totalHeight = textElements.Count * rowHeight;
            var startY = Math.Max(0, (size.Height - totalHeight) / 2);
            var columnWidth = fittedFont * ReaderLyricsAdaptiveFontPolicy.VerticalColumnWidthRatio;
            var x = slot * slotWidth + Math.Max(0, (slotWidth - columnWidth) / 2);
            var progress = focused
                ? ReaderLyricsModeProjection.CueProgress(cue, options.PlaybackSeconds, options.DelaySeconds)
                : 0;
            var completedGlyphs = (int)Math.Ceiling(textElements.Count * progress);

            for (var elementIndex = 0; elementIndex < textElements.Count; elementIndex++)
            {
                var element = textElements[elementIndex];
                var selected = string.Equals(options.SelectedCueId, cue.Id, StringComparison.Ordinal)
                    && element.Utf16Start < options.SelectionStart + options.SelectionLength
                    && element.Utf16Start + element.Text.Length > options.SelectionStart;
                var format = new CanvasTextFormat
                {
                    FontFamily = "Segoe UI, Yu Gothic UI, Meiryo",
                    FontSize = (float)fittedFont,
                    FontWeight = new FontWeight { Weight = 700 },
                    HorizontalAlignment = CanvasHorizontalAlignment.Center,
                    VerticalAlignment = CanvasVerticalAlignment.Center,
                };
                var color = focused
                    ? elementIndex < completedGlyphs ? Colors.White : FocusedPendingColor
                    : ContextColor;
                glyphs.Add(new VerticalGlyphLayout(
                    cue,
                    cueIndex,
                    element.Text,
                    element.Utf16Start,
                    new Rect(x, startY + elementIndex * rowHeight, columnWidth, rowHeight),
                    color,
                    selected,
                    format));
            }
        }

        return glyphs;
    }

    private static List<TextElement> TextElements(string text)
    {
        var result = new List<TextElement>();
        var enumerator = StringInfo.GetTextElementEnumerator(text ?? "");
        while (enumerator.MoveNext())
        {
            var value = enumerator.GetTextElement();
            result.Add(new TextElement(
                value is "\r" or "\n" ? "　" : value,
                enumerator.ElementIndex));
        }
        return result;
    }

    private static void DrawEmptyState(
        CanvasDrawingSession drawingSession,
        Size size,
        string emptyText)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI, Yu Gothic UI, Meiryo",
            FontSize = 30,
            FontWeight = new FontWeight { Weight = 600 },
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        drawingSession.DrawText(
            emptyText,
            new Rect(0, 0, size.Width, size.Height),
            ContextColor,
            format);
    }

    private static string NormalizeLine(string text) =>
        (text ?? "").Replace('\r', ' ').Replace('\n', ' ');

    private sealed record HorizontalCueLayout(
        SasayakiMatch Cue,
        int CueIndex,
        bool IsFocused,
        CanvasTextLayout Layout,
        Rect Bounds);

    private sealed class HorizontalLayoutCollection(List<HorizontalCueLayout> items) : IDisposable
    {
        public IReadOnlyList<HorizontalCueLayout> Items { get; } = items;

        public void Dispose()
        {
            foreach (var item in Items)
                item.Layout.Dispose();
        }
    }

    private sealed record VerticalGlyphLayout(
        SasayakiMatch Cue,
        int CueIndex,
        string Text,
        int Utf16Start,
        Rect Bounds,
        Color Color,
        bool IsSelected,
        CanvasTextFormat Format);

    private readonly record struct TextElement(string Text, int Utf16Start);
}
