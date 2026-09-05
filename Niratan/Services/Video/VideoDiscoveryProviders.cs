using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Video;

namespace Niratan.Services.Video;

internal static class VideoDiscoveryJson
{
    public static JsonDocument Parse(VideoMetadataResponse response) =>
        response.StatusCode is >= 200 and < 300
            ? JsonDocument.Parse(response.Content, new JsonDocumentOptions { MaxDepth = 64 })
            : throw new HttpRequestException($"Discovery provider returned HTTP {response.StatusCode}.");

    public static string? String(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? Int(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    public static double? Double(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetDouble(out var number)
            ? number
            : null;

    public static int? Year(string? value) =>
        value?.Length >= 4 && int.TryParse(value.AsSpan(0, 4), out var year) ? year : null;

    public static ImmutableArray<string> Titles(params string?[] values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Trim())
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .ToImmutableArray();
}

internal sealed class TmdbVideoDiscoveryProvider : IVideoDiscoveryProvider
{
    private readonly IVideoMetadataTransport _transport;
    private readonly IVideoMetadataCredentialStore _credentials;

    public TmdbVideoDiscoveryProvider(
        IVideoMetadataTransport transport,
        IVideoMetadataCredentialStore credentials)
    {
        _transport = transport;
        _credentials = credentials;
    }

    public string Id => "tmdb";
    public string DisplayName => "TMDB";

    public IReadOnlyList<VideoDiscoveryFeed> Feeds { get; } =
    [
        new("tmdb", "discover-movie", "Explore movies", VideoDiscoveryFeedKind.Explore,
            [VideoMetadataMediaKind.Movie], true, true),
        new("tmdb", "discover-tv", "Explore series", VideoDiscoveryFeedKind.Explore,
            [VideoMetadataMediaKind.Series, VideoMetadataMediaKind.Anime], true, true),
        new("tmdb", "trending-movie", "Trending movies", VideoDiscoveryFeedKind.Recommendation,
            [VideoMetadataMediaKind.Movie]),
        new("tmdb", "trending-tv", "Trending series", VideoDiscoveryFeedKind.Recommendation,
            [VideoMetadataMediaKind.Series, VideoMetadataMediaKind.Anime]),
        new("tmdb", "popular-movie", "Popular movies", VideoDiscoveryFeedKind.Recommendation,
            [VideoMetadataMediaKind.Movie]),
        new("tmdb", "popular-tv", "Popular series", VideoDiscoveryFeedKind.Recommendation,
            [VideoMetadataMediaKind.Series, VideoMetadataMediaKind.Anime]),
        new("tmdb", "top-rated-movie", "Top rated movies", VideoDiscoveryFeedKind.Recommendation,
            [VideoMetadataMediaKind.Movie]),
        new("tmdb", "top-rated-tv", "Top rated series", VideoDiscoveryFeedKind.Recommendation,
            [VideoMetadataMediaKind.Series, VideoMetadataMediaKind.Anime]),
        new("tmdb", "now-playing", "Now playing", VideoDiscoveryFeedKind.Recommendation,
            [VideoMetadataMediaKind.Movie]),
        new("tmdb", "upcoming", "Upcoming movies", VideoDiscoveryFeedKind.Recommendation,
            [VideoMetadataMediaKind.Movie]),
        new("tmdb", "on-air", "On the air", VideoDiscoveryFeedKind.Recommendation,
            [VideoMetadataMediaKind.Series]),
    ];

    public async Task<VideoDiscoveryPage> GetPageAsync(
        VideoDiscoveryRequest request,
        CancellationToken ct = default)
    {
        var token = await _credentials.ReadAsync(Id, "token", ct)
                    ?? throw new InvalidOperationException("TMDB access token is not configured.");
        var feed = Feeds.FirstOrDefault(item => item.Id.Equals(request.FeedId, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException("Unknown TMDB discovery feed.", nameof(request));
        var uri = BuildUri(feed, request);
        var response = await _transport.SendAsync(new VideoMetadataRequest(
            Id,
            HttpMethod.Get,
            TmdbCredentialAuth.Apply(uri, token),
            Headers: TmdbCredentialAuth.Headers(token)), ct);
        using var json = VideoDiscoveryJson.Parse(response);
        var root = json.RootElement;
        var items = root.TryGetProperty("results", out var results)
            ? results.EnumerateArray().Select(item => MapItem(item, request.MediaKind)).Where(item => item != null).Cast<VideoDiscoveryItem>().ToImmutableArray()
            : ImmutableArray<VideoDiscoveryItem>.Empty;
        return new VideoDiscoveryPage(
            Id,
            request.FeedId,
            request.Page,
            VideoDiscoveryJson.Int(root, "total_pages"),
            items);
    }

    private static Uri BuildUri(VideoDiscoveryFeed feed, VideoDiscoveryRequest request)
    {
        var language = Uri.EscapeDataString(request.Language);
        var region = Uri.EscapeDataString(request.Region);
        var page = Math.Max(1, request.Page);
        string path;
        var query = $"language={language}&region={region}&page={page}";
        switch (feed.Id)
        {
            case "discover-movie":
                path = "discover/movie";
                query += "&include_adult=false&include_video=false";
                query += $"&sort_by={Uri.EscapeDataString(request.SortBy ?? "popularity.desc")}";
                if (request.Year is int movieYear)
                    query += $"&primary_release_year={movieYear}";
                if (!string.IsNullOrWhiteSpace(request.GenreId))
                    query += $"&with_genres={Uri.EscapeDataString(request.GenreId)}";
                break;
            case "discover-tv":
                path = "discover/tv";
                query += "&include_adult=false";
                query += $"&sort_by={Uri.EscapeDataString(request.SortBy ?? "popularity.desc")}";
                if (request.Year is int tvYear)
                    query += $"&first_air_date_year={tvYear}";
                if (!string.IsNullOrWhiteSpace(request.GenreId))
                    query += $"&with_genres={Uri.EscapeDataString(request.GenreId)}";
                if (request.MediaKind == VideoMetadataMediaKind.Anime && string.IsNullOrWhiteSpace(request.GenreId))
                    query += "&with_genres=16";
                break;
            case "trending-movie":
                path = "trending/movie/" + (request.TimeWindow is "day" ? "day" : "week");
                query = $"language={language}";
                break;
            case "trending-tv":
                path = "trending/tv/" + (request.TimeWindow is "day" ? "day" : "week");
                query = $"language={language}";
                break;
            case "popular-movie": path = "movie/popular"; break;
            case "popular-tv": path = "tv/popular"; break;
            case "top-rated-movie": path = "movie/top_rated"; break;
            case "top-rated-tv": path = "tv/top_rated"; break;
            case "now-playing": path = "movie/now_playing"; break;
            case "upcoming": path = "movie/upcoming"; break;
            case "on-air": path = "tv/on_the_air"; break;
            default: throw new ArgumentException("Unknown TMDB discovery feed.", nameof(feed));
        }
        return new Uri($"https://api.themoviedb.org/3/{path}?{query}");
    }

    private VideoDiscoveryItem? MapItem(JsonElement item, VideoMetadataMediaKind requestedKind)
    {
        var id = VideoDiscoveryJson.Int(item, "id");
        if (id is null)
            return null;
        var isMovie = item.TryGetProperty("title", out _);
        var title = VideoDiscoveryJson.String(item, isMovie ? "title" : "name");
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var original = VideoDiscoveryJson.String(item, isMovie ? "original_title" : "original_name");
        var date = VideoDiscoveryJson.String(item, isMovie ? "release_date" : "first_air_date");
        var mediaKind = isMovie
            ? VideoMetadataMediaKind.Movie
            : requestedKind == VideoMetadataMediaKind.Anime
                ? VideoMetadataMediaKind.Anime
                : VideoMetadataMediaKind.Series;
        var idText = id.Value.ToString(CultureInfo.InvariantCulture);
        var poster = VideoDiscoveryJson.String(item, "poster_path");
        var backdrop = VideoDiscoveryJson.String(item, "backdrop_path");
        return new VideoDiscoveryItem(
            new VideoMetadataCandidate(
                Id,
                idText,
                mediaKind,
                title,
                original,
                VideoDiscoveryJson.Year(date),
                null,
                null,
                null,
                VideoDiscoveryJson.Titles(title, original),
                ImmutableDictionary<string, string>.Empty.Add("tmdb", idText),
                $"https://www.themoviedb.org/{(isMovie ? "movie" : "tv")}/{idText}"),
            VideoDiscoveryJson.String(item, "overview"),
            VideoDiscoveryJson.Double(item, "vote_average"),
            VideoDiscoveryJson.Int(item, "vote_count"),
            string.IsNullOrWhiteSpace(poster) ? null : "https://image.tmdb.org/t/p/w500" + poster,
            string.IsNullOrWhiteSpace(backdrop) ? null : "https://image.tmdb.org/t/p/w780" + backdrop);
    }
}

internal sealed class AniListVideoDiscoveryProvider : IVideoDiscoveryProvider
{
    private readonly IVideoMetadataTransport _transport;

    public AniListVideoDiscoveryProvider(IVideoMetadataTransport transport) => _transport = transport;

    public string Id => "anilist";
    public string DisplayName => "AniList";
    public IReadOnlyList<VideoDiscoveryFeed> Feeds { get; } =
    [
        new("anilist", "trending", "Trending anime", VideoDiscoveryFeedKind.Explore,
            [VideoMetadataMediaKind.Anime], true, false),
        new("anilist", "popular", "Popular anime", VideoDiscoveryFeedKind.Recommendation,
            [VideoMetadataMediaKind.Anime], true, false),
        new("anilist", "seasonal", "This season", VideoDiscoveryFeedKind.Recommendation,
            [VideoMetadataMediaKind.Anime], true, false),
    ];

    public async Task<VideoDiscoveryPage> GetPageAsync(
        VideoDiscoveryRequest request,
        CancellationToken ct = default)
    {
        var sort = request.SortBy?.ToLowerInvariant() switch
        {
            "vote_average.desc" => "SCORE_DESC",
            "release_date.desc" or "primary_release_date.desc" or "first_air_date.desc" =>
                "START_DATE_DESC",
            _ when request.FeedId.Equals("popular", StringComparison.OrdinalIgnoreCase)
                   || request.FeedId.Equals("seasonal", StringComparison.OrdinalIgnoreCase) =>
                "POPULARITY_DESC",
            _ => "TRENDING_DESC",
        };
        var genre = MapGenre(request.GenreId);
        var today = DateTime.UtcNow;
        var seasonName = today.Month switch
        {
            >= 2 and <= 4 => "SPRING",
            >= 5 and <= 7 => "SUMMER",
            >= 8 and <= 10 => "FALL",
            _ => "WINTER",
        };
        var seasonalFilter = request.FeedId.Equals("seasonal", StringComparison.OrdinalIgnoreCase)
            ? ",season:" + seasonName
              + (request.Year is null
                  ? ",seasonYear:" + today.Year.ToString(CultureInfo.InvariantCulture)
                  : string.Empty)
            : string.Empty;
        var yearFilter = request.Year is int year
            ? ",seasonYear:" + year.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        var genreFilter = genre is null
            ? string.Empty
            : ",genre:\"" + genre + "\"";
        var graph = "query($page:Int,$perPage:Int){Page(page:$page,perPage:$perPage){pageInfo{total currentPage lastPage hasNextPage}" +
            "media(type:ANIME,sort:[" + sort + "]" + seasonalFilter + yearFilter + genreFilter + "){id idMal title{romaji english native}synonyms description seasonYear averageScore genres coverImage{extraLarge large}bannerImage siteUrl}}}";
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            query = graph,
            variables = new { page = Math.Max(1, request.Page), perPage = 24 },
        });
        var response = await _transport.SendAsync(new VideoMetadataRequest(
            Id,
            HttpMethod.Post,
            new Uri("https://graphql.anilist.co"),
            body,
            "application/json"), ct);
        using var json = VideoDiscoveryJson.Parse(response);
        var page = json.RootElement.GetProperty("data").GetProperty("Page");
        var pageInfo = page.GetProperty("pageInfo");
        var items = page.GetProperty("media").EnumerateArray()
            .Select(MapItem)
            .Where(item => item != null)
            .Cast<VideoDiscoveryItem>()
            .ToImmutableArray();
        return new VideoDiscoveryPage(
            Id,
            request.FeedId,
            request.Page,
            VideoDiscoveryJson.Int(pageInfo, "lastPage"),
            items);
    }

    private static string? MapGenre(string? genreId)
    {
        var value = genreId?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value == "16")
            return null;
        return value.ToLowerInvariant() switch
        {
            "28" => "Action",
            "12" => "Adventure",
            "35" => "Comedy",
            "18" => "Drama",
            "14" => "Fantasy",
            "27" => "Horror",
            "10402" => "Music",
            "9648" => "Mystery",
            "10749" => "Romance",
            "878" => "Sci-Fi",
            "53" => "Thriller",
            "action" => "Action",
            "adventure" => "Adventure",
            "comedy" => "Comedy",
            "drama" => "Drama",
            "fantasy" => "Fantasy",
            "horror" => "Horror",
            "music" => "Music",
            "mystery" => "Mystery",
            "romance" => "Romance",
            "sci-fi" => "Sci-Fi",
            "thriller" => "Thriller",
            _ => throw new ArgumentException(
                "AniList does not recognize the requested genre filter.",
                nameof(genreId)),
        };
    }

    private static VideoDiscoveryItem? MapItem(JsonElement item)
    {
        var id = VideoDiscoveryJson.Int(item, "id");
        var hasTitles = item.TryGetProperty("title", out var titleObject);
        var native = hasTitles ? VideoDiscoveryJson.String(titleObject, "native") : null;
        var romaji = hasTitles ? VideoDiscoveryJson.String(titleObject, "romaji") : null;
        var english = hasTitles ? VideoDiscoveryJson.String(titleObject, "english") : null;
        var title = romaji ?? english ?? native;
        if (id is null || string.IsNullOrWhiteSpace(title))
            return null;
        var image = item.TryGetProperty("coverImage", out var cover)
            ? VideoDiscoveryJson.String(cover, "extraLarge") ?? VideoDiscoveryJson.String(cover, "large")
            : null;
        var idText = id.Value.ToString(CultureInfo.InvariantCulture);
        var mal = VideoDiscoveryJson.Int(item, "idMal");
        var ids = ImmutableDictionary<string, string>.Empty.Add("anilist", idText);
        if (mal is int malId)
            ids = ids.Add("mal", malId.ToString(CultureInfo.InvariantCulture));
        return new VideoDiscoveryItem(
            new VideoMetadataCandidate(
                "anilist",
                idText,
                VideoMetadataMediaKind.Anime,
                title,
                native,
                VideoDiscoveryJson.Int(item, "seasonYear"),
                null,
                null,
                null,
                VideoDiscoveryJson.Titles(
                    title,
                    native,
                    english),
                ids,
                VideoDiscoveryJson.String(item, "siteUrl") ?? $"https://anilist.co/anime/{idText}"),
            VideoDiscoveryJson.String(item, "description"),
            VideoDiscoveryJson.Double(item, "averageScore") is double score ? score / 10 : null,
            null,
            image,
            VideoDiscoveryJson.String(item, "bannerImage"));
    }
}
