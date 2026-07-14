using System;
using System.Collections.Generic;
using System.Linq;

namespace Niratan.Services.Video;

public readonly record struct VideoSubtitleCharacterRect(
    int CharacterIndex,
    double X,
    double Y,
    double Width,
    double Height);

public static class VideoSubtitleCharacterRectHitTester
{
    public static int GetTextPointerOffsetForCharacterIndex(int characterIndex) =>
        Math.Max(0, characterIndex);

    public static IReadOnlyList<VideoSubtitleCharacterRect> CreateCharacterHitRectsFromCharacterRects(
        IReadOnlyList<VideoSubtitleCharacterRect> characterRects,
        double? containerHeight = null)
    {
        var hitRects = new List<VideoSubtitleCharacterRect>(characterRects.Count);
        foreach (var rect in characterRects)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                continue;

            hitRects.Add(rect);
        }

        return ExpandLineBands(hitRects, containerHeight);
    }

    public static IReadOnlyList<VideoSubtitleCharacterRect> CreateCharacterHitRectsFromLeadingEdges(
        IReadOnlyList<VideoSubtitleCharacterRect> leadingEdges)
    {
        var hitRects = new List<VideoSubtitleCharacterRect>(leadingEdges.Count);
        for (var index = 0; index < leadingEdges.Count; index++)
        {
            var current = leadingEdges[index];
            if (current.Height <= 0)
                continue;

            var width = current.Width;
            if (TryGetNextSameLineEdge(leadingEdges, index, out var nextEdge))
            {
                width = nextEdge.X - current.X;
            }
            else if (TryGetPreviousSameLineEdge(leadingEdges, index, out var previousEdge))
            {
                width = current.X - previousEdge.X;
            }

            if (width <= 0)
                continue;

            hitRects.Add(new VideoSubtitleCharacterRect(
                current.CharacterIndex,
                current.X,
                current.Y,
                width,
                current.Height));
        }

        return hitRects;
    }

    public static int? TryResolveCharacterIndex(
        IEnumerable<VideoSubtitleCharacterRect> rects,
        double pointX,
        double pointY)
    {
        if (double.IsNaN(pointX)
            || double.IsInfinity(pointX)
            || double.IsNaN(pointY)
            || double.IsInfinity(pointY))
        {
            return null;
        }

        foreach (var rect in rects)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                continue;

            if (pointX >= rect.X
                && pointX <= rect.X + rect.Width
                && pointY >= rect.Y
                && pointY <= rect.Y + rect.Height)
            {
                return rect.CharacterIndex;
            }
        }

        return null;
    }

    private static bool TryGetNextSameLineEdge(
        IReadOnlyList<VideoSubtitleCharacterRect> leadingEdges,
        int index,
        out VideoSubtitleCharacterRect edge)
    {
        edge = default;
        for (var next = index + 1; next < leadingEdges.Count; next++)
        {
            if (IsSameVisualLine(leadingEdges[index], leadingEdges[next])
                && leadingEdges[next].X > leadingEdges[index].X)
            {
                edge = leadingEdges[next];
                return true;
            }

            if (!IsSameVisualLine(leadingEdges[index], leadingEdges[next]))
                break;
        }

        return false;
    }

    private static bool TryGetPreviousSameLineEdge(
        IReadOnlyList<VideoSubtitleCharacterRect> leadingEdges,
        int index,
        out VideoSubtitleCharacterRect edge)
    {
        edge = default;
        for (var previous = index - 1; previous >= 0; previous--)
        {
            if (IsSameVisualLine(leadingEdges[index], leadingEdges[previous])
                && leadingEdges[index].X > leadingEdges[previous].X)
            {
                edge = leadingEdges[previous];
                return true;
            }

            if (!IsSameVisualLine(leadingEdges[index], leadingEdges[previous]))
                break;
        }

        return false;
    }

    private static bool IsSameVisualLine(VideoSubtitleCharacterRect first, VideoSubtitleCharacterRect second)
    {
        if (first.Height <= 0 || second.Height <= 0)
            return false;

        var firstCenter = first.Y + first.Height / 2;
        var secondCenter = second.Y + second.Height / 2;
        return Math.Abs(firstCenter - secondCenter) <= Math.Max(first.Height, second.Height) * 0.35;
    }

    private static IReadOnlyList<VideoSubtitleCharacterRect> ExpandLineBands(
        IReadOnlyList<VideoSubtitleCharacterRect> rects,
        double? containerHeight)
    {
        if (!containerHeight.HasValue
            || !double.IsFinite(containerHeight.Value)
            || containerHeight.Value <= 0
            || rects.Count == 0)
        {
            return rects;
        }

        var lines = new List<List<VideoSubtitleCharacterRect>>();
        foreach (var rect in rects.OrderBy(rect => rect.Y + rect.Height / 2))
        {
            var line = lines.FirstOrDefault(existing => IsSameVisualLine(existing[0], rect));
            if (line == null)
            {
                lines.Add([rect]);
            }
            else
            {
                line.Add(rect);
            }
        }

        var lineCenters = lines
            .Select(line => line.Average(rect => rect.Y + rect.Height / 2))
            .ToArray();
        var expanded = new List<VideoSubtitleCharacterRect>(rects.Count);
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var top = lineIndex == 0
                ? 0
                : (lineCenters[lineIndex - 1] + lineCenters[lineIndex]) / 2;
            var bottom = lineIndex == lines.Count - 1
                ? containerHeight.Value
                : (lineCenters[lineIndex] + lineCenters[lineIndex + 1]) / 2;
            var height = bottom - top;
            if (height <= 0)
                continue;

            var line = lines[lineIndex]
                .OrderBy(rect => rect.X)
                .ToArray();
            for (var rectIndex = 0; rectIndex < line.Length; rectIndex++)
            {
                var rect = line[rectIndex];
                var left = rect.X;
                var right = rect.X + rect.Width;

                if (rectIndex < line.Length - 1)
                {
                    right = line[rectIndex + 1].X;
                }

                var width = right - left;
                if (width <= 0)
                    continue;

                expanded.Add(new VideoSubtitleCharacterRect(
                    rect.CharacterIndex,
                    left,
                    top,
                    width,
                    height));
            }
        }

        return expanded
            .OrderBy(rect => rect.CharacterIndex)
            .ToArray();
    }

}
