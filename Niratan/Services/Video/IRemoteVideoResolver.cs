using System;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;

namespace Niratan.Services.Video;

public enum RemoteVideoResolverError
{
    UnsupportedUrl,
    ResolutionFailed,
    InvalidResponse,
    NoPlayableStream,
    ContentUnavailable,
    SignInRequired,
    RegionRestricted,
    RateLimited,
    Cancelled,
    TimedOut,
}

public sealed class RemoteVideoResolverException(
    RemoteVideoResolverError error,
    Exception? innerException = null)
    : Exception(GetMessage(error), innerException)
{
    public RemoteVideoResolverError Error { get; } = error;

    private static string GetMessage(RemoteVideoResolverError error) => error switch
    {
        RemoteVideoResolverError.UnsupportedUrl => "This link is not supported.",
        RemoteVideoResolverError.InvalidResponse => "YouTube returned an invalid response.",
        RemoteVideoResolverError.NoPlayableStream => "Unable to find a playable YouTube video stream.",
        RemoteVideoResolverError.ContentUnavailable => "This video is unavailable or private.",
        RemoteVideoResolverError.SignInRequired => "This video requires sign-in or age verification.",
        RemoteVideoResolverError.RegionRestricted => "This video is not available in your region.",
        RemoteVideoResolverError.RateLimited => "YouTube is temporarily limiting requests. Try again later.",
        RemoteVideoResolverError.Cancelled => "YouTube video loading was cancelled.",
        RemoteVideoResolverError.TimedOut => "YouTube video loading timed out.",
        _ => "Unable to resolve this YouTube video. Try again.",
    };
}

public interface IRemoteVideoResolver
{
    Task<ResolvedRemoteVideoSource> ResolveAsync(
        string url,
        string? preferredSubtitleLanguage = null,
        bool forceRefresh = false,
        CancellationToken ct = default);

    Task<ResolvedRemoteVideoSource> ResolveAsync(
        RemoteVideoIdentity identity,
        string? preferredSubtitleLanguage = null,
        bool forceRefresh = false,
        CancellationToken ct = default);

    Task<string> DownloadSubtitleAsync(
        RemoteVideoSubtitleOption option,
        string outputPath,
        CancellationToken ct = default);
}
