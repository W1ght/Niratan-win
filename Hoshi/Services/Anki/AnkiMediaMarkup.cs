using System.IO;

namespace Hoshi.Services.Anki;

internal static class AnkiMediaMarkup
{
    public static string ForFieldPlaceholder(string filename)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();

        if (ext is ".mp3" or ".aac" or ".m4a" or ".wav" or ".ogg")
            return $"[sound:{filename}]";

        if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".avif" or ".svg")
            return $"<img src=\"{filename}\">";

        return filename;
    }

    public static string ForDictionaryHtmlReference(string filename) => filename;
}
