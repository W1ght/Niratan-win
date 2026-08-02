using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Niratan.Models.Video;

namespace Niratan.Services.Video;

internal abstract class VideoMetadataProviderBase : IVideoMetadataProvider
{
    protected static readonly TimeSpan MetadataTtl = TimeSpan.FromDays(30);
    protected readonly IVideoMetadataTransport Transport;

    protected VideoMetadataProviderBase(IVideoMetadataTransport transport)
    {
        Transport = transport;
    }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract VideoMetadataCapabilities Capabilities { get; }
    public abstract IReadOnlySet<VideoMetadataMediaKind> SupportedMediaKinds { get; }
    public abstract bool ArtworkEnabledByDefault { get; }
    public abstract string? AttributionUrl { get; }

    protected static JsonDocument ParseJson(VideoMetadataResponse response)
    {
        if (response.StatusCode is < 200 or >= 300)
            throw new HttpRequestException($"Metadata provider returned HTTP {response.StatusCode}.");
        return JsonDocument.Parse(response.Content, new JsonDocumentOptions { MaxDepth = 64 });
    }

    protected static int? YearFromDate(string? value) =>
        value?.Length >= 4 && int.TryParse(value.AsSpan(0, 4), out var year) ? year : null;

    protected static ImmutableArray<string> StringArray(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToImmutableArray()
            : [];

    protected static string? String(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    protected static int? Int(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    protected static double? Double(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : null;
}

internal sealed class TmdbVideoMetadataProvider : VideoMetadataProviderBase,
    IVideoMetadataSearchProvider,
    IVideoMetadataDetailsProvider,
    IVideoArtworkProvider
{
    private readonly IVideoMetadataCredentialStore _credentials;

    public TmdbVideoMetadataProvider(
        IVideoMetadataTransport transport,
        IVideoMetadataCredentialStore credentials)
        : base(transport)
    {
        _credentials = credentials;
    }

    public override string Id => "tmdb";
    public override string DisplayName => "TMDB";
    public override VideoMetadataCapabilities Capabilities =>
        VideoMetadataCapabilities.Search | VideoMetadataCapabilities.Details | VideoMetadataCapabilities.Artwork;
    public override IReadOnlySet<VideoMetadataMediaKind> SupportedMediaKinds { get; } =
        new HashSet<VideoMetadataMediaKind>
        {
            VideoMetadataMediaKind.Movie, VideoMetadataMediaKind.Series,
            VideoMetadataMediaKind.Episode, VideoMetadataMediaKind.Anime,
        };
    public override bool ArtworkEnabledByDefault => true;
    public override string AttributionUrl => "https://www.themoviedb.org/";

    public async Task<IReadOnlyList<VideoMetadataCandidate>> SearchAsync(
        VideoMetadataSearchQuery query,
        CancellationToken ct = default)
    {
        if (query.ExternalIds.TryGetValue("tmdb", out var explicitId))
            return [CreateExplicitCandidate(query, explicitId)];
        var token = await RequireTokenAsync(ct);
        var isMovie = query.MediaKind == VideoMetadataMediaKind.Movie;
        var endpoint = isMovie ? "movie" : "tv";
        var yearParameter = query.Year.HasValue
            ? $"&{(isMovie ? "year" : "first_air_date_year")}={query.Year.Value}"
            : string.Empty;
        var uri = new Uri(
            $"https://api.themoviedb.org/3/search/{endpoint}?query={Uri.EscapeDataString(query.Title)}" +
            $"&language={Uri.EscapeDataString(query.Language)}&region={Uri.EscapeDataString(query.Region)}{yearParameter}");
        var response = await Transport.SendAsync(new VideoMetadataRequest(
            Id, HttpMethod.Get, uri, Headers: AuthHeaders(token)), ct);
        using var json = ParseJson(response);
        if (!json.RootElement.TryGetProperty("results", out var results))
            return [];
        return results.EnumerateArray().Select(item =>
        {
            var id = item.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture);
            var title = String(item, isMovie ? "title" : "name") ?? string.Empty;
            var original = String(item, isMovie ? "original_title" : "original_name");
            var date = String(item, isMovie ? "release_date" : "first_air_date");
            return new VideoMetadataCandidate(
                Id,
                id,
                isMovie ? VideoMetadataMediaKind.Movie : query.MediaKind,
                title,
                original,
                YearFromDate(date),
                query.SeasonNumber,
                query.EpisodeNumber,
                query.AbsoluteEpisodeNumber,
                new[] { title, original }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToImmutableArray(),
                ImmutableDictionary<string, string>.Empty.Add("tmdb", id),
                $"https://www.themoviedb.org/{endpoint}/{id}");
        }).Where(candidate => candidate.Title.Length > 0).ToList();
    }

    public async Task<VideoMetadataDetails?> GetDetailsAsync(
        VideoMetadataCandidate identity,
        string language,
        string region,
        CancellationToken ct = default)
    {
        var token = await RequireTokenAsync(ct);
        var endpoint = identity.MediaKind == VideoMetadataMediaKind.Movie ? "movie" : "tv";
        var uri = new Uri(
            $"https://api.themoviedb.org/3/{endpoint}/{Uri.EscapeDataString(identity.ProviderItemId)}" +
            $"?language={Uri.EscapeDataString(language)}&append_to_response=external_ids,credits,keywords,content_ratings,recommendations");
        var response = await Transport.SendAsync(new VideoMetadataRequest(
            Id, HttpMethod.Get, uri, Headers: AuthHeaders(token)), ct);
        if (response.StatusCode == 404)
            return null;
        using var json = ParseJson(response);
        var root = json.RootElement;
        var title = String(root, endpoint == "movie" ? "title" : "name") ?? identity.Title;
        var original = String(root, endpoint == "movie" ? "original_title" : "original_name");
        var date = String(root, endpoint == "movie" ? "release_date" : "first_air_date");
        var genres = root.TryGetProperty("genres", out var genresElement)
            ? genresElement.EnumerateArray().Select(item => String(item, "name")).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToImmutableArray()
            : [];
        var people = ImmutableArray.CreateBuilder<VideoPersonCredit>();
        if (root.TryGetProperty("credits", out var credits))
        {
            if (credits.TryGetProperty("cast", out var cast))
            {
                foreach (var item in cast.EnumerateArray().Take(30))
                {
                    var name = String(item, "name");
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    var profilePath = String(item, "profile_path");
                    people.Add(new VideoPersonCredit(
                        Int(item, "id")?.ToString(CultureInfo.InvariantCulture) ?? name,
                        name,
                        String(item, "character"),
                        "Actor",
                        string.IsNullOrWhiteSpace(profilePath) ? null : "https://image.tmdb.org/t/p/h632" + profilePath));
                }
            }
            if (credits.TryGetProperty("crew", out var crew))
            {
                foreach (var item in crew.EnumerateArray()
                             .Where(item => String(item, "job") is "Director" or "Writer" or "Screenplay" or "Creator")
                             .Take(12))
                {
                    var name = String(item, "name");
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    var profilePath = String(item, "profile_path");
                    people.Add(new VideoPersonCredit(
                        Int(item, "id")?.ToString(CultureInfo.InvariantCulture) ?? name,
                        name,
                        String(item, "job"),
                        "Crew",
                        string.IsNullOrWhiteSpace(profilePath) ? null : "https://image.tmdb.org/t/p/h632" + profilePath));
                }
            }
        }
        var peopleSnapshot = people
            .DistinctBy(person => (person.ProviderPersonId, person.Role))
            .ToImmutableArray();
        var actors = peopleSnapshot.Where(person => person.Type == "Actor").Select(person => person.Name).ToImmutableArray();
        var studios = root.TryGetProperty("production_companies", out var companies)
            ? companies.EnumerateArray().Select(item => String(item, "name"))
                .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToImmutableArray()
            : [];
        var tags = ImmutableArray<string>.Empty;
        if (root.TryGetProperty("keywords", out var keywords))
        {
            var keywordProperty = endpoint == "movie" ? "keywords" : "results";
            if (keywords.TryGetProperty(keywordProperty, out var keywordItems))
            {
                tags = keywordItems.EnumerateArray().Select(item => String(item, "name"))
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
                    .Distinct(StringComparer.CurrentCultureIgnoreCase).ToImmutableArray();
            }
        }
        string? officialRating = null;
        if (root.TryGetProperty("content_ratings", out var ratings)
            && ratings.TryGetProperty("results", out var ratingItems))
        {
            var availableRatings = ratingItems.EnumerateArray()
                .Select(item => (Region: String(item, "iso_3166_1"), Rating: String(item, "rating")))
                .Where(item => !string.IsNullOrWhiteSpace(item.Rating))
                .ToList();
            officialRating = availableRatings.FirstOrDefault(item =>
                    string.Equals(item.Region, region, StringComparison.OrdinalIgnoreCase)).Rating
                ?? availableRatings.FirstOrDefault(item => item.Region == "JP").Rating
                ?? availableRatings.FirstOrDefault(item => item.Region == "US").Rating
                ?? availableRatings.FirstOrDefault().Rating;
        }
        var relatedItems = ImmutableArray<VideoRelatedItem>.Empty;
        if (root.TryGetProperty("recommendations", out var recommendations)
            && recommendations.TryGetProperty("results", out var recommendationItems))
        {
            relatedItems = recommendationItems.EnumerateArray().Take(20).Select(item =>
            {
                var relatedId = Int(item, "id")?.ToString(CultureInfo.InvariantCulture) ?? "";
                var relatedTitle = String(item, endpoint == "movie" ? "title" : "name") ?? "";
                var relatedOriginal = String(item, endpoint == "movie" ? "original_title" : "original_name");
                var posterPath = String(item, "poster_path");
                var backdropPath = String(item, "backdrop_path");
                return new VideoRelatedItem(
                    Id, relatedId, relatedTitle, relatedOriginal,
                    YearFromDate(String(item, endpoint == "movie" ? "release_date" : "first_air_date")),
                    string.IsNullOrWhiteSpace(posterPath) ? null : "https://image.tmdb.org/t/p/w500" + posterPath,
                    string.IsNullOrWhiteSpace(backdropPath) ? null : "https://image.tmdb.org/t/p/w780" + backdropPath,
                    $"https://www.themoviedb.org/{endpoint}/{relatedId}");
            }).Where(item => item.ProviderItemId.Length > 0 && item.Title.Length > 0).ToImmutableArray();
        }
        var ids = identity.ExternalIds.ToBuilder();
        ids["tmdb"] = identity.ProviderItemId;
        if (root.TryGetProperty("external_ids", out var external))
        {
            foreach (var pair in new[] { ("imdb", "imdb_id"), ("tvdb", "tvdb_id") })
            {
                var value = String(external, pair.Item2)
                            ?? (external.TryGetProperty(pair.Item2, out var numeric) && numeric.ValueKind == JsonValueKind.Number
                                ? numeric.GetRawText() : null);
                if (!string.IsNullOrWhiteSpace(value))
                    ids[pair.Item1] = value;
            }
        }
        var now = DateTimeOffset.UtcNow;
        return new VideoMetadataDetails(
            Id, identity.ProviderItemId, identity.MediaKind, title, original, null,
            String(root, "overview"), YearFromDate(date), identity.SeasonNumber,
            identity.EpisodeNumber, identity.AbsoluteEpisodeNumber,
            identity.Aliases.Add(title).Add(original ?? string.Empty).Where(value => value.Length > 0).Distinct().ToImmutableArray(),
            genres, actors, ids.ToImmutable(), identity.SourceUrl, now, now + MetadataTtl,
            String(root, "tagline"), officialRating, Double(root, "vote_average"),
            YearFromDate(String(root, endpoint == "movie" ? "release_date" : "last_air_date")),
            String(root, "status"), tags, studios, peopleSnapshot, relatedItems);
    }

    public async Task<IReadOnlyList<VideoArtworkCandidate>> GetArtworkAsync(
        VideoMetadataCandidate identity,
        CancellationToken ct = default)
    {
        var token = await RequireTokenAsync(ct);
        var endpoint = identity.MediaKind == VideoMetadataMediaKind.Movie ? "movie" : "tv";
        var uri = new Uri($"https://api.themoviedb.org/3/{endpoint}/{identity.ProviderItemId}/images");
        var response = await Transport.SendAsync(new VideoMetadataRequest(
            Id, HttpMethod.Get, uri, Headers: AuthHeaders(token)), ct);
        using var json = ParseJson(response);
        var result = new List<VideoArtworkCandidate>();
        AddArtwork(json.RootElement, "posters", "poster", result);
        AddArtwork(json.RootElement, "backdrops", "backdrop", result);
        AddArtwork(json.RootElement, "logos", "logo", result);
        return result;
    }

    private static void AddArtwork(
        JsonElement root,
        string property,
        string kind,
        ICollection<VideoArtworkCandidate> output)
    {
        if (!root.TryGetProperty(property, out var images))
            return;
        foreach (var image in images.EnumerateArray().Take(50))
        {
            var path = String(image, "file_path");
            if (string.IsNullOrWhiteSpace(path))
                continue;
            output.Add(new VideoArtworkCandidate(
                "tmdb",
                "https://image.tmdb.org/t/p/original" + path,
                kind,
                String(image, "iso_639_1"),
                Int(image, "width"),
                Int(image, "height"),
                "https://www.themoviedb.org/"));
        }
    }

    private async Task<string> RequireTokenAsync(CancellationToken ct) =>
        await _credentials.ReadAsync(Id, "token", ct)
        ?? throw new InvalidOperationException("TMDB v4 Read Token is not configured.");

    private static IReadOnlyDictionary<string, string> AuthHeaders(string token) =>
        new Dictionary<string, string> { ["Authorization"] = "Bearer " + token, ["Accept"] = "application/json" };

    private static VideoMetadataCandidate CreateExplicitCandidate(VideoMetadataSearchQuery query, string id) =>
        new("tmdb", id, query.MediaKind, query.Title, null, query.Year, query.SeasonNumber,
            query.EpisodeNumber, query.AbsoluteEpisodeNumber, [query.Title], query.ExternalIds,
            $"https://www.themoviedb.org/{(query.MediaKind == VideoMetadataMediaKind.Movie ? "movie" : "tv")}/{id}");
}

internal sealed class TvMazeVideoMetadataProvider : VideoMetadataProviderBase,
    IVideoMetadataSearchProvider,
    IVideoMetadataDetailsProvider,
    IVideoArtworkProvider
{
    public TvMazeVideoMetadataProvider(IVideoMetadataTransport transport) : base(transport) { }
    public override string Id => "tvmaze";
    public override string DisplayName => "TVmaze";
    public override VideoMetadataCapabilities Capabilities =>
        VideoMetadataCapabilities.Search | VideoMetadataCapabilities.Details | VideoMetadataCapabilities.Artwork | VideoMetadataCapabilities.EpisodeOrder;
    public override IReadOnlySet<VideoMetadataMediaKind> SupportedMediaKinds { get; } =
        new HashSet<VideoMetadataMediaKind> { VideoMetadataMediaKind.Series, VideoMetadataMediaKind.Episode };
    public override bool ArtworkEnabledByDefault => true;
    public override string AttributionUrl => "https://www.tvmaze.com/api";

    public async Task<IReadOnlyList<VideoMetadataCandidate>> SearchAsync(VideoMetadataSearchQuery query, CancellationToken ct = default)
    {
        var uri = new Uri($"https://api.tvmaze.com/search/shows?q={Uri.EscapeDataString(query.Title)}");
        var response = await Transport.SendAsync(new VideoMetadataRequest(Id, HttpMethod.Get, uri), ct);
        using var json = ParseJson(response);
        return json.RootElement.EnumerateArray().Select(result => result.GetProperty("show")).Select(show =>
        {
            var id = show.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture);
            var title = String(show, "name") ?? string.Empty;
            var externals = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
            externals[Id] = id;
            if (show.TryGetProperty("externals", out var ids))
            {
                foreach (var pair in new[] { ("imdb", "imdb"), ("tvdb", "thetvdb") })
                {
                    var value = String(ids, pair.Item2) ?? (ids.TryGetProperty(pair.Item2, out var number) && number.ValueKind == JsonValueKind.Number ? number.GetRawText() : null);
                    if (!string.IsNullOrWhiteSpace(value))
                        externals[pair.Item1] = value;
                }
            }
            return new VideoMetadataCandidate(
                Id, id, query.MediaKind, title, null, YearFromDate(String(show, "premiered")),
                query.SeasonNumber, query.EpisodeNumber, query.AbsoluteEpisodeNumber,
                [title], externals.ToImmutable(), String(show, "url"));
        }).Where(candidate => candidate.Title.Length > 0).ToList();
    }

    public async Task<VideoMetadataDetails?> GetDetailsAsync(VideoMetadataCandidate identity, string language, string region, CancellationToken ct = default)
    {
        var response = await Transport.SendAsync(new VideoMetadataRequest(
            Id, HttpMethod.Get, new Uri($"https://api.tvmaze.com/shows/{identity.ProviderItemId}?embed[]=episodes&embed[]=cast")), ct);
        if (response.StatusCode == 404)
            return null;
        using var json = ParseJson(response);
        var root = json.RootElement;
        var title = String(root, "name") ?? identity.Title;
        var people = ImmutableArray.CreateBuilder<VideoPersonCredit>();
        if (root.TryGetProperty("_embedded", out var embedded)
            && embedded.TryGetProperty("cast", out var cast))
        {
            foreach (var credit in cast.EnumerateArray().Take(30))
            {
                if (!credit.TryGetProperty("person", out var person))
                    continue;
                var name = String(person, "name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                string? imageUrl = null;
                if (person.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object)
                    imageUrl = String(image, "original") ?? String(image, "medium");
                var role = credit.TryGetProperty("character", out var character)
                    ? String(character, "name")
                    : null;
                people.Add(new VideoPersonCredit(
                    Int(person, "id")?.ToString(CultureInfo.InvariantCulture) ?? name,
                    name, role, "Actor", imageUrl));
            }
        }
        var peopleSnapshot = people.ToImmutable();
        var actors = peopleSnapshot.Select(person => person.Name).ToImmutableArray();
        var studios = ImmutableArray.CreateBuilder<string>();
        foreach (var property in new[] { "network", "webChannel" })
        {
            if (root.TryGetProperty(property, out var network) && network.ValueKind == JsonValueKind.Object
                && String(network, "name") is { Length: > 0 } studio)
                studios.Add(studio);
        }
        double? communityRating = null;
        if (root.TryGetProperty("rating", out var rating) && rating.ValueKind == JsonValueKind.Object)
            communityRating = Double(rating, "average");
        var now = DateTimeOffset.UtcNow;
        return new VideoMetadataDetails(
            Id, identity.ProviderItemId, identity.MediaKind, title, null, null,
            StripHtml(String(root, "summary")), YearFromDate(String(root, "premiered")),
            identity.SeasonNumber, identity.EpisodeNumber, identity.AbsoluteEpisodeNumber,
            identity.Aliases.Add(title).Distinct().ToImmutableArray(), StringArray(root, "genres"), actors,
            identity.ExternalIds, String(root, "url"), now, now + MetadataTtl,
            OfficialRating: null, CommunityRating: communityRating,
            EndYear: YearFromDate(String(root, "ended")), Status: String(root, "status"),
            Studios: studios.Distinct(StringComparer.CurrentCultureIgnoreCase).ToImmutableArray(),
            People: peopleSnapshot);
    }

    public async Task<IReadOnlyList<VideoArtworkCandidate>> GetArtworkAsync(VideoMetadataCandidate identity, CancellationToken ct = default)
    {
        var response = await Transport.SendAsync(new VideoMetadataRequest(
            Id, HttpMethod.Get, new Uri($"https://api.tvmaze.com/shows/{identity.ProviderItemId}")), ct);
        using var json = ParseJson(response);
        if (!json.RootElement.TryGetProperty("image", out var image))
            return [];
        return new[] { ("medium", "poster"), ("original", "poster") }
            .Select(pair => (pair.Item2, Url: String(image, pair.Item1)))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Url))
            .Select(pair => new VideoArtworkCandidate(Id, pair.Url!, "poster", null, null, null, String(json.RootElement, "url")))
            .ToList();
    }

    private static string? StripHtml(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? value
            : System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", string.Empty).Trim();
}

internal sealed class AniListVideoMetadataProvider : VideoMetadataProviderBase,
    IVideoMetadataSearchProvider,
    IVideoMetadataDetailsProvider
{
    public AniListVideoMetadataProvider(IVideoMetadataTransport transport) : base(transport) { }
    public override string Id => "anilist";
    public override string DisplayName => "AniList";
    public override VideoMetadataCapabilities Capabilities => VideoMetadataCapabilities.Search | VideoMetadataCapabilities.Details;
    public override IReadOnlySet<VideoMetadataMediaKind> SupportedMediaKinds { get; } =
        new HashSet<VideoMetadataMediaKind> { VideoMetadataMediaKind.Anime, VideoMetadataMediaKind.Series, VideoMetadataMediaKind.Episode };
    public override bool ArtworkEnabledByDefault => false;
    public override string AttributionUrl => "https://anilist.co/";

    public async Task<IReadOnlyList<VideoMetadataCandidate>> SearchAsync(VideoMetadataSearchQuery query, CancellationToken ct = default)
    {
        const string graph = "query($search:String,$id:Int,$idMal:Int){Page(perPage:20){media(search:$search,id:$id,idMal:$idMal,type:ANIME){id idMal title{romaji english native} synonyms seasonYear siteUrl}}}";
        int? id = query.ExternalIds.TryGetValue("anilist", out var own) && int.TryParse(own, out var ownId) ? ownId : null;
        int? mal = query.ExternalIds.TryGetValue("mal", out var malValue) && int.TryParse(malValue, out var malId) ? malId : null;
        var body = JsonSerializer.SerializeToUtf8Bytes(new { query = graph, variables = new { search = id.HasValue || mal.HasValue ? null : query.Title, id, idMal = mal } });
        var response = await Transport.SendAsync(new VideoMetadataRequest(
            Id, HttpMethod.Post, new Uri("https://graphql.anilist.co"), body, "application/json", IsIdempotent: true), ct);
        using var json = ParseJson(response);
        var media = json.RootElement.GetProperty("data").GetProperty("Page").GetProperty("media");
        return media.EnumerateArray().Select(item => ToCandidate(item, query)).ToList();
    }

    public async Task<VideoMetadataDetails?> GetDetailsAsync(VideoMetadataCandidate identity, string language, string region, CancellationToken ct = default)
    {
        const string graph = "query($id:Int){Media(id:$id,type:ANIME){id idMal title{romaji english native} synonyms description seasonYear endDate{year} status averageScore genres tags{name} studios{nodes{name isAnimationStudio}} characters(sort:[ROLE,RELEVANCE],perPage:25){edges{role node{name{full native}} voiceActors(language:JAPANESE,sort:[RELEVANCE]){id name{full native} image{large} siteUrl}}} recommendations(sort:RATING_DESC,perPage:8){nodes{mediaRecommendation{id title{romaji english native} seasonYear coverImage{large} bannerImage siteUrl}}} siteUrl externalLinks{site id url}}}";
        var body = JsonSerializer.SerializeToUtf8Bytes(new { query = graph, variables = new { id = int.Parse(identity.ProviderItemId, CultureInfo.InvariantCulture) } });
        var response = await Transport.SendAsync(new VideoMetadataRequest(
            Id, HttpMethod.Post, new Uri("https://graphql.anilist.co"), body, "application/json", IsIdempotent: true), ct);
        using var json = ParseJson(response);
        if (json.RootElement.GetProperty("data").GetProperty("Media") is not { ValueKind: JsonValueKind.Object } media)
            return null;
        var candidate = ToCandidate(media, new VideoMetadataSearchQuery(
            identity.Title, identity.MediaKind, identity.Year, identity.SeasonNumber,
            identity.EpisodeNumber, identity.AbsoluteEpisodeNumber, language, region, identity.ExternalIds));
        var ids = candidate.ExternalIds.ToBuilder();
        if (media.TryGetProperty("externalLinks", out var links))
        {
            foreach (var link in links.EnumerateArray())
            {
                var site = String(link, "site")?.ToLowerInvariant();
                var externalId = link.TryGetProperty("id", out var idElement) ? idElement.ToString() : null;
                if (!string.IsNullOrWhiteSpace(site) && !string.IsNullOrWhiteSpace(externalId))
                    ids[site] = externalId;
            }
        }
        var tags = media.TryGetProperty("tags", out var tagItems)
            ? tagItems.EnumerateArray().Select(item => String(item, "name"))
                .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
                .Distinct(StringComparer.CurrentCultureIgnoreCase).ToImmutableArray()
            : [];
        var studios = media.TryGetProperty("studios", out var studioConnection)
                      && studioConnection.TryGetProperty("nodes", out var studioNodes)
            ? studioNodes.EnumerateArray().Select(item => String(item, "name"))
                .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
                .Distinct(StringComparer.CurrentCultureIgnoreCase).ToImmutableArray()
            : [];
        var people = ImmutableArray.CreateBuilder<VideoPersonCredit>();
        if (media.TryGetProperty("characters", out var characters)
            && characters.TryGetProperty("edges", out var characterEdges))
        {
            foreach (var edge in characterEdges.EnumerateArray())
            {
                var characterName = edge.TryGetProperty("node", out var character)
                                    && character.TryGetProperty("name", out var characterNames)
                    ? String(characterNames, "native") ?? String(characterNames, "full")
                    : null;
                if (!edge.TryGetProperty("voiceActors", out var voiceActors))
                    continue;
                foreach (var actor in voiceActors.EnumerateArray().Take(1))
                {
                    var actorName = actor.TryGetProperty("name", out var actorNames)
                        ? String(actorNames, "native") ?? String(actorNames, "full")
                        : null;
                    if (string.IsNullOrWhiteSpace(actorName))
                        continue;
                    var imageUrl = actor.TryGetProperty("image", out var actorImage)
                        ? String(actorImage, "large")
                        : null;
                    people.Add(new VideoPersonCredit(
                        Int(actor, "id")?.ToString(CultureInfo.InvariantCulture) ?? actorName,
                        actorName,
                        characterName,
                        "Actor",
                        imageUrl));
                }
            }
        }
        var peopleSnapshot = people
            .DistinctBy(person => (person.ProviderPersonId, person.Role))
            .ToImmutableArray();
        var relatedItems = ImmutableArray.CreateBuilder<VideoRelatedItem>();
        if (media.TryGetProperty("recommendations", out var recommendations)
            && recommendations.TryGetProperty("nodes", out var recommendationNodes))
        {
            foreach (var node in recommendationNodes.EnumerateArray())
            {
                if (!node.TryGetProperty("mediaRecommendation", out var related)
                    || related.ValueKind != JsonValueKind.Object)
                    continue;
                var relatedId = Int(related, "id")?.ToString(CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(relatedId)
                    || !related.TryGetProperty("title", out var relatedTitles))
                    continue;
                var native = String(relatedTitles, "native");
                var romaji = String(relatedTitles, "romaji");
                var english = String(relatedTitles, "english");
                relatedItems.Add(new VideoRelatedItem(
                    Id,
                    relatedId,
                    native ?? romaji ?? english ?? relatedId,
                    native,
                    Int(related, "seasonYear"),
                    related.TryGetProperty("coverImage", out var coverImage)
                        ? String(coverImage, "large")
                        : null,
                    String(related, "bannerImage"),
                    String(related, "siteUrl")));
            }
        }
        var endYear = media.TryGetProperty("endDate", out var endDate)
            ? Int(endDate, "year")
            : null;
        var averageScore = Int(media, "averageScore");
        var now = DateTimeOffset.UtcNow;
        return new VideoMetadataDetails(
            Id, identity.ProviderItemId, VideoMetadataMediaKind.Anime, candidate.Title,
            candidate.OriginalTitle, null, StripHtml(String(media, "description")), candidate.Year,
            identity.SeasonNumber, identity.EpisodeNumber, identity.AbsoluteEpisodeNumber,
            candidate.Aliases, StringArray(media, "genres"),
            peopleSnapshot.Select(person => person.Name).Distinct().ToImmutableArray(),
            ids.ToImmutable(), candidate.SourceUrl,
            now, now + MetadataTtl,
            CommunityRating: averageScore.HasValue ? averageScore.Value / 10d : null,
            EndYear: endYear,
            Status: String(media, "status"),
            Tags: tags,
            Studios: studios,
            People: peopleSnapshot,
            RelatedItems: relatedItems.ToImmutable());
    }

    private static VideoMetadataCandidate ToCandidate(JsonElement item, VideoMetadataSearchQuery query)
    {
        var titleObject = item.GetProperty("title");
        var native = String(titleObject, "native");
        var romaji = String(titleObject, "romaji");
        var english = String(titleObject, "english");
        var title = native ?? romaji ?? english ?? query.Title;
        var id = item.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture);
        var ids = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        ids["anilist"] = id;
        if (item.TryGetProperty("idMal", out var mal) && mal.ValueKind == JsonValueKind.Number)
            ids["mal"] = mal.GetRawText();
        var aliases = StringArray(item, "synonyms")
            .AddRange(new[] { native, romaji, english }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!))
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToImmutableArray();
        return new VideoMetadataCandidate(
            "anilist", id, VideoMetadataMediaKind.Anime, title, native,
            Int(item, "seasonYear"), query.SeasonNumber, query.EpisodeNumber, query.AbsoluteEpisodeNumber,
            aliases, ids.ToImmutable(), String(item, "siteUrl"));
    }

    private static string? StripHtml(string? value) =>
        string.IsNullOrWhiteSpace(value) ? value : System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", string.Empty).Trim();
}

internal sealed class AniDbTitleIndexProvider : VideoMetadataProviderBase, IVideoMetadataSearchProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImmutableArray<AniDbTitle> _titles = [];
    private DateTimeOffset _loadedAt;

    public AniDbTitleIndexProvider(IVideoMetadataTransport transport) : base(transport) { }
    public override string Id => "anidb";
    public override string DisplayName => "AniDB";
    public override VideoMetadataCapabilities Capabilities => VideoMetadataCapabilities.Search | VideoMetadataCapabilities.TitleIndex;
    public override IReadOnlySet<VideoMetadataMediaKind> SupportedMediaKinds { get; } =
        new HashSet<VideoMetadataMediaKind> { VideoMetadataMediaKind.Anime, VideoMetadataMediaKind.Series };
    public override bool ArtworkEnabledByDefault => false;
    public override string AttributionUrl => "https://anidb.net/";

    public async Task<IReadOnlyList<VideoMetadataCandidate>> SearchAsync(VideoMetadataSearchQuery query, CancellationToken ct = default)
    {
        await EnsureIndexAsync(ct);
        var normalized = Normalize(query.Title);
        return _titles
            .Where(title => title.Normalized.Contains(normalized, StringComparison.Ordinal)
                            || normalized.Contains(title.Normalized, StringComparison.Ordinal))
            .GroupBy(title => title.AnimeId)
            .Take(20)
            .Select(group => new VideoMetadataCandidate(
                Id, group.Key, VideoMetadataMediaKind.Anime, group.First().Value,
                group.FirstOrDefault(title => title.Language == "ja")?.Value,
                query.Year, query.SeasonNumber, query.EpisodeNumber, query.AbsoluteEpisodeNumber,
                group.Select(title => title.Value).Distinct(StringComparer.CurrentCultureIgnoreCase).ToImmutableArray(),
                ImmutableDictionary<string, string>.Empty.Add("anidb", group.Key),
                $"https://anidb.net/anime/{group.Key}"))
            .ToList();
    }

    private async Task EnsureIndexAsync(CancellationToken ct)
    {
        if (_titles.Length > 0 && DateTimeOffset.UtcNow - _loadedAt < TimeSpan.FromDays(7))
            return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_titles.Length > 0 && DateTimeOffset.UtcNow - _loadedAt < TimeSpan.FromDays(7))
                return;
            var response = await Transport.SendAsync(new VideoMetadataRequest(
                Id, HttpMethod.Get, new Uri("https://anidb.net/api/anime-titles.xml.gz"), MaxResponseBytes: 32L * 1024 * 1024), ct);
            if (response.StatusCode is < 200 or >= 300)
                throw new HttpRequestException($"AniDB title index returned HTTP {response.StatusCode}.");
            await using var compressed = new MemoryStream(response.Content, writable: false);
            await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            var settings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 128L * 1024 * 1024,
            };
            using var reader = XmlReader.Create(gzip, settings);
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
            _titles = document.Descendants("anime").SelectMany(anime =>
            {
                var id = anime.Attribute("aid")?.Value ?? string.Empty;
                return anime.Elements("title").Select(title => new AniDbTitle(
                    id,
                    title.Attribute(XNamespace.Xml + "lang")?.Value ?? string.Empty,
                    title.Value.Trim(),
                    Normalize(title.Value)));
            }).Where(title => title.AnimeId.Length > 0 && title.Normalized.Length > 0).ToImmutableArray();
            _loadedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Normalize(string value) => string.Concat(
        value.Normalize(NormalizationForm.FormKC).ToUpperInvariant().Where(char.IsLetterOrDigit));
    private sealed record AniDbTitle(string AnimeId, string Language, string Value, string Normalized);
}

internal sealed class BangumiVideoMetadataProvider : VideoMetadataProviderBase,
    IVideoMetadataSearchProvider,
    IVideoMetadataDetailsProvider
{
    private readonly IVideoMetadataCredentialStore _credentials;
    private static readonly IReadOnlyDictionary<string, string> BaseHeaders = new Dictionary<string, string>
    {
        ["Accept"] = "application/json",
        ["User-Agent"] = "Niratan/0.8.2 (https://github.com/wight554/Hoshi-Reader)",
    };

    public BangumiVideoMetadataProvider(IVideoMetadataTransport transport, IVideoMetadataCredentialStore credentials)
        : base(transport) => _credentials = credentials;
    public override string Id => "bangumi";
    public override string DisplayName => "Bangumi";
    public override VideoMetadataCapabilities Capabilities => VideoMetadataCapabilities.Search | VideoMetadataCapabilities.Details;
    public override IReadOnlySet<VideoMetadataMediaKind> SupportedMediaKinds { get; } =
        new HashSet<VideoMetadataMediaKind> { VideoMetadataMediaKind.Anime, VideoMetadataMediaKind.Series, VideoMetadataMediaKind.Movie };
    public override bool ArtworkEnabledByDefault => false;
    public override string AttributionUrl => "https://bgm.tv/";

    public async Task<IReadOnlyList<VideoMetadataCandidate>> SearchAsync(VideoMetadataSearchQuery query, CancellationToken ct = default)
    {
        if (query.ExternalIds.TryGetValue("bangumi", out var explicitId))
            return [new VideoMetadataCandidate(Id, explicitId, query.MediaKind, query.Title, null, query.Year,
                query.SeasonNumber, query.EpisodeNumber, query.AbsoluteEpisodeNumber, [query.Title], query.ExternalIds,
                $"https://bgm.tv/subject/{explicitId}")];
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            keyword = query.Title,
            filter = new { type = new[] { 2, 6 } },
        });
        var response = await Transport.SendAsync(new VideoMetadataRequest(
            Id, HttpMethod.Post, new Uri("https://api.bgm.tv/v0/search/subjects?limit=20&offset=0"),
            body, "application/json", await HeadersAsync(ct), IsIdempotent: true), ct);
        using var json = ParseJson(response);
        if (!json.RootElement.TryGetProperty("data", out var data))
            return [];
        return data.EnumerateArray().Select(item =>
        {
            var id = item.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture);
            var native = String(item, "name");
            var translated = String(item, "name_cn");
            var title = native ?? translated ?? query.Title;
            return new VideoMetadataCandidate(
                Id, id, query.MediaKind, title, native, YearFromDate(String(item, "date")),
                query.SeasonNumber, query.EpisodeNumber, query.AbsoluteEpisodeNumber,
                new[] { native, translated }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToImmutableArray(),
                ImmutableDictionary<string, string>.Empty.Add("bangumi", id), $"https://bgm.tv/subject/{id}");
        }).ToList();
    }

    public async Task<VideoMetadataDetails?> GetDetailsAsync(VideoMetadataCandidate identity, string language, string region, CancellationToken ct = default)
    {
        var response = await Transport.SendAsync(new VideoMetadataRequest(
            Id, HttpMethod.Get, new Uri($"https://api.bgm.tv/v0/subjects/{identity.ProviderItemId}"),
            Headers: await HeadersAsync(ct)), ct);
        if (response.StatusCode == 404)
            return null;
        using var json = ParseJson(response);
        var root = json.RootElement;
        var native = String(root, "name");
        var translated = String(root, "name_cn");
        var now = DateTimeOffset.UtcNow;
        return new VideoMetadataDetails(
            Id, identity.ProviderItemId, identity.MediaKind, native ?? translated ?? identity.Title,
            native, translated, String(root, "summary"), YearFromDate(String(root, "date")),
            identity.SeasonNumber, identity.EpisodeNumber, identity.AbsoluteEpisodeNumber,
            identity.Aliases.Add(native ?? string.Empty).Add(translated ?? string.Empty).Where(value => value.Length > 0).Distinct().ToImmutableArray(),
            [], [], identity.ExternalIds.SetItem("bangumi", identity.ProviderItemId), identity.SourceUrl,
            now, now + MetadataTtl);
    }

    private async Task<IReadOnlyDictionary<string, string>> HeadersAsync(CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(BaseHeaders, StringComparer.OrdinalIgnoreCase);
        var token = await _credentials.ReadAsync(Id, "token", ct);
        if (!string.IsNullOrWhiteSpace(token))
            headers["Authorization"] = "Bearer " + token;
        return headers;
    }
}

internal sealed class TvDbLicenseGatedProvider : VideoMetadataProviderBase,
    IVideoMetadataSearchProvider,
    IVideoMetadataDetailsProvider,
    IVideoArtworkProvider
{
    public TvDbLicenseGatedProvider(IVideoMetadataTransport transport) : base(transport) { }
    public override string Id => "tvdb";
    public override string DisplayName => "TheTVDB (license required)";
    public override VideoMetadataCapabilities Capabilities => VideoMetadataCapabilities.Search | VideoMetadataCapabilities.Details | VideoMetadataCapabilities.EpisodeOrder;
    public override IReadOnlySet<VideoMetadataMediaKind> SupportedMediaKinds { get; } =
        new HashSet<VideoMetadataMediaKind> { VideoMetadataMediaKind.Series, VideoMetadataMediaKind.Episode };
    public override bool ArtworkEnabledByDefault => false;
    public override string AttributionUrl => "https://thetvdb.com/api-information";
    public Task<IReadOnlyList<VideoMetadataCandidate>> SearchAsync(VideoMetadataSearchQuery query, CancellationToken ct = default) =>
        throw new NotSupportedException("TheTVDB network access is disabled until Niratan has an approved project license.");
    public Task<VideoMetadataDetails?> GetDetailsAsync(VideoMetadataCandidate identity, string language, string region, CancellationToken ct = default) =>
        throw new NotSupportedException("TheTVDB network access is disabled until Niratan has an approved project license.");
    public Task<IReadOnlyList<VideoArtworkCandidate>> GetArtworkAsync(VideoMetadataCandidate identity, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VideoArtworkCandidate>>([]);
}
