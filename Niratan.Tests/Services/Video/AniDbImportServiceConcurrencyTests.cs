using FluentAssertions;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Niratan.Models.Video;
using Niratan.Services.Storage;
using Niratan.Services.Video;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Video;

public sealed class AniDbImportServiceConcurrencyTests
{
    [Fact]
    public async Task QueueSource_SkipsCatalogResetPendingAssetWithoutCreatingImportJob()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourceId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var asset = new VideoCatalogAssetSnapshot(
            assetId, @"C:\Anime\Reset Show S01E01.mkv", VideoMediaAssetKind.LocalFile,
            @"C:\Anime\Reset Show S01E01.mkv", "Reset Show", "Anime", 1,
            now, now, now, VideoMediaAvailability.Available, 1, 1,
            null, null, null, null, null, null, null, null, false, [],
            null, null, null, [sourceId], [], [])
        {
            CatalogResetPending = true,
        };
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(VideoCatalogSnapshot.Empty() with { Assets = [asset] });
        var store = new Mock<IAniDbCatalogStore>();
        store.Setup(item => item.ClaimImportJobAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AniDbImportJob?)null);
        store.Setup(item => item.ClaimMyListJobAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AniDbMyListJob?)null);
        var configuration = new Mock<IAniDbConfigurationProvider>();
        configuration.Setup(item => item.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AniDbClientConfiguration(
                "niratan_test", 1, "user", "password", 45500,
                true, false, false, AniDbMyListState.OnHdd, 0));
        await using var service = new AniDbImportService(
            repository.Object,
            store.Object,
            configuration.Object,
            Mock.Of<IAniDbEd2kHasher>(),
            Mock.Of<IAniDbUdpClient>(),
            Mock.Of<IAniDbHttpClient>(),
            new VideoPlaybackHistoryStore(Path.Combine(temp.Path, "history.json")),
            NullLogger<AniDbImportService>.Instance);

        await service.QueueSourceAsync(sourceId, ct);

        repository.Verify(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(item => item.EnqueueImportJobAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueueAsset_SkipsCatalogResetPendingAssetWithoutCreatingImportJob()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var assetId = Guid.NewGuid();
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(VideoCatalogSnapshot.Empty() with
            {
                Assets = [CatalogAsset(assetId, "asset-key", catalogResetPending: true)],
            });
        var store = IdleStore();
        await using var service = CreateService(temp, repository.Object, store.Object);

        await service.QueueAssetAsync(assetId, ct);

        store.Verify(item => item.EnqueueImportJobAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueueMyListState_PendingUnhashedAssetDoesNotBackdoorImport()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var assetId = Guid.NewGuid();
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(VideoCatalogSnapshot.Empty() with
            {
                Assets = [CatalogAsset(assetId, "asset-key", catalogResetPending: true)],
            });
        var store = IdleStore();
        store.Setup(item => item.GetAssetAsync(assetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AniDbAssetSnapshot?)null);
        var configuration = new Mock<IAniDbConfigurationProvider>();
        configuration.Setup(item => item.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(EnabledConfiguration(myListSyncEnabled: true));
        await using var service = CreateService(
            temp, repository.Object, store.Object, configuration.Object);

        await service.QueueMyListStateAsync("asset-key", watched: true, ct);

        store.Verify(item => item.EnqueueImportJobAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(item => item.EnqueueMyListJobAsync(
            It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Worker_CompletesStaleImportJobWithoutProjectingPendingAsset()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var assetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(VideoCatalogSnapshot.Empty() with
            {
                Assets = [CatalogAsset(assetId, "asset-key", catalogResetPending: true)],
            });
        var store = new Mock<IAniDbCatalogStore>();
        var claimed = 0;
        store.Setup(item => item.ClaimImportJobAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns<DateTimeOffset, CancellationToken>((_, _) => Task.FromResult(
                Interlocked.Exchange(ref claimed, 1) == 0
                    ? new AniDbImportJob(
                        assetId, AniDbImportJobStage.Queued, AniDbImportJobState.Running,
                        0, now, now, now, null)
                    : null));
        store.Setup(item => item.ClaimMyListJobAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AniDbMyListJob?)null);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Setup(item => item.CompleteImportJobAsync(assetId, It.IsAny<CancellationToken>()))
            .Callback(() => completed.TrySetResult())
            .Returns(Task.CompletedTask);
        var configuration = new Mock<IAniDbConfigurationProvider>();
        configuration.Setup(item => item.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(EnabledConfiguration());
        var hasher = new Mock<IAniDbEd2kHasher>();

        await using (var service = CreateService(
                         temp, repository.Object, store.Object, configuration.Object, hasher.Object))
        {
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        }

        hasher.Verify(item => item.HashAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(item => item.ApplyAniDbIdentityAsync(
            It.IsAny<Guid>(), It.IsAny<VideoAniDbIdentityProjection>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Worker_RetriesAtAnimeMetadataWhenMatchedFileHasNoAnimeEntity()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var assetId = Guid.NewGuid();
        var mediaPath = Path.Combine(temp.Path, "episode.mkv");
        await File.WriteAllBytesAsync(mediaPath, [0x01], ct);
        var asset = CatalogAsset(assetId, "asset-key", catalogResetPending: false) with
        {
            Location = mediaPath,
            FileSize = 1,
            ModifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(mediaPath)),
        };
        var now = DateTimeOffset.UtcNow;
        var match = new AniDbFileMatch(
            301, 19242, null, null, null, false, 1, null, null, false,
            null, null, [], [], null, null, null,
            [new AniDbFileEpisodeLink(1001, 100, false, 0) { AnimeId = 19242 }]);
        var hash = new AniDbEd2kHash(
            "31d6cfe0d16ae931b73c59d7e0c089c0",
            asset.FileSize,
            asset.ModifiedAt!.Value,
            now)
        {
            Crc32 = "00000000",
            Md5 = "d41d8cd98f00b204e9800998ecf8427e",
            Sha1 = "da39a3ee5e6b4b0d3255bfef95601890afd80709",
        };
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(VideoCatalogSnapshot.Empty() with { Assets = [asset] });
        var store = new Mock<IAniDbCatalogStore>();
        var claimed = 0;
        var runningJob = new AniDbImportJob(
            assetId, AniDbImportJobStage.Queued, AniDbImportJobState.Running,
            0, now, now, now, null);
        store.Setup(item => item.ClaimImportJobAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns<DateTimeOffset, CancellationToken>((_, _) => Task.FromResult(
                Interlocked.Exchange(ref claimed, 1) == 0 ? runningJob : null));
        store.Setup(item => item.ClaimMyListJobAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AniDbMyListJob?)null);
        store.Setup(item => item.GetAssetAsync(assetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AniDbAssetSnapshot(
                assetId, hash.Value, hash.FileSize, hash.ModifiedAt, hash.HashedAt,
                match, null, null)
            {
                Crc32 = hash.Crc32,
                Md5 = hash.Md5,
                Sha1 = hash.Sha1,
            });
        store.Setup(item => item.GetReleaseStateAsync(
                hash.Value, hash.FileSize, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AniDbReleaseState(
                hash.Value, hash.FileSize, AniDbReleaseStatus.Matched,
                match, null, false, null, now));
        store.Setup(item => item.GetAnimeAsync(19242, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AniDbAnime?)null);
        store.Setup(item => item.GetImportJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([runningJob with { Stage = AniDbImportJobStage.AnimeMetadata }]);
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Setup(item => item.RetryImportJobAsync(
                assetId,
                AniDbImportJobStage.AnimeMetadata,
                1,
                It.IsAny<DateTimeOffset>(),
                It.Is<string>(message => message.Contains("metadata was unavailable")),
                false,
                It.IsAny<CancellationToken>()))
            .Callback(() => retried.TrySetResult())
            .Returns(Task.CompletedTask);
        var configuration = new Mock<IAniDbConfigurationProvider>();
        configuration.Setup(item => item.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(EnabledConfiguration());
        var udp = new Mock<IAniDbUdpClient>();
        udp.SetupGet(item => item.Status).Returns(new AniDbClientStatus(
            AniDbClientConnectionState.Ready, null, now));
        var http = new Mock<IAniDbHttpClient>();
        http.Setup(item => item.GetAnimeAsync(19242, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AniDbAnime?)null);

        await using (var service = new AniDbImportService(
                         repository.Object,
                         store.Object,
                         configuration.Object,
                         Mock.Of<IAniDbEd2kHasher>(),
                         udp.Object,
                         http.Object,
                         new VideoPlaybackHistoryStore(Path.Combine(temp.Path, "history.json")),
                         NullLogger<AniDbImportService>.Instance))
        {
            await retried.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        }

        store.Verify(item => item.CompleteImportJobAsync(
            assetId, It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(item => item.ApplyAniDbIdentityAsync(
            It.IsAny<Guid>(), It.IsAny<VideoAniDbIdentityProjection>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Worker_MissingOrRejectedHttpClient_ProjectsReducedUdpAnimeAndEpisodeMetadata(
        bool hasExplicitHttpIdentity,
        int expectedHttpCalls)
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var assetId = Guid.NewGuid();
        var mediaPath = Path.Combine(temp.Path, "episode.mkv");
        await File.WriteAllBytesAsync(mediaPath, [0x01], ct);
        var modifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(mediaPath));
        var now = DateTimeOffset.UtcNow;
        var asset = CatalogAsset(assetId, mediaPath, catalogResetPending: false) with
        {
            Location = mediaPath,
            FileSize = 1,
            ModifiedAt = modifiedAt,
        };
        var match = new AniDbFileMatch(
            301, 19242, null, null, null, false, 1, null, null, false,
            null, null, [], [], null, null, null,
            [new AniDbFileEpisodeLink(1001, 100, false, 0) { AnimeId = 19242 }]);
        var store = new AniDbCatalogStore(Path.Combine(temp.Path, "anidb.sqlite3"));
        await store.UpsertHashAsync(
            assetId,
            asset.IdentityKey,
            new AniDbEd2kHash(
                "31d6cfe0d16ae931b73c59d7e0c089c0",
                1,
                modifiedAt,
                now)
            {
                Crc32 = "00000000",
                Md5 = "d41d8cd98f00b204e9800998ecf8427e",
                Sha1 = "da39a3ee5e6b4b0d3255bfef95601890afd80709",
            },
            ct);
        await store.UpsertFileMatchAsync(assetId, match, null, ct);
        await store.EnqueueImportJobAsync(assetId, ct);
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(VideoCatalogSnapshot.Empty() with { Assets = [asset] });
        VideoAniDbIdentityProjection? applied = null;
        repository.Setup(item => item.ApplyAniDbIdentityAsync(
                assetId,
                It.IsAny<VideoAniDbIdentityProjection>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, VideoAniDbIdentityProjection, CancellationToken>(
                (_, projection, _) => applied = projection)
            .Returns(Task.CompletedTask);
        var udp = new Mock<IAniDbUdpClient>();
        udp.SetupGet(item => item.Status).Returns(new AniDbClientStatus(
            AniDbClientConnectionState.Connected, null, now));
        udp.Setup(item => item.GetAnimeMetadataAsync(19242, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AniDbAnime(
                19242,
                "TV Series",
                "Re Zero kara Hajimeru Isekai Seikatsu 4th Season",
                "Re：ゼロから始める異世界生活 4th season",
                null,
                "2026-01-01",
                null,
                "19242.jpg",
                16,
                false,
                8.45,
                [new AniDbTitle("ja", "official", "Re：ゼロから始める異世界生活 4th season")],
                [],
                [],
                [],
                [],
                now,
                now.AddDays(7))
            {
                IsDegraded = true,
                Url = "https://anidb.net/anime/19242",
            });
        udp.Setup(item => item.GetEpisodeMetadataAsync(1001, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AniDbEpisode(
                1001,
                19242,
                AniDbEpisodeType.Regular,
                1,
                "01",
                24,
                "2026-01-01",
                null,
                8,
                [new AniDbTitle("ja", "episode", "始まり")]
            ));
        var myList = new AniDbMyListEntry(
            401,
            301,
            1001,
            19242,
            AniDbMyListState.OnHdd,
            false,
            null,
            now);
        udp.Setup(item => item.GetMyListAsync(
                "31d6cfe0d16ae931b73c59d7e0c089c0",
                1,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(myList);
        var http = new Mock<IAniDbHttpClient>();
        http.Setup(item => item.GetAnimeAsync(19242, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AniDbHttpApiException(302, "client version missing or invalid"));
        var configuration = new Mock<IAniDbConfigurationProvider>();
        var enabledConfiguration = EnabledConfiguration(myListSyncEnabled: true);
        if (hasExplicitHttpIdentity)
        {
            enabledConfiguration = enabledConfiguration with
            {
                HttpClientId = "registeredhttpclient",
                HttpClientVersion = 1,
            };
        }
        configuration.Setup(item => item.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(enabledConfiguration);

        await using (var service = new AniDbImportService(
                         repository.Object,
                         store,
                         configuration.Object,
                         Mock.Of<IAniDbEd2kHasher>(),
                         udp.Object,
                         http.Object,
                         new VideoPlaybackHistoryStore(Path.Combine(temp.Path, "history.json")),
                         NullLogger<AniDbImportService>.Instance))
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var job = (await store.GetImportJobsAsync(ct)).Single();
                if (job.State == AniDbImportJobState.Failed)
                    break;
                await Task.Delay(20, ct);
            }
            var failedJob = (await store.GetImportJobsAsync(ct)).Single();
            failedJob.State.Should().Be(AniDbImportJobState.Failed);
            failedJob.Stage.Should().Be(AniDbImportJobStage.AnimeMetadata);
            failedJob.LastError.Should().Contain("HTTP API client ID/version");
        }

        var cached = await store.GetAnimeAsync(19242, ct);
        cached.Should().NotBeNull();
        cached!.IsDegraded.Should().BeTrue();
        cached.Picture.Should().Be("19242.jpg");
        cached.Episodes.Should().ContainSingle().Which.Titles
            .Should().Contain(title => title.Value == "始まり");
        (await store.GetAssetAsync(assetId, ct))!.MyList.Should().BeEquivalentTo(myList);
        applied.Should().NotBeNull();
        applied!.AnimeId.Should().Be(19242);
        applied.Episodes.Should().ContainSingle().Which.EpisodeId.Should().Be(1001);
        http.Verify(item => item.GetAnimeAsync(
            19242, It.IsAny<CancellationToken>()), Times.Exactly(expectedHttpCalls));
        udp.Verify(item => item.GetMyListAsync(
            "31d6cfe0d16ae931b73c59d7e0c089c0",
            1,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TestLogin_WhenUdpAndHttpAreValid_RequeuesExistingFileMatches()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var matchedAssetId = Guid.NewGuid();
        var unmatchedAssetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(VideoCatalogSnapshot.Empty() with
            {
                Assets = [CatalogAsset(matchedAssetId, "matched", catalogResetPending: false)],
            });
        var match = new AniDbFileMatch(
            301, 19242, null, null, null, false, 1, null, null, false,
            null, null, [], [], null, null, null, []);
        var store = IdleStore();
        store.Setup(item => item.GetAssetsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new AniDbAssetSnapshot(matchedAssetId, "matched", 1, now, now, match, null, null),
                new AniDbAssetSnapshot(unmatchedAssetId, "unmatched", 1, now, now, null, null, null),
            ]);
        var udp = new Mock<IAniDbUdpClient>();
        udp.Setup(item => item.TestLoginAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        udp.SetupGet(item => item.Status).Returns(new AniDbClientStatus(
            AniDbClientConnectionState.Connected, null, now));
        var http = new Mock<IAniDbHttpClient>();
        http.Setup(item => item.ProbeAnimeAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AniDbAnime(
                1, "TV Series", "Probe", null, null, null, null, null, 1,
                false, null, [], [], [], [], [], now, now.AddDays(1)));
        await using var service = new AniDbImportService(
            repository.Object,
            store.Object,
            Mock.Of<IAniDbConfigurationProvider>(),
            Mock.Of<IAniDbEd2kHasher>(),
            udp.Object,
            http.Object,
            new VideoPlaybackHistoryStore(Path.Combine(temp.Path, "history.json")),
            NullLogger<AniDbImportService>.Instance);

        (await service.TestLoginAsync(ct)).Should().BeTrue();

        store.Verify(item => item.EnqueueImportJobAsync(
            matchedAssetId, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(item => item.EnqueueImportJobAsync(
            unmatchedAssetId, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProjectGroup_SkipsPendingPeersWhenAnotherAssetIsUnlocked()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var pendingAssetId = Guid.NewGuid();
        var unlockedAssetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(VideoCatalogSnapshot.Empty() with
            {
                Assets =
                [
                    CatalogAsset(pendingAssetId, "pending-key", catalogResetPending: true),
                    CatalogAsset(unlockedAssetId, "unlocked-key", catalogResetPending: false),
                ],
            });
        var match = new AniDbFileMatch(
            301, 101, null, null, null, false, 1, null, null, false,
            null, null, [], [], null, null, null, []);
        var store = IdleStore();
        store.Setup(item => item.GetAssetsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new AniDbAssetSnapshot(pendingAssetId, "pending", 1, now, now, match, null, null),
                new AniDbAssetSnapshot(unlockedAssetId, "unlocked", 1, now, now, match, null, null),
            ]);
        store.Setup(item => item.GetAnimeAsync(101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AniDbAnime(
                101, "TV Series", "Show", null, null, "2026-01-01", null, null,
                1, false, null, [], [], [], [], [], now, now.AddDays(30)));
        await using var service = CreateService(temp, repository.Object, store.Object);
        var group = new AniDbAnimeGroup(
            Guid.NewGuid(), 101, [101], false, now, now);

        await service.ProjectGroupAsync(group, ct);

        repository.Verify(item => item.ApplyAniDbIdentityAsync(
            pendingAssetId, It.IsAny<VideoAniDbIdentityProjection>(),
            It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(item => item.ApplyAniDbIdentityAsync(
            unlockedAssetId, It.IsAny<VideoAniDbIdentityProjection>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProjectGroup_AssignsEachAidAStableDisplaySeasonAndRemapsSeriesMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var firstAssetId = Guid.NewGuid();
        var secondAssetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var firstEpisode = new AniDbEpisode(
            1001, 101, AniDbEpisodeType.Regular, 1, "1", 24,
            "2020-01-08", null, null, [new AniDbTitle("en", "official", "First")]);
        var secondEpisode = new AniDbEpisode(
            2001, 202, AniDbEpisodeType.Regular, 1, "1", 24,
            "2024-01-08", null, null, [new AniDbTitle("en", "official", "Second")]);
        var firstAnime = new AniDbAnime(
            101, "TV Series", "First cour", null, null, "2020-01-01", null, null,
            1, false, null, [], [firstEpisode], [], [], [], now, now.AddDays(30));
        var secondAnime = new AniDbAnime(
            202, "TV Series", "Second cour", null, null, "2024-01-01", null, null,
            1, false, null, [], [secondEpisode], [], [], [], now, now.AddDays(30));
        var firstMatch = new AniDbFileMatch(
            301, 101, null, null, null, false, 1, null, null, false,
            null, null, [], [], null, null, null,
            [new AniDbFileEpisodeLink(1001, 100, false, 0) { AnimeId = 101 }]);
        var secondMatch = new AniDbFileMatch(
            302, 202, null, null, null, false, 1, null, null, false,
            null, null, [], [], null, null, null,
            [new AniDbFileEpisodeLink(2001, 100, false, 0) { AnimeId = 202 }]);
        var repository = new Mock<IVideoCatalogRepository>();
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(VideoCatalogSnapshot.Empty() with
            {
                Assets =
                [
                    CatalogAsset(firstAssetId, "first-key", catalogResetPending: false),
                    CatalogAsset(secondAssetId, "second-key", catalogResetPending: false),
                ],
            });
        var projections = new ConcurrentDictionary<Guid, VideoAniDbIdentityProjection>();
        repository.Setup(item => item.ApplyAniDbIdentityAsync(
                It.IsAny<Guid>(),
                It.IsAny<VideoAniDbIdentityProjection>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, VideoAniDbIdentityProjection, CancellationToken>(
                (assetId, projection, _) => projections[assetId] = projection)
            .Returns(Task.CompletedTask);
        var store = IdleStore();
        store.Setup(item => item.GetAssetsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new AniDbAssetSnapshot(firstAssetId, "first", 1, now, now, firstMatch, null, null),
                new AniDbAssetSnapshot(secondAssetId, "second", 1, now, now, secondMatch, null, null),
            ]);
        store.Setup(item => item.GetAnimeAsync(101, It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstAnime);
        store.Setup(item => item.GetAnimeAsync(202, It.IsAny<CancellationToken>()))
            .ReturnsAsync(secondAnime);
        store.Setup(item => item.GetAnimeByEpisodeAsync(1001, It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstAnime);
        store.Setup(item => item.GetAnimeByEpisodeAsync(2001, It.IsAny<CancellationToken>()))
            .ReturnsAsync(secondAnime);
        await using var service = CreateService(temp, repository.Object, store.Object);
        var group = new AniDbAnimeGroup(
            Guid.NewGuid(), 101, [202, 101], false, now, now);

        await service.ProjectGroupAsync(group, ct);

        projections[firstAssetId].Episodes.Should().ContainSingle()
            .Which.SeasonNumber.Should().Be(1);
        projections[secondAssetId].Episodes.Should().ContainSingle()
            .Which.SeasonNumber.Should().Be(2);
        projections[firstAssetId].SeriesMetadata.Seasons.Should().ContainSingle()
            .Which.Should().Match<VideoMetadataSeason>(season =>
                season.SeasonNumber == 1 && season.Title == "First cour");
        projections[secondAssetId].SeriesMetadata.Seasons.Should().ContainSingle()
            .Which.Should().Match<VideoMetadataSeason>(season =>
                season.SeasonNumber == 2 && season.Title == "Second cour");
    }

    [Fact]
    public async Task QueueEntryPoints_WithGenerationCapturedBeforeClear_AreRejectedAfterReset()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var repository = new Mock<IVideoCatalogRepository>();
        var store = new Mock<IAniDbCatalogStore>();
        store.Setup(item => item.ClaimImportJobAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AniDbImportJob?)null);
        store.Setup(item => item.ClaimMyListJobAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AniDbMyListJob?)null);
        store.Setup(item => item.ClearScrapingRecordsAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var manualAssetId = Guid.NewGuid();
        var manualIdentity = new VideoManualAniDbIdentity(
            manualAssetId,
            ImmutableHashSet.Create(101),
            ImmutableHashSet.Create(1001));
        store.Setup(item => item.GetManualCatalogIdentitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([manualIdentity]);
        var configuration = new Mock<IAniDbConfigurationProvider>();
        var udp = new Mock<IAniDbUdpClient>();
        var http = new Mock<IAniDbHttpClient>();
        await using var service = new AniDbImportService(
            repository.Object,
            store.Object,
            configuration.Object,
            Mock.Of<IAniDbEd2kHasher>(),
            udp.Object,
            http.Object,
            new VideoPlaybackHistoryStore(Path.Combine(temp.Path, "history.json")),
            NullLogger<AniDbImportService>.Instance);
        var sourceId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var oldGeneration = service.ScrapeGeneration;
        var oldAdmission = service.CaptureScrapeAdmission();
        var resetEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyCollection<VideoManualAniDbIdentity>? capturedManualIdentities = null;

        var clear = service.ClearScrapingRecordsAsync(
            async (manualAssets, resetToken) =>
            {
                capturedManualIdentities = manualAssets;
                resetEntered.TrySetResult();
                await releaseReset.Task.WaitAsync(resetToken);
            },
            ct);
        await resetEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        var duringResetAdmission = service.CaptureScrapeAdmission();
        try
        {
            await service.QueueSourceAsync(sourceId, ct).WaitAsync(TimeSpan.FromSeconds(2), ct);
            await service.QueueAssetAsync(assetId, ct).WaitAsync(TimeSpan.FromSeconds(2), ct);
            await service.QueueMyListStateAsync("asset-key", watched: true, ct)
                .WaitAsync(TimeSpan.FromSeconds(2), ct);
        }
        finally
        {
            releaseReset.TrySetResult();
        }
        await clear.WaitAsync(TimeSpan.FromSeconds(2), ct);
        await service.QueueSourceAsync(sourceId, oldGeneration, ct);
        await service.QueueSourceAsync(sourceId, oldAdmission, ct);
        await service.QueueSourceAsync(sourceId, duringResetAdmission, ct);
        await service.QueueAssetAsync(assetId, oldGeneration, ct);

        service.ScrapeGeneration.Should().Be(oldGeneration + 1);
        oldAdmission.Should().Be(new AniDbScrapeAdmissionStamp(oldGeneration, false));
        duringResetAdmission.Should().Be(
            new AniDbScrapeAdmissionStamp(oldGeneration + 1, true));
        capturedManualIdentities.Should().Equal(manualIdentity);
        repository.Verify(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(item => item.EnqueueImportJobAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CatalogReset_ClearsOldAssetErrorWithoutRemovingMyListSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var store = new AniDbCatalogStore(Path.Combine(temp.Path, "anidb.sqlite3"));
        var assetId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var myList = new AniDbMyListEntry(
            11,
            301,
            1001,
            101,
            AniDbMyListState.OnHdd,
            true,
            now,
            now);
        await store.UpsertHashAsync(
            assetId,
            @"C:\Anime\episode.mkv",
            new AniDbEd2kHash(
                "31d6cfe0d16ae931b73c59d7e0c089c0",
                100,
                now,
                now),
            ct);
        await store.UpsertMyListAsync(assetId, myList, "old scrape error", ct);

        await store.ClearScrapingRecordsAsync(ct);

        var asset = await store.GetAssetAsync(assetId, ct);
        asset.Should().NotBeNull();
        asset!.LastError.Should().BeNull();
        asset.MyList.Should().BeEquivalentTo(myList);
    }

    [Fact]
    public async Task LinkManualRelease_AndGlobalClear_AreSerializedByTheResetGate()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var repository = new Mock<IVideoCatalogRepository>();
        var store = new Mock<IAniDbCatalogStore>();
        store.Setup(item => item.ClaimImportJobAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AniDbImportJob?)null);
        store.Setup(item => item.ClaimMyListJobAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AniDbMyListJob?)null);
        var order = new ConcurrentQueue<string>();
        var linkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLink = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manualAssetId = Guid.NewGuid();
        const string manualHash = "31d6cfe0d16ae931b73c59d7e0c089c0";
        store.Setup(item => item.LinkManualReleaseAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<AniDbManualReleaseLink>(),
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                order.Enqueue("link-start");
                linkEntered.TrySetResult();
                await releaseLink.Task;
                order.Enqueue("link-finish");
            });
        store.Setup(item => item.GetAssetsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new AniDbAssetSnapshot(
                    manualAssetId, manualHash, 100, null, null, null, null, null),
            ]);
        store.Setup(item => item.EnqueueImportJobAsync(
                manualAssetId, It.IsAny<CancellationToken>()))
            .Callback(() => order.Enqueue("enqueue"))
            .Returns(Task.CompletedTask);
        var manualIdentity = new VideoManualAniDbIdentity(
            manualAssetId,
            ImmutableHashSet.Create(101),
            ImmutableHashSet.Create(1001));
        store.Setup(item => item.GetManualCatalogIdentitiesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => order.Enqueue("manual-snapshot"))
            .ReturnsAsync([manualIdentity]);
        store.Setup(item => item.ClearScrapingRecordsAsync(It.IsAny<CancellationToken>()))
            .Callback(() => order.Enqueue("store-clear"))
            .Returns(Task.CompletedTask);
        await using var service = new AniDbImportService(
            repository.Object,
            store.Object,
            Mock.Of<IAniDbConfigurationProvider>(),
            Mock.Of<IAniDbEd2kHasher>(),
            Mock.Of<IAniDbUdpClient>(),
            Mock.Of<IAniDbHttpClient>(),
            new VideoPlaybackHistoryStore(Path.Combine(temp.Path, "history.json")),
            NullLogger<AniDbImportService>.Instance);
        var manualLink = new AniDbManualReleaseLink(
            301,
            101,
            [new AniDbFileEpisodeLink(1001, 100, false, 0) { AnimeId = 101 }]);

        var link = service.LinkManualReleaseAsync(
            manualHash, 100, manualLink, ct);
        await linkEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        var clear = service.ClearScrapingRecordsAsync(
            (manualAssets, _) =>
            {
                manualAssets.Should().Equal(manualIdentity);
                order.Enqueue("catalog-clear");
                return Task.CompletedTask;
            },
            ct);
        try
        {
            order.Should().Equal("link-start");
        }
        finally
        {
            releaseLink.TrySetResult();
        }

        await Task.WhenAll(link, clear).WaitAsync(TimeSpan.FromSeconds(2), ct);

        order.Should().Equal(
            "link-start",
            "link-finish",
            "enqueue",
            "manual-snapshot",
            "catalog-clear",
            "store-clear");
    }

    [Fact]
    public async Task ClearScrapingRecords_WhenCatalogCleanupFails_DoesNotClearAniDbStore()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var store = new Mock<IAniDbCatalogStore>();
        store.Setup(item => item.ClaimImportJobAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AniDbImportJob?)null);
        store.Setup(item => item.ClaimMyListJobAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AniDbMyListJob?)null);
        store.Setup(item => item.GetManualCatalogIdentitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        store.Setup(item => item.ClearScrapingRecordsAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        await using var service = new AniDbImportService(
            Mock.Of<IVideoCatalogRepository>(),
            store.Object,
            Mock.Of<IAniDbConfigurationProvider>(),
            Mock.Of<IAniDbEd2kHasher>(),
            Mock.Of<IAniDbUdpClient>(),
            Mock.Of<IAniDbHttpClient>(),
            new VideoPlaybackHistoryStore(Path.Combine(temp.Path, "history.json")),
            NullLogger<AniDbImportService>.Instance);

        var action = () => service.ClearScrapingRecordsAsync(
            (_, _) => Task.FromException(new IOException("catalog cleanup failed")),
            ct);

        await action.Should().ThrowAsync<IOException>()
            .WithMessage("catalog cleanup failed");
        store.Verify(item => item.GetManualCatalogIdentitiesAsync(
            It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(item => item.ClearScrapingRecordsAsync(
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Mock<IAniDbCatalogStore> IdleStore()
    {
        var store = new Mock<IAniDbCatalogStore>();
        store.Setup(item => item.ClaimImportJobAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AniDbImportJob?)null);
        store.Setup(item => item.ClaimMyListJobAsync(
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AniDbMyListJob?)null);
        return store;
    }

    private static AniDbImportService CreateService(
        TempDirectory temp,
        IVideoCatalogRepository repository,
        IAniDbCatalogStore store,
        IAniDbConfigurationProvider? configuration = null,
        IAniDbEd2kHasher? hasher = null) =>
        new(
            repository,
            store,
            configuration ?? Mock.Of<IAniDbConfigurationProvider>(),
            hasher ?? Mock.Of<IAniDbEd2kHasher>(),
            Mock.Of<IAniDbUdpClient>(),
            Mock.Of<IAniDbHttpClient>(),
            new VideoPlaybackHistoryStore(Path.Combine(temp.Path, "history.json")),
            NullLogger<AniDbImportService>.Instance);

    private static AniDbClientConfiguration EnabledConfiguration(bool myListSyncEnabled = false) =>
        new(
            "niratan_test", 1, "user", "password", 45500,
            true, myListSyncEnabled, false, AniDbMyListState.OnHdd, 0);

    private static VideoCatalogAssetSnapshot CatalogAsset(
        Guid assetId,
        string identityKey,
        bool catalogResetPending)
    {
        var now = DateTimeOffset.UtcNow;
        return new VideoCatalogAssetSnapshot(
            assetId, identityKey, VideoMediaAssetKind.LocalFile,
            identityKey, "Reset Show", "Anime", 1,
            now, now, now, VideoMediaAvailability.Available, 1, 1,
            null, null, null, null, null, null, null, null, false, [],
            null, null, null, [], [], [])
        {
            CatalogResetPending = catalogResetPending,
        };
    }
}
