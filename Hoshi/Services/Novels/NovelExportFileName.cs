using System;
using System.IO;
using System.Linq;

namespace Hoshi.Services.Novels;

internal static class NovelExportFileName
{
    public static string CreateBaseName(string? title)
    {
        var value = title?.Trim() ?? string.Empty;
        while (value.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
            value = value[..^5];

        var invalid = Path.GetInvalidFileNameChars();
        value = new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.');

        return string.IsNullOrWhiteSpace(value) ? "book" : value;
    }
}
