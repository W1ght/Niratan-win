using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Niratan.Models.Video;

internal sealed class VideoLibraryCatalogDocument
{
    public List<VideoLibrarySourceDocument> Sources { get; set; } = [];
    public List<VideoLibraryItemDocument> Items { get; set; } = [];
    public List<RemoteVideoLibraryItemDocument> RemoteItems { get; set; } = [];
    public Dictionary<string, VideoLibraryItemMetadataDocument> ItemMetadataByPath { get; set; } = [];
    public List<VideoLibraryCollectionDocument> Collections { get; set; } = [];
}

internal sealed class VideoLibrarySourceDocument
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    // Niratan requires a macOS security-scoped bookmark. Windows keeps the field
    // structurally compatible but cannot manufacture or consume that capability.
    public byte[] Bookmark { get; set; } = [];
    public DateTimeOffset? LastScannedAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}

internal sealed class VideoLibraryItemDocument
{
    public string Path { get; set; } = "";
    public Guid SourceID { get; set; }
    public string Title { get; set; } = "";
    public string ParentFolder { get; set; } = "";
    public long FileSize { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public VideoMediaIdentityDocument MediaIdentity { get; set; } = new();
    public DateTimeOffset? ImportedAt { get; set; }
}

internal sealed class RemoteVideoLibraryItemDocument
{
    public RemoteVideoIdentityDocument Identity { get; set; } = new();
    public string? SubtitleLanguage { get; set; }
    public bool HasResolvedSubtitleMetadata { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset LastResolvedAt { get; set; }
}

internal sealed class RemoteVideoIdentityDocument
{
    public string ProviderID { get; set; } = "";
    public string RemoteID { get; set; } = "";
    public string OriginalURL { get; set; } = "";
    public string? CanonicalURL { get; set; }
    public string Title { get; set; } = "";
    public string? ThumbnailURL { get; set; }
    public double? Duration { get; set; }
}

internal sealed class VideoMediaIdentityDocument
{
    [JsonPropertyName("localFile")]
    public LocalVideoMediaIdentityDocument? LocalFile { get; set; }

    [JsonPropertyName("remote")]
    public RemoteVideoMediaIdentityDocument? Remote { get; set; }

    public static VideoMediaIdentityDocument Local(string path) =>
        new() { LocalFile = new LocalVideoMediaIdentityDocument { Path = path } };

    public static VideoMediaIdentityDocument RemoteVideo(string providerID, string remoteID) =>
        new()
        {
            Remote = new RemoteVideoMediaIdentityDocument
            {
                ProviderID = providerID,
                RemoteID = remoteID,
            },
        };
}

internal sealed class LocalVideoMediaIdentityDocument
{
    public string Path { get; set; } = "";
}

internal sealed class RemoteVideoMediaIdentityDocument
{
    public string ProviderID { get; set; } = "";
    public string RemoteID { get; set; } = "";
}

internal sealed class VideoLibraryItemMetadataDocument
{
    public string? DisplayTitle { get; set; }
    public bool IsFavorite { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<Guid> CollectionIDs { get; set; } = [];
    public string? BoundSubtitlePath { get; set; }

    // Windows-only extensions stay outside Niratan's user-visible catalog model.
    // Swift Codable ignores these keys if this file is inspected by Niratan.
    public string? PosterPath { get; set; }
    public string? ProfileID { get; set; }
}

internal sealed class VideoLibraryCollectionDocument
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "manual";
    public List<string> ItemPaths { get; set; } = [];
    public List<VideoLibrarySmartRuleDocument> SmartRules { get; set; } = [];
}

internal sealed class VideoLibrarySmartRuleDocument
{
    public Guid Id { get; set; }
    public string Field { get; set; } = "fileName";
    public string Match { get; set; } = "contains";
    public string Value { get; set; } = "";
}

internal sealed class VideoPlaybackHistoryDocument
{
    public Dictionary<string, double> Positions { get; set; } = [];
    public Dictionary<string, VideoPlaybackStateDocument> PlaybackStates { get; set; } = [];
    public Dictionary<string, VideoSubtitleSelectionDocument> SubtitleSelections { get; set; } = [];
}

internal sealed class VideoPlaybackStateDocument
{
    public double Position { get; set; }
    public double? Duration { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsFinished { get; set; }
    public VideoPlaybackResumeOptionsDocument ResumeOptions { get; set; } = new();
}

internal sealed class VideoPlaybackResumeOptionsDocument
{
    public double? Speed { get; set; }
    public double? SubtitleDelay { get; set; }
    public double? AudioDelay { get; set; }
    public VideoAudioSelectionDocument? AudioSelection { get; set; }
}

internal sealed class VideoSubtitleSelectionDocument
{
    [JsonPropertyName("off")]
    public EmptyVideoSelectionDocument? Off { get; set; }

    [JsonPropertyName("embedded")]
    public EmbeddedVideoSubtitleSelectionDocument? Embedded { get; set; }

    [JsonPropertyName("external")]
    public ExternalVideoSubtitleSelectionDocument? External { get; set; }

    [JsonPropertyName("remote")]
    public RemoteLanguageVideoSubtitleSelectionDocument? Remote { get; set; }
}

internal sealed class EmbeddedVideoSubtitleSelectionDocument
{
    [JsonPropertyName("_0")]
    public VideoSubtitleTrackIdentityDocument Value { get; set; } = new();
}

internal sealed class VideoSubtitleTrackIdentityDocument
{
    public int TrackID { get; set; }
    public int? FfIndex { get; set; }
    public string Title { get; set; } = "";
    public string? Language { get; set; }
    public string? Codec { get; set; }
}

internal sealed class ExternalVideoSubtitleSelectionDocument
{
    public string Path { get; set; } = "";
}

internal sealed class RemoteLanguageVideoSubtitleSelectionDocument
{
    public string Language { get; set; } = "";
}

internal sealed class VideoAudioSelectionDocument
{
    [JsonPropertyName("off")]
    public EmptyVideoSelectionDocument? Off { get; set; }

    [JsonPropertyName("embedded")]
    public EmbeddedVideoAudioSelectionDocument? Embedded { get; set; }
}

internal sealed class EmbeddedVideoAudioSelectionDocument
{
    [JsonPropertyName("_0")]
    public VideoAudioTrackIdentityDocument Value { get; set; } = new();
}

internal sealed class VideoAudioTrackIdentityDocument
{
    public int TrackID { get; set; }
    public int? FfIndex { get; set; }
    public string Title { get; set; } = "";
    public string? Language { get; set; }
    public string? Codec { get; set; }
}

internal sealed class EmptyVideoSelectionDocument
{
}
