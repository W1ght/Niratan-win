using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Niratan.Models.Video;
using Niratan.Services.Video;
using Niratan.Services.Storage;
using Niratan.Services.Novels;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Video;

public sealed class VideoMetadataProviderTests
{
    [Fact]
    public async Task TmdbSearch_UsesCredentialAndParsesFixtureWithoutLiveNetwork()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new FixtureTransport("""
            {"results":[{"id":123,"name":"アンナチュラル","original_name":"アンナチュラル","first_air_date":"2018-01-12","poster_path":"/poster.jpg","backdrop_path":"/backdrop.jpg"}]}
            """);
        var provider = new TmdbVideoMetadataProvider(transport, new FixtureCredentialStore("secret"));
        var query = new VideoMetadataSearchQuery(
            "アンナチュラル", VideoMetadataMediaKind.Series, 2018, null, null, null,
            "ja-JP", "JP", ImmutableDictionary<string, string>.Empty);

        var candidates = await provider.SearchAsync(query, ct);

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.ProviderItemId.Should().Be("123");
        candidate.PosterUrl.Should().Be("https://image.tmdb.org/t/p/w500/poster.jpg");
        candidate.BackdropUrl.Should().Be("https://image.tmdb.org/t/p/w780/backdrop.jpg");
        transport.LastRequest!.Headers!["Authorization"].Should().Be("Bearer secret");
        transport.LastRequest.Uri.Host.Should().Be("api.themoviedb.org");
    }

    [Fact]
    public async Task TmdbAnimeSearch_RemovesSeasonSuffixAndDoesNotFilterBySeasonYear()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new FixtureTransport("{\"results\":[]}");
        var provider = new TmdbVideoMetadataProvider(transport, new FixtureCredentialStore("secret"));
        var query = new VideoMetadataSearchQuery(
            "Mushoku Tensei: Jobless Reincarnation Season 2",
            VideoMetadataMediaKind.Anime,
            2023,
            2,
            1,
            null,
            "ja-JP",
            "JP",
            ImmutableDictionary<string, string>.Empty);

        await provider.SearchAsync(query, ct);

        transport.LastRequest!.Uri.Query.Should().Contain("query=Mushoku%20Tensei");
        transport.LastRequest.Uri.Query.Should().NotContain("Season%202");
        transport.LastRequest.Uri.Query.Should().NotContain("first_air_date_year");
    }

    [Fact]
    public async Task TmdbSearch_UsesEnglishResultLanguageForLatinTitles()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new FixtureTransport(
            "{\"results\":[{\"id\":94664,\"name\":\"Mushoku Tensei: Jobless Reincarnation\",\"original_name\":\"無職転生 ～異世界行ったら本気だす～\",\"first_air_date\":\"2021-01-11\"}]}" );
        var provider = new TmdbVideoMetadataProvider(transport, new FixtureCredentialStore("secret"));
        var query = new VideoMetadataSearchQuery(
            "Mushoku Tensei: Jobless Reincarnation Season 2",
            VideoMetadataMediaKind.Anime,
            2023,
            2,
            1,
            null,
            "ja-JP",
            "JP",
            ImmutableDictionary<string, string>.Empty);

        var candidates = await provider.SearchAsync(query, ct);

        candidates.Should().ContainSingle().Which.Title.Should().Be("Mushoku Tensei: Jobless Reincarnation");
        transport.LastRequest!.Uri.Query.Should().Contain("language=en-US");
    }

    [Fact]
    public async Task TmdbSeasonScopedSeriesSearch_DoesNotFilterBySeasonYear()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new FixtureTransport("{\"results\":[]}");
        var provider = new TmdbVideoMetadataProvider(transport, new FixtureCredentialStore("secret"));
        var query = new VideoMetadataSearchQuery(
            "Mushoku Tensei: Jobless Reincarnation Season 2",
            VideoMetadataMediaKind.Series,
            2023,
            2,
            1,
            null,
            "ja-JP",
            "JP",
            ImmutableDictionary<string, string>.Empty);

        await provider.SearchAsync(query, ct);

        transport.LastRequest!.Uri.Query.Should().NotContain("Season%202");
        transport.LastRequest.Uri.Query.Should().NotContain("first_air_date_year");
    }

    [Fact]
    public async Task TmdbDetails_ProjectsComprehensiveSeriesMetadataFromFixture()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new FixtureTransport("""
            {
              "id":123,"name":"作品","original_name":"作品 原題","overview":"概要",
              "first_air_date":"2020-01-01","last_air_date":"2024-03-01","status":"Returning Series",
              "tagline":"物語は続く","vote_average":8.25,
              "genres":[{"name":"Animation"}],
              "production_companies":[{"name":"Studio A"}],
              "external_ids":{"imdb_id":"tt123","tvdb_id":456},
              "credits":{"cast":[{"id":7,"name":"声優 A","character":"主人公","profile_path":"/person.jpg"}]},
              "keywords":{"results":[{"name":"time travel"}]},
              "content_ratings":{"results":[{"iso_3166_1":"JP","rating":"PG12"}]},
              "translations":{"translations":[
                {"iso_639_1":"en","iso_3166_1":"US","data":{"name":"English Work Title"}},
                {"iso_639_1":"fr","iso_3166_1":"FR","data":{"name":"Titre français"}}]},
              "recommendations":{"results":[{"id":99,"name":"関連作品","original_name":"Related","first_air_date":"2021-01-01","poster_path":"/p.jpg","backdrop_path":"/b.jpg"}]}
            }
            """);
        var provider = new TmdbVideoMetadataProvider(transport, new FixtureCredentialStore("secret"));
        var candidate = new VideoMetadataCandidate(
            "tmdb", "123", VideoMetadataMediaKind.Series, "作品", null, 2020,
            null, null, null, ["作品"], ImmutableDictionary<string, string>.Empty,
            "https://www.themoviedb.org/tv/123");

        var details = await provider.GetDetailsAsync(candidate, "ja-JP", "JP", ct);

        details.Should().NotBeNull();
        details!.Tagline.Should().Be("物語は続く");
        details.OfficialRating.Should().Be("PG12");
        details.CommunityRating.Should().Be(8.25);
        details.EndYear.Should().Be(2024);
        details.Status.Should().Be("Returning Series");
        details.Tags.Should().Contain("time travel");
        details.Studios.Should().Contain("Studio A");
        details.Aliases.Should().Contain("English Work Title");
        details.Aliases.Should().NotContain("Titre français");
        details.People.Should().ContainSingle(person => person.Name == "声優 A" && person.Role == "主人公");
        details.RelatedItems.Should().ContainSingle(item => item.ProviderItemId == "99");
        transport.LastRequest!.Uri.Query.Should().Contain("recommendations");
        transport.LastRequest.Uri.Query.Should().Contain("translations");
    }

    [Fact]
    public async Task TmdbDetails_LoadsEveryScrapedSeasonAndEpisode()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new AllSeasonsFixtureTransport();
        var provider = new TmdbVideoMetadataProvider(transport, new FixtureCredentialStore("secret"));
        var candidate = new VideoMetadataCandidate(
            "tmdb", "123", VideoMetadataMediaKind.Series, "作品", null, 2020,
            null, null, null, ["作品"], ImmutableDictionary<string, string>.Empty,
            "https://www.themoviedb.org/tv/123");

        var details = await provider.GetDetailsAsync(candidate, "ja-JP", "JP", ct);

        details.Should().NotBeNull();
        details!.Seasons.Should().HaveCount(25);
        details.Seasons[^1].Episodes.Should().HaveCount(201);
        details.Seasons[^1].Episodes[^1].EpisodeNumber.Should().Be(201);
        transport.SeasonRequestCount.Should().Be(25);
    }

    [Fact]
    public async Task TmdbDetails_UsesTvEpisodeGroupWhenDefaultOrderCollapsesMultipleSeasons()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new CollapsedTvOrderFixtureTransport();
        var provider = new TmdbVideoMetadataProvider(transport, new FixtureCredentialStore("secret"));
        var candidate = new VideoMetadataCandidate(
            "tmdb", "65942", VideoMetadataMediaKind.Series, "Re:ゼロから始める異世界生活", null, 2016,
            null, null, null, ["Re:Zero − Starting Life in Another World"],
            ImmutableDictionary<string, string>.Empty,
            "https://www.themoviedb.org/tv/65942");

        var details = await provider.GetDetailsAsync(candidate, "ja-JP", "JP", ct);

        details.Should().NotBeNull();
        details!.TmdbOrdering.Should().Be(new VideoTmdbOrdering(
            65942,
            "re-zero-tv",
            VideoTmdbOrderingType.Tv,
            IsPreferred: true));
        details.Seasons.Select(season => season.SeasonNumber).Should().Equal(0, 1, 2, 3, 4);
        details.Seasons[1].Title.Should().Be("Season 1");
        details.Seasons[1].TmdbShowId.Should().Be(65942);
        details.Seasons[1].TmdbOrderingId.Should().Be("re-zero-tv");
        details.Seasons[1].TmdbEpisodeGroupId.Should().Be("season-1");
        details.Seasons[1].TmdbOrderingType.Should().Be(VideoTmdbOrderingType.Tv);
        details.Seasons[1].Episodes.Select(episode => episode.EpisodeNumber).Should().Equal(1, 2);
        details.Seasons[1].Episodes[0].TmdbEpisodeId.Should().Be(1001);
        details.Seasons[1].Episodes[0].TmdbOrderingId.Should().Be("re-zero-tv");
        details.Seasons[1].Episodes[0].TmdbEpisodeGroupId.Should().Be("season-1");
        details.Seasons[1].Episodes[0].Ordinal.Should().Be(0);
        details.Seasons[2].Episodes.Select(episode => episode.EpisodeNumber).Should().Equal(1, 2);
        details.Seasons[2].Episodes[0].SourceUrl.Should()
            .EndWith("/tv/65942/season/1/episode/3");
        transport.RequestPaths.Should().Contain("/3/tv/65942/episode_groups");
        transport.RequestPaths.Should().Contain("/3/tv/episode_group/re-zero-tv");
        transport.RequestPaths.Should().Contain("/3/tv/65942/season/0");
        transport.RequestPaths.Should().NotContain("/3/tv/65942/season/1");
    }

    [Fact]
    public async Task AniDbExplicitIdentity_DoesNotRequireTitleSearchAndProvidesCanonicalTitles()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new AniDbTitleFixtureTransport();
        var provider = new AniDbTitleIndexProvider(transport);
        var query = new VideoMetadataSearchQuery(
            "Renamed release",
            VideoMetadataMediaKind.Anime,
            2016,
            1,
            1,
            1,
            "ja-JP",
            "JP",
            ImmutableDictionary<string, string>.Empty.Add("anidb", "11370"));

        var candidates = await provider.SearchAsync(query, ct);

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.ProviderId.Should().Be("anidb");
        candidate.ProviderItemId.Should().Be("11370");
        transport.RequestCount.Should().Be(0, "an explicit Shoko/AniDB identity is authoritative");

        var details = await provider.GetDetailsAsync(candidate, "ja-JP", "JP", ct);

        details.Should().NotBeNull();
        details!.Title.Should().Be("Re:ゼロから始める異世界生活");
        details.OriginalTitle.Should().Be("Re:ゼロから始める異世界生活");
        details.Aliases.Should().Contain("Re:Zero kara Hajimeru Isekai Seikatsu");
        details.ExternalIds.Should().Contain("anidb", "11370");
        transport.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task AniListDetails_ProjectsRichSeriesTextFromFixture()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new FixtureTransport("""
            {"data":{"Media":{"id":20987,"idMal":28825,
              "title":{"romaji":"Himouto! Umaru-chan","english":"Himouto! Umaru-chan","native":"干物妹！うまるちゃん"},
              "synonyms":[],"description":"概要","seasonYear":2015,"endDate":{"year":2015},
              "status":"FINISHED","averageScore":71,"genres":["Comedy"],
              "tags":[{"name":"School"}],"studios":{"nodes":[{"name":"Doga Kobo","isAnimationStudio":true}]},
              "characters":{"edges":[{"role":"MAIN","node":{"name":{"full":"Umaru Doma","native":"土間うまる"}},
                "voiceActors":[{"id":100,"name":{"full":"Aimi Tanaka","native":"田中あいみ"},"image":{"large":"https://img.test/person.jpg"},"siteUrl":"https://anilist.co/staff/100"}]}]},
              "recommendations":{"nodes":[{"mediaRecommendation":{"id":21268,"title":{"romaji":"Related Romaji","english":"Related English","native":"関連作品"},"seasonYear":2016,"coverImage":{"large":"https://img.test/poster.jpg"},"bannerImage":"https://img.test/backdrop.jpg","siteUrl":"https://anilist.co/anime/21268"}}]},
              "siteUrl":"https://anilist.co/anime/20987","externalLinks":[
                {"id":999999,"site":"IMDb","url":"https://www.imdb.com/title/tt1234567/"}]}}}
            """);
        var provider = new AniListVideoMetadataProvider(transport);
        var candidate = new VideoMetadataCandidate(
            "anilist", "20987", VideoMetadataMediaKind.Anime, "干物妹！うまるちゃん", null, 2015,
            null, 8, 8, ["Himouto! Umaru-chan"],
            ImmutableDictionary<string, string>.Empty
                .Add("anilist", "20987")
                .Add("tmdb", "12345"),
            "https://anilist.co/anime/20987");

        var details = await provider.GetDetailsAsync(candidate, "ja-JP", "JP", ct);

        details.Should().NotBeNull();
        details!.Title.Should().Be("Himouto! Umaru-chan");
        details!.OriginalTitle.Should().Be("干物妹！うまるちゃん");
        details.CommunityRating.Should().Be(7.1);
        details.Status.Should().Be("FINISHED");
        details.Tags.Should().Contain("School");
        details.Studios.Should().Contain("Doga Kobo");
        details.People.Should().ContainSingle(person => person.Name == "田中あいみ" && person.Role == "土間うまる");
        var related = details.RelatedItems.Should().ContainSingle(item => item.ProviderItemId == "21268").Subject;
        related.Title.Should().Be("Related Romaji");
        related.OriginalTitle.Should().Be("関連作品");
        related.Aliases.Should().Contain("Related English");
        details.ExternalIds.Should().Contain("tmdb", "12345");
        details.ExternalIds.Should().Contain("imdb", "tt1234567");
        details.ExternalIds.Values.Should().NotContain("999999");
    }

    [Fact]
    public async Task AniListTitleSearch_OmitsUnusedNullIdFilters()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new FixtureTransport("""
            {"data":{"Page":{"media":[{"id":20987,"idMal":28825,
              "title":{"romaji":"Himouto! Umaru-chan","english":"Himouto! Umaru-chan","native":"干物妹！うまるちゃん"},
              "synonyms":[],"seasonYear":2015,
              "coverImage":{"extraLarge":"https://img.test/poster-xl.jpg","large":"https://img.test/poster.jpg"},
              "bannerImage":"https://img.test/backdrop.jpg","siteUrl":"https://anilist.co/anime/20987"}]}}}
            """);
        var provider = new AniListVideoMetadataProvider(transport);
        var query = new VideoMetadataSearchQuery(
            "Himouto! Umaru-chan", VideoMetadataMediaKind.Anime, null, null, 8, 8,
            "ja-JP", "JP", ImmutableDictionary<string, string>.Empty.Add("anidb", "10972"));

        var candidates = await provider.SearchAsync(query, ct);

        var candidate = candidates.Should().ContainSingle().Subject;
        candidate.ProviderItemId.Should().Be("20987");
        candidate.Title.Should().Be("Himouto! Umaru-chan");
        candidate.PosterUrl.Should().Be("https://img.test/poster-xl.jpg");
        candidate.BackdropUrl.Should().Be("https://img.test/backdrop.jpg");
        using var body = JsonDocument.Parse(transport.LastRequest!.Body!);
        var variables = body.RootElement.GetProperty("variables");
        variables.GetProperty("search").GetString().Should().Be("Himouto! Umaru-chan");
        variables.TryGetProperty("id", out _).Should().BeFalse();
        variables.TryGetProperty("idMal", out _).Should().BeFalse();
    }

    [Fact]
    public async Task AniListTitleSearch_PrefersRomajiAndKeepsEnglishAsAlias()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new FixtureTransport("""
            {"data":{"Page":{"media":[{"id":21355,"idMal":31240,
              "title":{"romaji":"Re:Zero kara Hajimeru Isekai Seikatsu","english":"Re:ZERO -Starting Life in Another World-","native":"Re:ゼロから始める異世界生活"},
              "synonyms":[],"seasonYear":2016,
              "coverImage":{"extraLarge":"https://img.test/re-zero.jpg"},
              "bannerImage":"https://img.test/re-zero-banner.jpg","siteUrl":"https://anilist.co/anime/21355"}]}}}
            """);
        var provider = new AniListVideoMetadataProvider(transport);
        var query = new VideoMetadataSearchQuery(
            "re zero", VideoMetadataMediaKind.Anime, null, null, null, null,
            "en-US", "US", ImmutableDictionary<string, string>.Empty);

        var candidate = (await provider.SearchAsync(query, ct)).Should().ContainSingle().Subject;

        candidate.Title.Should().Be("Re:Zero kara Hajimeru Isekai Seikatsu");
        candidate.OriginalTitle.Should().Be("Re:ゼロから始める異世界生活");
        candidate.Aliases.Should().Contain("Re:ZERO -Starting Life in Another World-");
    }

    [Fact]
    public async Task AniListArtwork_ProvidesPortraitPosterAndLandscapeBackdrop()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new FixtureTransport("""
            {"data":{"Media":{
              "coverImage":{"extraLarge":"https://img.test/poster-xl.jpg","large":"https://img.test/poster.jpg","medium":"https://img.test/poster-small.jpg"},
              "bannerImage":"https://img.test/backdrop.jpg",
              "siteUrl":"https://anilist.co/anime/20987"}}}
            """);
        var provider = new AniListVideoMetadataProvider(transport);
        var candidate = new VideoMetadataCandidate(
            "anilist", "20987", VideoMetadataMediaKind.Anime, "干物妹！うまるちゃん", null,
            2015, null, null, null, ["Himouto! Umaru-chan"],
            ImmutableDictionary<string, string>.Empty.Add("anilist", "20987"),
            "https://anilist.co/anime/20987");

        var artwork = await provider.GetArtworkAsync(candidate, ct);

        artwork.Should().Contain(item => item.Kind == "poster"
                                         && item.Url == "https://img.test/poster-xl.jpg");
        artwork.Should().Contain(item => item.Kind == "backdrop"
                                         && item.Url == "https://img.test/backdrop.jpg");
        provider.ArtworkEnabledByDefault.Should().BeTrue();
    }

    [Fact]
    public async Task Transport_DoesNotRetryAuthenticationFailures()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new SequenceHandler(HttpStatusCode.Unauthorized);
        var transport = new VideoMetadataTransport(
            new HttpClient(handler),
            TimeProvider.System,
            NullLogger<VideoMetadataTransport>.Instance);

        var response = await transport.SendAsync(new VideoMetadataRequest(
            "tmdb", HttpMethod.Get, new Uri("https://api.themoviedb.org/3/movie/1")), ct);

        response.StatusCode.Should().Be(401);
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task Transport_RespectsRetryAfterFor429()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new SequenceHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        var transport = new VideoMetadataTransport(
            new HttpClient(handler),
            TimeProvider.System,
            NullLogger<VideoMetadataTransport>.Instance);

        var response = await transport.SendAsync(new VideoMetadataRequest(
            "tvmaze", HttpMethod.Get, new Uri("https://api.tvmaze.com/search/shows?q=test")), ct);

        response.StatusCode.Should().Be(200);
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task LocalNfo_DisablesExternalEntitiesAndNeverChangesSidecars()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var media = Path.Combine(temp.Path, "Episode 01.mkv");
        var nfo = Path.Combine(temp.Path, "Episode 01.nfo");
        await File.WriteAllBytesAsync(media, [1, 2, 3], ct);
        await File.WriteAllTextAsync(nfo, "<!DOCTYPE x [<!ENTITY leak SYSTEM 'file:///c:/windows/win.ini'>]><episodedetails><title>&leak;</title></episodedetails>", ct);
        var before = await File.ReadAllBytesAsync(nfo, ct);
        var provider = new LocalVideoMetadataProvider();

        var action = () => provider.ReadAsync(media, temp.Path, ct);

        await action.Should().ThrowAsync<XmlException>();
        (await File.ReadAllBytesAsync(nfo, ct)).Should().Equal(before);
    }

    [Fact]
    public async Task LocalArtwork_EnumeratesControlledNamesInStablePriorityOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var media = Path.Combine(temp.Path, "Episode 01.mkv");
        await File.WriteAllBytesAsync(media, [1], ct);
        foreach (var name in new[]
                 {
                     "season01-poster.jpg", "backdrop.png", "folder.webp",
                     "Episode 01.jpeg", "unrelated.jpg",
                 })
            await File.WriteAllBytesAsync(Path.Combine(temp.Path, name), [1], ct);

        var metadata = await new LocalVideoMetadataProvider().ReadAsync(media, temp.Path, ct);

        metadata.ArtworkPaths.Select(Path.GetFileName).Should().Equal(
            "folder.webp", "backdrop.png", "season01-poster.jpg", "Episode 01.jpeg");
        metadata.Artwork.Should().SatisfyRespectively(
            item => item.Should().Be(new LocalVideoArtwork(
                Path.Combine(temp.Path, "folder.webp"), "poster", LocalVideoMetadataScope.Container)),
            item => item.Should().Be(new LocalVideoArtwork(
                Path.Combine(temp.Path, "backdrop.png"), "backdrop", LocalVideoMetadataScope.Container)),
            item => item.Should().Be(new LocalVideoArtwork(
                Path.Combine(temp.Path, "season01-poster.jpg"), "poster", LocalVideoMetadataScope.Season)),
            item => item.Should().Be(new LocalVideoArtwork(
                Path.Combine(temp.Path, "Episode 01.jpeg"), "thumb", LocalVideoMetadataScope.Episode)));
        metadata.ArtworkPaths.Should().NotContain(path => Path.GetFileName(path) == "unrelated.jpg");
    }

    [Fact]
    public async Task LocalMetadata_ReadsJellyfinSeriesSeasonAndEpisodeSidecarsWithScopedArtwork()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var seriesDirectory = Directory.CreateDirectory(Path.Combine(temp.Path, "Example Show")).FullName;
        var seasonDirectory = Directory.CreateDirectory(Path.Combine(seriesDirectory, "Season 01")).FullName;
        var media = Path.Combine(seasonDirectory, "Example Show S01E02.mkv");
        var seriesNfo = Path.Combine(seriesDirectory, "tvshow.nfo");
        var seasonNfo = Path.Combine(seasonDirectory, "season.nfo");
        var episodeNfo = Path.ChangeExtension(media, ".nfo");
        var seriesPoster = Path.Combine(seriesDirectory, "poster.jpg");
        var seasonPoster = Path.Combine(seasonDirectory, "poster.jpg");
        var episodeThumb = Path.Combine(seasonDirectory, "Example Show S01E02-thumb.jpg");
        await File.WriteAllBytesAsync(media, [1], ct);
        await File.WriteAllTextAsync(seriesNfo, """
            <tvshow>
              <title>Series title</title><originaltitle>Original series</originaltitle>
              <plot>Series overview</plot><year>2024</year><genre>Drama</genre>
              <tagline>Series tagline</tagline><mpaa>TV-14</mpaa><rating>8.25</rating>
              <status>Continuing</status><tag>Favourite</tag><studio>Example Studio</studio>
              <director>Series Director</director><actor><name>Series Actor</name></actor>
              <uniqueid type="tmdb">123</uniqueid>
            </tvshow>
            """, ct);
        await File.WriteAllTextAsync(seasonNfo, "<season><title>Season title</title><season>1</season></season>", ct);
        await File.WriteAllTextAsync(episodeNfo, """
            <episodedetails><title>Episode title</title><season>1</season><episode>2</episode>
              <plot>Episode overview</plot><actor><name>Episode Actor</name></actor>
            </episodedetails>
            """, ct);
        foreach (var path in new[] { seriesPoster, seasonPoster, episodeThumb })
            await File.WriteAllBytesAsync(path, [1], ct);

        var metadata = await new LocalVideoMetadataProvider().ReadAsync(media, temp.Path, ct);

        metadata.ContainerMetadata.Should().NotBeNull();
        metadata.ContainerMetadata!.Title.Should().Be("Series title");
        metadata.ContainerMetadata.OriginalTitle.Should().Be("Original series");
        metadata.ContainerMetadata.Genres.Should().Equal("Drama");
        metadata.ContainerMetadata.Actors.Should().Equal("Series Actor");
        metadata.ContainerMetadata.ExternalIds.Should().Contain("tmdb", "123");
        metadata.ContainerMetadata.Tagline.Should().Be("Series tagline");
        metadata.ContainerMetadata.OfficialRating.Should().Be("TV-14");
        metadata.ContainerMetadata.CommunityRating.Should().Be(8.25);
        metadata.ContainerMetadata.Status.Should().Be("Continuing");
        metadata.ContainerMetadata.Tags.Should().Equal("Favourite");
        metadata.ContainerMetadata.Studios.Should().Equal("Example Studio");
        metadata.ContainerMetadata.Directors.Should().Equal("Series Director");
        metadata.SeasonMetadata!.Title.Should().Be("Season title");
        metadata.EpisodeMetadata!.Title.Should().Be("Episode title");
        metadata.EpisodeMetadata.Actors.Should().Equal("Episode Actor");
        metadata.SourceFiles.Should().BeEquivalentTo(seriesNfo, seasonNfo, episodeNfo);
        metadata.Artwork.Should().BeEquivalentTo(new[]
        {
            new LocalVideoArtwork(seriesPoster, "poster", LocalVideoMetadataScope.Container),
            new LocalVideoArtwork(seasonPoster, "poster", LocalVideoMetadataScope.Season),
            new LocalVideoArtwork(episodeThumb, "thumb", LocalVideoMetadataScope.Episode),
        });
        metadata.PreferredAssetArtworkPath(isMovie: false).Should().BeNull();
        metadata.PreferredAssetArtworkPath(isMovie: true).Should().Be(seriesPoster);
    }

    [Fact]
    public async Task Transport_ReusesFreshCatalogCacheWithoutSecondNetworkRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        await using var repository = new SQLiteVideoCatalogRepository(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"),
            new NiratanJsonFileStore(),
            NullLogger<SQLiteVideoCatalogRepository>.Instance);
        await repository.InitializeAsync(ct);
        var handler = new CacheHandler(HttpStatusCode.OK);
        var transport = new VideoMetadataTransport(
            new HttpClient(handler),
            TimeProvider.System,
            NullLogger<VideoMetadataTransport>.Instance,
            repository);
        var request = new VideoMetadataRequest(
            "tmdb", HttpMethod.Get, new Uri("https://api.themoviedb.org/3/movie/1"));

        var first = await transport.SendAsync(request, ct);
        var second = await transport.SendAsync(request, ct);

        first.Content.Should().Equal(Encoding.UTF8.GetBytes("{\"title\":\"cached\"}"));
        second.Content.Should().Equal(first.Content);
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task Transport_CoalescesConcurrentIdenticalQueriesIntoOneNetworkRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        await using var repository = new SQLiteVideoCatalogRepository(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"),
            new NiratanJsonFileStore(),
            NullLogger<SQLiteVideoCatalogRepository>.Instance);
        await repository.InitializeAsync(ct);
        var handler = new DelayedCacheHandler();
        var transport = new VideoMetadataTransport(
            new HttpClient(handler), TimeProvider.System,
            NullLogger<VideoMetadataTransport>.Instance, repository);
        var request = new VideoMetadataRequest(
            "tmdb", HttpMethod.Get, new Uri("https://api.themoviedb.org/3/search/tv?query=same"));

        var responses = await Task.WhenAll(
            transport.SendAsync(request, ct),
            transport.SendAsync(request, ct),
            transport.SendAsync(request, ct));

        handler.RequestCount.Should().Be(1);
        responses.Should().OnlyContain(response => response.StatusCode == 200);
        responses.Select(response => response.Content)
            .Should().OnlyContain(content => content.SequenceEqual(responses[0].Content));
    }

    [Fact]
    public async Task ArtworkCache_ValidatesImageAndAtomicallyReusesStoredEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var cache = new VideoArtworkCache(temp.Path);
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0];

        var stored = await cache.StoreAsync(
            "https://image.tmdb.org/t/p/original/poster.png",
            new MemoryStream(png), "image/png", "\"v1\"", null, ct);
        var loaded = await cache.GetAsync(stored.Url, ct);
        var invalid = () => cache.StoreAsync(
            "https://image.tmdb.org/t/p/original/not-image",
            new MemoryStream([1, 2, 3, 4]), "text/plain", null, null, ct);

        loaded.Should().NotBeNull();
        loaded!.LocalPath.Should().Be(stored.LocalPath);
        File.Exists(stored.LocalPath).Should().BeTrue();
        await invalid.Should().ThrowAsync<InvalidDataException>();
        Directory.EnumerateFiles(temp.Path, "*.tmp").Should().BeEmpty();

        await cache.ClearAsync(ct);

        Directory.EnumerateFiles(temp.Path).Should().BeEmpty();
        (await cache.GetAsync(stored.Url, ct)).Should().BeNull();
    }

    private sealed class FixtureTransport(string json) : IVideoMetadataTransport
    {
        public VideoMetadataRequest? LastRequest { get; private set; }
        public Task<VideoMetadataResponse> SendAsync(VideoMetadataRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new VideoMetadataResponse(
                200, Encoding.UTF8.GetBytes(json), "application/json", null, null, DateTimeOffset.UtcNow, false));
        }
    }

    private sealed class AllSeasonsFixtureTransport : IVideoMetadataTransport
    {
        public int SeasonRequestCount { get; private set; }

        public Task<VideoMetadataResponse> SendAsync(
            VideoMetadataRequest request,
            CancellationToken ct = default)
        {
            var isSeason = request.Uri.AbsolutePath.Contains("/season/", StringComparison.OrdinalIgnoreCase);
            var json = isSeason
                ? BuildSeasonJson(++SeasonRequestCount)
                : BuildDetailsJson();
            return Task.FromResult(new VideoMetadataResponse(
                200,
                Encoding.UTF8.GetBytes(json),
                "application/json",
                null,
                null,
                DateTimeOffset.UtcNow,
                false));
        }

        private static string BuildDetailsJson() => JsonSerializer.Serialize(new
        {
            id = 123,
            name = "作品",
            original_name = "作品",
            first_air_date = "2020-01-01",
            seasons = Enumerable.Range(1, 25).Select(season => new
            {
                season_number = season,
                name = $"Season {season}",
                episode_count = 201,
                overview = (string?)null,
                air_date = (string?)null,
                poster_path = (string?)null,
            }),
        });

        private static string BuildSeasonJson(int seasonNumber) => JsonSerializer.Serialize(new
        {
            episodes = Enumerable.Range(1, 201).Select(episode => new
            {
                episode_number = episode,
                name = $"S{seasonNumber}E{episode}",
                still_path = (string?)null,
                overview = (string?)null,
                air_date = (string?)null,
                runtime = (int?)null,
            }),
        });
    }

    private sealed class CollapsedTvOrderFixtureTransport : IVideoMetadataTransport
    {
        public List<string> RequestPaths { get; } = [];

        public Task<VideoMetadataResponse> SendAsync(
            VideoMetadataRequest request,
            CancellationToken ct = default)
        {
            RequestPaths.Add(request.Uri.AbsolutePath);
            var json = request.Uri.AbsolutePath switch
            {
                "/3/tv/65942/episode_groups" => """
                    {"results":[
                      {"id":"re-zero-tv","name":"Seasons (TV)","type":7,"group_count":4,"episode_count":8},
                      {"id":"re-zero-dvd","name":"DVD","type":3,"group_count":6,"episode_count":8}
                    ]}
                    """,
                "/3/tv/episode_group/re-zero-tv" => BuildEpisodeGroupJson(),
                "/3/tv/65942/season/0" => """
                    {"episodes":[{"episode_number":1,"name":"Special","air_date":"2016-06-01"}]}
                    """,
                _ => """
                    {
                      "id":65942,"name":"Re:ゼロから始める異世界生活","first_air_date":"2016-04-04",
                      "seasons":[
                        {"season_number":0,"name":"Specials","episode_count":1},
                        {"season_number":1,"name":"第1期～第4期","episode_count":8}
                      ]
                    }
                    """,
            };
            return Task.FromResult(new VideoMetadataResponse(
                200,
                Encoding.UTF8.GetBytes(json),
                "application/json",
                null,
                null,
                DateTimeOffset.UtcNow,
                false));
        }

        private static string BuildEpisodeGroupJson() => JsonSerializer.Serialize(new
        {
            id = "re-zero-tv",
            name = "Seasons (TV)",
            type = 7,
            groups = Enumerable.Range(0, 4).Select(seasonIndex => new
            {
                id = $"season-{seasonIndex + 1}",
                name = $"Season {seasonIndex + 1}",
                order = seasonIndex,
                episodes = Enumerable.Range(1, 2).Select(episodeIndex => new
                {
                    id = (seasonIndex + 1) * 1000 + episodeIndex,
                    order = episodeIndex - 1,
                    episode_number = seasonIndex * 2 + episodeIndex,
                    season_number = 1,
                    name = $"S{seasonIndex + 1}E{episodeIndex}",
                    air_date = $"20{16 + seasonIndex}-01-0{episodeIndex}",
                    still_path = (string?)null,
                    overview = (string?)null,
                    runtime = 24,
                }),
            }),
        });
    }

    private sealed class AniDbTitleFixtureTransport : IVideoMetadataTransport
    {
        public int RequestCount { get; private set; }

        public Task<VideoMetadataResponse> SendAsync(
            VideoMetadataRequest request,
            CancellationToken ct = default)
        {
            RequestCount++;
            const string xml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <animetitles>
                  <anime aid="11370">
                    <title xml:lang="x-jat" type="main">Re:Zero kara Hajimeru Isekai Seikatsu</title>
                    <title xml:lang="ja" type="official">Re:ゼロから始める異世界生活</title>
                    <title xml:lang="en" type="official">Re:ZERO -Starting Life in Another World-</title>
                  </anime>
                </animetitles>
                """;
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
                gzip.Write(Encoding.UTF8.GetBytes(xml));
            return Task.FromResult(new VideoMetadataResponse(
                200,
                output.ToArray(),
                "application/gzip",
                null,
                null,
                DateTimeOffset.UtcNow,
                false));
        }
    }

    private sealed class FixtureCredentialStore(string token) : IVideoMetadataCredentialStore
    {
        public Task<string?> ReadAsync(string providerId, string secretName, CancellationToken ct = default) =>
            Task.FromResult<string?>(token);
        public Task WriteAsync(string providerId, string secretName, string value, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task DeleteAsync(string providerId, string secretName, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class SequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses = new(statuses);
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            var status = _statuses.Count > 0 ? _statuses.Dequeue() : HttpStatusCode.OK;
            var response = new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (status == HttpStatusCode.TooManyRequests)
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }
    }

    private sealed class CacheHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            var response = new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"title\":\"cached\"}")),
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        }
    }

    private sealed class DelayedCacheHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _requestCount);
            await Task.Delay(80, ct);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"title\":\"shared\"}")),
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"shared-v1\"");
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return response;
        }
    }
}
