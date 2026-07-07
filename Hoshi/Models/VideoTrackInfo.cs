using System;
using System.Linq;

namespace Hoshi.Models;

public enum VideoTrackType
{
    Video,
    Audio,
    Subtitle,
}

public sealed record VideoTrackInfo(
    int Id,
    VideoTrackType Type,
    string Title,
    string? Language,
    string? Codec,
    int? FfIndex,
    string? ExternalFilename,
    bool IsImage,
    bool IsSelected)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Language)
            ? Title
            : $"{Title} · {Language}";

    public string Description
    {
        get
        {
            var codec = string.IsNullOrWhiteSpace(Codec) ? null : Codec;
            var source = string.IsNullOrWhiteSpace(ExternalFilename)
                ? null
                : System.IO.Path.GetFileName(ExternalFilename);
            return string.Join(" · ", new[] { codec, source }.Where(part => !string.IsNullOrWhiteSpace(part)));
        }
    }

    public static string DefaultTitle(VideoTrackType type, int id) =>
        type switch
        {
            VideoTrackType.Video => $"Video {id}",
            VideoTrackType.Audio => $"Audio {id}",
            VideoTrackType.Subtitle => $"Subtitle {id}",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };
}
