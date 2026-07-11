using System;
using System.Collections.Generic;
using System.IO;
using Hoshi.Models.Novel;

namespace Hoshi.Services.Novels;

public sealed record ReaderInternalLinkTarget(int ChapterIndex, string? Fragment);

public static class ReaderInternalLinkResolver
{
    public static ReaderInternalLinkTarget? Resolve(
        string containerDirectory,
        IReadOnlyList<EpubChapter> chapters,
        string href,
        string virtualHostName)
    {
        if (string.IsNullOrWhiteSpace(containerDirectory)
            || !Uri.TryCreate(href, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, virtualHostName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string targetPath;
        try
        {
            var relativePath = Uri.UnescapeDataString(uri.AbsolutePath)
                .TrimStart('/')
                .Replace('/', Path.DirectorySeparatorChar);
            var rootPath = Path.GetFullPath(containerDirectory);
            targetPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
            var relativeToRoot = Path.GetRelativePath(rootPath, targetPath);
            if (relativeToRoot == ".."
                || relativeToRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        for (var index = 0; index < chapters.Count; index++)
        {
            if (!string.Equals(
                    Path.GetFullPath(chapters[index].Href),
                    targetPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fragment = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
            return new ReaderInternalLinkTarget(
                index,
                string.IsNullOrWhiteSpace(fragment) ? null : fragment);
        }

        return null;
    }
}
