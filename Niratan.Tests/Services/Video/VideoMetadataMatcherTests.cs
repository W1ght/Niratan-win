using System.Collections.Immutable;
using FluentAssertions;
using Niratan.Models.Video;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class VideoMetadataMatcherTests
{
    private readonly VideoMetadataMatcher _matcher = new();

    [Fact]
    public void ExplicitExternalId_IsAcceptedAndIdentityLocked()
    {
        var parsed = Parsed("進撃の巨人", 2013, 1, ImmutableDictionary<string, string>.Empty.Add("tmdb", "1429"));
        var candidate = Candidate("tmdb", "1429", "Attack on Titan", 2013, 1);

        var match = _matcher.Score(parsed, VideoMetadataMediaKind.Anime, [candidate]).Single();

        match.IsAccepted.Should().BeTrue();
        match.IsIdentityLocked.Should().BeTrue();
        match.Evidence.Should().Contain("explicit external id");
    }

    [Fact]
    public void CloseSecondCandidate_StaysInReview()
    {
        var parsed = Parsed("リング", 1998, null, []);
        var results = _matcher.Score(parsed, VideoMetadataMediaKind.Movie,
        [
            Candidate("tmdb", "1", "リング", 1998, null, aliases: ["リング"]),
            Candidate("bangumi", "2", "リング", 1998, null, aliases: ["リング"]),
        ]);

        results.Should().OnlyContain(result => !result.IsAccepted);
    }

    [Fact]
    public void YearConflict_IsAHardConflict()
    {
        var result = _matcher.Score(
            Parsed("日本沈没", 1973, null, []),
            VideoMetadataMediaKind.Series,
            [Candidate("tmdb", "1", "日本沈没", 2021, null, VideoMetadataMediaKind.Series)])
            .Single();

        result.HasHardConflict.Should().BeTrue();
        result.IsAccepted.Should().BeFalse();
    }

    [Fact]
    public void ExactEpisodicSeries_ConfirmedByMultipleProviders_IsAccepted()
    {
        var results = _matcher.Score(
            Parsed("干物妹!うまるちゃん", null, 8, []),
            VideoMetadataMediaKind.Anime,
            [
                Candidate("anilist", "20987", "干物妹!うまるちゃん", 2015, null),
                Candidate("tmdb", "67126", "干物妹!うまるちゃん", 2015, null,
                    VideoMetadataMediaKind.Series),
            ]);

        results.Should().ContainSingle(result => result.IsAccepted);
        results.Single(result => result.IsAccepted).Evidence.Should()
            .Contain("confirmed by multiple providers");
    }

    [Fact]
    public void ExactEpisodicRemakesWithConflictingYears_StayInReview()
    {
        var results = _matcher.Score(
            Parsed("同名作品", null, 8, []),
            VideoMetadataMediaKind.Anime,
            [
                Candidate("anilist", "1", "同名作品", 2015, null),
                Candidate("tmdb", "2", "同名作品", 2023, null,
                    VideoMetadataMediaKind.Series),
            ]);

        results.Should().OnlyContain(result => !result.IsAccepted);
    }

    [Fact]
    public void AnimeJointMatch_UsesTmdbForRichDetailsAndArtwork()
    {
        var aniList = Candidate("anilist", "20987", "干物妹!うまるちゃん", 2015, null);
        var tmdb = Candidate("tmdb", "67126", "干物妹!うまるちゃん", 2015, null,
            VideoMetadataMediaKind.Series);
        var accepted = new VideoMetadataMatchScore(
            aniList, 1, 1, false, "joint", true, false);
        var tmdbScore = new VideoMetadataMatchScore(
            tmdb, 1, 1, false, "joint", false, false);

        var selected = VideoMetadataCoordinator.SelectPrimaryDetailsCandidate(
            VideoMetadataMediaKind.Anime, accepted, [accepted, tmdbScore]);

        selected.ProviderId.Should().Be("tmdb");
    }

    [Fact]
    public void AnimeSeasonScopedMatch_PrefersTmdbAcrossSeasonReleaseYears()
    {
        var aniList = Candidate(
            "anilist", "146065", "Mushoku Tensei: Jobless Reincarnation Season 2", 2023, null,
            season: 2);
        var tmdb = Candidate(
            "tmdb", "94796", "Mushoku Tensei: Jobless Reincarnation", 2021, null,
            VideoMetadataMediaKind.Series, season: 2);
        var accepted = new VideoMetadataMatchScore(
            aniList, 1, 1, false, "joint", true, false);
        var tmdbScore = new VideoMetadataMatchScore(
            tmdb, 0.82, 0.88, false, "season-scoped title", false, false);

        var selected = VideoMetadataCoordinator.SelectPrimaryDetailsCandidate(
            VideoMetadataMediaKind.Anime, accepted, [accepted, tmdbScore]);

        selected.ProviderId.Should().Be("tmdb");
    }

    [Fact]
    public void SeasonScopedSeriesMatch_AlsoPrefersTmdbAcrossSeasonReleaseYears()
    {
        var aniList = Candidate(
            "anilist", "146065", "Mushoku Tensei: Jobless Reincarnation Season 2", 2023, null,
            season: 2);
        var tmdb = Candidate(
            "tmdb", "94796", "Mushoku Tensei: Jobless Reincarnation", 2021, null,
            VideoMetadataMediaKind.Series, season: 2);
        var accepted = new VideoMetadataMatchScore(
            aniList, 1, 1, false, "joint", true, false);
        var tmdbScore = new VideoMetadataMatchScore(
            tmdb, 0.82, 0.88, false, "season-scoped title", false, false);

        var selected = VideoMetadataCoordinator.SelectPrimaryDetailsCandidate(
            VideoMetadataMediaKind.Series, accepted, [accepted, tmdbScore]);

        selected.ProviderId.Should().Be("tmdb");
    }

    [Fact]
    public void AnimeJointMatch_FallsBackToAniListDetailsWhenTmdbIsUnavailable()
    {
        var aniDb = Candidate("anidb", "10972", "干物妹!うまるちゃん", 2015, null);
        var aniList = Candidate("anilist", "20987", "干物妹!うまるちゃん", 2015, null);
        var accepted = new VideoMetadataMatchScore(
            aniDb, 1, 1, false, "joint", true, false);
        var aniListScore = new VideoMetadataMatchScore(
            aniList, 1, 1, false, "joint", false, false);

        var selected = VideoMetadataCoordinator.SelectPrimaryDetailsCandidate(
            VideoMetadataMediaKind.Anime, accepted, [accepted, aniListScore]);

        selected.ProviderId.Should().Be("anilist");
        selected.ExternalIds.Should().Contain("anidb", "10972");
    }

    [Fact]
    public void LockedAniDbIdentity_StillUsesExactAniListCandidateForRichDetails()
    {
        var aniDb = Candidate("anidb", "10972", "干物妹!うまるちゃん", 2015, null);
        var aniList = Candidate("anilist", "20987", "干物妹!うまるちゃん", 2015, null);
        var accepted = new VideoMetadataMatchScore(
            aniDb, 1, 1, false, "explicit external id", true, true);
        var aniListScore = new VideoMetadataMatchScore(
            aniList, 1, 1, false, "exact compatible alias", false, false);

        var selected = VideoMetadataCoordinator.SelectPrimaryDetailsCandidate(
            VideoMetadataMediaKind.Anime, accepted, [accepted, aniListScore]);

        selected.ProviderId.Should().Be("anilist");
        selected.ExternalIds.Should().Contain("anidb", "10972");
        selected.ExternalIds.Should().Contain("anilist", "20987");
    }

    [Fact]
    public void AutoSource_WithAniDbIdentity_UsesAnimeProviderRoute()
    {
        var kind = VideoMetadataCoordinator.ResolveMediaKind(
            VideoLibraryMediaType.Auto,
            hasAnimeExternalIdentity: true,
            hasAbsoluteEpisodeEvidence: false,
            hasSeriesHierarchyEvidence: false,
            hasEpisodeEvidence: true,
            hasYearEvidence: false);

        kind.Should().Be(VideoMetadataMediaKind.Anime);
    }

    [Fact]
    public void RouteEvidence_IncludesSeriesIdentityFromEpisodeParentChain()
    {
        var seriesId = Guid.NewGuid();
        var series = new VideoCatalogNodeSnapshot(
            seriesId, null, VideoCatalogNodeKind.Series, "Himouto! Umaru-chan",
            null, null, null, null, null, null, null, false, true, [],
            ImmutableDictionary<string, string>.Empty.Add("anidb", "10972"));
        var episode = new VideoCatalogNodeSnapshot(
            Guid.NewGuid(), seriesId, VideoCatalogNodeKind.Episode, "Episode 8",
            null, null, null, null, 1, 8, null, false, false, [],
            ImmutableDictionary<string, string>.Empty);
        var snapshot = VideoCatalogSnapshot.Empty() with { Nodes = [series, episode] };

        var evidence = VideoMetadataCoordinator.CollectRouteEvidenceNodes(snapshot, [episode]);

        evidence.Should().Contain(node => node.Id == seriesId
                                          && node.ExternalIds["anidb"] == "10972");
    }

    private static ParsedVideoIdentity Parsed(
        string title,
        int? year,
        int? episode,
        ImmutableDictionary<string, string> ids) =>
        new(title, title, null, year, episode.HasValue ? 1 : null, episode, episode,
            null, null, null, ParsedVideoSpecialKind.None, false, episode.HasValue, ids, []);

    private static VideoMetadataCandidate Candidate(
        string provider,
        string id,
        string title,
        int? year,
        int? episode,
        VideoMetadataMediaKind kind = VideoMetadataMediaKind.Anime,
        ImmutableArray<string> aliases = default,
        int? season = null) =>
        new(provider, id, kind, title, null, year, season ?? (episode.HasValue ? 1 : null), episode, null,
            aliases.IsDefault ? [title] : aliases,
            ImmutableDictionary<string, string>.Empty.Add(provider, id), null);
}
