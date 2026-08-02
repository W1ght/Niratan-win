using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Niratan.Models.Video;
using Niratan.Services.Storage;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class VideoMetadataCoordinatorConcurrencyTests
{
    [Fact]
    public async Task RefreshAsset_SearchesProvidersConcurrentlyButScoresInRouteOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var sourceId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var source = new VideoCatalogSourceSnapshot(
            sourceId, "Drama", @"C:\Drama", @"C:\Drama",
            VideoLibraryMediaType.JapaneseDramaTv, "ja-JP", "JP",
            ["tmdb", "tvmaze"], 0, DateTimeOffset.UtcNow, null, null);
        var asset = new VideoCatalogAssetSnapshot(
            assetId, @"C:\Drama\作品 S01E01.mkv", VideoMediaAssetKind.LocalFile,
            @"C:\Drama\作品 S01E01.mkv", "作品", "Drama", 1, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, VideoMediaAvailability.Available,
            1, 1, null, null, null, null, null, null, null, null, false, [], null, null,
            null, [sourceId], [], []);
        var snapshot = VideoCatalogSnapshot.Empty() with
        {
            Sources = [source],
            Assets = [asset],
        };
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        repository.Setup(item => item.ReplaceMatchCandidatesAsync(
                assetId, It.IsAny<IReadOnlyList<VideoMatchCandidateSnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        IReadOnlyList<VideoMetadataCandidate>? scoredCandidates = null;
        var matcher = new Mock<IVideoMetadataMatcher>();
        matcher.Setup(item => item.Score(
                It.IsAny<ParsedVideoIdentity>(),
                VideoMetadataMediaKind.Series,
                It.IsAny<IReadOnlyList<VideoMetadataCandidate>>()))
            .Callback<ParsedVideoIdentity, VideoMetadataMediaKind, IReadOnlyList<VideoMetadataCandidate>>(
                (_, _, candidates) => scoredCandidates = candidates.ToArray())
            .Returns([]);
        var concurrency = new SearchConcurrencyProbe();
        var coordinator = new VideoMetadataCoordinator(
            repository.Object,
            matcher.Object,
            [new DelayedSearchProvider("tmdb", 70, concurrency),
             new DelayedSearchProvider("tvmaze", 10, concurrency)],
            [],
            NullLogger<VideoMetadataCoordinator>.Instance);
        var progress = new List<VideoMetadataRefreshProgress>();
        coordinator.ProgressChanged += (_, item) => progress.Add(item);

        var result = await coordinator.RefreshAssetAsync(assetId, allowNetwork: true, ct);

        result.NeedsReview.Should().BeTrue();
        concurrency.Max.Should().Be(2);
        scoredCandidates!.Select(item => item.ProviderId).Should().Equal("tmdb", "tvmaze");
        progress.Should().Contain(item => item.Stage == VideoMetadataRefreshStage.Searching
                                          && item.CompletedProviders == 2);
        progress.Last().Stage.Should().Be(VideoMetadataRefreshStage.Completed);
    }

    [Fact]
    public async Task QueueSourceRefresh_RunsIndependentlyWithBoundedAssetConcurrencyAndPersistentProgress()
    {
        var ct = TestContext.Current.CancellationToken;
        var sourceId = Guid.NewGuid();
        var source = new VideoCatalogSourceSnapshot(
            sourceId, "Drama", @"C:\Drama", @"C:\Drama",
            VideoLibraryMediaType.JapaneseDramaTv, "ja-JP", "JP",
            ["tmdb"], 3, DateTimeOffset.UtcNow, null, null);
        var assets = Enumerable.Range(1, 4).Select(index => new VideoCatalogAssetSnapshot(
            Guid.NewGuid(), $@"C:\Drama\作品 S01E{index:00}.mkv", VideoMediaAssetKind.LocalFile,
            $@"C:\Drama\作品 S01E{index:00}.mkv", $"作品 {index}", "Drama", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            VideoMediaAvailability.Available, index, index, null, null, null, null, null,
            null, null, null, false, [], null, null, null, [sourceId], [], [])).ToImmutableArray();
        var snapshot = VideoCatalogSnapshot.Empty() with { Sources = [source], Assets = assets };
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        repository.Setup(item => item.BeginMetadataRefreshAsync(sourceId, 4, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        repository.Setup(item => item.UpdateMetadataRefreshAsync(
                It.IsAny<Guid>(), It.IsAny<VideoCatalogJobState>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(item => item.ReplaceMatchCandidatesAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyList<VideoMatchCandidateSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var matcher = new Mock<IVideoMetadataMatcher>();
        matcher.Setup(item => item.Score(
                It.IsAny<ParsedVideoIdentity>(), It.IsAny<VideoMetadataMediaKind>(),
                It.IsAny<IReadOnlyList<VideoMetadataCandidate>>()))
            .Returns([]);
        var concurrency = new SearchConcurrencyProbe();
        var coordinator = new VideoMetadataCoordinator(
            repository.Object, matcher.Object,
            [new DelayedSearchProvider("tmdb", 80, concurrency)], [],
            NullLogger<VideoMetadataCoordinator>.Instance);
        var completed = new TaskCompletionSource<VideoMetadataBatchProgress>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.BatchProgressChanged += (_, progress) =>
        {
            if (progress.State == VideoCatalogJobState.Completed)
                completed.TrySetResult(progress);
        };

        await coordinator.QueueSourceRefreshAsync(sourceId, forceRefresh: false, ct);

        coordinator.ActiveBatchProgress.Should().ContainSingle(item => item.State == VideoCatalogJobState.Running);
        var final = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        final.ProcessedCount.Should().Be(4);
        final.NeedsReviewCount.Should().Be(4);
        concurrency.Max.Should().Be(2);
        repository.Verify(item => item.UpdateMetadataRefreshAsync(
            It.IsAny<Guid>(), VideoCatalogJobState.Completed, 4,
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueueSourceRefresh_UsesCompletedJobAsNegativeCacheForUnchangedUnmatchedAssets()
    {
        var ct = TestContext.Current.CancellationToken;
        var sourceId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var completedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var snapshot = VideoCatalogSnapshot.Empty() with
        {
            Sources = [new VideoCatalogSourceSnapshot(
                sourceId, "Anime", @"C:\Anime", @"C:\Anime", VideoLibraryMediaType.Anime,
                "ja-JP", "JP", ["anilist"], 1, completedAt.AddDays(-1), completedAt, null)],
            Assets = [new VideoCatalogAssetSnapshot(
                assetId, @"C:\Anime\作品 01.mkv", VideoMediaAssetKind.LocalFile,
                @"C:\Anime\作品 01.mkv", "作品 01", "Anime", 1,
                completedAt.AddDays(-1), completedAt.AddDays(-2), completedAt,
                VideoMediaAvailability.Available, 1, 1, null, null, null, null, null,
                null, null, null, false, [], null, null, null, [sourceId], [], [])],
            Jobs = [new VideoCatalogJobSnapshot(
                Guid.NewGuid(), sourceId, VideoCatalogJobKind.MetadataRefresh,
                VideoCatalogJobState.Completed, 1, 1, 1, null,
                completedAt.AddMinutes(-2), completedAt)],
        };
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        var coordinator = new VideoMetadataCoordinator(
            repository.Object, Mock.Of<IVideoMetadataMatcher>(), [], [],
            NullLogger<VideoMetadataCoordinator>.Instance);

        await coordinator.QueueSourceRefreshAsync(sourceId, forceRefresh: false, ct);

        repository.Verify(item => item.BeginMetadataRefreshAsync(
            sourceId, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(item => item.ReplaceMatchCandidatesAsync(
            It.IsAny<Guid>(), It.IsAny<IReadOnlyList<VideoMatchCandidateSnapshot>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EpisodeRefresh_SearchesWithSeriesTitleAndKeepsEpisodeEvidence()
    {
        var ct = TestContext.Current.CancellationToken;
        var sourceId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var source = new VideoCatalogSourceSnapshot(
            sourceId, "Anime", @"C:\Anime", @"C:\Anime", VideoLibraryMediaType.Anime,
            "ja-JP", "JP", ["anilist"], 1, DateTimeOffset.UtcNow, null, null);
        var series = new VideoCatalogNodeSnapshot(
            seriesId, null, VideoCatalogNodeKind.Series, "Himouto! Umaru chan", null,
            null, null, 2015, null, null, null, false, false, [],
            ImmutableDictionary<string, string>.Empty);
        var episode = new VideoCatalogNodeSnapshot(
            episodeId, seriesId, VideoCatalogNodeKind.Episode, "Episode 8", null,
            null, null, null, null, 8, 8, false, false, [],
            ImmutableDictionary<string, string>.Empty);
        var asset = new VideoCatalogAssetSnapshot(
            assetId, @"C:\Anime\Himouto - 08.mkv", VideoMediaAssetKind.LocalFile,
            @"C:\Anime\Himouto - 08.mkv", "Himouto! Umaru chan", "Anime", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            VideoMediaAvailability.Available, 8, 8, null, null, null, null, null, null,
            null, null, false, [], null, null, null, [sourceId], [episodeId], []);
        var snapshot = VideoCatalogSnapshot.Empty() with
        {
            Sources = [source], Assets = [asset], Nodes = [series, episode],
        };
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        repository.Setup(item => item.ReplaceMatchCandidatesAsync(
                assetId, It.IsAny<IReadOnlyList<VideoMatchCandidateSnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        ParsedVideoIdentity? parsedIdentity = null;
        var matcher = new Mock<IVideoMetadataMatcher>();
        matcher.Setup(item => item.Score(
                It.IsAny<ParsedVideoIdentity>(), VideoMetadataMediaKind.Anime,
                It.IsAny<IReadOnlyList<VideoMetadataCandidate>>()))
            .Callback<ParsedVideoIdentity, VideoMetadataMediaKind, IReadOnlyList<VideoMetadataCandidate>>(
                (parsed, _, _) => parsedIdentity = parsed)
            .Returns([]);
        var provider = new RecordingSearchProvider();
        var coordinator = new VideoMetadataCoordinator(
            repository.Object, matcher.Object, [provider], [],
            NullLogger<VideoMetadataCoordinator>.Instance);

        await coordinator.RefreshAssetAsync(assetId, allowNetwork: true, ct);

        provider.Query!.Title.Should().Be("Himouto! Umaru chan");
        provider.Query.EpisodeNumber.Should().Be(8);
        parsedIdentity!.NormalizedTitle.Should().Be("Himouto! Umaru chan");
        parsedIdentity.EpisodeStart.Should().Be(8);
    }

    private sealed class SearchConcurrencyProbe
    {
        private int _active;
        private int _max;
        public int Max => Volatile.Read(ref _max);
        public void Enter()
        {
            var active = Interlocked.Increment(ref _active);
            var observed = Volatile.Read(ref _max);
            while (active > observed)
            {
                var original = Interlocked.CompareExchange(ref _max, active, observed);
                if (original == observed)
                    break;
                observed = original;
            }
        }
        public void Exit() => Interlocked.Decrement(ref _active);
    }

    private sealed class RecordingSearchProvider : IVideoMetadataSearchProvider
    {
        public string Id => "anilist";
        public string DisplayName => "AniList";
        public VideoMetadataCapabilities Capabilities => VideoMetadataCapabilities.Search;
        public IReadOnlySet<VideoMetadataMediaKind> SupportedMediaKinds { get; } =
            new HashSet<VideoMetadataMediaKind>(Enum.GetValues<VideoMetadataMediaKind>());
        public bool ArtworkEnabledByDefault => false;
        public string? AttributionUrl => null;
        public VideoMetadataSearchQuery? Query { get; private set; }

        public Task<IReadOnlyList<VideoMetadataCandidate>> SearchAsync(
            VideoMetadataSearchQuery query,
            CancellationToken ct = default)
        {
            Query = query;
            return Task.FromResult<IReadOnlyList<VideoMetadataCandidate>>([]);
        }
    }

    private sealed class DelayedSearchProvider(
        string id,
        int delayMilliseconds,
        SearchConcurrencyProbe probe) : IVideoMetadataSearchProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public VideoMetadataCapabilities Capabilities => VideoMetadataCapabilities.Search;
        public IReadOnlySet<VideoMetadataMediaKind> SupportedMediaKinds { get; } =
            new HashSet<VideoMetadataMediaKind>(Enum.GetValues<VideoMetadataMediaKind>());
        public bool ArtworkEnabledByDefault => false;
        public string? AttributionUrl => null;

        public async Task<IReadOnlyList<VideoMetadataCandidate>> SearchAsync(
            VideoMetadataSearchQuery query,
            CancellationToken ct = default)
        {
            probe.Enter();
            try
            {
                await Task.Delay(delayMilliseconds, ct);
                return [new VideoMetadataCandidate(
                    id, id + "-1", query.MediaKind, query.Title, null, null,
                    query.SeasonNumber, query.EpisodeNumber, query.AbsoluteEpisodeNumber,
                    [], ImmutableDictionary<string, string>.Empty, null)];
            }
            finally
            {
                probe.Exit();
            }
        }
    }
}
