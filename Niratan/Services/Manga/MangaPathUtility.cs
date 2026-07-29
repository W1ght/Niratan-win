using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Niratan.Services.Manga;

internal static partial class MangaPathUtility
{
    internal static readonly HashSet<string> ImageExtensions = new(
        [".avif", ".bmp", ".gif", ".heic", ".heif", ".jpeg", ".jpg",
         ".png", ".tif", ".tiff", ".webp"],
        StringComparer.OrdinalIgnoreCase);

    internal static bool IsImagePath(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path));

    internal static bool IsVisibleArchiveEntry(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase))
            return false;

        return normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(component =>
                !component.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
                && !component.StartsWith("._", StringComparison.Ordinal));
    }

    internal static string? ResolveArchivePath(string reference, string relativeTo)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;

        var raw = reference.Split('#', 2)[0].Split('?', 2)[0];
        try
        {
            raw = Uri.UnescapeDataString(raw);
        }
        catch (UriFormatException)
        {
            return null;
        }

        if (raw.StartsWith('/') || raw.Contains(':'))
            return null;

        var components = new List<string>();
        var basePath = relativeTo.Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(basePath))
        {
            var slash = basePath.LastIndexOf('/');
            if (slash >= 0)
                components.AddRange(basePath[..slash].Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var component in raw.Replace('\\', '/')
                     .Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".")
                continue;
            if (component == "..")
            {
                if (components.Count == 0)
                    return null;
                components.RemoveAt(components.Count - 1);
                continue;
            }

            components.Add(component);
        }

        return components.Count == 0 ? null : string.Join('/', components);
    }

    internal static IOrderedEnumerable<string> NaturalOrder(IEnumerable<string> paths) =>
        paths.OrderBy(path => path, NaturalStringComparer.Instance);

    internal static string SafeExtension(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return ImageExtensions.Contains(extension) ? extension : ".img";
    }

    internal static string GetCacheDirectory(
        string cacheRoot,
        string itemId,
        params string[] children)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        if (string.IsNullOrWhiteSpace(itemId)
            || Path.IsPathRooted(itemId)
            || itemId is "." or ".."
            || itemId.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidDataException("Manga cache identity is invalid.");
        }

        var root = Path.GetFullPath(cacheRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(
            new[] { root, itemId }.Concat(children).ToArray()));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Manga cache path escapes its root.");
        return path;
    }

    private sealed partial class NaturalStringComparer : IComparer<string>
    {
        internal static NaturalStringComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            var left = TokenRegex().Matches(x);
            var right = TokenRegex().Matches(y);
            var count = Math.Min(left.Count, right.Count);
            for (var index = 0; index < count; index++)
            {
                var leftToken = left[index].Value;
                var rightToken = right[index].Value;
                int comparison;
                if (long.TryParse(leftToken, out var leftNumber)
                    && long.TryParse(rightToken, out var rightNumber))
                {
                    comparison = leftNumber.CompareTo(rightNumber);
                    if (comparison == 0)
                        comparison = leftToken.Length.CompareTo(rightToken.Length);
                }
                else
                {
                    comparison = string.Compare(
                        leftToken,
                        rightToken,
                        StringComparison.CurrentCultureIgnoreCase);
                }

                if (comparison != 0)
                    return comparison;
            }

            return left.Count.CompareTo(right.Count);
        }

        [GeneratedRegex(@"\d+|\D+", RegexOptions.CultureInvariant)]
        private static partial Regex TokenRegex();
    }
}
