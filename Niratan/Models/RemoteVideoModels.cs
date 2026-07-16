using System;
using System.Collections.Generic;
using System.Linq;

namespace Niratan.Models;

public sealed record RemoteVideoIdentity(
    string ProviderId,
    string RemoteId,
    string OriginalUrl,
    string CanonicalUrl,
    string Title,
    string? ThumbnailUrl,
    TimeSpan? Duration)
{
    public string PersistenceKey => $"remote://{ProviderId}/{RemoteId}";

    public static bool IsPersistenceKey(string? value, string? providerId = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var prefix = string.IsNullOrWhiteSpace(providerId)
            ? "remote://"
            : $"remote://{providerId}/";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record RemoteVideoStream(
    string Url,
    string FormatId,
    int? Height,
    bool HasVideo,
    bool HasAudio,
    string Container,
    string? VideoCodec,
    string? AudioCodec,
    long Bitrate,
    IReadOnlyDictionary<string, string> HttpHeaders);

public sealed record RemoteVideoQualityOption(
    string Id,
    int Height,
    RemoteVideoStream PlaybackStream,
    RemoteVideoStream? AudioStream)
{
    public string DisplayName => $"{Height}p";
}

public sealed record RemoteVideoSubtitleOption(
    string Id,
    string Language,
    string Name,
    string SourceUrl,
    bool IsAutomatic)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Language : $"{Name} ({Language})";
}

public sealed record ResolvedRemoteVideoSource(
    RemoteVideoIdentity Identity,
    RemoteVideoStream PlaybackStream,
    RemoteVideoStream? AudioStream,
    RemoteVideoStream? MuxedFallbackStream,
    RemoteVideoStream MiningStream,
    IReadOnlyList<RemoteVideoSubtitleOption> SubtitleOptions,
    string? SelectedSubtitleLanguage,
    DateTimeOffset ResolvedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<RemoteVideoQualityOption> QualityOptions,
    TimeSpan? RequestedStartPosition = null)
{
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public RemoteVideoSubtitleOption? PreferredSubtitle(
        IEnumerable<string?>? preferredLanguages = null)
    {
        var languages = (preferredLanguages ?? [])
            .Concat(["ja", "en"])
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Select(language => language!.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var language in languages)
        {
            var match = SubtitleOptions.FirstOrDefault(option =>
                option.Language.Equals(language, StringComparison.OrdinalIgnoreCase)
                || option.Language.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        return SubtitleOptions.FirstOrDefault();
    }

    public ResolvedRemoteVideoSource? SelectQuality(string qualityId)
    {
        var option = QualityOptions.FirstOrDefault(candidate => candidate.Id == qualityId);
        return option == null
            ? null
            : this with
            {
                PlaybackStream = option.PlaybackStream,
                AudioStream = option.AudioStream,
            };
    }

    public int? SelectedHeight => QualityOptions.FirstOrDefault(option =>
        option.PlaybackStream.Url == PlaybackStream.Url
        && option.AudioStream?.Url == AudioStream?.Url)?.Height ?? PlaybackStream.Height;
}

public sealed record VideoPlaybackRequest(
    string PrimarySource,
    string? ExternalAudioSource,
    string? SubtitlePath,
    IReadOnlyDictionary<string, string> HttpHeaders,
    TimeSpan? StartPosition)
{
    public static VideoPlaybackRequest Local(
        string filePath,
        string? subtitlePath,
        TimeSpan? startPosition) =>
        new(filePath, null, subtitlePath, EmptyHeaders, startPosition);

    private static IReadOnlyDictionary<string, string> EmptyHeaders { get; } =
        new Dictionary<string, string>();
}

public sealed record VideoMiningMediaSource(
    string Source,
    IReadOnlyDictionary<string, string> HttpHeaders,
    bool IsRemote)
{
    public static VideoMiningMediaSource Local(string path) =>
        new(path, new Dictionary<string, string>(), false);

    public static VideoMiningMediaSource Remote(RemoteVideoStream stream) =>
        new(stream.Url, stream.HttpHeaders, true);
}

public sealed record VideoPlaybackLaunchRequest(
    VideoItem Video,
    IReadOnlyList<VideoItem> Playlist,
    ResolvedRemoteVideoSource? ResolvedRemoteSource = null);

public sealed class VideoMediaLoadedEventArgs : EventArgs;

public sealed class VideoMediaFailedEventArgs(string message, bool externalAudioFailed = false) : EventArgs
{
    public string Message { get; } = message;
    public bool ExternalAudioFailed { get; } = externalAudioFailed;
}
