using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Video;

namespace Niratan.Services.Video;

public interface IVideoMetadataProvider
{
    string Id { get; }
    string DisplayName { get; }
    VideoMetadataCapabilities Capabilities { get; }
    IReadOnlySet<VideoMetadataMediaKind> SupportedMediaKinds { get; }
    bool ArtworkEnabledByDefault { get; }
    string? AttributionUrl { get; }
}

public interface IVideoMetadataSearchProvider : IVideoMetadataProvider
{
    Task<IReadOnlyList<VideoMetadataCandidate>> SearchAsync(
        VideoMetadataSearchQuery query,
        CancellationToken ct = default);
}

public interface IVideoMetadataDetailsProvider : IVideoMetadataProvider
{
    Task<VideoMetadataDetails?> GetDetailsAsync(
        VideoMetadataCandidate identity,
        string language,
        string region,
        CancellationToken ct = default);
}

public interface IVideoArtworkProvider : IVideoMetadataProvider
{
    Task<IReadOnlyList<VideoArtworkCandidate>> GetArtworkAsync(
        VideoMetadataCandidate identity,
        CancellationToken ct = default);
}

public sealed record VideoMetadataRequest(
    string ProviderId,
    HttpMethod Method,
    Uri Uri,
    byte[]? Body = null,
    string? ContentType = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? ETag = null,
    DateTimeOffset? LastModified = null,
    bool IsIdempotent = true,
    long MaxResponseBytes = 8L * 1024 * 1024);

public sealed record VideoMetadataResponse(
    int StatusCode,
    byte[] Content,
    string? ContentType,
    string? ETag,
    DateTimeOffset? LastModified,
    DateTimeOffset ReceivedAt,
    bool IsNotModified);

public interface IVideoMetadataTransport
{
    Task<VideoMetadataResponse> SendAsync(
        VideoMetadataRequest request,
        CancellationToken ct = default);
}

public interface IVideoMetadataCredentialStore
{
    Task<string?> ReadAsync(string providerId, string secretName, CancellationToken ct = default);
    Task WriteAsync(string providerId, string secretName, string value, CancellationToken ct = default);
    Task DeleteAsync(string providerId, string secretName, CancellationToken ct = default);
}

public sealed record VideoArtworkCacheEntry(
    string LocalPath,
    string Url,
    string? ETag,
    DateTimeOffset? LastModified,
    long Size,
    DateTimeOffset LastAccessedAt);

public interface IVideoArtworkCache
{
    Task<VideoArtworkCacheEntry?> GetAsync(string url, CancellationToken ct = default);
    Task<VideoArtworkCacheEntry> StoreAsync(
        string url,
        Stream content,
        string? contentType,
        string? etag,
        DateTimeOffset? lastModified,
        CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
    Task TrimAsync(CancellationToken ct = default);
}
