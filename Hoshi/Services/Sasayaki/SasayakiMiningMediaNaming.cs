using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Hoshi.Services.Sasayaki;

public static class SasayakiMiningMediaNaming
{
    public static string CreateAudioClipFilename(string audiobookPath, TimeSpan start, TimeSpan end) =>
        $"hoshi_sasayaki_{StablePathHash(audiobookPath)}_{FormatMilliseconds(start)}_{FormatMilliseconds(end)}.m4a";

    private static string StablePathHash(string path)
    {
        var normalized = Path.GetFullPath(path).ToUpperInvariant();
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private static string FormatMilliseconds(TimeSpan time)
    {
        var milliseconds = Math.Max(0, (long)Math.Round(time.TotalMilliseconds));
        return milliseconds.ToString("D9");
    }
}
