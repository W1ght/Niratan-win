using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Niratan.Models.Video;

namespace Niratan.Services.Video;

public sealed record LocalVideoMetadata(
    string? Title,
    string? OriginalTitle,
    string? Overview,
    int? Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    int? AbsoluteEpisodeNumber,
    ImmutableArray<string> Genres,
    ImmutableArray<string> Actors,
    ImmutableDictionary<string, string> ExternalIds,
    ImmutableArray<string> ArtworkPaths,
    ImmutableArray<string> SourceFiles)
{
    public static LocalVideoMetadata Empty { get; } = new(
        null, null, null, null, null, null, null, [], [],
        ImmutableDictionary<string, string>.Empty,
        [], []);
}

public interface ILocalVideoMetadataProvider
{
    Task<LocalVideoMetadata> ReadAsync(
        string mediaPath,
        string sourceRoot,
        CancellationToken ct = default);
}

internal sealed class LocalVideoMetadataProvider : ILocalVideoMetadataProvider, IVideoMetadataProvider
{
    private const long MaxNfoBytes = 4L * 1024 * 1024;
    private static readonly string[] ArtworkExtensions = [".jpg", ".jpeg", ".png", ".webp"];
    private static readonly Regex SeasonArtworkName = new(
        "^season\\d{1,3}-(poster|fanart)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public string Id => "local";
    public string DisplayName => "Local NFO and artwork";
    public VideoMetadataCapabilities Capabilities => VideoMetadataCapabilities.Details | VideoMetadataCapabilities.Artwork;
    public IReadOnlySet<VideoMetadataMediaKind> SupportedMediaKinds { get; } =
        new HashSet<VideoMetadataMediaKind>(Enum.GetValues<VideoMetadataMediaKind>());
    public bool ArtworkEnabledByDefault => true;
    public string? AttributionUrl => null;

    public async Task<LocalVideoMetadata> ReadAsync(
        string mediaPath,
        string sourceRoot,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);

        var fullMediaPath = Path.GetFullPath(mediaPath);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        EnsureWithinRoot(fullMediaPath, fullRoot);
        var directory = Path.GetDirectoryName(fullMediaPath)
            ?? throw new InvalidDataException("Media path does not have a parent directory.");

        var nfoCandidates = new[]
            {
                Path.Combine(directory, "tvshow.nfo"),
                Path.Combine(directory, "movie.nfo"),
                Path.Combine(directory, "season.nfo"),
                Path.ChangeExtension(fullMediaPath, ".nfo"),
            }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToList();

        var metadata = LocalVideoMetadata.Empty;
        foreach (var nfoPath in nfoCandidates)
        {
            ct.ThrowIfCancellationRequested();
            EnsureWithinRoot(nfoPath, fullRoot);
            var info = new FileInfo(nfoPath);
            if (info.Length > MaxNfoBytes)
                throw new InvalidDataException($"NFO exceeds the {MaxNfoBytes} byte limit.");
            var parsed = await ReadNfoAsync(nfoPath, ct);
            metadata = Merge(metadata, parsed);
        }

        var artwork = DiscoverArtwork(directory, fullMediaPath, fullRoot);
        return metadata with
        {
            ArtworkPaths = metadata.ArtworkPaths
                .Concat(artwork)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray(),
            SourceFiles = nfoCandidates.ToImmutableArray(),
        };
    }

    private static async Task<LocalVideoMetadata> ReadNfoAsync(string path, CancellationToken ct)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxNfoBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = XmlReader.Create(stream, settings);
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
        var root = document.Root ?? throw new InvalidDataException("NFO is missing its root element.");

        static string? Value(XElement rootElement, string name) =>
            rootElement.Elements().FirstOrDefault(element =>
                element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() is { Length: > 0 } value
                ? value
                : null;
        static int? Number(XElement rootElement, string name) =>
            int.TryParse(Value(rootElement, name), out var value) ? value : null;

        var externalIds = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uniqueId in root.Descendants().Where(element =>
                     element.Name.LocalName.Equals("uniqueid", StringComparison.OrdinalIgnoreCase)))
        {
            var provider = uniqueId.Attribute("type")?.Value?.Trim().ToLowerInvariant();
            var id = uniqueId.Value.Trim();
            if (!string.IsNullOrWhiteSpace(provider) && id.Length > 0)
                externalIds[provider] = id;
        }

        var genres = root.Elements()
            .Where(element => element.Name.LocalName.Equals("genre", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToImmutableArray();
        var actors = root.Elements()
            .Where(element => element.Name.LocalName.Equals("actor", StringComparison.OrdinalIgnoreCase))
            .Select(element => Value(element, "name"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToImmutableArray();

        return new LocalVideoMetadata(
            Value(root, "title"),
            Value(root, "originaltitle"),
            Value(root, "plot") ?? Value(root, "outline"),
            Number(root, "year"),
            Number(root, "season"),
            Number(root, "episode"),
            Number(root, "absolute_number") ?? Number(root, "absoluteepisode"),
            genres,
            actors,
            externalIds.ToImmutable(),
            [],
            [path]);
    }

    private static LocalVideoMetadata Merge(LocalVideoMetadata current, LocalVideoMetadata overlay)
    {
        var ids = current.ExternalIds.ToBuilder();
        foreach (var pair in overlay.ExternalIds)
            ids[pair.Key] = pair.Value;
        return new LocalVideoMetadata(
            overlay.Title ?? current.Title,
            overlay.OriginalTitle ?? current.OriginalTitle,
            overlay.Overview ?? current.Overview,
            overlay.Year ?? current.Year,
            overlay.SeasonNumber ?? current.SeasonNumber,
            overlay.EpisodeNumber ?? current.EpisodeNumber,
            overlay.AbsoluteEpisodeNumber ?? current.AbsoluteEpisodeNumber,
            current.Genres.Concat(overlay.Genres).Distinct(StringComparer.CurrentCultureIgnoreCase).ToImmutableArray(),
            current.Actors.Concat(overlay.Actors).Distinct(StringComparer.CurrentCultureIgnoreCase).ToImmutableArray(),
            ids.ToImmutable(),
            current.ArtworkPaths.Concat(overlay.ArtworkPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
            current.SourceFiles.Concat(overlay.SourceFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray());
    }

    private static ImmutableArray<string> DiscoverArtwork(
        string directory,
        string mediaPath,
        string root)
    {
        var mediaName = Path.GetFileNameWithoutExtension(mediaPath);
        var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            mediaName, "poster", "folder", "cover", "fanart", "backdrop",
        };
        return Directory.EnumerateFiles(directory)
            .Where(path => ArtworkExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                return knownNames.Contains(name) || SeasonArtworkName.IsMatch(name);
            })
            .Select(path => Path.GetFullPath(path))
            .Select(path =>
            {
                EnsureWithinRoot(path, root);
                return path;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => ArtworkPriority(Path.GetFileNameWithoutExtension(path), mediaName))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static int ArtworkPriority(string name, string mediaName) =>
        name.Equals(mediaName, StringComparison.OrdinalIgnoreCase) ? 0
        : name.Equals("poster", StringComparison.OrdinalIgnoreCase) ? 1
        : name.Equals("folder", StringComparison.OrdinalIgnoreCase) ? 2
        : name.Equals("cover", StringComparison.OrdinalIgnoreCase) ? 3
        : name.Equals("fanart", StringComparison.OrdinalIgnoreCase) ? 4
        : name.Equals("backdrop", StringComparison.OrdinalIgnoreCase) ? 5
        : 6;

    private static void EnsureWithinRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Video sidecar path escapes its library source.");
        }
    }
}
