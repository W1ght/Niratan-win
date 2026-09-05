using System.Collections.Concurrent;
using System.Collections.Immutable;
using FluentAssertions;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;
using Niratan.Models.Video;
using Niratan.Services.Nyaa;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class VideoResourceSearchServiceTests
{
    [Fact]
    public void BuildSearchQueries_PrefersOriginalAndAliasTitles()
    {
        var service = new VideoResourceSearchService(new FixtureNyaaClient());
        var identity = CreateIdentity();

        service.BuildDefaultQuery(identity).Should().Be("Moana 2026");
        service.BuildSearchQueries(identity).Should().ContainInOrder(
            "Moana 2026", "Moana and the ... 2026");
        service.BuildSearchQueries(identity).Should().NotContain("モアナ 2026");
    }

    [Fact]
    public void BuildSearchQueries_PrefersLatinTitlesWhenLocalTitleIsPrimary()
    {
        var service = new VideoResourceSearchService(new FixtureNyaaClient());
        var identity = new VideoMetadataCandidate(
            "tmdb", "123", VideoMetadataMediaKind.Movie, "モアナ", "原題",
            2026, null, null, null,
            ["モアナ", "Moana", "Moana and the ..."],
            ImmutableDictionary<string, string>.Empty,
            null);

        service.BuildSearchQueries(identity).Should().ContainInOrder(
            "Moana 2026", "Moana and the ... 2026");
        service.BuildSearchQueries(identity).Should().NotContain(query => query.Contains('モ') || query.Contains('原'));
    }

    [Fact]
    public void BuildDefaultQuery_UsesLatinAliasAndOmitsSeriesAirYear()
    {
        var service = new VideoResourceSearchService(new FixtureNyaaClient());
        var identity = new VideoMetadataCandidate(
            "bangumi", "123", VideoMetadataMediaKind.Anime,
            "無職転生Ⅱ ～異世界行ったら本気だす～", "無職転生Ⅱ ～異世界行ったら本気だす～",
            2023, null, null, null,
            ["Mushoku Tensei II: Isekai Ittara Honki Dasu"],
            ImmutableDictionary<string, string>.Empty,
            null);

        service.BuildDefaultQuery(identity)
            .Should().Be("Mushoku Tensei II: Isekai Ittara Honki Dasu");
    }

    [Fact]
    public void BuildSubtitleQuery_AddsSubtitleFileHint()
    {
        var service = new VideoResourceSearchService(new FixtureNyaaClient());

        service.BuildSubtitleQuery(CreateIdentity()).Should().Be("Moana 2026 srt");
    }

    [Fact]
    public async Task SearchAsync_MergesAndDeduplicatesNyaaFriendlyQueries()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FixtureNyaaClient();
        var service = new VideoResourceSearchService(client);

        var result = await service.SearchAsync(new VideoResourceSearchRequest(CreateIdentity()), ct);

        result.IsSuccess.Should().BeTrue();
        client.Queries.Should().Contain("Moana 2026");
        client.Queries.Should().NotContain("モアナ 2026");
        result.Value.Should().HaveCount(1);
    }

    private static VideoMetadataCandidate CreateIdentity() => new(
        "tmdb", "123", VideoMetadataMediaKind.Movie, "モアナ", "Moana", 2026,
        null, null, null,
        ImmutableArray.Create("Moana and the ..."),
        ImmutableDictionary<string, string>.Empty,
        null);

    private sealed class FixtureNyaaClient : INyaaClient
    {
        public ConcurrentBag<string> Queries { get; } = [];

        public Task<Result<IReadOnlyList<NyaaTorrentItem>>> SearchAsync(
            NyaaSearchRequest request,
            CancellationToken ct = default)
        {
            Queries.Add(request.Query);
            var item = new NyaaTorrentItem(
                "fixture", request.Query, new Uri("https://nyaa.si/download/fixture.torrent"),
                new Uri("https://nyaa.si/view/fixture"), "Anime", 1, 10, 1, 2,
                DateTimeOffset.UtcNow, true, false);
            return Task.FromResult(Result<IReadOnlyList<NyaaTorrentItem>>.Success([item]));
        }
    }
}
