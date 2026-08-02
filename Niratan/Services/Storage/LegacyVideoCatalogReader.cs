using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;
using Niratan.Models.Video;
using Niratan.Services.Novels;

namespace Niratan.Services.Storage;

internal sealed record LegacyVideoCatalogReadResult(
    VideoLibraryCatalogDocument Document,
    string? Sha256,
    bool Exists);

internal sealed class LegacyVideoCatalogReader
{
    private static readonly Guid LooseFilesSourceId =
        Guid.Parse("00000000-0000-0000-0000-00000000A11C");

    private readonly INiratanJsonFileStore _json;

    public LegacyVideoCatalogReader(INiratanJsonFileStore json)
    {
        _json = json;
    }

    public async Task<LegacyVideoCatalogReadResult> ReadAsync(
        string path,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            return new LegacyVideoCatalogReadResult(new VideoLibraryCatalogDocument(), null, false);

        byte[] bytes;
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read | FileShare.Delete,
                         4096,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length > 256L * 1024 * 1024)
                throw new InvalidDataException("Legacy video catalog exceeds the 256 MiB migration limit.");
            bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, ct);
        }

        try
        {
            using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            ValidateWireShape(parsed.RootElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Legacy video catalog is invalid and was preserved.", ex);
        }

        var result = await _json.ReadAsync<VideoLibraryCatalogDocument>(path, ct);
        if (result.Status != NovelJsonReadStatus.Success || result.Value == null)
            throw new InvalidDataException($"Legacy video catalog could not be decoded: {result.Error}");
        Normalize(result.Value);
        ValidateSemantics(result.Value);
        return new LegacyVideoCatalogReadResult(
            result.Value,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            true);
    }

    private static void ValidateWireShape(JsonElement root)
    {
        RequireObject(root, "catalog");
        AssertOnlyProperties(root, "catalog",
            "schemaVersion", "sources", "items", "remoteItems", "itemMetadataByPath", "collections");
        if (root.TryGetProperty("schemaVersion", out var schemaVersion)
            && (schemaVersion.ValueKind != JsonValueKind.Number
                || !schemaVersion.TryGetInt32(out var version)
                || version != 0))
        {
            throw new InvalidDataException("The legacy catalog was written by an unsupported future version.");
        }

        ValidateArray(root, "sources", element =>
        {
            AssertOnlyProperties(element, "source",
                "id", "name", "path", "bookmark", "lastScannedAt", "lastError", "createdAt");
        });
        ValidateArray(root, "items", element =>
        {
            AssertOnlyProperties(element, "item",
                "path", "sourceID", "title", "parentFolder", "fileSize", "modifiedAt",
                "lastSeenAt", "mediaIdentity", "importedAt");
            if (element.TryGetProperty("mediaIdentity", out var identity))
            {
                AssertOnlyProperties(identity, "mediaIdentity", "localFile", "remote");
                ValidateOptionalObject(identity, "localFile", value =>
                    AssertOnlyProperties(value, "localFile identity", "path"));
                ValidateOptionalObject(identity, "remote", value =>
                    AssertOnlyProperties(value, "remote identity", "providerID", "remoteID"));
            }
        });
        ValidateArray(root, "remoteItems", element =>
        {
            AssertOnlyProperties(element, "remote item",
                "identity", "subtitleLanguage", "hasResolvedSubtitleMetadata", "addedAt", "lastResolvedAt");
            if (element.TryGetProperty("identity", out var identity))
            {
                AssertOnlyProperties(identity, "remote item identity",
                    "providerID", "remoteID", "originalURL", "canonicalURL", "title",
                    "thumbnailURL", "duration");
            }
        });

        if (root.TryGetProperty("itemMetadataByPath", out var metadata))
        {
            RequireObject(metadata, "itemMetadataByPath");
            foreach (var property in metadata.EnumerateObject())
            {
                AssertOnlyProperties(property.Value, "item metadata",
                    "displayTitle", "isFavorite", "tags", "collectionIDs", "boundSubtitlePath",
                    "posterPath", "profileID");
            }
        }

        ValidateArray(root, "collections", element =>
        {
            AssertOnlyProperties(element, "collection",
                "id", "name", "kind", "itemPaths", "smartRules");
            ValidateArray(element, "smartRules", rule =>
                AssertOnlyProperties(rule, "smart rule", "id", "field", "match", "value"));
        });
    }

    private static void ValidateArray(JsonElement parent, string name, Action<JsonElement> validate)
    {
        if (!parent.TryGetProperty(name, out var value))
            return;
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Legacy catalog {name} must be an array.");
        foreach (var element in value.EnumerateArray())
        {
            RequireObject(element, name + " item");
            validate(element);
        }
    }

    private static void ValidateOptionalObject(
        JsonElement parent,
        string name,
        Action<JsonElement> validate)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return;
        RequireObject(value, name);
        validate(value);
    }

    private static void RequireObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Legacy catalog {name} must be an object.");
    }

    private static void AssertOnlyProperties(
        JsonElement element,
        string name,
        params string[] allowed)
    {
        RequireObject(element, name);
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowedSet.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"Legacy catalog {name} contains unsupported field '{property.Name}'.");
            }
        }
    }

    private static void Normalize(VideoLibraryCatalogDocument document)
    {
        document.Sources ??= [];
        document.Items ??= [];
        document.RemoteItems ??= [];
        document.ItemMetadataByPath ??= [];
        document.Collections ??= [];
        foreach (var metadata in document.ItemMetadataByPath.Values)
        {
            metadata.Tags ??= [];
            metadata.CollectionIDs ??= [];
        }
        foreach (var collection in document.Collections)
        {
            collection.ItemPaths ??= [];
            collection.SmartRules ??= [];
        }
    }

    private static void ValidateSemantics(VideoLibraryCatalogDocument document)
    {
        if (document.Sources.GroupBy(source => source.Id).Any(group => group.Count() > 1))
            throw new InvalidDataException("Legacy video catalog contains duplicate source IDs.");
        if (document.Collections.GroupBy(collection => collection.Id).Any(group => group.Count() > 1))
            throw new InvalidDataException("Legacy video catalog contains duplicate collection IDs.");

        var sourceIds = document.Sources.Select(source => source.Id).ToHashSet();
        var collectionIds = document.Collections.Select(collection => collection.Id).ToHashSet();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in document.Sources)
        {
            if (source.Id == Guid.Empty || string.IsNullOrWhiteSpace(source.Path))
                throw new InvalidDataException("Legacy video catalog contains an invalid source.");
        }
        foreach (var item in document.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Path))
                throw new InvalidDataException("Legacy video catalog contains an item without a path.");
            if (item.SourceID != Guid.Empty
                && item.SourceID != LooseFilesSourceId
                && !sourceIds.Contains(item.SourceID))
            {
                throw new InvalidDataException("Legacy video catalog item references an unknown source.");
            }
            var identity = NormalizeIdentity(item.Path);
            if (!identities.Add(identity))
                throw new InvalidDataException("Legacy video catalog contains duplicate media identities.");
        }
        foreach (var item in document.RemoteItems)
        {
            if (string.IsNullOrWhiteSpace(item.Identity.ProviderID)
                || string.IsNullOrWhiteSpace(item.Identity.RemoteID))
            {
                throw new InvalidDataException("Legacy remote item has an invalid identity.");
            }
            if (!identities.Add($"remote://{item.Identity.ProviderID}/{item.Identity.RemoteID}"))
                throw new InvalidDataException("Legacy video catalog contains duplicate media identities.");
        }
        foreach (var metadata in document.ItemMetadataByPath.Values)
        {
            if (metadata.CollectionIDs.Any(id => !collectionIds.Contains(id)))
                throw new InvalidDataException("Legacy item metadata references an unknown collection.");
        }
    }

    internal static string NormalizeIdentity(string identity) =>
        RemoteVideoIdentity.IsPersistenceKey(identity)
            ? identity.Trim()
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(identity));
}
