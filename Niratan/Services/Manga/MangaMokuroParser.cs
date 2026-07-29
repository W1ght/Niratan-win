using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Niratan.Models.Manga;

namespace Niratan.Services.Manga;

internal static class MangaMokuroParser
{
    public static IReadOnlyList<string> GetPagePaths(byte[] data)
    {
        using var document = JsonDocument.Parse(data);
        if (!document.RootElement.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Mokuro metadata does not contain a pages array.");
        }

        return pages.EnumerateArray()
            .Select(page => TryGetString(page, "img_path"))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Trim())
            .ToList();
    }

    public static IReadOnlyList<MangaTextRegion> GetRegions(
        byte[] data,
        string pagePath,
        int pageIndex)
    {
        using var document = JsonDocument.Parse(data);
        if (!document.RootElement.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Mokuro metadata does not contain a pages array.");
        }

        var pageName = System.IO.Path.GetFileName(pagePath);
        JsonElement? selected = null;
        var currentIndex = 0;
        foreach (var page in pages.EnumerateArray())
        {
            var imagePath = TryGetString(page, "img_path");
            if (string.Equals(
                System.IO.Path.GetFileName(imagePath),
                pageName,
                StringComparison.OrdinalIgnoreCase))
            {
                selected = page;
                break;
            }

            if (currentIndex == pageIndex)
                selected ??= page;
            currentIndex++;
        }

        if (selected is not { } rawPage)
            return [];

        var imageWidth = TryGetDouble(rawPage, "img_width");
        var imageHeight = TryGetDouble(rawPage, "img_height");
        if (imageWidth <= 0 || imageHeight <= 0
            || !rawPage.TryGetProperty("blocks", out var blocks)
            || blocks.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var regions = new List<MangaTextRegion>();
        var blockIndex = 0;
        foreach (var block in blocks.EnumerateArray())
        {
            var lines = GetStrings(block, "lines");
            var box = GetNumbers(block, "box");
            if (lines.Count == 0 || box.Count < 4)
            {
                blockIndex++;
                continue;
            }

            var isVertical = block.TryGetProperty("vertical", out var vertical)
                && vertical.ValueKind == JsonValueKind.True;
            var blockId = $"mokuro-{pageIndex}-{blockIndex}";
            var sentence = string.Concat(lines);
            var blockBounds = Normalize(box[0], box[1], box[2], box[3], imageWidth, imageHeight);
            var lineBounds = GetLineBounds(block, imageWidth, imageHeight);
            var baseOffset = 0;

            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                var line = lines[lineIndex];
                var bounds = lineIndex < lineBounds.Count
                    ? lineBounds[lineIndex]
                    : FallbackLineBounds(blockBounds, lineIndex, lines.Count, isVertical);
                var characters = EnumerateUtf16Characters(line)
                    .Where(character => !char.IsWhiteSpace(character.Character))
                    .ToList();
                if (characters.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
                {
                    baseOffset += line.Length;
                    continue;
                }

                var lineId = $"{blockId}-{lineIndex}";
                for (var characterIndex = 0; characterIndex < characters.Count; characterIndex++)
                {
                    var character = characters[characterIndex];
                    var characterBounds = isVertical
                        ? new NormalizedBounds(
                            bounds.X,
                            bounds.Y + bounds.Height * characterIndex / characters.Count,
                            bounds.Width,
                            bounds.Height / characters.Count)
                        : new NormalizedBounds(
                            bounds.X + bounds.Width * characterIndex / characters.Count,
                            bounds.Y,
                            bounds.Width / characters.Count,
                            bounds.Height);
                    regions.Add(new MangaTextRegion(
                        $"{lineId}-{baseOffset + character.Offset}",
                        pageIndex,
                        blockId,
                        lineId,
                        sentence,
                        baseOffset + character.Offset,
                        isVertical,
                        characterBounds.X,
                        characterBounds.Y,
                        characterBounds.Width,
                        characterBounds.Height));
                }

                baseOffset += line.Length;
            }

            blockIndex++;
        }

        return regions;
    }

    private static List<NormalizedBounds> GetLineBounds(
        JsonElement block,
        double imageWidth,
        double imageHeight)
    {
        var result = new List<NormalizedBounds>();
        if (!block.TryGetProperty("lines_coords", out var lineCoordinates)
            || lineCoordinates.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var polygon in lineCoordinates.EnumerateArray())
        {
            if (polygon.ValueKind != JsonValueKind.Array)
                continue;
            var points = polygon.EnumerateArray()
                .Select(GetNumbers)
                .Where(point => point.Count >= 2)
                .ToList();
            if (points.Count == 0)
                continue;

            result.Add(Normalize(
                points.Min(point => point[0]),
                points.Min(point => point[1]),
                points.Max(point => point[0]),
                points.Max(point => point[1]),
                imageWidth,
                imageHeight));
        }

        return result;
    }

    private static NormalizedBounds FallbackLineBounds(
        NormalizedBounds block,
        int lineIndex,
        int lineCount,
        bool isVertical)
    {
        var count = Math.Max(1, lineCount);
        return isVertical
            ? new NormalizedBounds(
                block.X + block.Width * (count - lineIndex - 1) / count,
                block.Y,
                block.Width / count,
                block.Height)
            : new NormalizedBounds(
                block.X,
                block.Y + block.Height * lineIndex / count,
                block.Width,
                block.Height / count);
    }

    private static NormalizedBounds Normalize(
        double x1,
        double y1,
        double x2,
        double y2,
        double imageWidth,
        double imageHeight)
    {
        var left = Math.Clamp(Math.Min(x1, x2) / imageWidth, 0, 1);
        var top = Math.Clamp(Math.Min(y1, y2) / imageHeight, 0, 1);
        var right = Math.Clamp(Math.Max(x1, x2) / imageWidth, 0, 1);
        var bottom = Math.Clamp(Math.Max(y1, y2) / imageHeight, 0, 1);
        return new NormalizedBounds(left, top, right - left, bottom - top);
    }

    private static List<string> GetStrings(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim()
                : value.ToString().Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToList();
    }

    private static List<double> GetNumbers(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? GetNumbers(value) : [];

    private static List<double> GetNumbers(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray()
            .Select(number => number.ValueKind == JsonValueKind.Number
                && number.TryGetDouble(out var parsed)
                    ? parsed
                    : 0)
            .ToList();
    }

    private static string? TryGetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double TryGetDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var parsed)
            ? parsed
            : 0;

    private static IEnumerable<(char Character, int Offset)> EnumerateUtf16Characters(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            yield return (value[index], index);
            if (char.IsHighSurrogate(value[index])
                && index + 1 < value.Length
                && char.IsLowSurrogate(value[index + 1]))
            {
                index++;
            }
        }
    }

    private readonly record struct NormalizedBounds(
        double X,
        double Y,
        double Width,
        double Height);
}
