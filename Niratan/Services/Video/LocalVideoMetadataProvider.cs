using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Niratan.Models.Video;

namespace Niratan.Services.Video;

public enum LocalVideoMetadataScope
{
    Container,
    Season,
    Episode,
    Movie,
}

public sealed record LocalVideoArtwork(
    string Path,
    string Kind,
    LocalVideoMetadataScope Scope);

public sealed record LocalVideoMetadataValues(
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
    string? Tagline,
    string? OfficialRating,
    double? CommunityRating,
    string? Status,
    ImmutableArray<string> Tags,
    ImmutableArray<string> Studios,
    ImmutableArray<string> Directors)
{
    public static LocalVideoMetadataValues Empty { get; } = new(
        null, null, null, null, null, null, null, [], [],
        ImmutableDictionary<string, string>.Empty,
        null, null, null, null, [], [], []);

    internal bool HasValues =>
        !string.IsNullOrWhiteSpace(Title)
        || !string.IsNullOrWhiteSpace(OriginalTitle)
        || !string.IsNullOrWhiteSpace(Overview)
        || Year.HasValue
        || SeasonNumber.HasValue
        || EpisodeNumber.HasValue
        || AbsoluteEpisodeNumber.HasValue
        || !Genres.IsDefaultOrEmpty
        || !Actors.IsDefaultOrEmpty
        || ExternalIds.Count > 0
        || !string.IsNullOrWhiteSpace(Tagline)
        || !string.IsNullOrWhiteSpace(OfficialRating)
        || CommunityRating.HasValue
        || !string.IsNullOrWhiteSpace(Status)
        || !Tags.IsDefaultOrEmpty
        || !Studios.IsDefaultOrEmpty
        || !Directors.IsDefaultOrEmpty;
}

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
    ImmutableArray<string> SourceFiles,
    LocalVideoMetadataValues? ContainerMetadata = null,
    LocalVideoMetadataValues? SeasonMetadata = null,
    LocalVideoMetadataValues? EpisodeMetadata = null,
    LocalVideoMetadataValues? MovieMetadata = null,
    ImmutableArray<LocalVideoArtwork> Artwork = default)
{
    public static LocalVideoMetadata Empty { get; } = new(
        null, null, null, null, null, null, null, [], [],
        ImmutableDictionary<string, string>.Empty,
        [], []);

    public bool HasScopedMetadata =>
        ContainerMetadata != null || SeasonMetadata != null || EpisodeMetadata != null || MovieMetadata != null;

    public LocalVideoMetadataValues? ForScope(LocalVideoMetadataScope scope)
    {
        var scoped = scope switch
        {
            LocalVideoMetadataScope.Container => ContainerMetadata,
            LocalVideoMetadataScope.Season => SeasonMetadata,
            LocalVideoMetadataScope.Episode => EpisodeMetadata,
            LocalVideoMetadataScope.Movie => MovieMetadata ?? EpisodeMetadata,
            _ => null,
        };
        if (scoped != null)
            return scoped;
        if (HasScopedMetadata)
            return null;
        var values = ToValues();
        return values.HasValues ? values : null;
    }

    public ImmutableArray<LocalVideoArtwork> ArtworkForScope(LocalVideoMetadataScope scope)
    {
        if (!Artwork.IsDefault)
            return Artwork.Where(item => item.Scope == scope).ToImmutableArray();
        return ArtworkPaths
            .Select(path => new LocalVideoArtwork(path, InferLegacyArtworkKind(path), scope))
            .ToImmutableArray();
    }

    public string? PreferredAssetArtworkPath(bool isMovie)
    {
        var scopes = isMovie
            ? new[] { LocalVideoMetadataScope.Movie, LocalVideoMetadataScope.Container, LocalVideoMetadataScope.Episode }
            : new[] { LocalVideoMetadataScope.Episode };
        var candidates = Artwork.IsDefault
            ? ArtworkPaths.Select(path => new LocalVideoArtwork(
                path, InferLegacyArtworkKind(path), LocalVideoMetadataScope.Episode))
            : Artwork;
        return candidates
            .Where(item => scopes.Contains(item.Scope)
                           && (isMovie || item.Kind.Equals("poster", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.Kind.Equals("poster", StringComparison.OrdinalIgnoreCase) ? 0
                : item.Kind.Equals("thumb", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .Select(item => item.Path)
            .FirstOrDefault();
    }

    private LocalVideoMetadataValues ToValues() => new(
        Title, OriginalTitle, Overview, Year, SeasonNumber, EpisodeNumber,
        AbsoluteEpisodeNumber, Genres, Actors, ExternalIds,
        null, null, null, null, [], [], []);

    private static string InferLegacyArtworkKind(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Contains("fanart", StringComparison.OrdinalIgnoreCase)
               || name.Contains("backdrop", StringComparison.OrdinalIgnoreCase)
            ? "backdrop"
            : "poster";
    }
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
        "^season\\s*\\d{1,3}-(poster|fanart|backdrop|banner|thumb|landscape)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SeasonDirectoryName = new(
        "^(season\\s*\\d{1,3}|s\\d{1,3}|specials?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly ImmutableDictionary<string, string> GenericArtworkKinds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["poster"] = "poster", ["folder"] = "poster", ["cover"] = "poster",
            ["default"] = "poster", ["movie"] = "poster", ["show"] = "poster",
            ["fanart"] = "backdrop", ["backdrop"] = "backdrop", ["background"] = "backdrop",
            ["art"] = "backdrop", ["logo"] = "logo", ["clearlogo"] = "logo",
            ["thumb"] = "thumb", ["landscape"] = "thumb", ["banner"] = "banner",
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

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
        var mediaDirectory = Path.GetDirectoryName(fullMediaPath)
            ?? throw new InvalidDataException("Media path does not have a parent directory.");
        var seriesDirectory = FindSeriesDirectory(mediaDirectory, fullRoot);
        var seasonDirectory = FindSeasonDirectory(mediaDirectory, seriesDirectory, fullRoot);

        var seriesNfo = ExistingPath(Path.Combine(seriesDirectory, "tvshow.nfo"));
        var movieNfo = ExistingPath(Path.Combine(mediaDirectory, "movie.nfo"));
        var seasonNfo = seasonDirectory == null
            ? null
            : ExistingPath(Path.Combine(seasonDirectory, "season.nfo"));
        var episodeNfo = ExistingPath(Path.ChangeExtension(fullMediaPath, ".nfo"));

        var container = await ReadOptionalNfoAsync(seriesNfo, fullRoot, ct);
        var movie = await ReadOptionalNfoAsync(movieNfo, fullRoot, ct);
        var season = await ReadOptionalNfoAsync(seasonNfo, fullRoot, ct);
        var episode = await ReadOptionalNfoAsync(episodeNfo, fullRoot, ct);
        var effective = new[] { container, movie, season, episode }
            .Where(item => item != null)
            .Aggregate(LocalVideoMetadataValues.Empty, (current, overlay) => Merge(current, overlay!));
        var artwork = DiscoverArtwork(
            seriesDirectory, seasonDirectory, mediaDirectory, fullMediaPath, fullRoot);
        var sourceFiles = new[] { seriesNfo, movieNfo, seasonNfo, episodeNfo }
            .Where(path => path != null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return new LocalVideoMetadata(
            effective.Title,
            effective.OriginalTitle,
            effective.Overview,
            effective.Year,
            effective.SeasonNumber,
            effective.EpisodeNumber,
            effective.AbsoluteEpisodeNumber,
            effective.Genres,
            effective.Actors,
            effective.ExternalIds,
            artwork.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
            sourceFiles,
            container,
            season,
            episode,
            movie,
            artwork);
    }

    private static async Task<LocalVideoMetadataValues?> ReadOptionalNfoAsync(
        string? path,
        string root,
        CancellationToken ct)
    {
        if (path == null)
            return null;
        ct.ThrowIfCancellationRequested();
        EnsureWithinRoot(path, root);
        var info = new FileInfo(path);
        if (info.Length > MaxNfoBytes)
            throw new InvalidDataException($"NFO exceeds the {MaxNfoBytes} byte limit.");
        return await ReadNfoAsync(path, ct);
    }

    private static async Task<LocalVideoMetadataValues> ReadNfoAsync(string path, CancellationToken ct)
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
        static double? DecimalNumber(XElement rootElement, string name) =>
            double.TryParse(Value(rootElement, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        static ImmutableArray<string> Values(XElement rootElement, string name) =>
            rootElement.Elements()
                .Where(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(element => element.Value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToImmutableArray();

        var externalIds = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uniqueId in root.Descendants().Where(element =>
                     element.Name.LocalName.Equals("uniqueid", StringComparison.OrdinalIgnoreCase)))
        {
            var provider = uniqueId.Attribute("type")?.Value?.Trim().ToLowerInvariant();
            var id = uniqueId.Value.Trim();
            if (!string.IsNullOrWhiteSpace(provider) && id.Length > 0)
                externalIds[provider] = id;
        }
        foreach (var provider in new[] { "tmdb", "tvdb", "imdb", "anidb", "anilist", "mal", "bangumi" })
        {
            var id = Value(root, provider + "id");
            if (!string.IsNullOrWhiteSpace(id))
                externalIds[provider] = id;
        }

        var actors = root.Elements()
            .Where(element => element.Name.LocalName.Equals("actor", StringComparison.OrdinalIgnoreCase))
            .Select(element => Value(element, "name"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToImmutableArray();
        return new LocalVideoMetadataValues(
            Value(root, "title"),
            Value(root, "originaltitle"),
            Value(root, "plot") ?? Value(root, "outline"),
            Number(root, "year"),
            Number(root, "season") ?? Number(root, "seasonnumber"),
            Number(root, "episode"),
            Number(root, "absolute_number") ?? Number(root, "absoluteepisode"),
            Values(root, "genre"),
            actors,
            externalIds.ToImmutable(),
            Value(root, "tagline"),
            Value(root, "mpaa") ?? Value(root, "customrating") ?? Value(root, "certification"),
            DecimalNumber(root, "rating"),
            Value(root, "status"),
            Values(root, "tag"),
            Values(root, "studio"),
            Values(root, "director"));
    }

    private static LocalVideoMetadataValues Merge(
        LocalVideoMetadataValues current,
        LocalVideoMetadataValues overlay)
    {
        var ids = current.ExternalIds.ToBuilder();
        foreach (var pair in overlay.ExternalIds)
            ids[pair.Key] = pair.Value;
        return new LocalVideoMetadataValues(
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
            overlay.Tagline ?? current.Tagline,
            overlay.OfficialRating ?? current.OfficialRating,
            overlay.CommunityRating ?? current.CommunityRating,
            overlay.Status ?? current.Status,
            current.Tags.Concat(overlay.Tags).Distinct(StringComparer.CurrentCultureIgnoreCase).ToImmutableArray(),
            current.Studios.Concat(overlay.Studios).Distinct(StringComparer.CurrentCultureIgnoreCase).ToImmutableArray(),
            current.Directors.Concat(overlay.Directors).Distinct(StringComparer.CurrentCultureIgnoreCase).ToImmutableArray());
    }

    private static ImmutableArray<LocalVideoArtwork> DiscoverArtwork(
        string seriesDirectory,
        string? seasonDirectory,
        string mediaDirectory,
        string mediaPath,
        string root)
    {
        var mediaName = Path.GetFileNameWithoutExtension(mediaPath);
        var results = new List<LocalVideoArtwork>();
        foreach (var directory in new[] { seriesDirectory, seasonDirectory, mediaDirectory }
                     .Where(path => path != null)
                     .Select(path => path!)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var candidate in Directory.EnumerateFiles(directory)
                         .Where(path => ArtworkExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
            {
                var path = Path.GetFullPath(candidate);
                EnsureWithinRoot(path, root);
                var name = Path.GetFileNameWithoutExtension(path);
                var classified = ClassifyArtwork(
                    name, mediaName, directory, seriesDirectory, seasonDirectory, mediaDirectory);
                if (classified != null)
                    results.Add(new LocalVideoArtwork(path, classified.Value.Kind, classified.Value.Scope));
            }
        }
        return results
            .DistinctBy(
                item => $"{item.Scope}\0{item.Kind}\0{item.Path}",
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Scope)
            .ThenBy(item => ArtworkPriority(item.Kind))
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static (string Kind, LocalVideoMetadataScope Scope)? ClassifyArtwork(
        string name,
        string mediaName,
        string directory,
        string seriesDirectory,
        string? seasonDirectory,
        string mediaDirectory)
    {
        if (directory.Equals(mediaDirectory, StringComparison.OrdinalIgnoreCase))
        {
            if (name.Equals(mediaName, StringComparison.OrdinalIgnoreCase))
                return ("thumb", LocalVideoMetadataScope.Episode);
            if (name.StartsWith(mediaName + "-", StringComparison.OrdinalIgnoreCase)
                && GenericArtworkKinds.TryGetValue(name[(mediaName.Length + 1)..], out var mediaKind))
                return (mediaKind, LocalVideoMetadataScope.Episode);
        }

        var seasonMatch = SeasonArtworkName.Match(name);
        if (seasonMatch.Success)
            return (NormalizeArtworkKind(seasonMatch.Groups[1].Value), LocalVideoMetadataScope.Season);
        if (!GenericArtworkKinds.TryGetValue(name, out var kind))
            return null;
        if (seasonDirectory != null
            && directory.Equals(seasonDirectory, StringComparison.OrdinalIgnoreCase)
            && !directory.Equals(seriesDirectory, StringComparison.OrdinalIgnoreCase))
            return (kind, LocalVideoMetadataScope.Season);
        return (kind, LocalVideoMetadataScope.Container);
    }

    private static string NormalizeArtworkKind(string value) => value.ToLowerInvariant() switch
    {
        "fanart" or "backdrop" => "backdrop",
        "landscape" => "thumb",
        _ => value.ToLowerInvariant(),
    };

    private static int ArtworkPriority(string kind) => kind switch
    {
        "poster" => 0,
        "backdrop" => 1,
        "thumb" => 2,
        "logo" => 3,
        "banner" => 4,
        _ => 5,
    };

    private static string FindSeriesDirectory(string mediaDirectory, string root)
    {
        for (var current = mediaDirectory;
             current != null && IsWithinRoot(current, root);
             current = Path.GetDirectoryName(current))
        {
            if (File.Exists(Path.Combine(current, "tvshow.nfo")))
                return current;
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                break;
        }
        var season = FindSeasonDirectory(mediaDirectory, null, root);
        return season != null && !string.Equals(season, root, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(season) ?? season
            : mediaDirectory;
    }

    private static string? FindSeasonDirectory(
        string mediaDirectory,
        string? seriesDirectory,
        string root)
    {
        for (var current = mediaDirectory;
             current != null && IsWithinRoot(current, root);
             current = Path.GetDirectoryName(current))
        {
            if (seriesDirectory != null
                && string.Equals(current, seriesDirectory, StringComparison.OrdinalIgnoreCase))
                break;
            if (File.Exists(Path.Combine(current, "season.nfo"))
                || SeasonDirectoryName.IsMatch(new DirectoryInfo(current).Name))
                return current;
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                break;
        }
        return null;
    }

    private static string? ExistingPath(string path) =>
        File.Exists(path) ? Path.GetFullPath(path) : null;

    private static bool IsWithinRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               || string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureWithinRoot(string path, string root)
    {
        if (!IsWithinRoot(path, root))
            throw new InvalidDataException("Video sidecar path escapes its library source.");
    }
}
