using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Niratan.Services.Video;

public static class VideoMiningMediaNaming
{
    public static string CreateScreenshotFilename(string videoPath, TimeSpan timestamp) =>
        $"niratan_video_{StablePathHash(videoPath)}_{FormatMilliseconds(timestamp)}.webp";

    public static string CreateAudioClipFilename(string videoPath, TimeSpan start, TimeSpan end) =>
        $"niratan_video_{StablePathHash(videoPath)}_{FormatMilliseconds(start)}_{FormatMilliseconds(end)}.m4a";

    private static string StablePathHash(string videoPath)
    {
        var normalized = Path.GetFullPath(videoPath).ToUpperInvariant();
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private static string FormatMilliseconds(TimeSpan time)
    {
        var milliseconds = Math.Max(0, (long)Math.Round(time.TotalMilliseconds));
        return milliseconds.ToString("D9");
    }
}
