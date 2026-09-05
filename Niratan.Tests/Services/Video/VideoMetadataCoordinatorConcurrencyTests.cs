using System.Collections.Concurrent;
using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Niratan.Models.Settings;
using Moq;
using Niratan.Models.Video;
using Niratan.Services.Settings;
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
    public async Task RefreshAsset_FileMatchedByAniDb_UsesExactAnimeRouteAndWaitsForAnimeEntity()
    {
        var ct = TestContext.Current.CancellationToken;
        var sourceId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var source = new VideoCatalogSourceSnapshot(
            sourceId, "Auto", @"C:\Anime", @"C:\Anime",
            VideoLibraryMediaType.Auto, "ja-JP", "JP",
            [], 0, DateTimeOffset.UtcNow, null, null);
        var asset = new VideoCatalogAssetSnapshot(
            assetId, @"C:\Anime\re0\S04E01.mkv", VideoMediaAssetKind.LocalFile,
            @"C:\Anime\re0\S04E01.mkv", "re0", "Anime", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            VideoMediaAvailability.Available, 1, 1, null, null, null, null, null,
            null, null, null, false, [], null, null, null, [sourceId], [], []);
        var snapshot = VideoCatalogSnapshot.Empty() with { Sources = [source], Assets = [asset] };
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        repository.Setup(item => item.ReplaceMatchCandidatesAsync(
                assetId,
                It.IsAny<IReadOnlyList<VideoMatchCandidateSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var match = new AniDbFileMatch(
            301, 19242, null, null, null, false, 1, null, null, false,
            null, null, [], [], null, null, null,
            [new AniDbFileEpisodeLink(1001, 100, false, 0) { AnimeId = 19242 }]);
        var aniDbStore = new Mock<IAniDbCatalogStore>();
        aniDbStore.Setup(item => item.GetAssetAsync(assetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AniDbAssetSnapshot(
                assetId, "31d6cfe0d16ae931b73c59d7e0c089c0", 1,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, match, null, null));
        var candidate = new VideoMetadataCandidate(
            "anidb", "19242", VideoMetadataMediaKind.Anime, "Re:ZERO Season 4",
            null, 2026, null, 1, null, [],
            ImmutableDictionary<string, string>.Empty.Add("anidb", "19242"),
            "https://anidb.net/anime/19242");
        var provider = new FixtureMetadataProvider("anidb", [candidate], null, []);
        var coordinator = new VideoMetadataCoordinator(
            repository.Object,
            new VideoMetadataMatcher(),
            [provider],
            [provider],
            NullLogger<VideoMetadataCoordinator>.Instance,
            null,
            [],
            null,
            null,
            null,
            aniDbStore.Object);

        var result = await coordinator.RefreshAssetAsync(assetId, allowNetwork: true, ct);

        result.Matched.Should().BeFalse();
        result.NeedsReview.Should().BeTrue();
        result.ProviderId.Should().Be("anidb");
        result.Error.Should().Contain("metadata is still pending");
        repository.Verify(item => item.ApplyMetadataMatchAsync(
            It.IsAny<Guid>(),
            It.IsAny<VideoMetadataCandidate>(),
            It.IsAny<VideoMetadataDetails?>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshAsset_AutoSourcePendingAniDb_DoesNotRunGenericProviders()
    {
        var ct = TestContext.Current.CancellationToken;
        var sourceId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var source = new VideoCatalogSourceSnapshot(
            sourceId, "Auto", @"C:\Media", @"C:\Media",
            VideoLibraryMediaType.Auto, "ja-JP", "JP",
            [], 0, DateTimeOffset.UtcNow, null, null);
        var asset = new VideoCatalogAssetSnapshot(
            assetId, @"C:\Media\Show S01E01.mkv", VideoMediaAssetKind.LocalFile,
            @"C:\Media\Show S01E01.mkv", "Show", "", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            VideoMediaAvailability.Available, 1, 1, null, null, null, null, null,
            null, null, null, false, [], null, null, null, [sourceId], [], []);
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(VideoCatalogSnapshot.Empty() with { Sources = [source], Assets = [asset] });
        var store = new Mock<IAniDbCatalogStore>();
        store.Setup(item => item.GetAssetAsync(assetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AniDbAssetSnapshot?)null);
        var import = new Mock<IAniDbImportService>();
        var tmdb = new RecordingSearchProvider("tmdb");
        var coordinator = new VideoMetadataCoordinator(
            repository.Object,
            Mock.Of<IVideoMetadataMatcher>(),
            [tmdb],
            [],
            NullLogger<VideoMetadataCoordinator>.Instance,
            null,
            [],
            null,
            null,
            import.Object,
            store.Object);

        var result = await coordinator.RefreshAssetAsync(assetId, allowNetwork: true, ct);

        result.Error.Should().Contain("identification is still pending");
        tmdb.Query.Should().BeNull();
        import.Verify(item => item.QueueAssetAsync(
            assetId, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.ReplaceMatchCandidatesAsync(
            It.IsAny<Guid>(),
            It.IsAny<IReadOnlyList<VideoMatchCandidateSnapshot>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshAsset_AutoSourceUnrecognizedByAniDb_AllowsGenericFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var sourceId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var source = new VideoCatalogSourceSnapshot(
            sourceId, "Auto", @"C:\Media", @"C:\Media",
            VideoLibraryMediaType.Auto, "ja-JP", "JP",
            [], 0, DateTimeOffset.UtcNow, null, null);
        var asset = new VideoCatalogAssetSnapshot(
            assetId, @"C:\Media\Drama S01E01.mkv", VideoMediaAssetKind.LocalFile,
            @"C:\Media\Drama S01E01.mkv", "Drama", "", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            VideoMediaAvailability.Available, 1, 1, null, null, null, null, null,
            null, null, null, false, [], null, null, null, [sourceId], [], []);
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(VideoCatalogSnapshot.Empty() with { Sources = [source], Assets = [asset] });
        repository.Setup(item => item.ReplaceMatchCandidatesAsync(
                assetId,
                It.IsAny<IReadOnlyList<VideoMatchCandidateSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var store = new Mock<IAniDbCatalogStore>();
        const string hash = "31d6cfe0d16ae931b73c59d7e0c089c0";
        store.Setup(item => item.GetAssetAsync(assetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AniDbAssetSnapshot(
                assetId, hash, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                null, null, null));
        store.Setup(item => item.GetReleaseStateAsync(hash, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AniDbReleaseState(
                hash, 1, AniDbReleaseStatus.Unrecognized, null,
                DateTimeOffset.UtcNow.AddDays(30), false, null, DateTimeOffset.UtcNow));
        var tmdb = new RecordingSearchProvider("tmdb");
        var matcher = new Mock<IVideoMetadataMatcher>();
        matcher.Setup(item => item.Score(
                It.IsAny<ParsedVideoIdentity>(),
                VideoMetadataMediaKind.Series,
                It.IsAny<IReadOnlyList<VideoMetadataCandidate>>()))
            .Returns([]);
        var coordinator = new VideoMetadataCoordinator(
            repository.Object,
            matcher.Object,
            [tmdb],
            [],
            NullLogger<VideoMetadataCoordinator>.Instance,
            null,
            [],
            null,
            null,
            null,
            store.Object);

        await coordinator.RefreshAssetAsync(assetId, allowNetwork: true, ct);

        tmdb.Query.Should().NotBeNull();
        tmdb.Query!.MediaKind.Should().Be(VideoMetadataMediaKind.Series);
    }

    [Fact]
    public async Task AniDbIdentificationSettled_RefreshesOnlyThatAssetThroughResolvedRoute()
    {
        var ct = TestContext.Current.CancellationToken;
        var sourceId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var source = new VideoCatalogSourceSnapshot(
            sourceId, "Auto", @"C:\Media", @"C:\Media",
            VideoLibraryMediaType.Auto, "ja-JP", "JP",
            [], 0, DateTimeOffset.UtcNow, null, null);
        var asset = new VideoCatalogAssetSnapshot(
            assetId, @"C:\Media\Drama S01E01.mkv", VideoMediaAssetKind.LocalFile,
            @"C:\Media\Drama S01E01.mkv", "Drama", "", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            VideoMediaAvailability.Available, 1, 1, null, null, null, null, null,
            null, null, null, false, [], null, null, null, [sourceId], [], []);
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(VideoCatalogSnapshot.Empty() with { Sources = [source], Assets = [asset] });
        repository.Setup(item => item.ReplaceMatchCandidatesAsync(
                assetId,
                It.IsAny<IReadOnlyList<VideoMatchCandidateSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var store = new Mock<IAniDbCatalogStore>();
        const string hash = "31d6cfe0d16ae931b73c59d7e0c089c0";
        store.Setup(item => item.GetAssetAsync(assetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AniDbAssetSnapshot(
                assetId, hash, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                null, null, null));
        store.Setup(item => item.GetReleaseStateAsync(hash, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AniDbReleaseState(
                hash, 1, AniDbReleaseStatus.Unrecognized, null,
                DateTimeOffset.UtcNow.AddDays(30), false, null, DateTimeOffset.UtcNow));
        var import = new Mock<IAniDbImportService>();
        var tmdb = new RecordingSearchProvider("tmdb");
        var matcher = new Mock<IVideoMetadataMatcher>();
        matcher.Setup(item => item.Score(
                It.IsAny<ParsedVideoIdentity>(),
                VideoMetadataMediaKind.Series,
                It.IsAny<IReadOnlyList<VideoMetadataCandidate>>()))
            .Returns([]);
        _ = new VideoMetadataCoordinator(
            repository.Object,
            matcher.Object,
            [tmdb],
            [],
            NullLogger<VideoMetadataCoordinator>.Instance,
            null,
            [],
            null,
            null,
            import.Object,
            store.Object);

        import.Raise(
            item => item.AssetIdentificationSettled += null,
            import.Object,
            new AniDbAssetIdentificationSettledEventArgs(
                assetId,
                AniDbAssetIdentificationResult.Unrecognized));

        var query = await tmdb.Searched.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        query.MediaKind.Should().Be(VideoMetadataMediaKind.Series);
        query.Title.Should().Contain("Drama");
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
    public async Task GetTaskHistory_MapsPersistedCountersAndMarksOrphanedJobsInterrupted()
    {
        var ct = TestContext.Current.CancellationToken;
        var sourceId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var job = new VideoCatalogJobSnapshot(
            jobId, sourceId, VideoCatalogJobKind.MetadataRefresh,
            VideoCatalogJobState.Running, 0, 2, 5, "partial error",
            now.AddMinutes(-2), now, 1, 1);
        var snapshot = VideoCatalogSnapshot.Empty() with { Jobs = [job] };
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => snapshot);
        repository.Setup(item => item.UpdateMetadataRefreshAsync(
                jobId, VideoCatalogJobState.Interrupted, 2,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, VideoCatalogJobState, int, string?, CancellationToken>(
                (_, state, processed, error, _) =>
                {
                    snapshot = snapshot with
                    {
                        Jobs = [job with
                        {
                            State = state,
                            ProcessedCount = processed,
                            Error = error,
                            UpdatedAt = DateTimeOffset.UtcNow,
                        }],
                    };
                })
            .Returns(Task.CompletedTask);
        var coordinator = new VideoMetadataCoordinator(
            repository.Object, Mock.Of<IVideoMetadataMatcher>(), [], [],
            NullLogger<VideoMetadataCoordinator>.Instance);

        var history = await coordinator.GetTaskHistoryAsync(ct: ct);

        history.Should().ContainSingle();
        history[0].State.Should().Be(VideoCatalogJobState.Interrupted);
        history[0].ProcessedCount.Should().Be(2);
        history[0].MatchedCount.Should().Be(1);
        history[0].NeedsReviewCount.Should().Be(1);
        history[0].Error.Should().Contain("stopped");
        repository.Verify(item => item.UpdateMetadataRefreshAsync(
            jobId, VideoCatalogJobState.Interrupted, 2,
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelTask_UpdatesPersistedQueuedTaskWithoutTouchingSourceMedia()
    {
        var ct = TestContext.Current.CancellationToken;
        var sourceId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var job = new VideoCatalogJobSnapshot(
            jobId, sourceId, VideoCatalogJobKind.MetadataRefresh,
            VideoCatalogJobState.Queued, 0, 1, 2, null,
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow,
            1, 0, 0);
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(VideoCatalogSnapshot.Empty() with { Jobs = [job] });
        repository.Setup(item => item.UpdateMetadataRefreshAsync(
                jobId, VideoCatalogJobState.Cancelled, 1,
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(item => item.UpdateMetadataRefreshCountsAsync(
                jobId, 1, 0, It.IsAny<CancellationToken>(), 0))
            .Returns(Task.CompletedTask);
        var coordinator = new VideoMetadataCoordinator(
            repository.Object, Mock.Of<IVideoMetadataMatcher>(), [], [],
            NullLogger<VideoMetadataCoordinator>.Instance);

        await coordinator.CancelTaskAsync(jobId, ct);

        repository.Verify(item => item.UpdateMetadataRefreshAsync(
            jobId, VideoCatalogJobState.Cancelled, 1,
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.UpdateMetadataRefreshCountsAsync(
            jobId, 1, 0, It.IsAny<CancellationToken>(), 0), Times.Once);
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
            ImmutableDictionary<string, string>.Empty.Add("anilist", "20987"));
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
        var provider = new RecordingSearchProvider("anidb");
        var coordinator = new VideoMetadataCoordinator(
            repository.Object, matcher.Object, [provider], [],
            NullLogger<VideoMetadataCoordinator>.Instance);

        await coordinator.RefreshAssetAsync(assetId, allowNetwork: true, ct);

        provider.Query!.Title.Should().Be("Himouto! Umaru chan");
        provider.Query.EpisodeNumber.Should().Be(8);
        provider.Query.ExternalIds.Should().Contain("anilist", "20987");
        parsedIdentity!.NormalizedTitle.Should().Be("Himouto! Umaru chan");
        parsedIdentity.EpisodeStart.Should().Be(8);
        parsedIdentity.ExternalIds.Should().BeEmpty(
            "provider-discovered IDs are query hints until the identity is explicitly locked");
    }

    [Fact]
    public async Task LockedSeries_OnlyUsesIndividuallyLockedExternalIdsAsExplicitEvidence()
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
            null, null, 2015, null, null, null, false, true, [],
            ImmutableDictionary<string, string>.Empty
                .Add("anilist", "20987")
                .Add("tmdb", "67126"))
        {
            IdentityLockedProviders = ImmutableHashSet.Create(
                StringComparer.OrdinalIgnoreCase, "anilist"),
        };
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
        var provider = new RecordingSearchProvider("anidb");
        var coordinator = new VideoMetadataCoordinator(
            repository.Object, matcher.Object, [provider], [],
            NullLogger<VideoMetadataCoordinator>.Instance);

        await coordinator.RefreshAssetAsync(assetId, allowNetwork: true, ct);

        provider.Query!.ExternalIds.Should().Contain("anilist", "20987");
        provider.Query.ExternalIds.Should().Contain("tmdb", "67126");
        parsedIdentity!.ExternalIds.Should().ContainSingle();
        parsedIdentity.ExternalIds.Should().Contain("anilist", "20987");
        parsedIdentity.ExternalIds.Should().NotContainKey("tmdb");
    }

    [Fact]
    public async Task UnnumberedSupplementalInAutoSource_RoutesThroughSeriesAncestor()
    {
        var ct = TestContext.Current.CancellationToken;
        var sourceId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var source = new VideoCatalogSourceSnapshot(
            sourceId, "Auto", @"C:\Library", @"C:\Library",
            VideoLibraryMediaType.Auto, "ja-JP", "JP", ["anilist"], 1,
            DateTimeOffset.UtcNow, null, null);
        var series = new VideoCatalogNodeSnapshot(
            seriesId, null, VideoCatalogNodeKind.Series, "作品", null, null, null,
            2024, null, null, null, false, false, [],
            ImmutableDictionary<string, string>.Empty);
        var season = new VideoCatalogNodeSnapshot(
            seasonId, seriesId, VideoCatalogNodeKind.Season, "Specials", null, null, null,
            null, 0, null, null, true, false, [],
            ImmutableDictionary<string, string>.Empty);
        var supplemental = new VideoCatalogNodeSnapshot(
            episodeId, seasonId, VideoCatalogNodeKind.Episode, "PV 01", null, null, null,
            null, 0, null, null, true, false, [],
            ImmutableDictionary<string, string>.Empty);
        var asset = new VideoCatalogAssetSnapshot(
            assetId, @"C:\Library\作品\PV\PV 01.mkv", VideoMediaAssetKind.LocalFile,
            @"C:\Library\作品\PV\PV 01.mkv", "作品", "作品", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            VideoMediaAvailability.Available, null, null, null, null, null, null, null,
            null, null, null, false, [], null, null, null, [sourceId], [episodeId], []);
        var snapshot = VideoCatalogSnapshot.Empty() with
        {
            Sources = [source], Nodes = [series, season, supplemental], Assets = [asset],
        };
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        repository.Setup(item => item.ReplaceMatchCandidatesAsync(
                assetId, It.IsAny<IReadOnlyList<VideoMatchCandidateSnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var matcher = new Mock<IVideoMetadataMatcher>();
        matcher.Setup(item => item.Score(
                It.IsAny<ParsedVideoIdentity>(), VideoMetadataMediaKind.Series,
                It.IsAny<IReadOnlyList<VideoMetadataCandidate>>()))
            .Returns([]);
        var provider = new RecordingSearchProvider();
        var coordinator = new VideoMetadataCoordinator(
            repository.Object, matcher.Object, [provider], [],
            NullLogger<VideoMetadataCoordinator>.Instance);

        await coordinator.RefreshAssetAsync(assetId, allowNetwork: true, ct);

        provider.Query.Should().NotBeNull();
        provider.Query!.MediaKind.Should().Be(VideoMetadataMediaKind.Series);
        provider.Query.Title.Should().Be("作品");
        matcher.Verify(item => item.Score(
            It.IsAny<ParsedVideoIdentity>(), VideoMetadataMediaKind.Series,
            It.IsAny<IReadOnlyList<VideoMetadataCandidate>>()), Times.Once);
    }

    [Fact]
    public void NeedsMetadata_UsesSeriesAncestorSnapshotForEpisodeAssets()
    {
        var now = DateTimeOffset.UtcNow;
        var seriesId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var asset = new VideoCatalogAssetSnapshot(
            Guid.NewGuid(), @"C:\Anime\Show S01E01.mkv", VideoMediaAssetKind.LocalFile,
            @"C:\Anime\Show S01E01.mkv", "Show", "Anime", 1,
            now.AddDays(-2), now.AddDays(-3), now.AddDays(-2),
            VideoMediaAvailability.Available, 1, 1, null, null, null, null, null,
            null, null, null, false, [], null, null, null, [], [episodeId], []);
        var series = new VideoCatalogNodeSnapshot(
            seriesId, null, VideoCatalogNodeKind.Series, "Show", null, null, null,
            2024, null, null, null, false, false, [],
            ImmutableDictionary<string, string>.Empty.Add("anilist", "42"),
            MetadataExpiresAt: now.AddDays(1));
        var episode = new VideoCatalogNodeSnapshot(
            episodeId, seriesId, VideoCatalogNodeKind.Episode, "Episode 1", null,
            null, null, null, 1, 1, null, false, false, [],
            ImmutableDictionary<string, string>.Empty);

        VideoMetadataCoordinator.NeedsMetadata(
                asset,
                new[] { series, episode }.ToDictionary(node => node.Id),
                now)
            .Should().BeFalse("the series metadata snapshot is still fresh");

        VideoMetadataCoordinator.NeedsMetadata(
                asset,
                new[] { series with { MetadataExpiresAt = now.AddMinutes(-1) }, episode }
                    .ToDictionary(node => node.Id),
                now)
            .Should().BeTrue("an expired series owner must refresh its episode-backed assets");

        VideoMetadataCoordinator.NeedsMetadata(
                asset,
                new[] { series with { MetadataExpiresAt = null }, episode }
                    .ToDictionary(node => node.Id),
                lastCompletedRefresh: null)
            .Should().BeTrue("an explicit ID without a fetched snapshot still needs its first refresh");

        VideoMetadataCoordinator.NeedsMetadata(
                asset with { CatalogResetPending = true },
                new[] { series with { MetadataExpiresAt = null }, episode }
                    .ToDictionary(node => node.Id),
                lastCompletedRefresh: null)
            .Should().BeFalse(
                "ordinary automatic metadata must not repopulate a catalog that was explicitly reset");
    }

    [Fact]
    public async Task AnimeArtworkRefresh_RetainsLinkedAniDbAndTmdbCandidatesWithBoundedSecondaryImages()
    {
        var ct = TestContext.Current.CancellationToken;
        var sourceId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var source = new VideoCatalogSourceSnapshot(
            sourceId, "Anime", @"C:\Anime", @"C:\Anime", VideoLibraryMediaType.Anime,
            "ja-JP", "JP", ["anidb", "tmdb"], 1, now, null, null);
        var series = new VideoCatalogNodeSnapshot(
            seriesId, null, VideoCatalogNodeKind.Series, "作品", "作品", null, null,
            2024, null, null, null, false, true, [],
            ImmutableDictionary<string, string>.Empty.Add("anidb", "1"))
        {
            IdentityLockedProviders = ImmutableHashSet.Create(
                StringComparer.OrdinalIgnoreCase, "anidb"),
        };
        var asset = new VideoCatalogAssetSnapshot(
            assetId, @"C:\Anime\作品 01.mkv", VideoMediaAssetKind.LocalFile,
            @"C:\Anime\作品 01.mkv", "作品", "Anime", 1,
            now, now, now, VideoMediaAvailability.Available, 1, 1,
            null, null, null, null, null, null, null, null, false, [], null, null,
            null, [sourceId], [seriesId], []);
        var snapshot = VideoCatalogSnapshot.Empty() with
        {
            Sources = [source],
            Nodes = [series],
            Assets = [asset],
        };
        var anidbCandidate = new VideoMetadataCandidate(
            "anidb", "1", VideoMetadataMediaKind.Anime, "作品", "作品", 2024,
            1, 1, 1, [], ImmutableDictionary<string, string>.Empty.Add("anidb", "1"),
            "https://anidb.net/anime/1");
        var tmdbCandidate = new VideoMetadataCandidate(
            "tmdb", "2", VideoMetadataMediaKind.Anime, "作品", "作品", 2024,
            1, 1, 1, [], ImmutableDictionary<string, string>.Empty.Add("tmdb", "2"),
            "https://www.themoviedb.org/tv/2");
        var tmdbPosters = Enumerable.Range(1, 10)
            .Select(index => new VideoArtworkCandidate(
                "tmdb", $"https://image.tmdb.org/t/p/original/poster-{index}.jpg", "poster",
                index == 1 ? "ja" : "en", 1000, 1500, tmdbCandidate.SourceUrl))
            .ToArray();
        var anidbProvider = new FixtureMetadataProvider(
            "anidb",
            [anidbCandidate],
            null,
            [new VideoArtworkCandidate(
                "anidb", "https://cdn.anidb.net/images/main/cover.jpg", "poster",
                "ja", 680, 1000, anidbCandidate.SourceUrl)]);
        var details = new VideoMetadataDetails(
            "tmdb", "2", VideoMetadataMediaKind.Anime, "作品", "作品", null, "概要",
            2024, 1, 1, 1, [], ["Animation"], ["Actor"],
            ImmutableDictionary<string, string>.Empty.Add("tmdb", "2").Add("anidb", "1"),
            tmdbCandidate.SourceUrl, now, now.AddDays(30),
            People: [new VideoPersonCredit(
                "person-1", "Actor", "Hero", "Actor",
                "https://image.tmdb.org/t/p/original/person.jpg")],
            RelatedItems: [new VideoRelatedItem(
                "tmdb", "related-1", "Related", null, 2023,
                "https://image.tmdb.org/t/p/original/related-poster.jpg",
                "https://image.tmdb.org/t/p/original/related-backdrop.jpg",
                "https://www.themoviedb.org/tv/3")],
            Seasons: [new VideoMetadataSeason(
                1, "Season 1", null, null, 1,
                "https://image.tmdb.org/t/p/original/season.jpg",
                [new VideoMetadataEpisode(
                    1, "Episode 1", null, null, null, 24,
                    "https://image.tmdb.org/t/p/original/still.jpg",
                    "https://www.themoviedb.org/tv/2/season/1/episode/1")])]);
        var tmdbProvider = new FixtureMetadataProvider(
            "tmdb",
            [tmdbCandidate],
            details,
            tmdbPosters.Concat(
            [
                new VideoArtworkCandidate(
                    "tmdb", "https://image.tmdb.org/t/p/original/backdrop.jpg", "backdrop",
                    null, 1920, 1080, tmdbCandidate.SourceUrl),
                new VideoArtworkCandidate(
                    "tmdb", "https://image.tmdb.org/t/p/original/logo.png", "logo",
                    "ja", 1000, 400, tmdbCandidate.SourceUrl),
            ]).ToArray());
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        repository.Setup(item => item.ReplaceMatchCandidatesAsync(
                assetId, It.IsAny<IReadOnlyList<VideoMatchCandidateSnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(item => item.ApplyMetadataMatchAsync(
                assetId, It.IsAny<VideoMetadataCandidate>(), It.IsAny<VideoMetadataDetails?>(),
                It.IsAny<bool>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var persisted = new ConcurrentBag<VideoArtworkCandidate>();
        repository.Setup(item => item.UpsertArtworkCandidateAsync(
                assetId,
                It.IsAny<VideoMetadataMediaKind>(),
                It.IsAny<VideoArtworkCandidate>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, VideoMetadataMediaKind, VideoArtworkCandidate, string?, string?,
                DateTimeOffset?, bool, string?, CancellationToken>(
                (_, _, artwork, _, _, _, _, _, _) => persisted.Add(artwork))
            .Returns(Task.CompletedTask);
        var matcher = new Mock<IVideoMetadataMatcher>();
        matcher.Setup(item => item.Score(
                It.IsAny<ParsedVideoIdentity>(), VideoMetadataMediaKind.Anime,
                It.IsAny<IReadOnlyList<VideoMetadataCandidate>>()))
            .Returns(
            [
                new VideoMetadataMatchScore(
                    anidbCandidate, 1, 1, false, "locked AniDB", true, true),
                new VideoMetadataMatchScore(
                    tmdbCandidate, .95, .95, false, "linked TMDB", false, false),
            ]);
        var cache = new Mock<IVideoArtworkCache>();
        cache.Setup(item => item.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, CancellationToken _) => new VideoArtworkCacheEntry(
                "cached-" + Uri.EscapeDataString(url), url, null, null, 1, now));
        var settings = Mock.Of<ISettingsService>(service => service.Current == new AppSettings());
        var coordinator = new VideoMetadataCoordinator(
            repository.Object,
            matcher.Object,
            [anidbProvider, tmdbProvider],
            [anidbProvider, tmdbProvider],
            NullLogger<VideoMetadataCoordinator>.Instance,
            settings,
            [anidbProvider, tmdbProvider],
            Mock.Of<IVideoMetadataTransport>(),
            cache.Object);

        var result = await coordinator.RefreshAssetAsync(assetId, allowNetwork: true, ct);

        result.Matched.Should().BeTrue();
        var candidates = persisted
            .DistinctBy(item => $"{item.ProviderId}\0{item.OwnerKind}\0{item.Kind}\0{item.Url}")
            .ToArray();
        candidates.Should().Contain(item => item.ProviderId == "anidb"
                                            && item.Kind == "poster"
                                            && item.Language == "ja"
                                            && item.Width == 680
                                            && item.Height == 1000);
        candidates.Count(item => item.ProviderId == "tmdb" && item.Kind == "poster"
                                  && item.OwnerKind == VideoMetadataMediaKind.Anime)
            .Should().Be(8, "series posters are retained as a bounded candidate set");
        candidates.Should().Contain(item => item.Kind == "backdrop"
                                            && item.OwnerKind == VideoMetadataMediaKind.Anime);
        candidates.Should().Contain(item => item.Kind == "logo"
                                            && item.OwnerKind == VideoMetadataMediaKind.Anime);
        candidates.Should().Contain(item => item.Kind == "person:person-1");
        candidates.Should().Contain(item => item.Kind == "related:tmdb:related-1:poster");
        candidates.Should().Contain(item => item.Kind == "related:tmdb:related-1:backdrop");
        candidates.Should().Contain(item => item.OwnerKind == VideoMetadataMediaKind.Season
                                            && item.SeasonNumber == 1
                                            && item.Kind == "poster");
        candidates.Should().Contain(item => item.OwnerKind == VideoMetadataMediaKind.Episode
                                            && item.SeasonNumber == 1
                                            && item.EpisodeNumber == 1
                                            && item.Kind == "thumb");
    }

    [Fact]
    public void BatchRegistry_StaleCompletionCannotRemoveReplacementExecution()
    {
        using var oldCancellation = new CancellationTokenSource();
        using var replacementCancellation = new CancellationTokenSource();
        var oldExecution = new VideoMetadataBatchExecution(oldCancellation);
        var replacement = new VideoMetadataBatchExecution(replacementCancellation);
        var registry = new VideoMetadataBatchRegistry();
        var sourceId = Guid.NewGuid();

        registry.TryAdd(sourceId, oldExecution).Should().BeTrue();
        registry.Remove(sourceId, oldExecution).Should().BeTrue();
        registry.TryAdd(sourceId, replacement).Should().BeTrue();

        registry.Remove(sourceId, oldExecution).Should().BeFalse();
        registry.TryGetValue(sourceId, out var current).Should().BeTrue();
        current.Should().BeSameAs(replacement);
    }

    [Fact]
    public async Task ClearAllScrapeRecords_DrainsDirectRefreshBeforeRepositoryClear()
    {
        var ct = TestContext.Current.CancellationToken;
        var (snapshot, assetId, _) = SingleAssetSnapshot();
        var order = new ConcurrentQueue<string>();
        var clearCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = ScrapeResetRepository(snapshot, order, clearCalled);
        repository.Setup(item => item.ReplaceMatchCandidatesAsync(
                assetId,
                It.IsAny<IReadOnlyList<VideoMatchCandidateSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => order.Enqueue("refresh-write"))
            .Returns(Task.CompletedTask);
        var provider = new BlockingSearchProvider();
        var matcher = new Mock<IVideoMetadataMatcher>();
        matcher.Setup(item => item.Score(
                It.IsAny<ParsedVideoIdentity>(),
                It.IsAny<VideoMetadataMediaKind>(),
                It.IsAny<IReadOnlyList<VideoMetadataCandidate>>()))
            .Returns([]);
        var coordinator = new VideoMetadataCoordinator(
            repository.Object,
            matcher.Object,
            [provider],
            [],
            NullLogger<VideoMetadataCoordinator>.Instance);

        var refresh = coordinator.RefreshAssetAsync(assetId, allowNetwork: true, ct);
        await provider.Started.Task.WaitAsync(ct);
        var clear = coordinator.ClearAllScrapeRecordsAsync(ct);

        await Task.Delay(30, ct);
        clearCalled.Task.IsCompleted.Should().BeFalse();
        provider.Release();
        await refresh;
        await clear;

        order.Should().Equal("refresh-write", "clear");
    }

    [Fact]
    public async Task ClearAllScrapeRecords_DrainsPreviewBeforeRepositoryClear()
    {
        var ct = TestContext.Current.CancellationToken;
        var (snapshot, assetId, candidate) = SingleAssetSnapshot();
        var order = new ConcurrentQueue<string>();
        var clearCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = ScrapeResetRepository(snapshot, order, clearCalled);
        var provider = new BlockingDetailsProvider(() => order.Enqueue("preview-provider-finished"));
        var coordinator = new VideoMetadataCoordinator(
            repository.Object,
            Mock.Of<IVideoMetadataMatcher>(),
            [],
            [provider],
            NullLogger<VideoMetadataCoordinator>.Instance);

        var preview = coordinator.PreviewRematchAsync(assetId, candidate, ct);
        await provider.Started.Task.WaitAsync(ct);
        var clear = coordinator.ClearAllScrapeRecordsAsync(ct);

        await Task.Delay(30, ct);
        clearCalled.Task.IsCompleted.Should().BeFalse();
        provider.Release();
        await preview;
        await clear;

        order.Should().Equal("preview-provider-finished", "clear");
    }

    [Fact]
    public async Task ClearAllScrapeRecords_DrainsConfirmRematchBeforeRepositoryClear()
    {
        var ct = TestContext.Current.CancellationToken;
        var (snapshot, assetId, candidate) = SingleAssetSnapshot();
        var order = new ConcurrentQueue<string>();
        var clearCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = ScrapeResetRepository(snapshot, order, clearCalled);
        repository.Setup(item => item.ApplyMetadataMatchAsync(
                assetId,
                candidate,
                It.IsAny<VideoMetadataDetails?>(),
                true,
                false,
                It.IsAny<CancellationToken>()))
            .Callback(() => order.Enqueue("confirm-write"))
            .ReturnsAsync(true);
        var provider = new BlockingDetailsProvider();
        var coordinator = new VideoMetadataCoordinator(
            repository.Object,
            Mock.Of<IVideoMetadataMatcher>(),
            [],
            [provider],
            NullLogger<VideoMetadataCoordinator>.Instance);
        var preview = new VideoRematchPreview(
            assetId,
            [],
            candidate,
            [],
            "Series / Season 1 / Episode 1",
            false);

        var confirm = coordinator.ConfirmRematchAsync(preview, ct);
        await provider.Started.Task.WaitAsync(ct);
        var clear = coordinator.ClearAllScrapeRecordsAsync(ct);

        await Task.Delay(30, ct);
        clearCalled.Task.IsCompleted.Should().BeFalse();
        provider.Release();
        await confirm;
        await clear;

        order.Should().Equal("confirm-write", "clear");
    }

    [Fact]
    public async Task ClearAllScrapeRecords_RejectsEntrypointsStartedDuringReset()
    {
        var ct = TestContext.Current.CancellationToken;
        var (snapshot, assetId, candidate) = SingleAssetSnapshot();
        var sourceId = snapshot.Sources.Single().Id;
        var resetEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.ClearAllScrapeRecordsAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken token) =>
            {
                resetEntered.TrySetResult();
                await releaseReset.Task.WaitAsync(token);
            });
        var coordinator = new VideoMetadataCoordinator(
            repository.Object,
            Mock.Of<IVideoMetadataMatcher>(),
            [],
            [],
            NullLogger<VideoMetadataCoordinator>.Instance);
        var preview = new VideoRematchPreview(
            assetId,
            [],
            candidate,
            [],
            "Series / Season 1 / Episode 1",
            false);

        var clear = coordinator.ClearAllScrapeRecordsAsync(ct);
        await resetEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        try
        {
            var queueAll = coordinator.QueueAllSourcesAsync(ct: ct);
            var queueSource = coordinator.QueueSourceRefreshAsync(sourceId, ct: ct);
            queueAll.IsCompletedSuccessfully.Should().BeTrue();
            queueSource.IsCompletedSuccessfully.Should().BeTrue();
            await queueAll.WaitAsync(TimeSpan.FromSeconds(2), ct);
            await queueSource.WaitAsync(TimeSpan.FromSeconds(2), ct);
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => coordinator.RefreshAssetAsync(assetId, allowNetwork: true, ct));
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => coordinator.PreviewRematchAsync(assetId, candidate, ct));
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => coordinator.ConfirmRematchAsync(preview, ct));
            repository.Verify(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            releaseReset.TrySetResult();
        }
        await clear.WaitAsync(TimeSpan.FromSeconds(2), ct);
    }

    [Fact]
    public async Task ClearAllScrapeRecords_WhenArtworkCleanupFails_ClearsLiveProgressAndRethrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (snapshot, _, _) = SingleAssetSnapshot();
        var sourceId = snapshot.Sources.Single().Id;
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        repository.Setup(item => item.BeginMetadataRefreshAsync(
                sourceId, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        repository.Setup(item => item.ReplaceMatchCandidatesAsync(
                It.IsAny<Guid>(),
                It.IsAny<IReadOnlyList<VideoMatchCandidateSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(item => item.UpdateMetadataRefreshAsync(
                It.IsAny<Guid>(),
                It.IsAny<VideoCatalogJobState>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(item => item.UpdateMetadataRefreshCountsAsync(
                It.IsAny<Guid>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int>()))
            .Returns(Task.CompletedTask);
        repository.Setup(item => item.ClearAllScrapeRecordsAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var artwork = new Mock<IVideoArtworkCache>();
        artwork.Setup(item => item.ClearAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("artwork cleanup failed"));
        var coordinator = new VideoMetadataCoordinator(
            repository.Object,
            Mock.Of<IVideoMetadataMatcher>(),
            [],
            [],
            NullLogger<VideoMetadataCoordinator>.Instance,
            null,
            [],
            null,
            artwork.Object);
        await coordinator.QueueSourceRefreshAsync(sourceId, ct: ct);
        coordinator.ActiveBatchProgress.Should().NotBeEmpty();

        var action = () => coordinator.ClearAllScrapeRecordsAsync(ct);

        await action.Should().ThrowAsync<IOException>()
            .WithMessage("artwork cleanup failed");
        coordinator.ActiveBatchProgress.Should().BeEmpty();
        repository.Verify(item => item.ClearAllScrapeRecordsAsync(
            It.IsAny<CancellationToken>()), Times.Once);
        artwork.Verify(item => item.ClearAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RetryTask_StartedBeforeClear_DoesNotRequeueFromOldSnapshot()
    {
        await AssertRetryStartedBeforeClearDoesNotRequeueAsync(
            (coordinator, jobId, ct) => coordinator.RetryTaskAsync(jobId, ct));
    }

    [Fact]
    public async Task RetryFailedTasks_StartedBeforeClear_DoesNotRequeueFromOldSnapshot()
    {
        await AssertRetryStartedBeforeClearDoesNotRequeueAsync(
            (coordinator, _, ct) => coordinator.RetryFailedTasksAsync(ct));
    }

    private static async Task AssertRetryStartedBeforeClearDoesNotRequeueAsync(
        Func<VideoMetadataCoordinator, Guid, CancellationToken, Task> retryAction)
    {
        var ct = TestContext.Current.CancellationToken;
        var (snapshot, _, _) = SingleAssetSnapshot();
        var sourceId = snapshot.Sources.Single().Id;
        var jobId = Guid.NewGuid();
        snapshot = snapshot with
        {
            Jobs =
            [
                new VideoCatalogJobSnapshot(
                    jobId,
                    sourceId,
                    VideoCatalogJobKind.MetadataRefresh,
                    VideoCatalogJobState.Failed,
                    1,
                    0,
                    1,
                    "failed",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ],
        };
        var snapshotStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken token) =>
            {
                snapshotStarted.TrySetResult();
                await releaseSnapshot.Task.WaitAsync(token);
                return snapshot;
            });
        repository.Setup(item => item.ClearAllScrapeRecordsAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(item => item.ClearRemoteMetadataAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(item => item.BeginMetadataRefreshAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        var coordinator = new VideoMetadataCoordinator(
            repository.Object,
            Mock.Of<IVideoMetadataMatcher>(),
            [],
            [],
            NullLogger<VideoMetadataCoordinator>.Instance);

        var retry = retryAction(coordinator, jobId, ct);
        await snapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        var clear = coordinator.ClearAllScrapeRecordsAsync(ct);
        try
        {
            await clear.WaitAsync(TimeSpan.FromSeconds(2), ct);
        }
        finally
        {
            releaseSnapshot.TrySetResult();
        }
        await retry.WaitAsync(TimeSpan.FromSeconds(2), ct);

        repository.Verify(item => item.ClearRemoteMetadataAsync(
            sourceId, It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(item => item.BeginMetadataRefreshAsync(
            sourceId, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ForceSourceRefresh_ClearsRemoteMetadataForCatalogResetPendingAsset()
    {
        var ct = TestContext.Current.CancellationToken;
        var (snapshot, _, _) = SingleAssetSnapshot();
        var sourceId = snapshot.Sources.Single().Id;
        snapshot = snapshot with
        {
            Assets = [snapshot.Assets.Single() with { CatalogResetPending = true }],
        };
        var order = new ConcurrentQueue<string>();
        var batchFinished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        repository.Setup(item => item.ClearRemoteMetadataAsync(
                sourceId, It.IsAny<CancellationToken>()))
            .Callback(() => order.Enqueue("source-clear"))
            .Returns(Task.CompletedTask);
        repository.Setup(item => item.BeginMetadataRefreshAsync(
                sourceId, 1, It.IsAny<CancellationToken>()))
            .Callback(() => order.Enqueue("metadata-begin"))
            .ReturnsAsync(Guid.NewGuid());
        repository.Setup(item => item.ReplaceMatchCandidatesAsync(
                It.IsAny<Guid>(),
                It.IsAny<IReadOnlyList<VideoMatchCandidateSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(item => item.UpdateMetadataRefreshAsync(
                It.IsAny<Guid>(),
                It.IsAny<VideoCatalogJobState>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback((Guid _, VideoCatalogJobState state, int _, string? _, CancellationToken _) =>
            {
                if (state is VideoCatalogJobState.Completed or VideoCatalogJobState.Failed)
                    batchFinished.TrySetResult(true);
            })
            .Returns(Task.CompletedTask);
        repository.Setup(item => item.UpdateMetadataRefreshCountsAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);
        var matcher = new Mock<IVideoMetadataMatcher>();
        matcher.Setup(item => item.Score(
                It.IsAny<ParsedVideoIdentity>(),
                It.IsAny<VideoMetadataMediaKind>(),
                It.IsAny<IReadOnlyList<VideoMetadataCandidate>>()))
            .Returns([]);
        var coordinator = new VideoMetadataCoordinator(
            repository.Object,
            matcher.Object,
            [],
            [],
            NullLogger<VideoMetadataCoordinator>.Instance);

        await coordinator.QueueSourceRefreshAsync(sourceId, forceRefresh: true, ct);
        await batchFinished.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);

        order.Should().ContainInOrder("source-clear", "metadata-begin");
        repository.Verify(item => item.ClearRemoteMetadataAsync(
            sourceId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ForceRefreshAfterClear_RequeuesAniDbBeforeMetadataBatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var (snapshot, _, _) = SingleAssetSnapshot();
        var sourceId = snapshot.Sources.Single().Id;
        var manualAssetId = Guid.NewGuid();
        var manualIdentity = new VideoManualAniDbIdentity(
            manualAssetId,
            ImmutableHashSet.Create(101),
            ImmutableHashSet.Create(1001));
        var order = new ConcurrentQueue<string>();
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        repository.Setup(item => item.ClearAllScrapeRecordsAsync(
                It.IsAny<IReadOnlyCollection<VideoManualAniDbIdentity>>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => order.Enqueue("catalog-clear"))
            .Returns(Task.CompletedTask);
        repository.Setup(item => item.ClearRemoteMetadataAsync(sourceId, It.IsAny<CancellationToken>()))
            .Callback(() => order.Enqueue("source-clear"))
            .Returns(Task.CompletedTask);
        repository.Setup(item => item.BeginMetadataRefreshAsync(
                sourceId, 1, It.IsAny<CancellationToken>()))
            .Callback(() => order.Enqueue("metadata-begin"))
            .ReturnsAsync(Guid.NewGuid());
        repository.Setup(item => item.ReplaceMatchCandidatesAsync(
                It.IsAny<Guid>(),
                It.IsAny<IReadOnlyList<VideoMatchCandidateSnapshot>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(item => item.UpdateMetadataRefreshAsync(
                It.IsAny<Guid>(),
                It.IsAny<VideoCatalogJobState>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(item => item.UpdateMetadataRefreshCountsAsync(
                It.IsAny<Guid>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<int>()))
            .Returns(Task.CompletedTask);
        var aniDb = new Mock<IAniDbImportService>();
        aniDb.Setup(item => item.ClearScrapingRecordsAsync(
                It.IsAny<Func<IReadOnlyCollection<VideoManualAniDbIdentity>, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                Func<IReadOnlyCollection<VideoManualAniDbIdentity>, CancellationToken, Task> cleanup,
                CancellationToken token) =>
            {
                order.Enqueue("anidb-clear");
                await cleanup([manualIdentity], token);
            });
        aniDb.Setup(item => item.QueueSourceAsync(sourceId, It.IsAny<CancellationToken>()))
            .Callback(() => order.Enqueue("anidb-queue"))
            .Returns(Task.CompletedTask);
        var matcher = new Mock<IVideoMetadataMatcher>();
        matcher.Setup(item => item.Score(
                It.IsAny<ParsedVideoIdentity>(),
                It.IsAny<VideoMetadataMediaKind>(),
                It.IsAny<IReadOnlyList<VideoMetadataCandidate>>()))
            .Returns([]);
        var coordinator = new VideoMetadataCoordinator(
            repository.Object,
            matcher.Object,
            [],
            [],
            NullLogger<VideoMetadataCoordinator>.Instance,
            null,
            [],
            null,
            null,
            aniDb.Object);

        await coordinator.ClearAllScrapeRecordsAsync(ct);
        await coordinator.QueueAllSourcesAsync(forceRefresh: true, ct);

        order.Should().ContainInOrder(
            "anidb-clear",
            "catalog-clear",
            "source-clear",
            "anidb-queue",
            "metadata-begin");
        aniDb.Verify(item => item.QueueSourceAsync(sourceId, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.ClearAllScrapeRecordsAsync(
            It.Is<IReadOnlyCollection<VideoManualAniDbIdentity>>(
                identities => identities.SequenceEqual(new[] { manualIdentity })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<IVideoCatalogRepository> ScrapeResetRepository(
        VideoCatalogSnapshot snapshot,
        ConcurrentQueue<string> order,
        TaskCompletionSource<bool> clearCalled)
    {
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        repository.Setup(item => item.ClearAllScrapeRecordsAsync(It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                order.Enqueue("clear");
                clearCalled.TrySetResult(true);
            })
            .Returns(Task.CompletedTask);
        return repository;
    }

    private static (VideoCatalogSnapshot Snapshot, Guid AssetId, VideoMetadataCandidate Candidate)
        SingleAssetSnapshot()
    {
        var sourceId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var source = new VideoCatalogSourceSnapshot(
            sourceId, "Drama", @"C:\Drama", @"C:\Drama",
            VideoLibraryMediaType.JapaneseDramaTv, "ja-JP", "JP",
            ["tmdb"], 1, DateTimeOffset.UtcNow, null, null);
        var asset = new VideoCatalogAssetSnapshot(
            assetId, @"C:\Drama\Work S01E01.mkv", VideoMediaAssetKind.LocalFile,
            @"C:\Drama\Work S01E01.mkv", "Work", "Drama", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            VideoMediaAvailability.Available, 1, 1, null, null, null, null, null,
            null, null, null, false, [], null, null, null, [sourceId], [], []);
        var candidate = new VideoMetadataCandidate(
            "tmdb", "1", VideoMetadataMediaKind.Series, "Work", null, 2026,
            1, 1, null, [], ImmutableDictionary<string, string>.Empty, null);
        return (VideoCatalogSnapshot.Empty() with { Sources = [source], Assets = [asset] },
            assetId,
            candidate);
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

    private sealed class BlockingSearchProvider : IVideoMetadataSearchProvider
    {
        private readonly TaskCompletionSource<bool> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "tmdb";
        public string DisplayName => "TMDB";
        public VideoMetadataCapabilities Capabilities => VideoMetadataCapabilities.Search;
        public IReadOnlySet<VideoMetadataMediaKind> SupportedMediaKinds { get; } =
            new HashSet<VideoMetadataMediaKind>(Enum.GetValues<VideoMetadataMediaKind>());
        public bool ArtworkEnabledByDefault => false;
        public string? AttributionUrl => null;

        public async Task<IReadOnlyList<VideoMetadataCandidate>> SearchAsync(
            VideoMetadataSearchQuery query,
            CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            await _release.Task;
            return [];
        }

        public void Release() => _release.TrySetResult(true);
    }

    private sealed class BlockingDetailsProvider(Action? beforeReturn = null)
        : IVideoMetadataDetailsProvider
    {
        private readonly TaskCompletionSource<bool> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "tmdb";
        public string DisplayName => "TMDB";
        public VideoMetadataCapabilities Capabilities => VideoMetadataCapabilities.Details;
        public IReadOnlySet<VideoMetadataMediaKind> SupportedMediaKinds { get; } =
            new HashSet<VideoMetadataMediaKind>(Enum.GetValues<VideoMetadataMediaKind>());
        public bool ArtworkEnabledByDefault => false;
        public string? AttributionUrl => null;

        public async Task<VideoMetadataDetails?> GetDetailsAsync(
            VideoMetadataCandidate identity,
            string language,
            string region,
            CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            await _release.Task;
            beforeReturn?.Invoke();
            return null;
        }

        public void Release() => _release.TrySetResult(true);
    }

    private sealed class FixtureMetadataProvider(
        string id,
        IReadOnlyList<VideoMetadataCandidate> search,
        VideoMetadataDetails? details,
        IReadOnlyList<VideoArtworkCandidate> artwork) :
        IVideoMetadataSearchProvider,
        IVideoMetadataDetailsProvider,
        IVideoArtworkProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public VideoMetadataCapabilities Capabilities =>
            VideoMetadataCapabilities.Search
            | VideoMetadataCapabilities.Details
            | VideoMetadataCapabilities.Artwork;
        public IReadOnlySet<VideoMetadataMediaKind> SupportedMediaKinds { get; } =
            new HashSet<VideoMetadataMediaKind>(Enum.GetValues<VideoMetadataMediaKind>());
        public bool ArtworkEnabledByDefault => true;
        public string? AttributionUrl => null;

        public Task<IReadOnlyList<VideoMetadataCandidate>> SearchAsync(
            VideoMetadataSearchQuery query,
            CancellationToken ct = default) => Task.FromResult(search);

        public Task<VideoMetadataDetails?> GetDetailsAsync(
            VideoMetadataCandidate identity,
            string language,
            string region,
            CancellationToken ct = default) => Task.FromResult(details);

        public Task<IReadOnlyList<VideoArtworkCandidate>> GetArtworkAsync(
            VideoMetadataCandidate identity,
            CancellationToken ct = default) => Task.FromResult(artwork);
    }

    private sealed class RecordingSearchProvider(string id = "anilist") : IVideoMetadataSearchProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public VideoMetadataCapabilities Capabilities => VideoMetadataCapabilities.Search;
        public IReadOnlySet<VideoMetadataMediaKind> SupportedMediaKinds { get; } =
            new HashSet<VideoMetadataMediaKind>(Enum.GetValues<VideoMetadataMediaKind>());
        public bool ArtworkEnabledByDefault => false;
        public string? AttributionUrl => null;
        public VideoMetadataSearchQuery? Query { get; private set; }
        public TaskCompletionSource<VideoMetadataSearchQuery> Searched { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<VideoMetadataCandidate>> SearchAsync(
            VideoMetadataSearchQuery query,
            CancellationToken ct = default)
        {
            Query = query;
            Searched.TrySetResult(query);
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
