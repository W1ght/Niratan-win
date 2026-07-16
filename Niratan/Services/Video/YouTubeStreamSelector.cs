using System;
using System.Collections.Generic;
using System.Linq;
using Niratan.Models;

namespace Niratan.Services.Video;

public sealed record RemoteVideoStreamSelection(
    RemoteVideoStream Playback,
    RemoteVideoStream? ExternalAudio,
    RemoteVideoStream? MuxedFallback,
    RemoteVideoStream Mining,
    IReadOnlyList<RemoteVideoQualityOption> QualityOptions);

public static class YouTubeStreamSelector
{
    public const int MaximumHeight = 1080;

    public static RemoteVideoStreamSelection Select(IReadOnlyList<RemoteVideoStream> streams)
    {
        var audio = streams
            .Where(stream => stream.HasAudio && !stream.HasVideo)
            .OrderByDescending(IsPreferredAudio)
            .ThenByDescending(stream => stream.Bitrate)
            .FirstOrDefault();

        var muxed = streams
            .Where(stream => stream.HasAudio && stream.HasVideo && IsSupportedHeight(stream.Height))
            .OrderByDescending(stream => stream.Height)
            .ThenByDescending(IsPreferredVideo)
            .ThenByDescending(stream => stream.Bitrate)
            .FirstOrDefault();

        var qualityOptions = new List<RemoteVideoQualityOption>();
        foreach (var group in streams
                     .Where(stream => stream.HasVideo
                                      && IsSupportedHeight(stream.Height)
                                      && (stream.HasAudio || audio != null))
                     .GroupBy(stream => stream.Height!.Value))
        {
            var best = group
                .OrderByDescending(IsPreferredVideo)
                .ThenByDescending(stream => stream.Bitrate)
                .First();
            qualityOptions.Add(new RemoteVideoQualityOption(
                best.FormatId,
                group.Key,
                best,
                best.HasAudio ? null : audio));
        }

        qualityOptions = qualityOptions
            .OrderByDescending(option => option.Height)
            .ThenBy(option => option.Id, StringComparer.Ordinal)
            .ToList();

        if (qualityOptions.FirstOrDefault() is { } selected)
        {
            return new RemoteVideoStreamSelection(
                selected.PlaybackStream,
                selected.AudioStream,
                selected.AudioStream == null ? null : muxed,
                selected.AudioStream ?? muxed ?? selected.PlaybackStream,
                qualityOptions);
        }

        if (muxed != null)
            return new RemoteVideoStreamSelection(muxed, null, null, muxed, []);

        throw new RemoteVideoResolverException(RemoteVideoResolverError.NoPlayableStream);
    }

    private static bool IsSupportedHeight(int? height) => height is > 0 and <= MaximumHeight;

    private static bool IsPreferredAudio(RemoteVideoStream stream) =>
        stream.Container.Equals("mp4", StringComparison.OrdinalIgnoreCase)
        || stream.Container.Equals("m4a", StringComparison.OrdinalIgnoreCase)
        || stream.AudioCodec?.Contains("aac", StringComparison.OrdinalIgnoreCase) == true
        || stream.AudioCodec?.Contains("mp4a", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsPreferredVideo(RemoteVideoStream stream) =>
        stream.Container.Equals("mp4", StringComparison.OrdinalIgnoreCase)
        && (stream.VideoCodec?.Contains("avc", StringComparison.OrdinalIgnoreCase) == true
            || stream.VideoCodec?.Contains("h264", StringComparison.OrdinalIgnoreCase) == true);
}
