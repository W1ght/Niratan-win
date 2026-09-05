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
using System.Text.RegularExpressions;
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
        parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    protected static double? Double(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : null;
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
        var searchTitle = isMovie ? query.Title : RemoveSeasonMarker(query.Title);
        // TMDB localizes `name` using the search language.  A parsed Latin-title
        // such as "Mushoku Tensei" otherwise comes back with only its Japanese
        // title under ja-JP, which makes the identity matcher reject a valid
        // TMDB result before AniList/other fallbacks are considered.
        var searchLanguage = query.Language;
        if (query.Language.Equals("ja-JP", StringComparison.OrdinalIgnoreCase)
            && !ContainsJapaneseScript(searchTitle))
        {
            searchLanguage = "en-US";
        }
        var includeYearFilter = isMovie
                                || query.MediaKind != VideoMetadataMediaKind.Anime
                                && !query.SeasonNumber.HasValue
                                && !query.EpisodeNumber.HasValue;
        var yearParameter = query.Year.HasValue && includeYearFilter
            ? $"&{(isMovie ? "year" : "first_air_date_year")}={query.Year.Value}"
            : string.Empty;
        var uri = new Uri(
            $"https://api.themoviedb.org/3/search/{endpoint}?query={Uri.EscapeDataString(searchTitle)}" +
            $"&language={Uri.EscapeDataString(searchLanguage)}&region={Uri.EscapeDataString(query.Region)}{yearParameter}");
        var response = await Transport.SendAsync(new VideoMetadataRequest(
            Id,
            HttpMethod.Get,
            TmdbCredentialAuth.Apply(uri, token),
            Headers: TmdbCredentialAuth.Headers(token)), ct);
        using var json = ParseJson(response);
        if (!json.RootElement.TryGetProperty("results", out var results))
            return [];
        return results.EnumerateArray().Select(item =>
        {
            var id = item.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture);
            var title = String(item, isMovie ? "title" : "name") ?? string.Empty;
            var original = String(item, isMovie ? "original_title" : "original_name");
            var date = String(item, isMovie ? "release_date" : "first_air_date");
            var posterPath = String(item, "poster_path");
            var backdropPath = String(item, "backdrop_path");
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
                $"https://www.themoviedb.org/{endpoint}/{id}",
                string.IsNullOrWhiteSpace(posterPath) ? null : "https://image.tmdb.org/t/p/w500" + posterPath,
                string.IsNullOrWhiteSpace(backdropPath) ? null : "https://image.tmdb.org/t/p/w780" + backdropPath);
        }).Where(candidate => candidate.Title.Length > 0).ToList();
    }

    private static string RemoveSeasonMarker(string title)
    {
        var withoutEnglishMarker = Regex.Replace(
            title,
            @"\b(?:season|s)\s*\d+\b",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var withoutCjkMarker = Regex.Replace(withoutEnglishMarker, @"第\s*\d+\s*季", " ");
        return Regex.Replace(withoutCjkMarker, @"\s{2,}", " ").Trim();
    }

    private static bool ContainsJapaneseScript(string value) =>
        value.Any(character => character is (>= '\u3040' and <= '\u30ff') or (>= '\u3400' and <= '\u9fff'));

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
            $"?language={Uri.EscapeDataString(language)}&append_to_response=external_ids,credits,keywords,content_ratings,recommendations,translations");
        var response = await Transport.SendAsync(new VideoMetadataRequest(
            Id,
            HttpMethod.Get,
            TmdbCredentialAuth.Apply(uri, token),
            Headers: TmdbCredentialAuth.Headers(token)), ct);
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
        var seasonGraph = endpoint == "tv"
            ? await LoadSeasonsAsync(identity, root, language, token, ct)
            : new TmdbSeasonLoadResult([], null);
        var aliases = identity.Aliases
            .AddRange(ReadTmdbEnglishTitles(root, endpoint))
            .Add(title)
            .Add(original ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToImmutableArray();
        var now = DateTimeOffset.UtcNow;
        return new VideoMetadataDetails(
            Id, identity.ProviderItemId, identity.MediaKind, title, original, null,
            String(root, "overview"), YearFromDate(date), identity.SeasonNumber,
            identity.EpisodeNumber, identity.AbsoluteEpisodeNumber,
            aliases,
            genres, actors, ids.ToImmutable(), identity.SourceUrl, now, now + MetadataTtl,
            String(root, "tagline"), officialRating, Double(root, "vote_average"),
            YearFromDate(String(root, endpoint == "movie" ? "release_date" : "last_air_date")),
            String(root, "status"), tags, studios, peopleSnapshot, relatedItems,
            Seasons: seasonGraph.Seasons,
            TmdbOrdering: seasonGraph.Ordering);
    }

    private static IEnumerable<string> ReadTmdbEnglishTitles(JsonElement root, string endpoint)
    {
        if (!root.TryGetProperty("translations", out var translations)
            || !translations.TryGetProperty("translations", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in items.EnumerateArray()
                     .Where(item => string.Equals(String(item, "iso_639_1"), "en", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(item => String(item, "iso_3166_1") is "US" ? 0 : String(item, "iso_3166_1") is "GB" ? 1 : 2))
        {
            if (!item.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var translatedTitle = String(data, endpoint == "movie" ? "title" : "name");
            if (!string.IsNullOrWhiteSpace(translatedTitle))
                yield return translatedTitle;
        }
    }

    private async Task<TmdbSeasonLoadResult> LoadSeasonsAsync(
        VideoMetadataCandidate identity,
        JsonElement root,
        string language,
        string token,
        CancellationToken ct)
    {
        if (!root.TryGetProperty("seasons", out var seasonItems)
            || seasonItems.ValueKind != JsonValueKind.Array)
        {
            return new([], null);
        }

        var tmdbShowId = int.TryParse(
            identity.ProviderItemId,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsedShowId)
            ? parsedShowId
            : (int?)null;
        var defaultOrderingId = tmdbShowId?.ToString(CultureInfo.InvariantCulture)
                                ?? identity.ProviderItemId;
        var regularSeasonCount = seasonItems.EnumerateArray()
            .Select(summary => Int(summary, "season_number"))
            .Count(seasonNumber => seasonNumber is > 0);
        var alternateRegularSeasons = regularSeasonCount == 1
            ? await TryLoadTvEpisodeGroupSeasonsAsync(
                identity,
                regularSeasonCount,
                language,
                token,
                ct)
            : null;

        var seasons = new List<VideoMetadataSeason>();
        foreach (var (summary, summaryOrdinal) in seasonItems.EnumerateArray().Select((item, index) => (item, index)))
        {
            var seasonNumber = Int(summary, "season_number");
            if (seasonNumber is null)
                continue;
            if (seasonNumber > 0 && alternateRegularSeasons is { Seasons.IsDefaultOrEmpty: false })
                continue;

            var title = String(summary, "name")
                        ?? (seasonNumber == 0 ? "Specials" : $"Season {seasonNumber}");
            var overview = String(summary, "overview");
            var airDate = String(summary, "air_date");
            var episodeCount = Int(summary, "episode_count");
            var posterPath = String(summary, "poster_path");
            var posterUrl = string.IsNullOrWhiteSpace(posterPath)
                ? null
                : "https://image.tmdb.org/t/p/w500" + posterPath;
            var tmdbSeasonId = Int(summary, "id");

            var episodes = await LoadSeasonEpisodesAsync(
                identity,
                seasonNumber.Value,
                language,
                token,
                tmdbShowId,
                tmdbSeasonId,
                defaultOrderingId,
                ct);
            seasons.Add(new VideoMetadataSeason(
                seasonNumber.Value,
                title,
                overview,
                airDate,
                episodeCount,
                posterUrl,
                episodes)
            {
                TmdbShowId = tmdbShowId,
                TmdbSeasonId = tmdbSeasonId,
                TmdbOrderingId = defaultOrderingId,
                TmdbOrderingType = VideoTmdbOrderingType.Default,
                Ordinal = summaryOrdinal,
            });
        }
        if (alternateRegularSeasons is { Seasons.IsDefaultOrEmpty: false })
            seasons.AddRange(alternateRegularSeasons.Seasons);

        var ordering = alternateRegularSeasons?.Ordering
                       ?? (tmdbShowId.HasValue
                           ? new VideoTmdbOrdering(
                               tmdbShowId.Value,
                               defaultOrderingId,
                               VideoTmdbOrderingType.Default,
                               IsPreferred: true)
                           : null);
        return new(
            seasons.OrderBy(season => season.SeasonNumber).ToImmutableArray(),
            ordering);
    }

    private async Task<TmdbSeasonLoadResult?> TryLoadTvEpisodeGroupSeasonsAsync(
        VideoMetadataCandidate identity,
        int defaultRegularSeasonCount,
        string language,
        string token,
        CancellationToken ct)
    {
        try
        {
            var listUri = new Uri(
                $"https://api.themoviedb.org/3/tv/{Uri.EscapeDataString(identity.ProviderItemId)}" +
                "/episode_groups");
            var listResponse = await Transport.SendAsync(new VideoMetadataRequest(
                Id,
                HttpMethod.Get,
                TmdbCredentialAuth.Apply(listUri, token),
                Headers: TmdbCredentialAuth.Headers(token)), ct);
            if (listResponse.StatusCode is < 200 or >= 300)
                return null;

            using var listJson = ParseJson(listResponse);
            if (!listJson.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var episodeGroup = results.EnumerateArray()
                .Where(item => Int(item, "type") == 7)
                .Select(item => new
                {
                    Id = String(item, "id"),
                    GroupCount = Int(item, "group_count") ?? 0,
                    EpisodeCount = Int(item, "episode_count") ?? 0,
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Id)
                               && item.GroupCount > defaultRegularSeasonCount)
                .OrderByDescending(item => item.GroupCount)
                .ThenByDescending(item => item.EpisodeCount)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(episodeGroup?.Id)
                || !int.TryParse(
                    identity.ProviderItemId,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var tmdbShowId))
            {
                return null;
            }
            var episodeGroupId = episodeGroup.Id!;

            var detailsUri = new Uri(
                $"https://api.themoviedb.org/3/tv/episode_group/{Uri.EscapeDataString(episodeGroupId)}" +
                $"?language={Uri.EscapeDataString(language)}");
            var detailsResponse = await Transport.SendAsync(new VideoMetadataRequest(
                Id,
                HttpMethod.Get,
                TmdbCredentialAuth.Apply(detailsUri, token),
                Headers: TmdbCredentialAuth.Headers(token)), ct);
            if (detailsResponse.StatusCode is < 200 or >= 300)
                return null;

            using var detailsJson = ParseJson(detailsResponse);
            if (!detailsJson.RootElement.TryGetProperty("groups", out var groups)
                || groups.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var orderedGroups = groups.EnumerateArray()
                .Select((group, index) => new
                {
                    Group = group,
                    OriginalIndex = index,
                    Order = Int(group, "order") ?? index,
                })
                .OrderBy(item => item.Order)
                .ThenBy(item => item.OriginalIndex)
                .ToList();
            if (orderedGroups.Count <= defaultRegularSeasonCount)
                return null;

            var seasons = orderedGroups.Select((groupItem, seasonIndex) =>
            {
                var group = groupItem.Group;
                var seasonNumber = seasonIndex + 1;
                var title = String(group, "name") ?? $"Season {seasonNumber}";
                var tmdbEpisodeGroupId = String(group, "id");
                var episodes = ReadEpisodeGroupEpisodes(
                    identity,
                    group,
                    tmdbShowId,
                    episodeGroupId,
                    tmdbEpisodeGroupId,
                    seasonNumber);
                return new VideoMetadataSeason(
                    seasonNumber,
                    title,
                    null,
                    episodes.Select(episode => episode.AirDate)
                        .FirstOrDefault(airDate => !string.IsNullOrWhiteSpace(airDate)),
                    episodes.Length,
                    null,
                    episodes)
                {
                    TmdbShowId = tmdbShowId,
                    TmdbOrderingId = episodeGroupId,
                    TmdbEpisodeGroupId = tmdbEpisodeGroupId,
                    TmdbOrderingType = VideoTmdbOrderingType.Tv,
                    Ordinal = groupItem.Order,
                };
            }).ToImmutableArray();
            return new(
                seasons,
                new VideoTmdbOrdering(
                    tmdbShowId,
                    episodeGroupId,
                    VideoTmdbOrderingType.Tv,
                    IsPreferred: true));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException
                                      or JsonException
                                      or InvalidOperationException
                                      or FormatException)
        {
            // Alternate orders are an enrichment. Keep the provider's default
            // seasons when TMDB has no usable TV order or the endpoint fails.
            return null;
        }
    }

    private static ImmutableArray<VideoMetadataEpisode> ReadEpisodeGroupEpisodes(
        VideoMetadataCandidate identity,
        JsonElement group,
        int tmdbShowId,
        string orderingId,
        string? episodeGroupId,
        int logicalSeasonNumber)
    {
        if (!group.TryGetProperty("episodes", out var episodeItems)
            || episodeItems.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return episodeItems.EnumerateArray()
            .Select((episode, index) => new
            {
                Episode = episode,
                OriginalIndex = index,
                Order = Int(episode, "order") ?? index,
            })
            .OrderBy(item => item.Order)
            .ThenBy(item => item.OriginalIndex)
            .Select((item, logicalIndex) =>
            {
                var episode = item.Episode;
                var logicalEpisodeNumber = logicalIndex + 1;
                var providerSeasonNumber = Int(episode, "season_number");
                var providerEpisodeNumber = Int(episode, "episode_number");
                var stillPath = String(episode, "still_path");
                var sourceUrl = providerSeasonNumber.HasValue && providerEpisodeNumber.HasValue
                    ? $"https://www.themoviedb.org/tv/{identity.ProviderItemId}" +
                      $"/season/{providerSeasonNumber}/episode/{providerEpisodeNumber}"
                    : identity.SourceUrl;
                return new VideoMetadataEpisode(
                    logicalEpisodeNumber,
                    String(episode, "name") ?? $"Episode {logicalEpisodeNumber}",
                    String(episode, "original_name"),
                    String(episode, "overview"),
                    String(episode, "air_date"),
                    Int(episode, "runtime"),
                    string.IsNullOrWhiteSpace(stillPath)
                        ? null
                        : "https://image.tmdb.org/t/p/w500" + stillPath,
                    sourceUrl)
                {
                    TmdbShowId = tmdbShowId,
                    TmdbEpisodeId = Int(episode, "id"),
                    TmdbOrderingId = orderingId,
                    TmdbEpisodeGroupId = episodeGroupId,
                    Ordinal = item.Order,
                };
            })
            .ToImmutableArray();
    }

    private async Task<ImmutableArray<VideoMetadataEpisode>> LoadSeasonEpisodesAsync(
        VideoMetadataCandidate identity,
        int seasonNumber,
        string language,
        string token,
        int? tmdbShowId,
        int? tmdbSeasonId,
        string orderingId,
        CancellationToken ct)
    {
        var uri = new Uri(
            $"https://api.themoviedb.org/3/tv/{Uri.EscapeDataString(identity.ProviderItemId)}" +
            $"/season/{seasonNumber}?language={Uri.EscapeDataString(language)}");
        var response = await Transport.SendAsync(new VideoMetadataRequest(
            Id,
            HttpMethod.Get,
            TmdbCredentialAuth.Apply(uri, token),
            Headers: TmdbCredentialAuth.Headers(token)), ct);
        using var json = ParseJson(response);
        if (!json.RootElement.TryGetProperty("episodes", out var episodeItems)
            || episodeItems.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return episodeItems.EnumerateArray()
            .Select((item, ordinal) =>
            {
                var episodeNumber = Int(item, "episode_number");
                if (episodeNumber is null)
                    return null;
                var title = String(item, "name") ?? $"Episode {episodeNumber}";
                var stillPath = String(item, "still_path");
                return new VideoMetadataEpisode(
                    episodeNumber.Value,
                    title,
                    String(item, "original_name"),
                    String(item, "overview"),
                    String(item, "air_date"),
                    Int(item, "runtime"),
                    string.IsNullOrWhiteSpace(stillPath)
                        ? null
                        : "https://image.tmdb.org/t/p/w500" + stillPath,
                    $"https://www.themoviedb.org/tv/{identity.ProviderItemId}/season/{seasonNumber}/episode/{episodeNumber}")
                {
                    TmdbShowId = tmdbShowId,
                    TmdbEpisodeId = Int(item, "id"),
                    TmdbSeasonId = tmdbSeasonId,
                    TmdbOrderingId = orderingId,
                    Ordinal = ordinal,
                };
            })
            .Where(episode => episode is not null)
            .Cast<VideoMetadataEpisode>()
            .OrderBy(episode => episode.EpisodeNumber)
            .ToImmutableArray();
    }

    private sealed record TmdbSeasonLoadResult(
        ImmutableArray<VideoMetadataSeason> Seasons,
        VideoTmdbOrdering? Ordering);

    public async Task<IReadOnlyList<VideoArtworkCandidate>> GetArtworkAsync(
        VideoMetadataCandidate identity,
        CancellationToken ct = default)
    {
        var token = await RequireTokenAsync(ct);
        var endpoint = identity.MediaKind == VideoMetadataMediaKind.Movie ? "movie" : "tv";
        var uri = new Uri($"https://api.themoviedb.org/3/{endpoint}/{identity.ProviderItemId}/images");
        var response = await Transport.SendAsync(new VideoMetadataRequest(
            Id,
            HttpMethod.Get,
            TmdbCredentialAuth.Apply(uri, token),
            Headers: TmdbCredentialAuth.Headers(token)), ct);
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

    private static VideoMetadataCandidate CreateExplicitCandidate(VideoMetadataSearchQuery query, string id) =>
        new("tmdb", id, query.MediaKind, query.Title, null, query.Year, query.SeasonNumber,
            query.EpisodeNumber, query.AbsoluteEpisodeNumber, [query.Title],
            ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.OrdinalIgnoreCase)
                .Add("tmdb", id),
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
        var seasons = ParseTvMazeSeasons(root, identity);
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
            People: peopleSnapshot,
            Seasons: seasons);
    }

    private static ImmutableArray<VideoMetadataSeason> ParseTvMazeSeasons(
        JsonElement root,
        VideoMetadataCandidate identity)
    {
        if (!root.TryGetProperty("_embedded", out var embedded)
            || !embedded.TryGetProperty("episodes", out var episodeItems)
            || episodeItems.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return episodeItems.EnumerateArray()
            .Select(item =>
            {
                var seasonNumber = Int(item, "season");
                var episodeNumber = Int(item, "number");
                if (seasonNumber is null || episodeNumber is null)
                    return null;
                var imageUrl = item.TryGetProperty("image", out var image)
                    && image.ValueKind == JsonValueKind.Object
                    ? String(image, "original") ?? String(image, "medium")
                    : null;
                return new
                {
                    Season = seasonNumber.Value,
                    Episode = new VideoMetadataEpisode(
                        episodeNumber.Value,
                        String(item, "name") ?? $"Episode {episodeNumber}",
                        null,
                        StripHtml(String(item, "summary")),
                        String(item, "airdate"),
                        Int(item, "runtime"),
                        imageUrl,
                        String(item, "url")
                            ?? $"https://www.tvmaze.com/shows/{identity.ProviderItemId}"),
                };
            })
            .Where(item => item is not null)
            .GroupBy(item => item!.Season)
            .Select(group => new VideoMetadataSeason(
                group.Key,
                group.Key == 0 ? "Specials" : $"Season {group.Key}",
                null,
                group.Select(item => item!.Episode.AirDate).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                group.Count(),
                null,
                group.Select(item => item!.Episode).OrderBy(episode => episode.EpisodeNumber).ToImmutableArray()))
            .OrderBy(season => season.SeasonNumber)
            .ToImmutableArray();
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
    IVideoMetadataDetailsProvider,
    IVideoArtworkProvider
{
    public AniListVideoMetadataProvider(IVideoMetadataTransport transport) : base(transport) { }
    public override string Id => "anilist";
    public override string DisplayName => "AniList";
    public override VideoMetadataCapabilities Capabilities =>
        VideoMetadataCapabilities.Search
        | VideoMetadataCapabilities.Details
        | VideoMetadataCapabilities.Artwork;
    public override IReadOnlySet<VideoMetadataMediaKind> SupportedMediaKinds { get; } =
        new HashSet<VideoMetadataMediaKind> { VideoMetadataMediaKind.Anime, VideoMetadataMediaKind.Series, VideoMetadataMediaKind.Episode };
    public override bool ArtworkEnabledByDefault => true;
    public override string AttributionUrl => "https://anilist.co/";

    public async Task<IReadOnlyList<VideoMetadataCandidate>> SearchAsync(VideoMetadataSearchQuery query, CancellationToken ct = default)
    {
        const string fields = "{id idMal title{romaji english native} synonyms seasonYear coverImage{extraLarge large} bannerImage siteUrl}";
        int? id = query.ExternalIds.TryGetValue("anilist", out var own) && int.TryParse(own, out var ownId) ? ownId : null;
        int? mal = query.ExternalIds.TryGetValue("mal", out var malValue) && int.TryParse(malValue, out var malId) ? malId : null;
        string graph;
        object variables;
        if (id.HasValue)
        {
            graph = $"query NiratanAnimeByIdV3($id:Int){{Page(perPage:20){{media(id:$id,type:ANIME){fields}}}}}";
            variables = new { id };
        }
        else if (mal.HasValue)
        {
            graph = $"query NiratanAnimeByMalIdV3($idMal:Int){{Page(perPage:20){{media(idMal:$idMal,type:ANIME){fields}}}}}";
            variables = new { idMal = mal };
        }
        else
        {
            // AniList treats explicitly supplied null id/idMal arguments as filters
            // and returns no rows, so title search must omit those arguments entirely.
            graph = $"query NiratanAnimeSearchV3($search:String){{Page(perPage:20){{media(search:$search,type:ANIME){fields}}}}}";
            variables = new { search = query.Title };
        }
        var body = JsonSerializer.SerializeToUtf8Bytes(new { query = graph, variables });
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
        var ids = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in identity.ExternalIds ?? ImmutableDictionary<string, string>.Empty)
            ids[pair.Key] = pair.Value;
        foreach (var pair in candidate.ExternalIds)
            ids[pair.Key] = pair.Value;
        if (media.TryGetProperty("externalLinks", out var links))
        {
            foreach (var link in links.EnumerateArray())
            {
                var site = String(link, "site");
                var url = String(link, "url");
                if (!string.IsNullOrWhiteSpace(site)
                    && site.Contains("imdb", StringComparison.OrdinalIgnoreCase)
                    && TryParseImdbTitleId(url, out var imdbId))
                {
                    ids["imdb"] = imdbId;
                }
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
                    romaji ?? english ?? native ?? relatedId,
                    native,
                    Int(related, "seasonYear"),
                    related.TryGetProperty("coverImage", out var coverImage)
                        ? String(coverImage, "large")
                        : null,
                    String(related, "bannerImage"),
                    String(related, "siteUrl"),
                    Aliases: new[] { native, romaji, english }
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value!)
                        .Distinct(StringComparer.CurrentCultureIgnoreCase)
                        .ToImmutableArray()));
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

    public async Task<IReadOnlyList<VideoArtworkCandidate>> GetArtworkAsync(
        VideoMetadataCandidate identity,
        CancellationToken ct = default)
    {
        const string graph = "query($id:Int){Media(id:$id,type:ANIME){coverImage{extraLarge large medium} bannerImage siteUrl}}";
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            query = graph,
            variables = new
            {
                id = int.Parse(identity.ProviderItemId, CultureInfo.InvariantCulture),
            },
        });
        var response = await Transport.SendAsync(new VideoMetadataRequest(
            Id,
            HttpMethod.Post,
            new Uri("https://graphql.anilist.co"),
            body,
            "application/json",
            IsIdempotent: true), ct);
        using var json = ParseJson(response);
        if (json.RootElement.GetProperty("data").GetProperty("Media") is not
            { ValueKind: JsonValueKind.Object } media)
            return [];

        var sourceUrl = String(media, "siteUrl") ?? identity.SourceUrl ?? AttributionUrl;
        var artwork = new List<VideoArtworkCandidate>();
        if (media.TryGetProperty("coverImage", out var coverImage))
        {
            foreach (var url in new[]
                     {
                         String(coverImage, "extraLarge"),
                         String(coverImage, "large"),
                         String(coverImage, "medium"),
                     }
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.Ordinal))
            {
                artwork.Add(new VideoArtworkCandidate(
                    Id, url!, "poster", null, null, null, sourceUrl));
            }
        }

        var bannerUrl = String(media, "bannerImage");
        if (!string.IsNullOrWhiteSpace(bannerUrl))
        {
            artwork.Add(new VideoArtworkCandidate(
                Id, bannerUrl, "backdrop", null, null, null, sourceUrl));
        }
        return artwork;
    }

    private static VideoMetadataCandidate ToCandidate(JsonElement item, VideoMetadataSearchQuery query)
    {
        var titleObject = item.GetProperty("title");
        var native = String(titleObject, "native");
        var romaji = String(titleObject, "romaji");
        var english = String(titleObject, "english");
        var id = item.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture);
        var ids = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        ids["anilist"] = id;
        if (item.TryGetProperty("idMal", out var mal) && mal.ValueKind == JsonValueKind.Number)
            ids["mal"] = mal.GetRawText();
        var aliases = StringArray(item, "synonyms")
            .AddRange(new[] { native, romaji, english }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!))
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToImmutableArray();
        return new VideoMetadataCandidate(
            "anilist", id, VideoMetadataMediaKind.Anime, romaji ?? english ?? native ?? query.Title, native,
            Int(item, "seasonYear"), query.SeasonNumber, query.EpisodeNumber, query.AbsoluteEpisodeNumber,
            aliases,
            ids.ToImmutable(),
            String(item, "siteUrl"),
            item.TryGetProperty("coverImage", out var coverImage)
                ? String(coverImage, "extraLarge") ?? String(coverImage, "large")
                : null,
            String(item, "bannerImage"));
    }

    private static bool TryParseImdbTitleId(string? value, out string id)
    {
        id = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.IdnHost.Equals("imdb.com", StringComparison.OrdinalIgnoreCase)
                 || uri.IdnHost.Equals("www.imdb.com", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var segment = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => Regex.IsMatch(
                value,
                @"^tt\d+$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        if (segment is null)
            return false;
        id = segment.ToLowerInvariant();
        return true;
    }

    private static string? StripHtml(string? value) =>
        string.IsNullOrWhiteSpace(value) ? value : System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", string.Empty).Trim();
}

internal sealed class AniDbTitleIndexProvider : VideoMetadataProviderBase,
    IVideoMetadataSearchProvider,
    IVideoMetadataDetailsProvider,
    IVideoArtworkProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IAniDbCatalogStore? _catalog;
    private readonly IAniDbHttpClient? _http;
    private ImmutableArray<AniDbTitle> _titles = [];
    private DateTimeOffset _loadedAt;

    public AniDbTitleIndexProvider(IVideoMetadataTransport transport) : base(transport) { }
    public AniDbTitleIndexProvider(
        IVideoMetadataTransport transport,
        IAniDbCatalogStore catalog,
        IAniDbHttpClient http) : base(transport)
    {
        _catalog = catalog;
        _http = http;
    }
    public override string Id => "anidb";
    public override string DisplayName => "AniDB";
    public override VideoMetadataCapabilities Capabilities =>
        VideoMetadataCapabilities.Search | VideoMetadataCapabilities.Details
        | VideoMetadataCapabilities.Artwork | VideoMetadataCapabilities.EpisodeOrder
        | VideoMetadataCapabilities.TitleIndex;
    public override IReadOnlySet<VideoMetadataMediaKind> SupportedMediaKinds { get; } =
        new HashSet<VideoMetadataMediaKind> { VideoMetadataMediaKind.Anime, VideoMetadataMediaKind.Series };
    public override bool ArtworkEnabledByDefault => true;
    public override string AttributionUrl => "https://anidb.net/";

    public async Task<IReadOnlyList<VideoMetadataCandidate>> SearchAsync(VideoMetadataSearchQuery query, CancellationToken ct = default)
    {
        // AID embedded by a Shoko renamer or Local NFO is authoritative. Do not
        // turn an exact file -> AniDB anime mapping back into a fuzzy title search.
        if (query.ExternalIds.TryGetValue("anidb", out var explicitId)
            && !string.IsNullOrWhiteSpace(explicitId))
        {
            return [new VideoMetadataCandidate(
                Id,
                explicitId.Trim(),
                VideoMetadataMediaKind.Anime,
                query.Title,
                null,
                query.Year,
                query.SeasonNumber,
                query.EpisodeNumber,
                query.AbsoluteEpisodeNumber,
                [query.Title],
                ImmutableDictionary<string, string>.Empty
                    .WithComparers(StringComparer.OrdinalIgnoreCase)
                    .Add("anidb", explicitId.Trim()),
                $"https://anidb.net/anime/{Uri.EscapeDataString(explicitId.Trim())}")];
        }

        await EnsureIndexAsync(ct);
        var normalized = Normalize(query.Title);
        return _titles
            .Where(title => title.Normalized.Contains(normalized, StringComparison.Ordinal)
                            || normalized.Contains(title.Normalized, StringComparison.Ordinal))
            .GroupBy(title => title.AnimeId)
            .Take(20)
            .Select(group =>
            {
                var primary = SelectPreferredTitle(group, query.Language)
                              ?? SelectPreferredTitle(group, "en")
                              ?? SelectPreferredTitle(group, "x-jat")
                              ?? group.First().Value;
                return new VideoMetadataCandidate(
                    Id, group.Key, VideoMetadataMediaKind.Anime, primary,
                    SelectPreferredTitle(group, "ja"),
                    query.Year, query.SeasonNumber, query.EpisodeNumber, query.AbsoluteEpisodeNumber,
                    group.Select(title => title.Value).Distinct(StringComparer.CurrentCultureIgnoreCase).ToImmutableArray(),
                    ImmutableDictionary<string, string>.Empty.Add("anidb", group.Key),
                    $"https://anidb.net/anime/{group.Key}");
            })
            .ToList();
    }

    public async Task<VideoMetadataDetails?> GetDetailsAsync(
        VideoMetadataCandidate identity,
        string language,
        string region,
        CancellationToken ct = default)
    {
        var anime = await LoadAnimeAsync(identity.ProviderItemId, ct);
        if (anime != null)
        {
            var details = AniDbImportService.ToDetails(anime);
            var preferredTitle = SelectPreferredAnimeTitle(anime.Titles, language)
                                 ?? SelectPreferredAnimeTitle(anime.Titles, "en")
                                 ?? SelectPreferredAnimeTitle(anime.Titles, "x-jat")
                                 ?? details.Title;
            var richOriginalTitle = SelectPreferredAnimeTitle(anime.Titles, "ja") ?? details.OriginalTitle;
            return details with
            {
                Title = preferredTitle,
                OriginalTitle = richOriginalTitle,
                ExternalIds = details.ExternalIds.SetItems(identity.ExternalIds),
            };
        }

        await EnsureIndexAsync(ct);
        var titles = _titles
            .Where(title => title.AnimeId.Equals(identity.ProviderItemId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var aliases = titles
            .Select(title => title.Value)
            .Append(identity.Title)
            .Concat(identity.Aliases.IsDefault ? [] : identity.Aliases)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToImmutableArray();
        var title = SelectPreferredTitle(titles, language)
                    ?? SelectPreferredTitle(titles, "en")
                    ?? SelectPreferredTitle(titles, "x-jat")
                    ?? identity.Title;
        var originalTitle = SelectPreferredTitle(titles, "ja") ?? identity.OriginalTitle;
        var now = DateTimeOffset.UtcNow;
        return new VideoMetadataDetails(
            Id,
            identity.ProviderItemId,
            VideoMetadataMediaKind.Anime,
            title,
            originalTitle,
            null,
            null,
            identity.Year,
            identity.SeasonNumber,
            identity.EpisodeNumber,
            identity.AbsoluteEpisodeNumber,
            aliases,
            [],
            [],
            identity.ExternalIds.SetItem("anidb", identity.ProviderItemId),
            identity.SourceUrl ?? $"https://anidb.net/anime/{identity.ProviderItemId}",
            now,
            now + MetadataTtl);
    }

    public async Task<IReadOnlyList<VideoArtworkCandidate>> GetArtworkAsync(
        VideoMetadataCandidate identity,
        CancellationToken ct = default)
    {
        var anime = await LoadAnimeAsync(identity.ProviderItemId, ct);
        var url = AniDbImageUrl(anime?.Picture);
        return url == null
            ? []
            : [new VideoArtworkCandidate(Id, url, "poster", null, null, null, AttributionUrl)];
    }

    private async Task<AniDbAnime?> LoadAnimeAsync(string providerItemId, CancellationToken ct)
    {
        if (_catalog == null || _http == null || !int.TryParse(providerItemId, out var animeId) || animeId <= 0)
            return null;
        var cached = await _catalog.GetAnimeAsync(animeId, ct);
        if (cached is { ExpiresAt: var expiresAt } && expiresAt > DateTimeOffset.UtcNow)
            return cached;
        var anime = await _http.GetAnimeAsync(animeId, ct);
        if (anime != null)
            await _catalog.UpsertAnimeAsync(anime, ct);
        return anime ?? cached;
    }

    internal static string? AniDbImageUrl(string? picture)
    {
        if (string.IsNullOrWhiteSpace(picture)) return null;
        var normalized = picture.Replace('\\', '/').Trim();
        var fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        if (fileName.Length == 0 || fileName is "." or "..") return null;
        return "https://cdn.anidb.net/images/main/" + Uri.EscapeDataString(fileName);
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
                    title.Attribute("type")?.Value ?? string.Empty,
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

    private static string? SelectPreferredTitle(IEnumerable<AniDbTitle> titles, string language)
    {
        var languageCode = language.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(languageCode))
            return null;
        return titles
            .Where(title => title.Language.Equals(languageCode, StringComparison.OrdinalIgnoreCase))
            .OrderBy(title => title.Type.Equals("official", StringComparison.OrdinalIgnoreCase) ? 0
                : title.Type.Equals("main", StringComparison.OrdinalIgnoreCase) ? 1
                : title.Type.Equals("synonym", StringComparison.OrdinalIgnoreCase) ? 2
                : 3)
            .Select(title => title.Value)
            .FirstOrDefault();
    }

    private static string? SelectPreferredAnimeTitle(
        IEnumerable<global::Niratan.Services.Video.AniDbTitle> titles,
        string language)
    {
        var languageCode = language.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(languageCode)) return null;
        return titles
            .Where(title => title.Language.Equals(languageCode, StringComparison.OrdinalIgnoreCase))
            .OrderBy(title => title.Type.Equals("official", StringComparison.OrdinalIgnoreCase) ? 0
                : title.Type.Equals("main", StringComparison.OrdinalIgnoreCase) ? 1
                : title.Type.Equals("synonym", StringComparison.OrdinalIgnoreCase) ? 2
                : 3)
            .Select(title => title.Value)
            .FirstOrDefault();
    }

    private sealed record AniDbTitle(
        string AnimeId,
        string Language,
        string Type,
        string Value,
        string Normalized);
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
