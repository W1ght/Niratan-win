using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Niratan.Models.Manga;

public sealed class MihonExtensionConfiguration
{
    public int SchemaVersion { get; set; } = 2;

    public List<MihonRepositoryConfiguration> Repositories { get; set; } = [];

    public List<MihonLibraryEntry> Library { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RepositoryUrl { get; set; }

    public string BridgeUrl { get; set; } = "http://127.0.0.1:48981";
    public string JavaExecutablePath { get; set; } = string.Empty;
    public string ServerJarPath { get; set; } = string.Empty;
}

public sealed class MihonLibraryEntry
{
    public string SourceId { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string SourceLang { get; set; } = string.Empty;
    public string SourceBaseUrl { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public MihonManga Manga { get; set; } = new();
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MihonRepositoryConfiguration
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string IndexUrl { get; set; } = string.Empty;
}

public sealed class MihonRepositoryRefreshFailure
{
    public string RepositoryId { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class MihonRepositoryRefreshResult
{
    public List<MihonExtensionSource> Sources { get; set; } = [];
    public List<MihonRepositoryRefreshFailure> Failures { get; set; } = [];
}

public sealed class MihonExtensionSource
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Lang { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public string PackageDisplayName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ApkFileName { get; set; } = string.Empty;
    public string ApkDownloadUrl { get; set; } = string.Empty;
    public string IconDownloadUrl { get; set; } = string.Empty;
    public string RepositoryId { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public bool IsNsfw { get; set; }
    public bool IsInstalled { get; set; }
    public int PackageSourceCount { get; set; }

    public string Label
    {
        get
        {
            var prefix = string.IsNullOrWhiteSpace(Lang) ? string.Empty : $"[{Lang}] ";
            var suffix = IsInstalled ? " ✓" : string.Empty;
            return $"{prefix}{Name}{suffix}";
        }
    }
}

public sealed class MihonInstalledExtension
{
    public string SourceId { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string Lang { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string IconDownloadUrl { get; set; } = string.Empty;
    public string ApkPath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public bool IsNsfw { get; set; }
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> Headers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string Label =>
        string.IsNullOrWhiteSpace(Lang)
            ? SourceName
            : $"[{Lang}] {SourceName}";
}

public sealed class MihonInstalledExtensionCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<MihonInstalledExtension> Extensions { get; set; } = [];
}

public sealed class MihonManga
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Artist { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public List<string> Genres { get; set; } = [];
    public int Status { get; set; }

    [JsonPropertyName("thumbnail_url")]
    public string? ThumbnailUrl { get; set; }
}

public sealed class MihonPagedManga
{
    [JsonPropertyName("mangas")]
    public List<MihonManga> MangaList { get; set; } = [];

    public bool HasNextPage { get; set; }
}

public sealed class MihonChapter
{
    public string Url { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("date_upload")]
    public long UploadDate { get; set; }

    [JsonPropertyName("chapter_number")]
    public float ChapterNumber { get; set; }

    public string? Scanlator { get; set; }
}

public sealed class MihonPage
{
    public int Index { get; set; }
    public string? Url { get; set; }
    public string ImageUrl { get; set; } = string.Empty;
}
