using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Niratan.Models.Video;
using Niratan.Services.Novels;
using Niratan.Services.Storage;
using Niratan.Services.Video;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Storage;

public sealed class SQLiteVideoCatalogRepositoryTests
{
    [Fact]
    public async Task Migration_IsAtomicAndPreservesLegacyAndHistoryBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var catalogPath = Path.Combine(temp.Path, "video_library.json");
        var databasePath = Path.Combine(temp.Path, "video_library.sqlite3");
        var historyPath = Path.Combine(temp.Path, "video_playback_history.json");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var mediaPath = Path.Combine(sourcePath, "作品 S01E01.mkv");
        await File.WriteAllBytesAsync(mediaPath, [1, 2, 3, 4], ct);
        await File.WriteAllTextAsync(historyPath, "{\"positions\":{}}", ct);
        var sourceId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        var document = new VideoLibraryCatalogDocument
        {
            Sources = [new VideoLibrarySourceDocument { Id = sourceId, Name = "Anime", Path = sourcePath }],
            Items = [new VideoLibraryItemDocument
            {
                Path = mediaPath,
                SourceID = sourceId,
                Title = "作品 S01E01",
                ParentFolder = "Anime",
                FileSize = 4,
                LastSeenAt = DateTimeOffset.UtcNow,
                MediaIdentity = VideoMediaIdentityDocument.Local(mediaPath),
            }],
            Collections = [new VideoLibraryCollectionDocument
            {
                Id = collectionId,
                Name = "Watchlist",
                ItemPaths = [mediaPath],
            }],
        };
        await new NiratanJsonFileStore().WriteAsync(catalogPath, document, ct);
        var catalogHash = SHA256.HashData(await File.ReadAllBytesAsync(catalogPath, ct));
        var historyHash = SHA256.HashData(await File.ReadAllBytesAsync(historyPath, ct));

        await using var repository = Create(databasePath, catalogPath);
        var result = await repository.InitializeAsync(ct);

        result.Mode.Should().Be(VideoCatalogMode.Sqlite);
        result.Snapshot.Sources.Should().ContainSingle();
        result.Snapshot.Assets.Should().ContainSingle();
        result.Snapshot.Collections.Should().ContainSingle();
        SHA256.HashData(await File.ReadAllBytesAsync(catalogPath, ct)).Should().Equal(catalogHash);
        SHA256.HashData(await File.ReadAllBytesAsync(historyPath, ct)).Should().Equal(historyHash);
        await AssertHealthyAsync(databasePath, ct);
    }

    [Fact]
    public async Task Initialize_WaitsOutAnotherInstanceHoldingTheMigrationLock()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var catalogPath = Path.Combine(temp.Path, "video_library.json");
        var databasePath = Path.Combine(temp.Path, "video_library.sqlite3");
        await new NiratanJsonFileStore().WriteAsync(catalogPath, new VideoLibraryCatalogDocument(), ct);

        var holder = new FileStream(
            databasePath + ".migration.lock",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        var releasedAt = (DateTimeOffset?)null;
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
            releasedAt = DateTimeOffset.UtcNow;
            await holder.DisposeAsync();
        }, ct);

        await using var repository = Create(databasePath, catalogPath);
        var result = await repository.InitializeAsync(ct);
        var acquiredAt = DateTimeOffset.UtcNow;
        await release;

        // Initialization must block on the other instance rather than surface its IOException.
        result.Mode.Should().Be(VideoCatalogMode.Sqlite);
        releasedAt.Should().NotBeNull();
        acquiredAt.Should().BeOnOrAfter(releasedAt!.Value);
        await AssertHealthyAsync(databasePath, ct);
    }

    [Fact]
    public void MigrationLockBudget_IsDecoupledFromTheSqliteBusyTimeout()
    {
        var source = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "Niratan", "Services", "Storage", "SQLiteVideoCatalogRepository.cs")));

        // The migration lock covers a whole legacy migration plus compatibility repairs. Sizing
        // it with the 5s per-statement busy timeout made a concurrent first launch throw
        // IOException on a slow disk instead of waiting, which is what broke the v0.11.0 build.
        source.Should().Contain("MigrationLockTimeoutMilliseconds");
        source.Should().Contain(
            "var deadline = DateTimeOffset.UtcNow.AddMilliseconds(MigrationLockTimeoutMilliseconds);");
        source.Should().NotContain(
            "var deadline = DateTimeOffset.UtcNow.AddMilliseconds(BusyTimeoutMilliseconds);");
    }

    [Fact]
    public async Task ConcurrentMigration_ProducesOneHealthyDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var catalogPath = Path.Combine(temp.Path, "video_library.json");
        var databasePath = Path.Combine(temp.Path, "video_library.sqlite3");
        await new NiratanJsonFileStore().WriteAsync(catalogPath, new VideoLibraryCatalogDocument(), ct);
        await using var first = Create(databasePath, catalogPath);
        await using var second = Create(databasePath, catalogPath);

        var results = await Task.WhenAll(first.InitializeAsync(ct), second.InitializeAsync(ct));

        results.Should().OnlyContain(result => result.Mode == VideoCatalogMode.Sqlite);
        await AssertHealthyAsync(databasePath, ct);
    }

    [Fact]
    public async Task ScanBatch_RejectsStaleGenerationAndNeverChangesSourceMedia()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var databasePath = Path.Combine(temp.Path, "video_library.sqlite3");
        var legacyPath = Path.Combine(temp.Path, "video_library.json");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var mediaPath = Path.Combine(sourcePath, "作品 S01E01.mkv");
        await File.WriteAllBytesAsync(mediaPath, [1, 2, 3, 4, 5], ct);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(mediaPath, ct));
        var sourceId = Guid.NewGuid();
        await using var repository = Create(databasePath, legacyPath);
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"),
            Name = "Anime",
            FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var staleGeneration = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.IncrementalScan, ct);
        var currentGeneration = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, ct);
        var parser = new VideoFileNameParser();
        var asset = new VideoScanAsset(
            new VideoCatalogAssetUpsert(
                mediaPath,
                VideoMediaAssetKind.LocalFile,
                mediaPath,
                "作品 S01E01",
                "Anime",
                5,
                File.GetLastWriteTimeUtc(mediaPath),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                VideoMediaAvailability.Available,
                sourceId),
            parser.Parse(mediaPath, sourcePath, VideoLibraryMediaType.Anime));

        var staleAccepted = await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, staleGeneration, DateTimeOffset.UtcNow, [asset], true), ct);
        var currentAccepted = await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, currentGeneration, DateTimeOffset.UtcNow, [asset], true), ct);

        staleAccepted.Should().BeFalse();
        currentAccepted.Should().BeTrue();
        var snapshot = await repository.GetSnapshotAsync(ct);
        snapshot.Assets.Should().ContainSingle();
        snapshot.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Series);
        snapshot.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Season);
        snapshot.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Episode);
        snapshot.Nodes.Should().NotContain(node => node.Kind == VideoCatalogNodeKind.Unmatched);
        SHA256.HashData(await File.ReadAllBytesAsync(mediaPath, ct)).Should().Equal(sourceHash);
    }

    [Fact]
    public async Task SourceScanGenerationCompareAndSwap_RejectsStaleBeginAndCancel()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourceId = Guid.NewGuid();
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"),
            Name = "Anime",
            FolderPath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);

        var initialGeneration = (await repository.GetSnapshotAsync(ct)).Sources.Single().ScanGeneration;
        var firstGeneration = await repository.TryBeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, initialGeneration, ct);
        var staleBegin = await repository.TryBeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, initialGeneration, ct);
        var secondGeneration = await repository.TryBeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, firstGeneration!.Value, ct);
        var staleCancel = await repository.CancelSourceScanAsync(
            sourceId, firstGeneration.Value, ct);

        firstGeneration.Should().Be(initialGeneration + 1);
        staleBegin.Should().BeNull();
        secondGeneration.Should().Be(firstGeneration.Value + 1);
        staleCancel.Should().BeFalse();
        var afterStaleOperations = await repository.GetSnapshotAsync(ct);
        afterStaleOperations.Sources.Single().ScanGeneration.Should().Be(secondGeneration.Value);
        afterStaleOperations.Jobs.Should().ContainSingle(job =>
            job.Generation == secondGeneration.Value && job.State == VideoCatalogJobState.Running);

        (await repository.CancelSourceScanAsync(sourceId, secondGeneration.Value, ct)).Should().BeTrue();
        var cancelled = await repository.GetSnapshotAsync(ct);
        cancelled.Sources.Single().ScanGeneration.Should().Be(secondGeneration.Value + 1);
        cancelled.Jobs.Should().ContainSingle(job =>
            job.Generation == secondGeneration.Value && job.State == VideoCatalogJobState.Cancelled);
    }

    [Fact]
    public async Task IncrementalScan_PromotesUnchangedParsedEpisodesIntoOneLocalSeries()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Series")).FullName;
        var sourceId = Guid.NewGuid();
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"),
            Name = "Series",
            FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Auto,
        }, ct);

        var now = DateTimeOffset.UtcNow;
        var paths = new[]
        {
            Path.Combine(sourcePath, "作品 - 08.mkv"),
            Path.Combine(sourcePath, "作品 - 09.mkv"),
        };
        for (var index = 0; index < paths.Length; index++)
        {
            await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
                paths[index], VideoMediaAssetKind.LocalFile, paths[index], "作品", "Series",
                10 + index, now, now, now, VideoMediaAvailability.Available, sourceId,
                EpisodeStart: 8 + index, EpisodeEnd: 8 + index), ct);
        }

        var generation = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.IncrementalScan, ct);
        var batch = paths.Select((path, index) => new VideoScanAsset(
            new VideoCatalogAssetUpsert(
                path, VideoMediaAssetKind.LocalFile, path, "作品", "Series", 10 + index,
                now, now, now, VideoMediaAvailability.Available, sourceId,
                EpisodeStart: 8 + index, EpisodeEnd: 8 + index),
            new ParsedVideoIdentity(
                Path.GetFileNameWithoutExtension(path), "作品", null, null, null,
                8 + index, 8 + index, 8 + index, null, null, ParsedVideoSpecialKind.None,
                false, true, System.Collections.Immutable.ImmutableDictionary<string, string>.Empty, []),
            SkipMetadataProcessing: true)).ToArray();

        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, generation, now, batch, true), ct)).Should().BeTrue();

        var snapshot = await repository.GetSnapshotAsync(ct);
        snapshot.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Series);
        snapshot.Nodes.Count(node => node.Kind == VideoCatalogNodeKind.Episode).Should().Be(2);
        snapshot.Nodes.Should().NotContain(node => node.Kind == VideoCatalogNodeKind.Unmatched);
        snapshot.Assets.Should().OnlyContain(asset => asset.NodeIds.Length == 1);
        snapshot.Assets.SelectMany(asset => asset.NodeIds).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task SeriesArtwork_IsStoredOnSeriesOwnerInsteadOfEpisode()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var mediaPath = Path.Combine(sourcePath, "作品 S01E01.mkv");
        var posterPath = Path.Combine(temp.Path, "series-poster.jpg");
        await File.WriteAllBytesAsync(mediaPath, [1], ct);
        await File.WriteAllBytesAsync(posterPath, [0xFF, 0xD8, 0xFF], ct);
        var sourceId = Guid.NewGuid();
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Anime", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var generation = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, ct);
        var parser = new VideoFileNameParser();
        var parsed = parser.Parse(mediaPath, sourcePath, VideoLibraryMediaType.Anime);
        var now = DateTimeOffset.UtcNow;
        var scanAsset = new VideoScanAsset(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, parsed.NormalizedTitle, "Anime",
            1, now, now, now, VideoMediaAvailability.Available, sourceId,
            parsed.EpisodeStart, parsed.EpisodeEnd), parsed);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, generation, now, [scanAsset], true), ct)).Should().BeTrue();
        var assetId = (await repository.GetSnapshotAsync(ct)).Assets.Single().Id;

        await repository.ApplyArtworkAsync(
            assetId, VideoMetadataMediaKind.Series, "tmdb", "poster",
            "https://image.tmdb.org/series.jpg", posterPath, null, now, ct);

        var snapshot = await repository.GetSnapshotAsync(ct);
        snapshot.Nodes.Single(node => node.Kind == VideoCatalogNodeKind.Series).PosterPath
            .Should().Be(posterPath);
        snapshot.Nodes.Single(node => node.Kind == VideoCatalogNodeKind.Episode).PosterPath
            .Should().BeNull();
    }

    [Fact]
    public async Task ArtworkCandidates_PersistShokoStateAndKeepExistingAndUserPreferenceStable()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var mediaPath = Path.Combine(temp.Path, "Show S01E01.mkv");
        var anidbPath = Path.Combine(temp.Path, "anidb.jpg");
        var tmdbPath = Path.Combine(temp.Path, "tmdb.jpg");
        var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        await using var repository = Create(database, Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "Show", temp.Path,
            1, now, now, now, VideoMediaAvailability.Available,
            EpisodeStart: 1, EpisodeEnd: 1), ct);
        var assetId = (await repository.GetSnapshotAsync(ct)).Assets.Single().Id;
        var anidb = new VideoArtworkCandidate(
            "anidb", "https://cdn.anidb.net/images/main/1.jpg", "poster",
            "ja", 680, 1000, "https://anidb.net/anime/1")
        {
            OwnerKind = VideoMetadataMediaKind.Series,
            IsEnabled = true,
            IsDesired = true,
            IsPreferred = true,
            Ordinal = 0,
        };
        var tmdb = new VideoArtworkCandidate(
            "tmdb", "https://image.tmdb.org/t/p/original/2.jpg", "poster",
            "en", 1000, 1500, "https://www.themoviedb.org/tv/2")
        {
            OwnerKind = VideoMetadataMediaKind.Series,
            IsEnabled = true,
            IsDesired = true,
            Ordinal = 1,
        };

        await repository.UpsertArtworkCandidateAsync(
            assetId, VideoMetadataMediaKind.Anime, anidb, anidbPath,
            "\"anidb\"", now, downloadAttempted: true, ct: ct);
        await repository.UpsertArtworkCandidateAsync(
            assetId, VideoMetadataMediaKind.Anime, tmdb, tmdbPath,
            "\"tmdb\"", now, downloadAttempted: true, ct: ct);

        var initial = (await repository.GetSnapshotAsync(ct)).Nodes.Single();
        initial.PosterPath.Should().Be(anidbPath);
        initial.ArtworkCandidates.Should().HaveCount(2);
        initial.ArtworkCandidates.Single(item => item.ProviderId == "anidb").Should().Match<VideoCatalogArtworkSnapshot>(
            item => item.Language == "ja"
                    && item.Width == 680
                    && item.Height == 1000
                    && item.IsEnabled
                    && item.IsDesired
                    && item.IsPreferred
                    && item.IsSelected
                    && item.Ordinal == 0
                    && item.DownloadAttempts == 1);

        var tmdbId = initial.ArtworkCandidates.Single(item => item.ProviderId == "tmdb").Id;
        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO node_user_data(node_id,preferred_artwork_id,updated_at)
                VALUES($node,$artwork,$now)
                ON CONFLICT(node_id) DO UPDATE SET preferred_artwork_id=excluded.preferred_artwork_id,
                    updated_at=excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$node", initial.Id.ToString("D"));
            command.Parameters.AddWithValue("$artwork", tmdbId.ToString("D"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }

        await repository.UpsertArtworkCandidateAsync(
            assetId, VideoMetadataMediaKind.Anime, anidb with { Ordinal = 7 }, anidbPath,
            "\"anidb-v2\"", now.AddDays(1), downloadAttempted: false, ct: ct);
        var refreshed = (await repository.GetSnapshotAsync(ct)).Nodes.Single();
        refreshed.PosterPath.Should().Be(tmdbPath, "an explicit user preference must survive refresh");
        refreshed.ArtworkCandidates.Single(item => item.ProviderId == "anidb").IsSelected.Should().BeTrue();
        refreshed.ArtworkCandidates.Single(item => item.ProviderId == "tmdb").IsUserPreferred.Should().BeTrue();
    }

    [Fact]
    public async Task ArtworkCandidateCompatibility_AddsColumnsWithoutReplacingExistingRows()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var legacy = Path.Combine(temp.Path, "video_library.json");
        var mediaPath = Path.Combine(temp.Path, "Show.mkv");
        var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        Guid nodeId;
        Guid artworkId;
        await using (var repository = Create(database, legacy))
        {
            await repository.InitializeAsync(ct);
            await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
                mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "Show", temp.Path,
                1, now, now, now, VideoMediaAvailability.Available), ct);
            var snapshot = await repository.GetSnapshotAsync(ct);
            nodeId = snapshot.Nodes.Single().Id;
            artworkId = Guid.NewGuid();
        }

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                DROP INDEX IF EXISTS ux_artwork_identity_nullsafe;
                ALTER TABLE artwork RENAME TO artwork_with_candidate_state;
                CREATE TABLE artwork(
                    id TEXT PRIMARY KEY, node_id TEXT NOT NULL REFERENCES catalog_nodes(id) ON DELETE CASCADE,
                    provider_id TEXT NOT NULL, kind TEXT NOT NULL, remote_url TEXT NULL, local_path TEXT NULL,
                    etag TEXT NULL, last_modified TEXT NULL, selected INTEGER NOT NULL DEFAULT 0,
                    ordinal INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL,
                    UNIQUE(node_id,provider_id,kind,local_path,remote_url));
                INSERT INTO artwork(id,node_id,provider_id,kind,remote_url,local_path,selected,ordinal,created_at)
                VALUES($id,$node,'anidb','poster','https://cdn.anidb.net/images/main/legacy.jpg',
                    $path,1,3,$now);
                DROP TABLE artwork_with_candidate_state;
                """;
            command.Parameters.AddWithValue("$id", artworkId.ToString("D"));
            command.Parameters.AddWithValue("$node", nodeId.ToString("D"));
            command.Parameters.AddWithValue("$path", Path.Combine(temp.Path, "legacy.jpg"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }

        await using var reopened = Create(database, legacy);
        var migrated = await reopened.InitializeAsync(ct);

        var artwork = migrated.Snapshot.Nodes.Single().ArtworkCandidates.Should().ContainSingle().Subject;
        artwork.Id.Should().Be(artworkId);
        artwork.IsSelected.Should().BeTrue();
        artwork.IsPreferred.Should().BeTrue("legacy selected artwork becomes the stable preferred candidate");
        artwork.IsEnabled.Should().BeTrue();
        artwork.IsDesired.Should().BeTrue();
        artwork.Ordinal.Should().Be(3);
        artwork.DownloadAttempts.Should().Be(0);
    }

    [Fact]
    public async Task MetadataJob_PersistsProgressAndIsNotChangedByScanControls()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var sourceId = Guid.NewGuid();
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Anime", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        await repository.BeginSourceScanAsync(sourceId, VideoCatalogJobKind.IncrementalScan, ct);
        var metadataJobId = await repository.BeginMetadataRefreshAsync(sourceId, 12, ct);

        await repository.UpdateMetadataRefreshAsync(
            metadataJobId, VideoCatalogJobState.Running, 5, null, ct);
        await repository.UpdateMetadataRefreshCountsAsync(metadataJobId, 3, 1, ct, 2);
        await repository.SetSourceScanPausedAsync(sourceId, true, ct);
        var running = await repository.GetSnapshotAsync(ct);

        running.Jobs.Single(job => job.Id == metadataJobId).State.Should().Be(VideoCatalogJobState.Running);
        running.Jobs.Single(job => job.Id == metadataJobId).ProcessedCount.Should().Be(5);
        running.Jobs.Single(job => job.Id == metadataJobId).MatchedCount.Should().Be(3);
        running.Jobs.Single(job => job.Id == metadataJobId).NeedsReviewCount.Should().Be(1);
        running.Jobs.Single(job => job.Id == metadataJobId).FailedCount.Should().Be(2);
        running.Jobs.Single(job => job.Kind == VideoCatalogJobKind.IncrementalScan).State
            .Should().Be(VideoCatalogJobState.Paused);

        await repository.UpdateMetadataRefreshAsync(
            metadataJobId, VideoCatalogJobState.Completed, 12, null, ct);
        (await repository.GetSnapshotAsync(ct)).Jobs.Single(job => job.Id == metadataJobId).State
            .Should().Be(VideoCatalogJobState.Completed);
    }

    [Fact]
    public async Task FreshDatabase_DoesNotInvalidateCompletedMetadataJobOnReopen()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var legacy = Path.Combine(temp.Path, "video_library.json");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var mediaPath = Path.Combine(sourcePath, "作品 - 08.mkv");
        var sourceId = Guid.NewGuid();
        await using (var repository = Create(database, legacy))
        {
            await repository.InitializeAsync(ct);
            await repository.UpsertSourceAsync(new VideoLibrarySource
            {
                Id = sourceId.ToString("D"), Name = "Anime", FolderPath = sourcePath,
                MediaType = VideoLibraryMediaType.Auto,
            }, ct);
            var now = DateTimeOffset.UtcNow;
            var generation = await repository.BeginSourceScanAsync(
                sourceId, VideoCatalogJobKind.IncrementalScan, ct);
            var parsed = new ParsedVideoIdentity(
                "作品 - 08", "作品", null, null, null, 8, 8, 8, null, null,
                ParsedVideoSpecialKind.None, false, true,
                System.Collections.Immutable.ImmutableDictionary<string, string>.Empty, []);
            var scanAsset = new VideoScanAsset(new VideoCatalogAssetUpsert(
                mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "作品", "Anime", 1,
                now, now, now, VideoMediaAvailability.Available, sourceId, 8, 8), parsed);
            (await repository.ApplyScanBatchAsync(new VideoScanBatch(
                sourceId, generation, now, [scanAsset], true), ct)).Should().BeTrue();
            var jobId = await repository.BeginMetadataRefreshAsync(sourceId, 1, ct);
            await repository.UpdateMetadataRefreshAsync(
                jobId, VideoCatalogJobState.Completed, 1, null, ct);
        }

        await using (var reopened = Create(database, legacy))
        {
            var result = await reopened.InitializeAsync(ct);
            result.Snapshot.Jobs.Single(job => job.Kind == VideoCatalogJobKind.MetadataRefresh).State
                .Should().Be(VideoCatalogJobState.Completed);
        }
        await using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM migration_audit WHERE category='anilist-null-id-search-v9';";
        (await command.ExecuteScalarAsync(ct)).Should().Be(1L);
    }

    [Fact]
    public async Task MetadataSnapshot_InitializesOptionalCollectionsBeforeSerialization()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var mediaPath = Path.Combine(temp.Path, "作品 S01E01.mkv");
        await File.WriteAllBytesAsync(mediaPath, [1], ct);
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "作品 S01E01", temp.Path,
            1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            VideoMediaAvailability.Available, EpisodeStart: 1, EpisodeEnd: 1), ct);
        var assetId = (await repository.GetSnapshotAsync(ct)).Assets.Single().Id;
        var candidate = new VideoMetadataCandidate(
            "anilist", "42", VideoMetadataMediaKind.Anime, "作品", "作品 原題", 2024,
            1, 1, null, ["作品"],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("anilist", "42"),
            "https://anilist.co/anime/42");
        var now = DateTimeOffset.UtcNow;
        var details = new VideoMetadataDetails(
            "anilist", "42", VideoMetadataMediaKind.Anime, "作品", "作品 原題", null,
            "あらすじ", 2024, 1, 1, null, ["作品"], ["Animation"], [],
            candidate.ExternalIds, candidate.SourceUrl, now, now.AddDays(30));

        await repository.ApplyMetadataMatchAsync(assetId, candidate, details, false, false, ct);
        var series = (await repository.GetSnapshotAsync(ct)).Nodes
            .Single(node => node.Kind == VideoCatalogNodeKind.Series);

        series.Genres.Should().Contain("Animation");
        series.Tags.Should().BeEmpty();
        series.Studios.Should().BeEmpty();
        series.People.Should().BeEmpty();
        series.RelatedItems.Should().BeEmpty();
    }

    [Fact]
    public async Task AnimeMetadata_KeepsAniDbAsLockedIdentityWhileTmdbEnrichesDetails()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var mediaPath = Path.Combine(temp.Path, "Re Zero - 01 [anidbid-11370].mkv");
        await File.WriteAllBytesAsync(mediaPath, [1], ct);
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        var now = DateTimeOffset.UtcNow;
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "Re Zero", temp.Path,
            1, now, now, now, VideoMediaAvailability.Available, EpisodeStart: 1, EpisodeEnd: 1), ct);
        var assetId = (await repository.GetSnapshotAsync(ct)).Assets.Single().Id;
        var aniDb = new VideoMetadataCandidate(
            "anidb", "11370", VideoMetadataMediaKind.Anime,
            "Re:Zero kara Hajimeru Isekai Seikatsu", "Re:ゼロから始める異世界生活", 2016,
            null, 1, 1, ["Re:ゼロから始める異世界生活"],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("anidb", "11370"),
            "https://anidb.net/anime/11370");
        var tmdbDetails = new VideoMetadataDetails(
            "tmdb", "65942", VideoMetadataMediaKind.Anime,
            "Re:ゼロから始める異世界生活", "Re:ゼロから始める異世界生活", null,
            "TMDB overview", 2016, null, null, null,
            ["Re:Zero − Starting Life in Another World"], ["Animation"], [],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("tmdb", "65942"),
            "https://www.themoviedb.org/tv/65942", now, now.AddDays(30));

        await repository.ApplyMetadataMatchAsync(
            assetId, aniDb, tmdbDetails, lockIdentity: true, preserveExistingHierarchy: false, ct);

        var series = (await repository.GetSnapshotAsync(ct)).Nodes
            .Single(node => node.Kind == VideoCatalogNodeKind.Series);
        series.PrimaryTitle.Should().Be("Re:ゼロから始める異世界生活");
        series.Overview.Should().Be("TMDB overview");
        series.ExternalIds.Should().Contain("anidb", "11370");
        series.ExternalIds.Should().Contain("tmdb", "65942");
        series.IdentityLockedProviders.Should().BeEquivalentTo("anidb");
    }

    [Fact]
    public async Task AnimeMetadata_KeepsDistinctAniDbAnimeSeriesWhenTmdbCrossReferenceIsShared()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var firstPath = Path.Combine(temp.Path, "Re Zero - 01 [anidbid-11370].mkv");
        var secondPath = Path.Combine(temp.Path, "Re Zero Season 2 - 01 [anidbid-15632].mkv");
        await File.WriteAllBytesAsync(firstPath, [1], ct);
        await File.WriteAllBytesAsync(secondPath, [2], ct);

        foreach (var (path, title) in new[]
                 {
                     (firstPath, "Re Zero"),
                     (secondPath, "Re Zero Season 2"),
                 })
        {
            await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
                path, VideoMediaAssetKind.LocalFile, path, title, temp.Path,
                1, now, now, now, VideoMediaAvailability.Available,
                EpisodeStart: 1, EpisodeEnd: 1), ct);
        }

        var assets = (await repository.GetSnapshotAsync(ct)).Assets
            .ToDictionary(asset => asset.Location, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, aniDbId, title, year) in new[]
                 {
                     (firstPath, "11370", "Re:ゼロから始める異世界生活", 2016),
                     (secondPath, "15632", "Re:ゼロから始める異世界生活 2nd season", 2020),
                 })
        {
            var candidate = new VideoMetadataCandidate(
                "anidb", aniDbId, VideoMetadataMediaKind.Anime, title, title, year,
                null, 1, 1, [title],
                System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("anidb", aniDbId),
                $"https://anidb.net/anime/{aniDbId}");
            var richDetails = new VideoMetadataDetails(
                "tmdb", "65942", VideoMetadataMediaKind.Anime, title, title, null,
                null, year, null, null, null, [title], [], [],
                System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("tmdb", "65942"),
                "https://www.themoviedb.org/tv/65942", now, now.AddDays(30));
            await repository.ApplyMetadataMatchAsync(
                assets[path].Id, candidate, richDetails,
                lockIdentity: true, preserveExistingHierarchy: false, ct);
        }

        var series = (await repository.GetSnapshotAsync(ct)).Nodes
            .Where(node => node.Kind == VideoCatalogNodeKind.Series)
            .ToArray();
        series.Should().HaveCount(2, "each AniDB AID is a Shoko-style AnimeSeries identity");
        series.Select(node => node.ExternalIds["anidb"])
            .Should().BeEquivalentTo("11370", "15632");
        series.Should().OnlyContain(node => node.ExternalIds["tmdb"] == "65942");
        series.Should().OnlyContain(node =>
            node.IdentityLockedProviders.SetEquals(new[] { "anidb" }));
    }

    [Fact]
    public async Task AniDbIdentity_DoesNotClaimSiblingAssetsFromSharedScannerSeries()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var firstPath = Path.Combine(sourcePath, "Shared Scanner Show S01E01.mkv");
        var secondPath = Path.Combine(sourcePath, "Shared Scanner Show S01E02.mkv");
        var independentPath = Path.Combine(sourcePath, "Independent Scanner Show S01E01.mkv");
        await File.WriteAllBytesAsync(firstPath, [1], ct);
        await File.WriteAllBytesAsync(secondPath, [2], ct);
        await File.WriteAllBytesAsync(independentPath, [3], ct);
        var now = new DateTimeOffset(2026, 8, 23, 18, 0, 0, TimeSpan.Zero);
        var sourceId = Guid.NewGuid();
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"),
            Name = "Anime",
            FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Auto,
        }, ct);

        var paths = new[] { firstPath, secondPath, independentPath };
        var parsedByPath = VideoScanBundleClassifier.Parse(
            paths, sourcePath, VideoLibraryMediaType.Auto, new VideoFileNameParser());
        var generation = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, ct);
        var scanAssets = paths.Select(path =>
        {
            var parsed = parsedByPath[path];
            return new VideoScanAsset(
                new VideoCatalogAssetUpsert(
                    path, VideoMediaAssetKind.LocalFile, path, parsed.NormalizedTitle,
                    sourcePath, 1, now, now, now, VideoMediaAvailability.Available,
                    sourceId, parsed.EpisodeStart, parsed.EpisodeEnd),
                parsed);
        }).ToArray();
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, generation, now, scanAssets, true), ct)).Should().BeTrue();

        static VideoCatalogNodeSnapshot OwnerFor(VideoCatalogSnapshot snapshot, string path)
        {
            var asset = snapshot.Assets.Single(item =>
                string.Equals(item.Location, path, StringComparison.OrdinalIgnoreCase));
            var node = snapshot.Nodes.Single(item => item.Id == asset.NodeIds.Single());
            while (node.Kind != VideoCatalogNodeKind.Series)
            {
                node.ParentId.Should().HaveValue();
                node = snapshot.Nodes.Single(item => item.Id == node.ParentId.Value);
            }
            return node;
        }

        var scanned = await repository.GetSnapshotAsync(ct);
        scanned.Nodes.Count(node => node.Kind == VideoCatalogNodeKind.Series).Should().Be(2);
        var sharedScannerSeriesId = OwnerFor(scanned, firstPath).Id;
        OwnerFor(scanned, secondPath).Id.Should().Be(sharedScannerSeriesId);
        var independentScannerSeriesId = OwnerFor(scanned, independentPath).Id;
        independentScannerSeriesId.Should().NotBe(sharedScannerSeriesId);
        var assetsByPath = scanned.Assets.ToDictionary(
            asset => asset.Location, StringComparer.OrdinalIgnoreCase);

        await repository.ApplyAniDbIdentityAsync(
            assetsByPath[firstPath].Id,
            CreateAniDbProjection(101, 1001, 1, now, "shared-scanner-group"),
            ct);

        var firstProjected = await repository.GetSnapshotAsync(ct);
        var aid101Owner = OwnerFor(firstProjected, firstPath);
        var untouchedOwner = OwnerFor(firstProjected, secondPath);
        aid101Owner.Id.Should().NotBe(sharedScannerSeriesId);
        aid101Owner.ExternalIds.Should().Contain("anidb", "101");
        untouchedOwner.Id.Should().Be(sharedScannerSeriesId);
        untouchedOwner.ExternalIds.Should().NotContainKey("anidb");

        await repository.ApplyAniDbIdentityAsync(
            assetsByPath[secondPath].Id,
            CreateAniDbProjection(202, 2002, 2, now, "shared-scanner-group"),
            ct);
        await repository.ApplyAniDbIdentityAsync(
            assetsByPath[independentPath].Id,
            CreateAniDbProjection(303, 3003, 1, now, "independent-scanner-group"),
            ct);

        var projected = await repository.GetSnapshotAsync(ct);
        aid101Owner = OwnerFor(projected, firstPath);
        var aid202Owner = OwnerFor(projected, secondPath);
        var aid303Owner = OwnerFor(projected, independentPath);
        aid101Owner.Id.Should().NotBe(aid202Owner.Id);
        aid101Owner.ExternalIds.Should().Contain("anidb", "101")
            .And.Contain("anidb-group", "shared-scanner-group");
        aid202Owner.ExternalIds.Should().Contain("anidb", "202")
            .And.Contain("anidb-group", "shared-scanner-group");
        aid303Owner.Id.Should().Be(independentScannerSeriesId,
            "other series in the same source must not prevent single-asset hierarchy reuse");
        aid303Owner.ExternalIds.Should().Contain("anidb", "303")
            .And.Contain("anidb-group", "independent-scanner-group");
    }

    [Fact]
    public async Task AniDbIdentity_ProjectsEveryEidUnderItsAuthoritativeAid()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var mediaPath = Path.Combine(temp.Path, "Combined Episodes 01-02.mkv");
        await File.WriteAllBytesAsync(mediaPath, [1], ct);
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "Combined Episodes 01-02", temp.Path,
            1, now, now, now, VideoMediaAvailability.Available,
            EpisodeStart: 1, EpisodeEnd: 2), ct);
        var assetId = (await repository.GetSnapshotAsync(ct)).Assets.Single().Id;
        var details = new VideoMetadataDetails(
            "anidb", "123", VideoMetadataMediaKind.Anime, "Series", "シリーズ", null,
            "Overview", 2024, null, null, null, ["Series"], [], [],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
                .Add("anidb", "123")
                .Add("tmdb", "456"),
            "https://anidb.net/anime/123", now, now.AddDays(30));
        var otherDetails = new VideoMetadataDetails(
            "anidb", "456", VideoMetadataMediaKind.Anime, "Other Series", "別作品", null,
            "Other overview", 2025, null, null, null, ["Other Series"], [], [],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("anidb", "456"),
            "https://anidb.net/anime/456", now, now.AddDays(30));
        var projection = new VideoAniDbIdentityProjection(
            123,
            789,
            "group-stable",
            details,
            [
                new VideoAniDbEpisodeProjection(
                    1001, 1, 1, "Episode 1", "第1話", null, 0, 50, false,
                    new DateOnly(2024, 1, 1)),
                new VideoAniDbEpisodeProjection(
                    1002, 1, 2, "Episode 2", "第2話", null, 1, 50, true,
                    new DateOnly(2024, 1, 8))
                {
                    AnimeId = 456,
                    AnimeGroupId = "group-other",
                    AnimeMetadata = otherDetails,
                },
            ]);

        await repository.ApplyAniDbIdentityAsync(assetId, projection, ct);

        var snapshot = await repository.GetSnapshotAsync(ct);
        snapshot.Nodes.Count(node => node.Kind == VideoCatalogNodeKind.Series).Should().Be(2);
        var series = snapshot.Nodes.Single(node =>
            node.Kind == VideoCatalogNodeKind.Series
            && node.ExternalIds.GetValueOrDefault("anidb") == "123");
        series.ExternalIds.Should().Contain("anidb", "123");
        series.ExternalIds.Should().Contain("anidb-group", "group-stable");
        series.ExternalIds.Should().Contain("tmdb", "456");
        series.ExternalIds.Should().NotContainKey("anidb-file");
        series.ExternalIds.Should().NotContainKey("anidb-episode");
        series.IdentityLockedProviders.Should().BeEquivalentTo("anidb");

        var episodes = snapshot.Nodes
            .Where(node => node.Kind == VideoCatalogNodeKind.Episode)
            .OrderBy(node => node.EpisodeNumber)
            .ToArray();
        episodes.Should().HaveCount(2);
        episodes.Select(node => node.ExternalIds["anidb-episode"])
            .Should().Equal("1001", "1002");
        episodes.Single(node => node.ExternalIds["anidb-episode"] == "1001")
            .ExternalIds.Should().Contain("anidb", "123");
        episodes.Single(node => node.ExternalIds["anidb-episode"] == "1002")
            .ExternalIds.Should().Contain("anidb", "456").And.Contain("anidb-group", "group-other");
        episodes.Should().OnlyContain(node =>
            node.IdentityLockedProviders.SetEquals(new[] { "anidb-episode" }));
        snapshot.Assets.Single().NodeIds.Should().BeEquivalentTo(episodes.Select(node => node.Id));
    }

    [Fact]
    public async Task MetadataSnapshot_ProjectsPersistedSeasonOrderingAfterRepositoryRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var databasePath = Path.Combine(temp.Path, "video_library.sqlite3");
        var legacyPath = Path.Combine(temp.Path, "video_library.json");
        var mediaPath = Path.Combine(temp.Path, "Show S01E01.mkv");
        await File.WriteAllBytesAsync(mediaPath, [1], ct);
        var now = DateTimeOffset.UtcNow;
        await using (var repository = Create(databasePath, legacyPath))
        {
            await repository.InitializeAsync(ct);
            await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
                mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "Show", temp.Path,
                1, now, now, now, VideoMediaAvailability.Available,
                EpisodeStart: 1, EpisodeEnd: 1), ct);
            var assetId = (await repository.GetSnapshotAsync(ct)).Assets.Single().Id;
            var candidate = new VideoMetadataCandidate(
                "tmdb", "77", VideoMetadataMediaKind.Series, "Show", "Show", 2024,
                null, null, null, ["Show"],
                System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("tmdb", "77"),
                "https://www.themoviedb.org/tv/77");
            var details = new VideoMetadataDetails(
                "tmdb", "77", VideoMetadataMediaKind.Series, "Show", "Show", null,
                "Overview", 2024, null, null, null, ["Show"], [], [], candidate.ExternalIds,
                candidate.SourceUrl, now, now.AddDays(30),
                Seasons:
                [
                    new VideoMetadataSeason(1, "Season 1", null, null, 1, null,
                        [new VideoMetadataEpisode(1, "One", null, null, null, null, null, null)]),
                    new VideoMetadataSeason(2, "Season 2", null, null, 2, null,
                        [
                            new VideoMetadataEpisode(1, "One", null, null, null, null, null, null),
                            new VideoMetadataEpisode(2, "Two", null, null, null, null, null, null),
                        ]),
                ]);
            await repository.ApplyMetadataMatchAsync(
                assetId, candidate, details, lockIdentity: true, preserveExistingHierarchy: false, ct);
        }

        await using var reopened = Create(databasePath, legacyPath);
        await reopened.InitializeAsync(ct);
        var series = (await reopened.GetSnapshotAsync(ct)).Nodes
            .Single(node => node.Kind == VideoCatalogNodeKind.Series);
        series.Seasons.Select(season => season.SeasonNumber).Should().Equal(1, 2);
        series.Seasons[1].Episodes.Should().HaveCount(2);
    }

    [Fact]
    public async Task TmdbCrossReferences_PersistTypedOrderingWithoutReplacingAniDbIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var databasePath = Path.Combine(temp.Path, "video_library.sqlite3");
        var legacyPath = Path.Combine(temp.Path, "video_library.json");
        var mediaPath = Path.Combine(temp.Path, "Show - 01.mkv");
        await File.WriteAllBytesAsync(mediaPath, [1], ct);
        var now = DateTimeOffset.UtcNow;

        await using (var repository = Create(databasePath, legacyPath))
        {
            await repository.InitializeAsync(ct);
            await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
                mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "Show", temp.Path,
                1, now, now, now, VideoMediaAvailability.Available,
                EpisodeStart: 1, EpisodeEnd: 1), ct);
            var assetId = (await repository.GetSnapshotAsync(ct)).Assets.Single().Id;
            var aniDbDetails = new VideoMetadataDetails(
                "anidb", "123", VideoMetadataMediaKind.Anime, "Show", "Show", null,
                "AniDB overview", 2024, null, null, null, ["Show"], [], [],
                System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("anidb", "123"),
                "https://anidb.net/anime/123", now, now.AddDays(30));
            await repository.ApplyAniDbIdentityAsync(
                assetId,
                new VideoAniDbIdentityProjection(
                    123,
                    456,
                    "group-123",
                    aniDbDetails,
                    [new VideoAniDbEpisodeProjection(
                        1001, 1, 1, "One", "One", null, 0, 100, false,
                        new DateOnly(2024, 1, 1)) { AnimeId = 123 }]),
                ct);

            var acceptedAniDb = new VideoMetadataCandidate(
                "anidb", "123", VideoMetadataMediaKind.Anime, "Show", "Show", 2024,
                1, 1, 1, ["Show"],
                System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("anidb", "123"),
                "https://anidb.net/anime/123");
            var tmdbDetails = new VideoMetadataDetails(
                "tmdb", "65942", VideoMetadataMediaKind.Anime, "Show", "Show", null,
                "TMDB overview", 2024, 1, 1, 1, ["Show"], [], [],
                System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
                    .Add("anidb", "123").Add("tmdb", "65942"),
                "https://www.themoviedb.org/tv/65942", now, now.AddDays(30),
                Seasons:
                [
                    new VideoMetadataSeason(1, "Season 1", null, "2024-01-01", 1, null,
                        [new VideoMetadataEpisode(
                            1, "One", "One", null, "2024-01-01", 24, null,
                            "https://www.themoviedb.org/tv/65942/season/1/episode/1")
                        {
                            TmdbShowId = 65942,
                            TmdbEpisodeId = 7001,
                            TmdbOrderingId = "tv-order",
                            TmdbEpisodeGroupId = "tv-season-1",
                            Ordinal = 0,
                        }])
                    {
                        TmdbShowId = 65942,
                        TmdbOrderingId = "tv-order",
                        TmdbEpisodeGroupId = "tv-season-1",
                        TmdbOrderingType = VideoTmdbOrderingType.Tv,
                        Ordinal = 0,
                    },
                ],
                TmdbOrdering: new VideoTmdbOrdering(
                    65942, "tv-order", VideoTmdbOrderingType.Tv, IsPreferred: true));
            (await repository.ApplyMetadataMatchAsync(
                assetId, acceptedAniDb, tmdbDetails, true, true, ct)).Should().BeTrue();
        }

        await using var reopened = Create(databasePath, legacyPath);
        await reopened.InitializeAsync(ct);
        var snapshot = await reopened.GetSnapshotAsync(ct);
        var series = snapshot.Nodes.Single(node => node.Kind == VideoCatalogNodeKind.Series);
        series.ExternalIds.Should().Contain("anidb", "123");
        series.IdentityLockedProviders.Should().Contain("anidb");
        series.TmdbShowCrossReferences.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new
            {
                AniDbAnimeId = 123,
                TmdbShowId = 65942,
                ChosenOrderingId = "tv-order",
                ChosenOrderingType = VideoTmdbOrderingType.Tv,
                MatchRating = VideoMetadataMatchRating.FirstAvailable,
            });
        series.TmdbOrderings.Should().ContainSingle(ordering =>
            ordering.OrderingId == "tv-order"
            && ordering.Type == VideoTmdbOrderingType.Tv
            && ordering.IsPreferred);

        var episode = snapshot.Nodes.Single(node => node.Kind == VideoCatalogNodeKind.Episode);
        episode.ExternalIds.Should().Contain("anidb-episode", "1001");
        episode.TmdbEpisodeCrossReferences.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new
            {
                AniDbAnimeId = 123,
                AniDbEpisodeId = 1001,
                TmdbShowId = 65942,
                TmdbEpisodeId = 7001,
                OrderingId = "tv-order",
                SeasonId = "tv-season-1",
                SeasonNumber = 1,
                EpisodeNumber = 1,
                Ordinal = 0,
                MatchRating = VideoMetadataMatchRating.DateAndTitleKindaMatches,
            });
    }

    [Fact]
    public async Task MetadataMatch_ReusesExistingStructuredSeriesHierarchy()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var mediaPath = Path.Combine(sourcePath, "Himouto! Umaru-chan - 08.mkv");
        var sourceId = Guid.NewGuid();
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Anime", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var now = DateTimeOffset.UtcNow;
        var generation = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.IncrementalScan, ct);
        var parsed = new ParsedVideoIdentity(
            "Himouto! Umaru-chan - 08", "Himouto! Umaru-chan", null, 2015,
            null, 8, 8, 8, null, null, ParsedVideoSpecialKind.None, false, true,
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty, []);
        var scanAsset = new VideoScanAsset(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "Himouto! Umaru-chan", sourcePath,
            1, now, now, now, VideoMediaAvailability.Available, sourceId, 8, 8), parsed);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, generation, now, [scanAsset], true), ct)).Should().BeTrue();
        var before = await repository.GetSnapshotAsync(ct);
        var assetId = before.Assets.Single().Id;
        before.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Series);
        var candidate = new VideoMetadataCandidate(
            "anilist", "20987", VideoMetadataMediaKind.Anime, "干物妹！うまるちゃん", "干物妹！うまるちゃん", 2015,
            null, 8, 8, ["Himouto! Umaru-chan"],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("anilist", "20987"),
            "https://anilist.co/anime/20987");

        await repository.ApplyMetadataMatchAsync(assetId, candidate, null, false, true, ct);
        var after = await repository.GetSnapshotAsync(ct);

        after.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Series);
        after.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Episode);
        after.Nodes.Single(node => node.Kind == VideoCatalogNodeKind.Series).ExternalIds
            .Should().Contain("anilist", "20987");

        await repository.ClearRemoteMetadataAsync(sourceId, ct);
        var cleared = await repository.GetSnapshotAsync(ct);
        cleared.Assets.Should().ContainSingle(asset => asset.Location == mediaPath);
        cleared.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Series);
        cleared.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Episode);
        cleared.Nodes.Should().OnlyContain(node => node.ExternalIds.Count == 0);
        cleared.Nodes.Should().OnlyContain(node => node.MetadataExpiresAt == null);
    }

    [Fact]
    public async Task AutomaticMovieMatch_CannotReplaceEpisodeInsideStructuredSeries()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Shows")).FullName;
        var mediaPath = Path.Combine(sourcePath, "Show S01E01.mkv");
        var sourceId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Shows", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Auto,
        }, ct);
        var parsed = new VideoFileNameParser().Parse(mediaPath, sourcePath, VideoLibraryMediaType.Auto);
        var generation = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.IncrementalScan, ct);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, generation, now,
            [new VideoScanAsset(new VideoCatalogAssetUpsert(
                mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, parsed.NormalizedTitle, "Shows", 1,
                now, now, now, VideoMediaAvailability.Available, sourceId,
                parsed.EpisodeStart, parsed.EpisodeEnd), parsed)], true), ct)).Should().BeTrue();
        var before = await repository.GetSnapshotAsync(ct);
        var asset = before.Assets.Single();
        var nodeIds = asset.NodeIds;
        var movie = new VideoMetadataCandidate(
            "tmdb", "99", VideoMetadataMediaKind.Movie, "Wrong Movie", null, 2026,
            null, null, null, [],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("tmdb", "99"),
            "https://www.themoviedb.org/movie/99");

        await repository.ApplyMetadataMatchAsync(
            asset.Id, movie, null, false, preserveExistingHierarchy: true, ct);
        await repository.ApplyArtworkAsync(
            asset.Id, VideoMetadataMediaKind.Movie, "tmdb", "poster",
            "https://image.tmdb.org/wrong.jpg", Path.Combine(temp.Path, "wrong.jpg"),
            null, now, ct);

        var after = await repository.GetSnapshotAsync(ct);
        after.Assets.Single().NodeIds.Should().Equal(nodeIds);
        after.Nodes.Should().NotContain(node => node.Kind == VideoCatalogNodeKind.Movie);
        after.Nodes.Single(node => node.Kind == VideoCatalogNodeKind.Episode).PosterPath.Should().BeNull();
    }

    [Theory]
    [InlineData(VideoCatalogNodeKind.Season)]
    [InlineData(VideoCatalogNodeKind.Episode)]
    public async Task AutomaticMovieMatch_CannotReplaceLegacyRootEpisodicNode(
        VideoCatalogNodeKind originalKind)
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var mediaPath = Path.Combine(temp.Path, "Legacy S01E01.mkv");
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        await using var repository = Create(database, Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "Legacy S01E01", "fixture", 1,
            now, now, now, VideoMediaAvailability.Available, EpisodeStart: 1, EpisodeEnd: 1), ct);
        var before = await repository.GetSnapshotAsync(ct);
        var asset = before.Assets.Single();
        var nodeId = before.Nodes.Single().Id;

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE catalog_nodes SET kind=$kind WHERE id=$node;";
            command.Parameters.AddWithValue("$kind", originalKind.ToString().ToLowerInvariant());
            command.Parameters.AddWithValue("$node", nodeId.ToString("D"));
            await command.ExecuteNonQueryAsync(ct);
        }

        var movie = new VideoMetadataCandidate(
            "tmdb", "99", VideoMetadataMediaKind.Movie, "Wrong Movie", null, 2026,
            null, null, null, [],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("tmdb", "99"),
            "https://www.themoviedb.org/movie/99");
        await repository.ReplaceMatchCandidatesAsync(
            asset.Id,
            [new VideoMatchCandidateSnapshot(
                Guid.NewGuid(), asset.Id, "tmdb", "99", "Wrong Movie", 2026,
                0.95, 0.95, "fixture", false, now)],
            ct);
        var applied = await repository.ApplyMetadataMatchAsync(
            asset.Id, movie, null, false, preserveExistingHierarchy: true, ct);

        var after = await repository.GetSnapshotAsync(ct);
        applied.Should().BeFalse();
        after.Assets.Single().NodeIds.Should().Equal(nodeId);
        after.Nodes.Single(node => node.Id == nodeId).Kind.Should().Be(originalKind);
        after.Nodes.Should().NotContain(node => node.Kind == VideoCatalogNodeKind.Movie);
        after.MatchCandidates.Should().ContainSingle(candidate => candidate.AssetId == asset.Id);
    }

    [Fact]
    public async Task ExplicitMovieSource_SafelyDemotesSingleAssetLegacyEpisodeHierarchy()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Movies")).FullName;
        var mediaPath = Path.Combine(sourcePath, "OVA The Movie (2020).mkv");
        var sourceId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        await using var repository = Create(database, Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Movies", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Movie,
        }, ct);
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "OVA The Movie", "Movies", 1,
            now, now, now, VideoMediaAvailability.Available, sourceId), ct);
        var initial = await repository.GetSnapshotAsync(ct);
        var assetId = initial.Assets.Single().Id;
        var oldUnmatched = initial.Nodes.Single().Id;
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM node_assets WHERE asset_id=$asset;
                DELETE FROM catalog_nodes WHERE id=$old;
                INSERT INTO catalog_nodes(id,parent_id,kind,primary_title,season_number,episode_number,is_special,identity_locked,created_at,updated_at)
                VALUES($series,NULL,'series','OVA The Movie',NULL,NULL,0,0,$now,$now),
                      ($season,$series,'season','Specials',0,NULL,1,0,$now,$now),
                      ($episode,$season,'episode','OVA The Movie',0,1,1,0,$now,$now);
                INSERT INTO node_assets(node_id,asset_id,is_preferred,ordinal)
                VALUES($episode,$asset,1,0);
                """;
            command.Parameters.AddWithValue("$asset", assetId.ToString("D"));
            command.Parameters.AddWithValue("$old", oldUnmatched.ToString("D"));
            command.Parameters.AddWithValue("$series", seriesId.ToString("D"));
            command.Parameters.AddWithValue("$season", seasonId.ToString("D"));
            command.Parameters.AddWithValue("$episode", episodeId.ToString("D"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }

        var staleMetadataJob = await repository.BeginMetadataRefreshAsync(sourceId, 1, ct);
        await repository.UpdateMetadataRefreshAsync(
            staleMetadataJob, VideoCatalogJobState.Completed, 1, null, ct);
        var parsed = new VideoFileNameParser().Parse(
            mediaPath, sourcePath, VideoLibraryMediaType.Movie);
        var repairGeneration = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.IncrementalScan, ct);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, repairGeneration, now,
            [new VideoScanAsset(new VideoCatalogAssetUpsert(
                mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, parsed.NormalizedTitle, "Movies", 1,
                now, now, now, VideoMediaAvailability.Available, sourceId),
                parsed, RebuildHierarchy: true)],
            true), ct)).Should().BeTrue();

        var repaired = await repository.GetSnapshotAsync(ct);
        var repairedNode = repaired.Nodes.Should().ContainSingle().Subject;
        repairedNode.Id.Should().Be(episodeId);
        repairedNode.Kind.Should().Be(VideoCatalogNodeKind.Unmatched);
        repairedNode.ParentId.Should().BeNull();
        repairedNode.IsSpecial.Should().BeFalse();
        repaired.Jobs.Single(job => job.Id == staleMetadataJob).State
            .Should().Be(VideoCatalogJobState.Cancelled);

        var movie = new VideoMetadataCandidate(
            "tmdb", "100", VideoMetadataMediaKind.Movie, "OVA The Movie", null, 2020,
            null, null, null, [],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("tmdb", "100"),
            "https://www.themoviedb.org/movie/100");
        var applied = await repository.ApplyMetadataMatchAsync(
            assetId, movie, null, false, preserveExistingHierarchy: true, ct);

        var after = await repository.GetSnapshotAsync(ct);
        applied.Should().BeTrue();
        var rootMovie = after.Nodes.Should().ContainSingle().Subject;
        rootMovie.Id.Should().Be(episodeId);
        rootMovie.Kind.Should().Be(VideoCatalogNodeKind.Movie);
        rootMovie.ParentId.Should().BeNull();
        rootMovie.IsSpecial.Should().BeFalse();
        rootMovie.SeasonNumber.Should().BeNull();
        rootMovie.EpisodeNumber.Should().BeNull();
        after.Assets.Single().NodeIds.Should().Equal(episodeId);
    }

    [Fact]
    public async Task FreshMovieScan_DoesNotCreateEpisodeHierarchyFromLegacySpecialEvidence()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Movies")).FullName;
        var mediaPath = Path.Combine(sourcePath, "OVA The Movie (2020).mkv");
        var sourceId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Movies", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Movie,
        }, ct);
        var parsed = new VideoFileNameParser().Parse(
            mediaPath, sourcePath, VideoLibraryMediaType.Movie) with
        {
            SeasonNumber = 0,
            EpisodeStart = 1,
            EpisodeEnd = 1,
            SpecialKind = ParsedVideoSpecialKind.Ova,
            HasEpisodeEvidence = true,
        };
        var generation = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.IncrementalScan, ct);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, generation, now,
            [new VideoScanAsset(new VideoCatalogAssetUpsert(
                mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "OVA The Movie", "Movies", 1,
                now, now, now, VideoMediaAvailability.Available, sourceId, 1, 1), parsed)],
            true), ct)).Should().BeTrue();

        var snapshot = await repository.GetSnapshotAsync(ct);
        snapshot.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Unmatched);
        snapshot.Nodes.Should().NotContain(node => node.Kind == VideoCatalogNodeKind.Series
                                                   || node.Kind == VideoCatalogNodeKind.Season
                                                   || node.Kind == VideoCatalogNodeKind.Episode);
        snapshot.Assets.Single().EpisodeStart.Should().BeNull();
        snapshot.Assets.Single().EpisodeEnd.Should().BeNull();
    }

    [Fact]
    public async Task HierarchyRepair_DoesNotConvertRootMovieWithEpisodicLookingTitle()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Movies")).FullName;
        var mediaPath = Path.Combine(sourcePath, "OVA The Movie (2020).mkv");
        var sourceId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Movies", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Movie,
        }, ct);
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "OVA The Movie", "Movies", 1,
            now, now, now, VideoMediaAvailability.Available, sourceId), ct);
        var assetId = (await repository.GetSnapshotAsync(ct)).Assets.Single().Id;
        var movie = new VideoMetadataCandidate(
            "tmdb", "100", VideoMetadataMediaKind.Movie, "OVA The Movie", null, 2020,
            null, null, null, [],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("tmdb", "100"),
            "https://www.themoviedb.org/movie/100");
        await repository.ApplyMetadataMatchAsync(
            assetId, movie, null, false, preserveExistingHierarchy: false, ct);
        var parsed = new VideoFileNameParser().Parse(
            mediaPath, sourcePath, VideoLibraryMediaType.Movie);
        parsed.SpecialKind.Should().Be(ParsedVideoSpecialKind.None);
        parsed.NormalizedTitle.Should().Be("OVA The Movie");

        var generation = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.IncrementalScan, ct);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, generation, now,
            [new VideoScanAsset(new VideoCatalogAssetUpsert(
                mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, parsed.NormalizedTitle, "Movies", 1,
                now, now, now, VideoMediaAvailability.Available, sourceId),
                parsed, RebuildHierarchy: true)], true), ct)).Should().BeTrue();

        var after = await repository.GetSnapshotAsync(ct);
        after.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Movie);
        after.Nodes.Should().NotContain(node => node.Kind == VideoCatalogNodeKind.Series
                                                || node.Kind == VideoCatalogNodeKind.Season
                                                || node.Kind == VideoCatalogNodeKind.Episode);
        after.Assets.Single().NodeIds.Should().ContainSingle();
    }

    [Fact]
    public async Task ManualRematch_CanChangeLockedSeriesToMovieWithSameProviderId()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var mediaPath = Path.Combine(temp.Path, "Work.mkv");
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "Work", "fixture", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            VideoMediaAvailability.Available), ct);
        var assetId = (await repository.GetSnapshotAsync(ct)).Assets.Single().Id;
        var externalIds = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
            .Add("tmdb", "100");
        var series = new VideoMetadataCandidate(
            "tmdb", "100", VideoMetadataMediaKind.Series, "Work Series", null, 2020,
            null, null, null, [], externalIds, "https://www.themoviedb.org/tv/100");
        (await repository.ApplyMetadataMatchAsync(
            assetId, series, null, lockIdentity: true, preserveExistingHierarchy: false, ct))
            .Should().BeTrue();
        (await repository.GetSnapshotAsync(ct)).Nodes.Single().Kind.Should().Be(VideoCatalogNodeKind.Series);

        var movie = series with
        {
            MediaKind = VideoMetadataMediaKind.Movie,
            Title = "Work Movie",
            SourceUrl = "https://www.themoviedb.org/movie/100",
        };
        (await repository.ApplyMetadataMatchAsync(
            assetId, movie, null, lockIdentity: true, preserveExistingHierarchy: false, ct))
            .Should().BeTrue();

        var rematched = (await repository.GetSnapshotAsync(ct)).Nodes.Should().ContainSingle().Subject;
        rematched.Kind.Should().Be(VideoCatalogNodeKind.Movie);
        rematched.PrimaryTitle.Should().Be("Work Movie");
        rematched.IdentityLockedProviders.Should().BeEquivalentTo("tmdb");
    }

    [Fact]
    public async Task ManualMovieRematch_DoesNotMutateSharedSeriesAndDetachesEpisode()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        await using var repository = Create(database, Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        var seriesAssetPath = Path.Combine(temp.Path, "Legacy direct series.mkv");
        var episodeAssetPath = Path.Combine(temp.Path, "Legacy episode.mkv");
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            seriesAssetPath, VideoMediaAssetKind.LocalFile, seriesAssetPath, "Legacy", "fixture", 1,
            now, now, now, VideoMediaAvailability.Available), ct);
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            episodeAssetPath, VideoMediaAssetKind.LocalFile, episodeAssetPath, "Legacy E01", "fixture", 1,
            now, now, now, VideoMediaAvailability.Available, EpisodeStart: 1, EpisodeEnd: 1), ct);
        var initial = await repository.GetSnapshotAsync(ct);
        var seriesAsset = initial.Assets.Single(asset => asset.IdentityKey == seriesAssetPath);
        var episodeAsset = initial.Assets.Single(asset => asset.IdentityKey == episodeAssetPath);
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM node_assets WHERE asset_id IN ($seriesAsset,$episodeAsset);
                DELETE FROM catalog_nodes WHERE id IN ($oldSeries,$oldEpisode);
                INSERT INTO catalog_nodes(id,parent_id,kind,primary_title,season_number,episode_number,is_special,identity_locked,created_at,updated_at)
                VALUES($series,NULL,'series','Legacy',NULL,NULL,0,0,$now,$now),
                      ($season,$series,'season','Season 1',1,NULL,0,0,$now,$now),
                      ($episode,$season,'episode','Episode 1',1,1,0,0,$now,$now);
                INSERT INTO node_assets(node_id,asset_id,is_preferred,ordinal)
                VALUES($series,$seriesAsset,1,0),($episode,$episodeAsset,1,0);
                """;
            command.Parameters.AddWithValue("$seriesAsset", seriesAsset.Id.ToString("D"));
            command.Parameters.AddWithValue("$episodeAsset", episodeAsset.Id.ToString("D"));
            command.Parameters.AddWithValue("$oldSeries", seriesAsset.NodeIds.Single().ToString("D"));
            command.Parameters.AddWithValue("$oldEpisode", episodeAsset.NodeIds.Single().ToString("D"));
            command.Parameters.AddWithValue("$series", seriesId.ToString("D"));
            command.Parameters.AddWithValue("$season", seasonId.ToString("D"));
            command.Parameters.AddWithValue("$episode", episodeId.ToString("D"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }

        VideoMetadataCandidate Movie(string id, string title) => new(
            "tmdb", id, VideoMetadataMediaKind.Movie, title, null, 2020,
            null, null, null, [],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("tmdb", id),
            $"https://www.themoviedb.org/movie/{id}");
        (await repository.ApplyMetadataMatchAsync(
            seriesAsset.Id, Movie("101", "First Movie"), null,
            lockIdentity: true, preserveExistingHierarchy: false, ct)).Should().BeTrue();

        var afterSeriesAsset = await repository.GetSnapshotAsync(ct);
        afterSeriesAsset.Nodes.Single(node => node.Id == seriesId).Kind
            .Should().Be(VideoCatalogNodeKind.Series);
        var firstMovie = afterSeriesAsset.Nodes.Should().ContainSingle(node =>
            node.Kind == VideoCatalogNodeKind.Movie).Subject;
        firstMovie.Id.Should().NotBe(seriesId);
        firstMovie.ParentId.Should().BeNull();
        afterSeriesAsset.Assets.Single(asset => asset.Id == episodeAsset.Id).NodeIds
            .Should().Equal(episodeId);

        (await repository.ApplyMetadataMatchAsync(
            episodeAsset.Id, Movie("102", "Second Movie"), null,
            lockIdentity: true, preserveExistingHierarchy: false, ct)).Should().BeTrue();

        var final = await repository.GetSnapshotAsync(ct);
        final.Nodes.Should().HaveCount(2).And.OnlyContain(node =>
            node.Kind == VideoCatalogNodeKind.Movie && node.ParentId == null);
        final.Nodes.Single(node => node.Id == episodeId).PrimaryTitle.Should().Be("Second Movie");
        final.Nodes.Should().NotContain(node => node.Id == seriesId || node.Id == seasonId);
    }

    [Fact]
    public async Task AutomaticSeriesMatch_PreservesProtectedLegacyRootEpisodeForReview()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var mediaPath = Path.Combine(temp.Path, "Show S01E01.mkv");
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        await using var repository = Create(database, Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "Show", "fixture", 1,
            now, now, now, VideoMediaAvailability.Available, EpisodeStart: 1, EpisodeEnd: 1), ct);
        var initial = await repository.GetSnapshotAsync(ct);
        var asset = initial.Assets.Single();
        var nodeId = initial.Nodes.Single().Id;
        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE catalog_nodes SET kind='episode',identity_locked=1,
                    season_number=1,episode_number=1 WHERE id=$node;
                INSERT INTO external_ids(node_id,provider_id,external_id,is_identity_locked)
                VALUES($node,'anilist','42',1);
                INSERT INTO metadata_field_values(node_id,field,value,provider_id,priority,is_locked,updated_at)
                VALUES($node,'overview','Protected local overview','local',300,0,$now);
                """;
            command.Parameters.AddWithValue("$node", nodeId.ToString("D"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }
        await repository.ReplaceMatchCandidatesAsync(
            asset.Id,
            [new VideoMatchCandidateSnapshot(
                Guid.NewGuid(), asset.Id, "anilist", "42", "Show", 2020,
                0.99, 0.99, "fixture", false, now)],
            ct);
        var series = new VideoMetadataCandidate(
            "anilist", "42", VideoMetadataMediaKind.Anime, "Show", null, 2020,
            1, 1, null, [],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("anilist", "42"),
            "https://anilist.co/anime/42");

        var applied = await repository.ApplyMetadataMatchAsync(
            asset.Id, series, null, lockIdentity: true, preserveExistingHierarchy: true, ct);

        applied.Should().BeFalse();
        var after = await repository.GetSnapshotAsync(ct);
        after.Assets.Single().NodeIds.Should().Equal(nodeId);
        after.Nodes.Single().Kind.Should().Be(VideoCatalogNodeKind.Episode);
        after.Nodes.Single().Overview.Should().BeNull();
        after.Nodes.Single().IdentityLockedProviders.Should().BeEquivalentTo("anilist");
        after.MatchCandidates.Should().ContainSingle(candidate => candidate.AssetId == asset.Id);
        await using var verify = new SqliteConnection($"Data Source={database};Pooling=False");
        await verify.OpenAsync(ct);
        using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText =
            "SELECT value FROM metadata_field_values WHERE node_id=$node AND provider_id='local';";
        verifyCommand.Parameters.AddWithValue("$node", nodeId.ToString("D"));
        (await verifyCommand.ExecuteScalarAsync(ct)).Should().Be("Protected local overview");
    }

    [Fact]
    public async Task Snapshot_ExposesOnlyIndividuallyLockedExternalIdProviders()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        await using var repository = Create(database, Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        var mediaPath = Path.Combine(temp.Path, "作品 S01E01.mkv");
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "作品", temp.Path,
            1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            VideoMediaAvailability.Available, EpisodeStart: 1, EpisodeEnd: 1), ct);
        var initial = await repository.GetSnapshotAsync(ct);
        var nodeId = initial.Nodes.Single().Id;
        var assetId = initial.Assets.Single().Id;

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE catalog_nodes SET identity_locked=1 WHERE id=$node;
                INSERT INTO external_ids(node_id,provider_id,external_id,is_identity_locked)
                VALUES($node,'anilist','20987',1),($node,'tmdb','67126',0);
                """;
            command.Parameters.AddWithValue("$node", nodeId.ToString("D"));
            await command.ExecuteNonQueryAsync(ct);
        }

        var node = (await repository.GetSnapshotAsync(ct)).Nodes.Single(item => item.Id == nodeId);

        node.IdentityLocked.Should().BeTrue();
        node.ExternalIds.Keys.Should().BeEquivalentTo("anilist", "tmdb");
        node.IdentityLockedProviders.Should().BeEquivalentTo("anilist");

        var tmdb = new VideoMetadataCandidate(
            "tmdb", "67126", VideoMetadataMediaKind.Movie, "作品", null, null,
            null, null, null, [],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
                .Add("tmdb", "67126")
                .Add("anilist", "untrusted-cross-id"),
            "https://www.themoviedb.org/movie/67126");
        await repository.ApplyMetadataMatchAsync(
            assetId, tmdb, null, lockIdentity: true, preserveExistingHierarchy: true, ct);

        var refreshedNode = (await repository.GetSnapshotAsync(ct)).Nodes.Single(item => item.Id == nodeId);
        refreshedNode.IdentityLockedProviders.Should().BeEquivalentTo(["anilist"],
            "an unlocked ID must not become locked merely because its node is locked");
        refreshedNode.ExternalIds.Should().Contain("anilist", "20987",
            "automatic supplemental metadata cannot replace a locked provider identity");
    }

    [Theory]
    [InlineData(false, VideoCatalogNodeKind.Series)]
    [InlineData(true, VideoCatalogNodeKind.Episode)]
    public async Task MetadataRebind_PromotesProtectedUnmatchedNodeInPlace(
        bool numberedEpisode,
        VideoCatalogNodeKind expectedKind)
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var mediaPath = Path.Combine(temp.Path, "Show S01E01.mkv");
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        await using var repository = Create(database, Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "Show S01E01", "fixture", 1,
            now, now, now, VideoMediaAvailability.Available,
            EpisodeStart: numberedEpisode ? 1 : null,
            EpisodeEnd: numberedEpisode ? 1 : null), ct);
        var before = await repository.GetSnapshotAsync(ct);
        var assetId = before.Assets.Single().Id;
        var unmatchedId = before.Nodes.Single().Id;

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO metadata_field_values(node_id,field,value,provider_id,priority,is_locked,updated_at)
                VALUES($node,'overview','Local NFO overview','local',300,0,$now),
                      ($node,'originalTitle','Local Original','local',300,0,$now),
                      ($node,'year','2001','local',300,0,$now);
                UPDATE catalog_nodes SET identity_locked=1 WHERE id=$node;
                INSERT INTO external_ids(node_id,provider_id,external_id,is_identity_locked)
                VALUES($node,'anilist','42',1);
                """;
            command.Parameters.AddWithValue("$node", unmatchedId.ToString("D"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }

        var series = new VideoMetadataCandidate(
            "anilist", "42", VideoMetadataMediaKind.Anime, "Show", null, 2026,
            numberedEpisode ? 1 : null, numberedEpisode ? 1 : null, null, [],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("anilist", "42"),
            "https://anilist.co/anime/42");
        var details = numberedEpisode
            ? null
            : new VideoMetadataDetails(
                "anilist", "42", VideoMetadataMediaKind.Anime, "Show", "Remote Original", null,
                "Remote overview", 2026, null, null, null, [], [], [], series.ExternalIds,
                series.SourceUrl, now, now.AddDays(30));
        await repository.ApplyMetadataMatchAsync(
            assetId, series, details, false, preserveExistingHierarchy: false, ct);

        var after = await repository.GetSnapshotAsync(ct);
        after.Nodes.Single(node => node.Id == unmatchedId).Kind.Should().Be(expectedKind);
        after.Nodes.Should().NotContain(node => node.Kind == VideoCatalogNodeKind.Unmatched);
        after.Assets.Single().NodeIds.Should().Equal(unmatchedId);
        after.Nodes.Single(node => node.Id == unmatchedId).IdentityLockedProviders
            .Should().BeEquivalentTo("anilist");
        if (!numberedEpisode)
        {
            var promotedSeries = after.Nodes.Single(node => node.Id == unmatchedId);
            promotedSeries.Overview.Should().Be("Local NFO overview");
            promotedSeries.OriginalTitle.Should().Be("Local Original");
            promotedSeries.Year.Should().Be(2001);
        }
        await using var verify = new SqliteConnection($"Data Source={database};Pooling=False");
        await verify.OpenAsync(ct);
        using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText =
            "SELECT value FROM metadata_field_values WHERE node_id=$node AND provider_id='local' AND field='overview';";
        verifyCommand.Parameters.AddWithValue("$node", unmatchedId.ToString("D"));
        (await verifyCommand.ExecuteScalarAsync(ct)).Should().Be("Local NFO overview");
    }

    [Fact]
    public async Task AutomaticSeriesRefresh_DoesNotPromoteChildProviderLocksToSeries()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Shows")).FullName;
        var mediaPath = Path.Combine(sourcePath, "Show S01E01.mkv");
        var sourceId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        await using var repository = Create(database, Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Shows", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var parsed = new VideoFileNameParser().Parse(mediaPath, sourcePath, VideoLibraryMediaType.Anime);
        var generation = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.IncrementalScan, ct);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, generation, now,
            [new VideoScanAsset(new VideoCatalogAssetUpsert(
                mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, parsed.NormalizedTitle, "Shows", 1,
                now, now, now, VideoMediaAvailability.Available, sourceId,
                parsed.EpisodeStart, parsed.EpisodeEnd), parsed)], true), ct)).Should().BeTrue();
        var before = await repository.GetSnapshotAsync(ct);
        var assetId = before.Assets.Single().Id;
        var seriesId = before.Nodes.Single(node => node.Kind == VideoCatalogNodeKind.Series).Id;
        var episodeId = before.Nodes.Single(node => node.Kind == VideoCatalogNodeKind.Episode).Id;

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE catalog_nodes SET identity_locked=1 WHERE id IN ($series,$episode);
                INSERT INTO external_ids(node_id,provider_id,external_id,is_identity_locked)
                VALUES($series,'anilist','42',1),($episode,'tvdb','episode-1',1);
                """;
            command.Parameters.AddWithValue("$series", seriesId.ToString("D"));
            command.Parameters.AddWithValue("$episode", episodeId.ToString("D"));
            await command.ExecuteNonQueryAsync(ct);
        }

        var candidate = new VideoMetadataCandidate(
            "anilist", "42", VideoMetadataMediaKind.Anime, "Show", null, 2026,
            null, null, null, [],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
                .Add("anilist", "42")
                .Add("tvdb", "series-cross-id"),
            "https://anilist.co/anime/42");
        await repository.ApplyMetadataMatchAsync(
            assetId, candidate, null, lockIdentity: true, preserveExistingHierarchy: true, ct);

        var after = await repository.GetSnapshotAsync(ct);
        var series = after.Nodes.Single(node => node.Id == seriesId);
        var episode = after.Nodes.Single(node => node.Id == episodeId);
        series.IdentityLockedProviders.Should().BeEquivalentTo("anilist");
        series.ExternalIds.Should().Contain("tvdb", "series-cross-id");
        episode.IdentityLockedProviders.Should().BeEquivalentTo("tvdb");
        episode.ExternalIds.Should().Contain("tvdb", "episode-1");
    }

    [Fact]
    public async Task SeriesMetadata_EnrichesOwnerWithoutFlatteningSeasonOrSpecialHierarchy()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var regularPath = Path.Combine(sourcePath, "作品 S03E04.mkv");
        var previewPath = Path.Combine(sourcePath, "PV", "作品 S03E04 PV.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
        await File.WriteAllBytesAsync(regularPath, [1], ct);
        await File.WriteAllBytesAsync(previewPath, [2], ct);
        var sourceId = Guid.NewGuid();
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Anime", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var now = DateTimeOffset.UtcNow;
        var generation = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, ct);
        var regular = new ParsedVideoIdentity(
            "作品 S03E04", "作品", null, null, 3, 4, 4, null, null, null,
            ParsedVideoSpecialKind.None, false, true,
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty, []);
        var preview = regular with
        {
            OriginalName = "作品 S03E04 PV",
            SeasonNumber = 0,
            SpecialKind = ParsedVideoSpecialKind.Preview,
            EpisodeTitle = "PV 04",
        };
        var scanAssets = new[]
        {
            new VideoScanAsset(new VideoCatalogAssetUpsert(
                regularPath, VideoMediaAssetKind.LocalFile, regularPath, "作品", "Anime", 1,
                now, now, now, VideoMediaAvailability.Available, sourceId, 4, 4), regular),
            new VideoScanAsset(new VideoCatalogAssetUpsert(
                previewPath, VideoMediaAssetKind.LocalFile, previewPath, "作品", "Anime", 1,
                now, now, now, VideoMediaAvailability.Available, sourceId, 4, 4), preview),
        };
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, generation, now, scanAssets, true), ct)).Should().BeTrue();
        var before = await repository.GetSnapshotAsync(ct);
        var beforeBindings = before.Assets.ToDictionary(asset => asset.Id, asset => asset.NodeIds.Single());
        before.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Season && node.SeasonNumber == 3);
        before.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Season && node.SeasonNumber == 0);

        var candidate = new VideoMetadataCandidate(
            "anilist", "42", VideoMetadataMediaKind.Anime, "作品（配信名）", "作品", 2024,
            null, null, null, ["作品"],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("anilist", "42"),
            "https://anilist.co/anime/42");
        foreach (var asset in before.Assets)
            await repository.ApplyMetadataMatchAsync(asset.Id, candidate, null, false, true, ct);

        var after = await repository.GetSnapshotAsync(ct);
        after.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Series);
        var series = after.Nodes.Single(node => node.Kind == VideoCatalogNodeKind.Series);
        series.PrimaryTitle.Should().Be("作品（配信名）");
        series.SeasonNumber.Should().BeNull();
        series.EpisodeNumber.Should().BeNull();
        series.IdentityLocked.Should().BeFalse();
        after.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Season && node.SeasonNumber == 3);
        after.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Season && node.SeasonNumber == 0);
        after.Nodes.Count(node => node.Kind == VideoCatalogNodeKind.Episode).Should().Be(2);
        after.Assets.Should().OnlyContain(asset => beforeBindings[asset.Id] == asset.NodeIds.Single());
        after.Assets.SelectMany(asset => asset.NodeIds).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task MultiEpisodeAsset_UsesOneLogicalEpisodeNodeAndKeepsEndingNumberOnAsset()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Shows")).FullName;
        var mediaPath = Path.Combine(sourcePath, "作品 S01E01-E02.mkv");
        await File.WriteAllBytesAsync(mediaPath, [1], ct);
        var sourceId = Guid.NewGuid();
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Shows", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.JapaneseDramaTv,
        }, ct);
        var parsed = new VideoFileNameParser().Parse(
            mediaPath, sourcePath, VideoLibraryMediaType.JapaneseDramaTv);
        var now = DateTimeOffset.UtcNow;
        var generation = await repository.BeginSourceScanAsync(sourceId, VideoCatalogJobKind.FullScan, ct);
        var scanAsset = new VideoScanAsset(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, parsed.NormalizedTitle, "Shows", 1,
            now, now, now, VideoMediaAvailability.Available, sourceId,
            parsed.EpisodeStart, parsed.EpisodeEnd), parsed);

        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, generation, now, [scanAsset], true), ct)).Should().BeTrue();

        var snapshot = await repository.GetSnapshotAsync(ct);
        snapshot.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Episode);
        var asset = snapshot.Assets.Single();
        asset.EpisodeStart.Should().Be(1);
        asset.EpisodeEnd.Should().Be(2);
        asset.NodeIds.Should().ContainSingle();
    }

    [Fact]
    public async Task CompatibilityRepairV11_ReparsesLocalAssetsAfterBrokenV10Marker()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var legacy = Path.Combine(temp.Path, "video_library.json");
        var mediaPath = Path.Combine(temp.Path, "作品 S01E01.mkv");
        var modified = DateTimeOffset.UtcNow;
        await using (var repository = Create(database, legacy))
        {
            await repository.InitializeAsync(ct);
            await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
                mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "作品", "fixture", 1,
                modified, modified, modified, VideoMediaAvailability.Available), ct);
        }
        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM migration_audit WHERE category='jellyfin-folder-hierarchy-v11';
                INSERT OR IGNORE INTO migration_audit(id,category,details_json,created_at)
                VALUES('broken-v10','episodic-bundle-hierarchy-v10','{}','2026-08-02T00:00:00.0000000Z');
                """;
            await command.ExecuteNonQueryAsync(ct);
        }

        await using (var reopened = Create(database, legacy))
            await reopened.InitializeAsync(ct);

        await using var verify = new SqliteConnection($"Data Source={database};Pooling=False");
        await verify.OpenAsync(ct);
        using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = "SELECT modified_at FROM media_assets WHERE kind='local';";
        (await verifyCommand.ExecuteScalarAsync(ct)).Should().Be(DBNull.Value);
        verifyCommand.CommandText =
            "SELECT COUNT(*) FROM migration_audit WHERE category='jellyfin-folder-hierarchy-v11';";
        (await verifyCommand.ExecuteScalarAsync(ct)).Should().Be(1L);
    }

    [Fact]
    public async Task CompatibilityRepairV11_DoesNotCancelMetadataJobBeforeAnyBindingChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var legacy = Path.Combine(temp.Path, "video_library.json");
        var sourceId = Guid.NewGuid();
        Guid jobId;
        await using (var repository = Create(database, legacy))
        {
            await repository.InitializeAsync(ct);
            await repository.UpsertSourceAsync(new VideoLibrarySource
            {
                Id = sourceId.ToString("D"), Name = "Remote", FolderPath = temp.Path,
                MediaType = VideoLibraryMediaType.Auto,
            }, ct);
            jobId = await repository.BeginMetadataRefreshAsync(sourceId, 0, ct);
            await repository.UpdateMetadataRefreshAsync(
                jobId, VideoCatalogJobState.Completed, 0, null, ct);
        }
        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM migration_audit WHERE category='jellyfin-folder-hierarchy-v11';";
            await command.ExecuteNonQueryAsync(ct);
        }

        await using var reopened = Create(database, legacy);
        var snapshot = (await reopened.InitializeAsync(ct)).Snapshot;

        snapshot.Jobs.Single(job => job.Id == jobId).State.Should().Be(VideoCatalogJobState.Completed);
    }

    [Fact]
    public async Task CompatibilityRepairV12_DeduplicatesNullRemoteArtworkAndRemapsPreference()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var legacy = Path.Combine(temp.Path, "video_library.json");
        var mediaPath = Path.Combine(temp.Path, "Show S01E01.mkv");
        var posterPath = Path.Combine(temp.Path, "poster.jpg");
        var nodeId = Guid.NewGuid();
        var discardedArtworkId = Guid.NewGuid();
        var keptArtworkId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        await File.WriteAllBytesAsync(mediaPath, [1], ct);
        await File.WriteAllBytesAsync(posterPath, [1], ct);
        await using (var repository = Create(database, legacy))
        {
            await repository.InitializeAsync(ct);
            await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
                mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "Show", "fixture", 1,
                now, now, now, VideoMediaAvailability.Available), ct);
        }

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                DROP INDEX ux_artwork_identity_nullsafe;
                DELETE FROM migration_audit WHERE category='local-sidecar-scopes-v12';
                INSERT INTO catalog_nodes(id,kind,primary_title,is_special,identity_locked,created_at,updated_at)
                VALUES($node,'series','Show',0,0,$now,$now);
                INSERT INTO artwork(id,node_id,provider_id,kind,local_path,selected,ordinal,created_at)
                VALUES($discarded,$node,'local','poster',$poster,0,0,$now),
                      ($kept,$node,'local','poster',$poster,1,1,$now);
                INSERT INTO node_user_data(node_id,preferred_artwork_id,updated_at)
                VALUES($node,$discarded,$now);
                """;
            command.Parameters.AddWithValue("$node", nodeId.ToString("D"));
            command.Parameters.AddWithValue("$discarded", discardedArtworkId.ToString("D"));
            command.Parameters.AddWithValue("$kept", keptArtworkId.ToString("D"));
            command.Parameters.AddWithValue("$poster", posterPath);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }

        await using (var reopened = Create(database, legacy))
            await reopened.InitializeAsync(ct);

        await using var verify = new SqliteConnection($"Data Source={database};Pooling=False");
        await verify.OpenAsync(ct);
        using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText =
            "SELECT COUNT(*) FROM artwork WHERE node_id=$node AND provider_id='local' AND kind='poster' AND local_path=$poster;";
        verifyCommand.Parameters.AddWithValue("$node", nodeId.ToString("D"));
        verifyCommand.Parameters.AddWithValue("$poster", posterPath);
        (await verifyCommand.ExecuteScalarAsync(ct)).Should().Be(1L);
        verifyCommand.CommandText =
            "SELECT preferred_artwork_id FROM node_user_data WHERE node_id=$node;";
        (await verifyCommand.ExecuteScalarAsync(ct)).Should().Be(keptArtworkId.ToString("D"));
        verifyCommand.CommandText =
            "SELECT modified_at FROM media_assets WHERE identity_key=$media;";
        verifyCommand.Parameters.AddWithValue("$media", mediaPath);
        (await verifyCommand.ExecuteScalarAsync(ct)).Should().Be(DBNull.Value);
        verifyCommand.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ux_artwork_identity_nullsafe';";
        (await verifyCommand.ExecuteScalarAsync(ct)).Should().Be(1L);
        verifyCommand.CommandText =
            "SELECT COUNT(*) FROM migration_audit WHERE category='local-sidecar-scopes-v12';";
        (await verifyCommand.ExecuteScalarAsync(ct)).Should().Be(1L);
    }

    [Fact]
    public async Task CompatibilityRepairV9_DoesNotCascadeProtectedDescendantMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var legacy = Path.Combine(temp.Path, "video_library.json");
        var seriesId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        await using (var repository = Create(database, legacy))
            await repository.InitializeAsync(ct);

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM migration_audit WHERE category='anilist-null-id-search-v9';
                INSERT INTO catalog_nodes(id,parent_id,kind,primary_title,is_special,identity_locked,created_at,updated_at)
                VALUES($series,NULL,'series','Protected Series',0,0,$now,$now),
                      ($episode,$series,'episode','Protected Episode',0,0,$now,$now);
                INSERT INTO metadata_field_values(node_id,field,value,provider_id,priority,is_locked,updated_at)
                VALUES($episode,'overview','Local overview','local',300,0,$now);
                """;
            command.Parameters.AddWithValue("$series", seriesId.ToString("D"));
            command.Parameters.AddWithValue("$episode", episodeId.ToString("D"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }

        await using var reopened = Create(database, legacy);
        var nodes = (await reopened.InitializeAsync(ct)).Snapshot.Nodes;

        nodes.Select(node => node.Id).Should().Contain(seriesId);
        nodes.Select(node => node.Id).Should().Contain(episodeId);
    }

    [Fact]
    public async Task FreshDatabase_RecordsCompatibilityRepairsBeforeFirstAssetIsStored()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var legacy = Path.Combine(temp.Path, "video_library.json");
        var mediaPath = Path.Combine(temp.Path, "作品 S01E01.mkv");
        var modified = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        await using (var repository = Create(database, legacy))
        {
            await repository.InitializeAsync(ct);
            await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
                mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "作品", "fixture", 1,
                modified, modified, modified, VideoMediaAvailability.Available), ct);
        }

        await using (var reopened = Create(database, legacy))
        {
            var snapshot = (await reopened.InitializeAsync(ct)).Snapshot;
            snapshot.Assets.Should().ContainSingle().Which.ModifiedAt.Should().Be(modified);
        }

        await using var verify = new SqliteConnection($"Data Source={database};Pooling=False");
        await verify.OpenAsync(ct);
        using var command = verify.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM migration_audit WHERE category IN ('anilist-null-id-search-v9','jellyfin-folder-hierarchy-v11','local-sidecar-scopes-v12');";
        (await command.ExecuteScalarAsync(ct)).Should().Be(3L);
    }

    [Fact]
    public async Task EmptyNodePruning_PreservesLocalAndLockedMetadataButDropsProviderCacheScaffolds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var sourceId = Guid.NewGuid();
        var localFieldNode = Guid.NewGuid();
        var lockedFieldNode = Guid.NewGuid();
        var localArtworkNode = Guid.NewGuid();
        var providerCacheNode = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        await using var repository = Create(database, Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Anime", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var generation = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.IncrementalScan, ct);

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO catalog_nodes(id,kind,primary_title,is_special,identity_locked,created_at,updated_at)
                VALUES($local,'series','Local',0,0,$now,$now),
                      ($locked,'series','Locked',0,0,$now,$now),
                      ($artwork,'series','Artwork',0,0,$now,$now),
                      ($cache,'series','Cache',0,0,$now,$now);
                INSERT INTO metadata_field_values(node_id,field,value,provider_id,priority,is_locked,updated_at)
                VALUES($local,'title','Local title','local',300,0,$now),
                      ($locked,'title','Locked title','tmdb',200,1,$now),
                      ($cache,'title','Cached title','tmdb',200,0,$now);
                INSERT INTO artwork(id,node_id,provider_id,kind,local_path,selected,ordinal,created_at)
                VALUES($artworkId,$artwork,'local','poster',$artworkPath,1,0,$now);
                """;
            command.Parameters.AddWithValue("$local", localFieldNode.ToString("D"));
            command.Parameters.AddWithValue("$locked", lockedFieldNode.ToString("D"));
            command.Parameters.AddWithValue("$artwork", localArtworkNode.ToString("D"));
            command.Parameters.AddWithValue("$cache", providerCacheNode.ToString("D"));
            command.Parameters.AddWithValue("$artworkId", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$artworkPath", Path.Combine(temp.Path, "poster.jpg"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }

        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, generation, now, [], true), ct)).Should().BeTrue();

        var ids = (await repository.GetSnapshotAsync(ct)).Nodes.Select(node => node.Id).ToHashSet();
        ids.Should().Contain(localFieldNode);
        ids.Should().Contain(lockedFieldNode);
        ids.Should().Contain(localArtworkNode);
        ids.Should().NotContain(providerCacheNode);
    }

    [Fact]
    public async Task FullScanWithStableHierarchy_DoesNotCancelCompletedMetadataRefresh()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var mediaPath = Path.Combine(sourcePath, "作品 S01E01.mkv");
        var sourceId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Anime", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var parsed = new VideoFileNameParser().Parse(mediaPath, sourcePath, VideoLibraryMediaType.Anime);
        VideoScanAsset ScanAsset(bool rebuildHierarchy = false) => new(
            new VideoCatalogAssetUpsert(
                mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, parsed.NormalizedTitle, "Anime", 1,
                now, now, now, VideoMediaAvailability.Available, sourceId,
                parsed.EpisodeStart, parsed.EpisodeEnd),
            parsed,
            RebuildHierarchy: rebuildHierarchy);

        var firstGeneration = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, ct);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, firstGeneration, now, [ScanAsset()], true), ct)).Should().BeTrue();
        var metadataJob = await repository.BeginMetadataRefreshAsync(sourceId, 1, ct);
        await repository.UpdateMetadataRefreshAsync(
            metadataJob, VideoCatalogJobState.Completed, 1, null, ct);

        var secondGeneration = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, ct);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, secondGeneration, now, [ScanAsset()], true), ct)).Should().BeTrue();

        (await repository.GetSnapshotAsync(ct)).Jobs
            .Single(job => job.Id == metadataJob).State.Should().Be(VideoCatalogJobState.Completed);
    }

    [Fact]
    public async Task HierarchyRepair_RebindsLegacyDirectSeriesSupplementalToCanonicalSpecials()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var regularPath = Path.Combine(sourcePath, "Show S03E01.mkv");
        var previewPath = Path.Combine(sourcePath, "PV", "Different Release PV 01.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
        var sourceId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        await using var repository = Create(database, Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Anime", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var regular = new ParsedVideoIdentity(
            "Show S03E01", "Show", null, null, 3, 1, 1, null, null, null,
            ParsedVideoSpecialKind.None, false, true,
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty, []);
        var firstGeneration = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.IncrementalScan, ct);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, firstGeneration, now,
            [new VideoScanAsset(new VideoCatalogAssetUpsert(
                regularPath, VideoMediaAssetKind.LocalFile, regularPath, "Show", "Anime", 1,
                now, now, now, VideoMediaAvailability.Available, sourceId, 1, 1), regular)],
            true), ct)).Should().BeTrue();
        var canonical = (await repository.GetSnapshotAsync(ct)).Nodes
            .Single(node => node.Kind == VideoCatalogNodeKind.Series);
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            previewPath, VideoMediaAssetKind.LocalFile, previewPath, "Different Release", "Anime", 1,
            now, now, now, VideoMediaAvailability.Available, sourceId), ct);
        var previewAsset = (await repository.GetSnapshotAsync(ct)).Assets
            .Single(asset => asset.IdentityKey == previewPath);

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE catalog_nodes SET identity_locked=1 WHERE id=$canonical;
                DELETE FROM node_assets WHERE asset_id=$asset;
                INSERT INTO catalog_nodes(id,parent_id,kind,primary_title,is_special,identity_locked,created_at,updated_at)
                VALUES($fake,NULL,'series','Different Release',0,0,$now,$now);
                INSERT INTO catalog_aliases(node_id,provider_id,alias,normalized_alias)
                VALUES($fake,'filename','Different Release','differentrelease');
                INSERT INTO node_assets(node_id,asset_id,is_preferred,ordinal)
                VALUES($fake,$asset,1,0);
                """;
            command.Parameters.AddWithValue("$canonical", canonical.Id.ToString("D"));
            command.Parameters.AddWithValue("$asset", previewAsset.Id.ToString("D"));
            command.Parameters.AddWithValue("$fake", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }

        var preview = new ParsedVideoIdentity(
            "Different Release PV 01", "Show", "Show", null, 0, null, null, null, null, null,
            ParsedVideoSpecialKind.Preview, false, true,
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty, [], "PV 01");
        var repairGeneration = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.IncrementalScan, ct);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, repairGeneration, now,
            [new VideoScanAsset(new VideoCatalogAssetUpsert(
                previewPath, VideoMediaAssetKind.LocalFile, previewPath, "Show", "Anime", 1,
                now, now, now, VideoMediaAvailability.Available, sourceId),
                preview, RebuildHierarchy: true)],
            true), ct)).Should().BeTrue();

        var repaired = await repository.GetSnapshotAsync(ct);
        repaired.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Series)
            .Which.Id.Should().Be(canonical.Id);
        var specialSeason = repaired.Nodes.Should().ContainSingle(node =>
            node.Kind == VideoCatalogNodeKind.Season && node.ParentId == canonical.Id
            && node.SeasonNumber == 0 && node.IsSpecial).Subject;
        var special = repaired.Nodes.Should().ContainSingle(node =>
            node.Kind == VideoCatalogNodeKind.Episode && node.ParentId == specialSeason.Id).Subject;
        special.IsSpecial.Should().BeTrue();
        special.EpisodeNumber.Should().BeNull();
        repaired.Assets.Single(asset => asset.Id == previewAsset.Id).NodeIds.Should().Equal(special.Id);
    }

    [Fact]
    public async Task MetadataSnapshot_ProjectsGenresActorsAttributionAndCachedBackdrop()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var mediaPath = Path.Combine(temp.Path, "作品 S01E01.mkv");
        var backdropPath = Path.Combine(temp.Path, "cached-backdrop.jpg");
        var personPath = Path.Combine(temp.Path, "cached-person.jpg");
        var relatedPath = Path.Combine(temp.Path, "cached-related.jpg");
        await File.WriteAllBytesAsync(mediaPath, [1], ct);
        await File.WriteAllBytesAsync(backdropPath, [0xFF, 0xD8, 0xFF], ct);
        await File.WriteAllBytesAsync(personPath, [0xFF, 0xD8, 0xFF], ct);
        await File.WriteAllBytesAsync(relatedPath, [0xFF, 0xD8, 0xFF], ct);
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "作品 S01E01", temp.Path,
            1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            VideoMediaAvailability.Available, EpisodeStart: 1, EpisodeEnd: 1), ct);
        var assetId = (await repository.GetSnapshotAsync(ct)).Assets.Single().Id;
        var candidate = new VideoMetadataCandidate(
            "tmdb", "42", VideoMetadataMediaKind.Episode, "作品", "作品 原題", 2024,
            1, 1, null, ["作品"],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("tmdb", "42"),
            "https://www.themoviedb.org/tv/42");
        var now = DateTimeOffset.UtcNow;
        var details = new VideoMetadataDetails(
            "tmdb", "42", VideoMetadataMediaKind.Episode, "第一話", "作品 原題", null,
            "あらすじ", 2024, 1, 1, null, ["作品"], ["Animation", "Comedy"],
            ["Actor A", "Actor B"], candidate.ExternalIds, candidate.SourceUrl,
            now, now.AddDays(30), "tagline", "PG12", 8.1, 2025, "Returning",
            ["time travel"], ["Studio A"],
            [new VideoPersonCredit("7", "Actor A", "Hero", "Actor", null)],
            [new VideoRelatedItem("tmdb", "99", "Related", null, 2023, null, null, null)]);

        await repository.ApplyMetadataMatchAsync(assetId, candidate, details, false, false, ct);
        await repository.ApplyArtworkAsync(
            assetId, VideoMetadataMediaKind.Episode, "tmdb", "backdrop", "https://image.tmdb.org/backdrop.jpg",
            backdropPath, "\"v1\"", now, ct);
        await repository.ApplyArtworkAsync(
            assetId, VideoMetadataMediaKind.Episode, "tmdb", "person:7", "https://image.tmdb.org/person.jpg",
            personPath, null, now, ct);
        await repository.ApplyArtworkAsync(
            assetId, VideoMetadataMediaKind.Episode, "tmdb", "related:tmdb:99:poster", "https://image.tmdb.org/related.jpg",
            relatedPath, null, now, ct);
        var node = (await repository.GetSnapshotAsync(ct)).Nodes.Single(item => item.Kind == VideoCatalogNodeKind.Episode);

        node.Genres.Should().Equal("Animation", "Comedy");
        node.Actors.Should().Equal("Actor A", "Actor B");
        node.ProviderSourceUrls.Should().Contain("tmdb", "https://www.themoviedb.org/tv/42");
        node.BackdropPath.Should().Be(backdropPath);
        node.MetadataExpiresAt.Should().BeCloseTo(now.AddDays(30), TimeSpan.FromSeconds(1));
        node.Tagline.Should().Be("tagline");
        node.OfficialRating.Should().Be("PG12");
        node.CommunityRating.Should().Be(8.1);
        node.EndYear.Should().Be(2025);
        node.Status.Should().Be("Returning");
        node.Tags.Should().Contain("time travel");
        node.Studios.Should().Contain("Studio A");
        node.People.Should().ContainSingle(person => person.Role == "Hero"
                                                     && person.LocalImagePath == personPath);
        node.RelatedItems.Should().ContainSingle(item => item.ProviderItemId == "99"
                                                           && item.LocalPosterPath == relatedPath);
    }

    [Fact]
    public async Task ClearAllScrapeRecords_ResetsCatalogToUnmatchedAndPreservesMediaAndUserState()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var mediaPath = Path.Combine(sourcePath, "Show S01E01.mkv");
        var localPosterPath = Path.Combine(sourcePath, "poster.jpg");
        var remotePosterPath = Path.Combine(temp.Path, "cached-tmdb-poster.jpg");
        await File.WriteAllBytesAsync(mediaPath, [1, 2, 3], ct);
        await File.WriteAllBytesAsync(localPosterPath, [4, 5, 6], ct);
        await File.WriteAllBytesAsync(remotePosterPath, [7, 8, 9], ct);
        var sourceId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        await using var repository = Create(database, Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"),
            Name = "Anime",
            FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);

        var parser = new VideoFileNameParser();
        var parsed = parser.Parse(mediaPath, sourcePath, VideoLibraryMediaType.Anime);
        var generation = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, ct);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId,
            generation,
            now,
            [new VideoScanAsset(new VideoCatalogAssetUpsert(
                mediaPath,
                VideoMediaAssetKind.LocalFile,
                mediaPath,
                "Show S01E01",
                sourcePath,
                3,
                now,
                now,
                now,
                VideoMediaAvailability.Available,
                sourceId,
                1,
                1), parsed)],
            true), ct)).Should().BeTrue();

        var seeded = await repository.GetSnapshotAsync(ct);
        var asset = seeded.Assets.Should().ContainSingle().Subject;
        var series = seeded.Nodes.Single(node => node.Kind == VideoCatalogNodeKind.Series);
        var episode = seeded.Nodes.Single(node => node.Kind == VideoCatalogNodeKind.Episode);
        var metadataJob = await repository.BeginMetadataRefreshAsync(sourceId, 1, ct);
        await repository.UpdateMetadataRefreshAsync(
            metadataJob, VideoCatalogJobState.Completed, 1, null, ct);
        await repository.UpsertProviderCacheAsync(new VideoProviderCacheEntry(
            "tmdb:test",
            "tmdb",
            "\"etag\"",
            now,
            [1, 2, 3],
            "application/json",
            now,
            now.AddDays(30)), ct);

        var localArtworkId = Guid.NewGuid();
        var remoteArtworkId = Guid.NewGuid();
        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE catalog_nodes
                SET primary_title='Remote title',overview='Remote overview',year=2024,identity_locked=1
                WHERE id=$series;
                INSERT INTO metadata_snapshots(
                    id,node_id,provider_id,provider_item_id,payload_json,source_url,
                    fetched_at,expires_at)
                VALUES($snapshot,$series,'tmdb','202','{}','https://www.themoviedb.org/tv/202',$now,$expires);
                INSERT OR REPLACE INTO metadata_field_values(
                    node_id,field,value,provider_id,priority,is_locked,updated_at)
                VALUES
                    ($series,'title','Local title','local',300,0,$now),
                    ($series,'externalIds','{"anidb":"101"}','local',300,0,$now),
                    ($series,'title','Remote title','tmdb',200,1,$now),
                    ($series,'overview','Remote overview','tmdb',200,0,$now);
                INSERT INTO artwork(
                    id,node_id,provider_id,kind,local_path,selected,ordinal,created_at,updated_at)
                VALUES
                    ($localArtwork,$series,'local','poster',$localPoster,1,0,$now,$now),
                    ($remoteArtwork,$series,'tmdb','poster',$remotePoster,1,0,$now,$now);
                INSERT OR REPLACE INTO external_ids(
                    node_id,provider_id,external_id,is_identity_locked)
                VALUES
                    ($series,'anidb','101',1),
                    ($series,'tmdb','202',1);
                INSERT OR REPLACE INTO catalog_aliases(
                    node_id,provider_id,alias,normalized_alias)
                VALUES
                    ($series,'filename','Show','show'),
                    ($series,'local','Local alias','localalias'),
                    ($series,'tmdb','Remote alias','remotealias');
                INSERT INTO match_candidates(
                    id,asset_id,provider_id,provider_item_id,title,score,title_score,
                    evidence,hard_conflict,created_at)
                VALUES($candidate,$asset,'tmdb','202','Remote title',0.95,0.95,'title',0,$now);
                INSERT INTO tmdb_show_xrefs(
                    series_node_id,anidb_anime_id,tmdb_show_id,chosen_ordering_id,
                    chosen_ordering_type,match_rating,created_at,updated_at)
                VALUES($series,101,202,'order',7,0,$now,$now);
                INSERT INTO tmdb_orderings(
                    series_node_id,tmdb_show_id,ordering_id,ordering_type,is_preferred,
                    is_user_preferred,created_at,updated_at)
                VALUES($series,202,'order',7,1,1,$now,$now);
                INSERT INTO tmdb_episode_xrefs(
                    episode_node_id,series_node_id,anidb_anime_id,anidb_episode_id,
                    tmdb_show_id,tmdb_episode_id,ordering_id,season_id,season_number,
                    episode_number,ordinal,match_rating,created_at,updated_at)
                VALUES($episode,$series,101,1001,202,2001,'order','season-1',1,1,0,0,$now,$now);
                INSERT INTO node_user_data(
                    node_id,is_favorite,preferred_artwork_id,updated_at)
                VALUES($series,1,$remoteArtwork,$now);
                """;
            command.Parameters.AddWithValue("$series", series.Id.ToString("D"));
            command.Parameters.AddWithValue("$episode", episode.Id.ToString("D"));
            command.Parameters.AddWithValue("$asset", asset.Id.ToString("D"));
            command.Parameters.AddWithValue("$snapshot", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$candidate", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$localArtwork", localArtworkId.ToString("D"));
            command.Parameters.AddWithValue("$remoteArtwork", remoteArtworkId.ToString("D"));
            command.Parameters.AddWithValue("$localPoster", localPosterPath);
            command.Parameters.AddWithValue("$remotePoster", remotePosterPath);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$expires", now.AddDays(30).ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }

        await repository.ClearAllScrapeRecordsAsync(ct);

        var cleared = await repository.GetSnapshotAsync(ct);
        cleared.Sources.Should().ContainSingle(source => source.Id == sourceId);
        var clearedAsset = cleared.Assets.Should().ContainSingle(item =>
            item.Id == asset.Id && item.Location == mediaPath && item.Availability == VideoMediaAvailability.Available);
        clearedAsset.Subject.IsFavorite.Should().BeTrue(
            "favorite state on deleted catalog ancestry is migrated to the media asset");
        clearedAsset.Subject.SourceIds.Should().Equal(sourceId);
        var unmatched = AssertResetToDistinctRootUnmatched(cleared, asset.Id);
        unmatched.Id.Should().NotBe(series.Id).And.NotBe(episode.Id);
        unmatched.PrimaryTitle.Should().Be(clearedAsset.Subject.Title,
            "the reset node starts from source media identity, not any catalog field projection");
        unmatched.Overview.Should().BeNull();
        unmatched.Year.Should().BeNull();
        unmatched.IdentityLocked.Should().BeFalse();
        unmatched.ExternalIds.Should().BeEmpty(
            "manual and automatic node identities are cleared with the catalog projection");
        unmatched.IdentityLockedProviders.Should().BeEmpty();
        unmatched.Aliases.Should().BeEmpty("filename and Local aliases are catalog projections too");
        unmatched.ArtworkCandidates.Should().BeEmpty("Local and online artwork projections are reset together");
        unmatched.TmdbShowCrossReferences.Should().BeEmpty();
        unmatched.TmdbEpisodeCrossReferences.Should().BeEmpty();
        unmatched.TmdbOrderings.Should().BeEmpty();
        cleared.Nodes.Select(node => node.Id).Should().NotContain([series.Id, episode.Id]);
        cleared.Nodes.Should().NotContain(node =>
            node.Kind == VideoCatalogNodeKind.Series
            || node.Kind == VideoCatalogNodeKind.Season
            || node.Kind == VideoCatalogNodeKind.Episode
            || node.Kind == VideoCatalogNodeKind.Movie);
        cleared.MatchCandidates.Should().BeEmpty();
        cleared.Jobs.Should().NotContain(job => job.Kind == VideoCatalogJobKind.MetadataRefresh);
        cleared.Jobs.Should().Contain(job => job.Kind == VideoCatalogJobKind.FullScan);
        (await repository.GetProviderCacheAsync("tmdb:test", ct)).Should().BeNull();

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM library_sources;";
            (await command.ExecuteScalarAsync(ct)).Should().Be(1L);
            command.CommandText = "SELECT COUNT(*) FROM media_assets;";
            (await command.ExecuteScalarAsync(ct)).Should().Be(1L);
            command.CommandText = "SELECT COUNT(*) FROM source_assets;";
            (await command.ExecuteScalarAsync(ct)).Should().Be(1L);
            command.CommandText = "SELECT COUNT(*) FROM asset_user_data WHERE asset_id=$asset;";
            command.Parameters.AddWithValue("$asset", asset.Id.ToString("D"));
            (await command.ExecuteScalarAsync(ct)).Should().Be(1L);
            command.CommandText = "SELECT COUNT(*) FROM metadata_snapshots;";
            (await command.ExecuteScalarAsync(ct)).Should().Be(0L);
            command.CommandText = "SELECT COUNT(*) FROM metadata_field_values;";
            (await command.ExecuteScalarAsync(ct)).Should().Be(0L);
            command.CommandText = "SELECT COUNT(*) FROM external_ids;";
            (await command.ExecuteScalarAsync(ct)).Should().Be(0L);
            command.CommandText = "SELECT COUNT(*) FROM artwork;";
            (await command.ExecuteScalarAsync(ct)).Should().Be(0L);
            command.CommandText = "SELECT COUNT(*) FROM catalog_aliases;";
            (await command.ExecuteScalarAsync(ct)).Should().Be(0L);
            command.CommandText = "SELECT COUNT(*) FROM match_candidates;";
            (await command.ExecuteScalarAsync(ct)).Should().Be(0L);
            command.CommandText = "SELECT COUNT(*) FROM tmdb_show_xrefs;";
            (await command.ExecuteScalarAsync(ct)).Should().Be(0L);
            command.CommandText = "SELECT COUNT(*) FROM tmdb_orderings;";
            (await command.ExecuteScalarAsync(ct)).Should().Be(0L);
            command.CommandText = "SELECT COUNT(*) FROM tmdb_episode_xrefs;";
            (await command.ExecuteScalarAsync(ct)).Should().Be(0L);
            command.CommandText = "SELECT COUNT(*) FROM node_user_data;";
            (await command.ExecuteScalarAsync(ct)).Should().Be(0L);
        }

        File.Exists(mediaPath).Should().BeTrue();
        File.Exists(localPosterPath).Should().BeTrue();
    }

    [Fact]
    public async Task ClearAllScrapeRecords_CollapsesAutomaticTopologyAndPreservesAssetState()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var mediaPath = Path.Combine(sourcePath, "Show S01E01.mkv");
        await File.WriteAllBytesAsync(mediaPath, [1, 2, 3, 4], ct);
        var mediaHash = SHA256.HashData(await File.ReadAllBytesAsync(mediaPath, ct));
        var sourceId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        await using var repository = Create(database, Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"),
            Name = "Anime",
            FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var parsed = new VideoFileNameParser().Parse(
            mediaPath, sourcePath, VideoLibraryMediaType.Anime);
        var generation = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, ct);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId,
            generation,
            now,
            [new VideoScanAsset(new VideoCatalogAssetUpsert(
                mediaPath,
                VideoMediaAssetKind.LocalFile,
                mediaPath,
                parsed.NormalizedTitle,
                sourcePath,
                4,
                now,
                now,
                now,
                VideoMediaAvailability.Available,
                sourceId,
                parsed.EpisodeStart,
                parsed.EpisodeEnd), parsed)],
            true), ct)).Should().BeTrue();
        await repository.UpsertCollectionAsync(new VideoCollection
        {
            Id = collectionId.ToString("D"),
            Name = "Keep collection",
            Kind = VideoCollectionKind.Manual,
        }, ct);
        await repository.SetCollectionAssetsAsync(collectionId, [mediaPath], ct);
        var assetId = (await repository.GetSnapshotAsync(ct)).Assets.Single().Id;
        var remoteSeriesId = Guid.NewGuid();
        var remoteSeasonId = Guid.NewGuid();
        var remoteEpisodeId = Guid.NewGuid();
        var remoteArtworkId = Guid.NewGuid();
        var tagId = Guid.NewGuid();

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM node_assets WHERE asset_id=$asset;
                UPDATE asset_user_data
                SET display_title='Keep display title',is_favorite=1,profile_id='profile',updated_at=$now
                WHERE asset_id=$asset;
                INSERT INTO tags(id,name,normalized_name) VALUES($tag,'keep-tag','keeptag');
                INSERT INTO asset_tags(asset_id,tag_id) VALUES($asset,$tag);
                INSERT INTO catalog_nodes(
                    id,parent_id,kind,primary_title,is_special,identity_locked,created_at,updated_at)
                VALUES($series,NULL,'series','Remote Show',0,0,$now,$now);
                INSERT INTO catalog_nodes(
                    id,parent_id,kind,primary_title,season_number,is_special,identity_locked,created_at,updated_at)
                VALUES($season,$series,'season','Remote Season',99,0,0,$now,$now);
                INSERT INTO catalog_nodes(
                    id,parent_id,kind,primary_title,season_number,episode_number,is_special,
                    identity_locked,created_at,updated_at)
                VALUES($episode,$season,'episode','Remote Episode',99,8,0,0,$now,$now);
                INSERT INTO node_assets(node_id,asset_id,is_preferred,ordinal)
                VALUES($episode,$asset,1,0);
                INSERT INTO metadata_field_values(
                    node_id,field,value,provider_id,priority,is_locked,updated_at)
                VALUES($series,'title','Remote Show','tmdb',200,0,$now);
                INSERT INTO external_ids(node_id,provider_id,external_id,is_identity_locked)
                VALUES($series,'tmdb','999',0);
                INSERT INTO artwork(
                    id,node_id,provider_id,kind,remote_url,local_path,selected,ordinal,created_at,updated_at)
                VALUES($artwork,$series,'tmdb','poster','https://image.tmdb.org/remote.jpg',
                       $cachePath,1,0,$now,$now);
                INSERT INTO node_user_data(node_id,preferred_artwork_id,updated_at)
                VALUES($series,$artwork,$now);
                """;
            command.Parameters.AddWithValue("$asset", assetId.ToString("D"));
            command.Parameters.AddWithValue("$series", remoteSeriesId.ToString("D"));
            command.Parameters.AddWithValue("$season", remoteSeasonId.ToString("D"));
            command.Parameters.AddWithValue("$episode", remoteEpisodeId.ToString("D"));
            command.Parameters.AddWithValue("$artwork", remoteArtworkId.ToString("D"));
            command.Parameters.AddWithValue("$tag", tagId.ToString("D"));
            command.Parameters.AddWithValue("$cachePath", Path.Combine(temp.Path, "remote.jpg"));
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }

        await repository.ClearAllScrapeRecordsAsync(ct);

        var cleared = await repository.GetSnapshotAsync(ct);
        cleared.Sources.Should().ContainSingle(source => source.Id == sourceId);
        var clearedAsset = cleared.Assets.Should().ContainSingle().Subject;
        clearedAsset.Id.Should().Be(assetId);
        clearedAsset.Location.Should().Be(mediaPath);
        clearedAsset.DisplayTitle.Should().Be("Keep display title");
        clearedAsset.IsFavorite.Should().BeTrue();
        clearedAsset.Tags.Should().Equal("keep-tag");
        clearedAsset.CollectionIds.Should().Equal(collectionId);
        cleared.Collections.Should().ContainSingle(collection =>
            collection.Id == collectionId && collection.AssetIds.Contains(assetId));
        cleared.Nodes.Select(node => node.Id).Should().NotContain(
            [remoteSeriesId, remoteSeasonId, remoteEpisodeId]);
        var unmatched = AssertResetToDistinctRootUnmatched(cleared, assetId);
        unmatched.PrimaryTitle.Should().Be("Keep display title");
        unmatched.ExternalIds.Should().BeEmpty();
        unmatched.IdentityLockedProviders.Should().BeEmpty();
        unmatched.Aliases.Should().BeEmpty();
        unmatched.ArtworkCandidates.Should().BeEmpty();
        SHA256.HashData(await File.ReadAllBytesAsync(mediaPath, ct)).Should().Equal(mediaHash);

        await using var verification = new SqliteConnection($"Data Source={database};Pooling=False");
        await verification.OpenAsync(ct);
        using var verify = verification.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM node_user_data WHERE node_id=$series;";
        verify.Parameters.AddWithValue("$series", remoteSeriesId.ToString("D"));
        (await verify.ExecuteScalarAsync(ct)).Should().Be(0L,
            "an empty row left by deleted remote artwork must not protect automatic topology");
        verify.CommandText = "SELECT COUNT(*) FROM source_assets WHERE asset_id=$asset;";
        verify.Parameters.AddWithValue("$asset", assetId.ToString("D"));
        (await verify.ExecuteScalarAsync(ct)).Should().Be(1L);
        verify.CommandText = "SELECT COUNT(*) FROM asset_user_data WHERE asset_id=$asset;";
        (await verify.ExecuteScalarAsync(ct)).Should().Be(1L);
        verify.CommandText = "SELECT COUNT(*) FROM asset_tags WHERE asset_id=$asset;";
        (await verify.ExecuteScalarAsync(ct)).Should().Be(1L);
        verify.CommandText = "SELECT COUNT(*) FROM collection_assets WHERE asset_id=$asset;";
        (await verify.ExecuteScalarAsync(ct)).Should().Be(1L);
        verify.CommandText = "SELECT COUNT(*) FROM metadata_field_values;";
        (await verify.ExecuteScalarAsync(ct)).Should().Be(0L);
        verify.CommandText = "SELECT COUNT(*) FROM external_ids;";
        (await verify.ExecuteScalarAsync(ct)).Should().Be(0L);
        verify.CommandText = "SELECT COUNT(*) FROM artwork;";
        (await verify.ExecuteScalarAsync(ct)).Should().Be(0L);
    }

    [Fact]
    public async Task ClearAllScrapeRecords_PersistsResetMarkerUntilExplicitFullScan()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var legacy = Path.Combine(temp.Path, "video_library.json");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var mediaPath = Path.Combine(sourcePath, "Marker Show S01E01.mkv");
        var newMediaPath = Path.Combine(sourcePath, "Marker Show S01E02.mkv");
        await File.WriteAllBytesAsync(mediaPath, [3, 1, 4], ct);
        var sourceId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 23, 17, 0, 0, TimeSpan.Zero);
        var parsed = new VideoFileNameParser().Parse(
            mediaPath, sourcePath, VideoLibraryMediaType.Anime);
        var scanAsset = new VideoScanAsset(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, parsed.NormalizedTitle,
            sourcePath, 3, now, now, now, VideoMediaAvailability.Available, sourceId,
            parsed.EpisodeStart, parsed.EpisodeEnd), parsed);
        Guid assetId;
        Guid newAssetId;
        VideoScanAsset newScanAsset;

        await using (var repository = Create(database, legacy))
        {
            await repository.InitializeAsync(ct);
            await repository.UpsertSourceAsync(new VideoLibrarySource
            {
                Id = sourceId.ToString("D"),
                Name = "Anime",
                FolderPath = sourcePath,
                MediaType = VideoLibraryMediaType.Anime,
            }, ct);
            var fullGeneration = await repository.BeginSourceScanAsync(
                sourceId, VideoCatalogJobKind.FullScan, ct);
            (await repository.ApplyScanBatchAsync(new VideoScanBatch(
                sourceId, fullGeneration, now, [scanAsset], true), ct)).Should().BeTrue();
            assetId = (await repository.GetSnapshotAsync(ct)).Assets.Single().Id;

            await repository.ClearAllScrapeRecordsAsync(ct);
            var reset = await repository.GetSnapshotAsync(ct);
            AssertResetToDistinctRootUnmatched(reset, assetId);
            reset.Assets.Single(asset => asset.Id == assetId).CatalogResetPending.Should().BeTrue();

            await File.WriteAllBytesAsync(newMediaPath, [1, 5, 9], ct);
            var newParsed = new VideoFileNameParser().Parse(
                newMediaPath, sourcePath, VideoLibraryMediaType.Anime);
            newScanAsset = new VideoScanAsset(new VideoCatalogAssetUpsert(
                newMediaPath, VideoMediaAssetKind.LocalFile, newMediaPath,
                newParsed.NormalizedTitle, sourcePath, 3, now, now, now,
                VideoMediaAvailability.Available, sourceId,
                newParsed.EpisodeStart, newParsed.EpisodeEnd), newParsed);

            var incrementalGeneration = await repository.BeginSourceScanAsync(
                sourceId, VideoCatalogJobKind.IncrementalScan, ct);
            (await repository.ApplyScanBatchAsync(new VideoScanBatch(
                sourceId, incrementalGeneration, now.AddMinutes(1),
                [scanAsset, newScanAsset], true), ct)).Should().BeTrue();
            var afterIncremental = await repository.GetSnapshotAsync(ct);
            newAssetId = afterIncremental.Assets.Single(asset =>
                string.Equals(asset.Location, newMediaPath, StringComparison.OrdinalIgnoreCase)).Id;
            AssertResetToDistinctRootUnmatched(afterIncremental, assetId);
            AssertResetToDistinctRootUnmatched(afterIncremental, newAssetId);
            afterIncremental.Assets.Should().OnlyContain(asset => asset.CatalogResetPending,
                "the source-level marker also covers files first seen after the clear");
        }

        await using (var reopened = Create(database, legacy))
        {
            await reopened.InitializeAsync(ct);
            var incrementalGeneration = await reopened.BeginSourceScanAsync(
                sourceId, VideoCatalogJobKind.IncrementalScan, ct);
            (await reopened.ApplyScanBatchAsync(new VideoScanBatch(
                sourceId, incrementalGeneration, now.AddMinutes(2),
                [scanAsset, newScanAsset], true), ct)).Should().BeTrue();
            var afterReopenedIncremental = await reopened.GetSnapshotAsync(ct);
            AssertResetToDistinctRootUnmatched(afterReopenedIncremental, assetId);
            AssertResetToDistinctRootUnmatched(afterReopenedIncremental, newAssetId);
            afterReopenedIncremental.Assets.Should().OnlyContain(asset => asset.CatalogResetPending,
                "both asset and source reset markers persist in SQLite");

            var fullGeneration = await reopened.BeginSourceScanAsync(
                sourceId, VideoCatalogJobKind.FullScan, ct);
            (await reopened.ApplyScanBatchAsync(new VideoScanBatch(
                sourceId, fullGeneration, now.AddMinutes(3),
                [scanAsset, newScanAsset], true), ct)).Should().BeTrue();
            var rebuilt = await reopened.GetSnapshotAsync(ct);
            rebuilt.Assets.Should().OnlyContain(asset => !asset.CatalogResetPending);
            var series = rebuilt.Nodes.Should().ContainSingle(node =>
                node.Kind == VideoCatalogNodeKind.Series).Subject;
            var season = rebuilt.Nodes.Should().ContainSingle(node =>
                node.Kind == VideoCatalogNodeKind.Season && node.ParentId == series.Id).Subject;
            var episodes = rebuilt.Nodes.Where(node =>
                    node.Kind == VideoCatalogNodeKind.Episode && node.ParentId == season.Id)
                .OrderBy(node => node.EpisodeNumber)
                .ToArray();
            episodes.Should().HaveCount(2);
            episodes.Select(node => node.EpisodeNumber).Should().Equal(1, 2);
            rebuilt.Assets.Single(asset => asset.Id == assetId).NodeIds.Should().Equal(episodes[0].Id);
            rebuilt.Assets.Single(asset => asset.Id == newAssetId).NodeIds.Should().Equal(episodes[1].Id);
        }
    }

    [Fact]
    public async Task PausedFullScan_IsStillFullAndClearsCatalogResetMarker()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var mediaPath = Path.Combine(sourcePath, "Paused Show S01E01.mkv");
        await File.WriteAllBytesAsync(mediaPath, [2, 7, 1], ct);
        var sourceId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 23, 18, 0, 0, TimeSpan.Zero);
        var parsed = new VideoFileNameParser().Parse(
            mediaPath, sourcePath, VideoLibraryMediaType.Anime);
        var scanAsset = new VideoScanAsset(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, parsed.NormalizedTitle,
            sourcePath, 3, now, now, now, VideoMediaAvailability.Available, sourceId,
            parsed.EpisodeStart, parsed.EpisodeEnd), parsed);
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"),
            Name = "Anime",
            FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var initialGeneration = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, ct);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, initialGeneration, now, [scanAsset], true), ct)).Should().BeTrue();
        var assetId = (await repository.GetSnapshotAsync(ct)).Assets.Single().Id;
        await repository.ClearAllScrapeRecordsAsync(ct);
        (await repository.GetSnapshotAsync(ct)).Assets.Single().CatalogResetPending.Should().BeTrue();

        var rebuildGeneration = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, ct);
        await repository.SetSourceScanPausedAsync(sourceId, true, ct);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, rebuildGeneration, now.AddMinutes(1), [scanAsset], false,
            IsFinal: false, TotalCount: 1), ct)).Should().BeTrue();

        var rebuilt = await repository.GetSnapshotAsync(ct);
        rebuilt.Assets.Single(asset => asset.Id == assetId).CatalogResetPending.Should().BeFalse();
        rebuilt.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Series);
        rebuilt.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Season);
        rebuilt.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Episode);
        rebuilt.Jobs.Should().ContainSingle(job =>
            job.Generation == rebuildGeneration && job.State == VideoCatalogJobState.Paused);
    }

    [Fact]
    public async Task ClearAllScrapeRecords_RemovesAutomaticAniDbAidAndEidLocks()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var mediaPath = Path.Combine(sourcePath, "Automatic Show S01E01.mkv");
        await File.WriteAllBytesAsync(mediaPath, [4, 2], ct);
        var sourceId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 23, 15, 0, 0, TimeSpan.Zero);
        await using var repository = Create(database, Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Anime", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var parsed = new VideoFileNameParser().Parse(
            mediaPath, sourcePath, VideoLibraryMediaType.Anime);
        var generation = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, ct);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId,
            generation,
            now,
            [new VideoScanAsset(new VideoCatalogAssetUpsert(
                mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, parsed.NormalizedTitle,
                sourcePath, 2, now, now, now, VideoMediaAvailability.Available, sourceId,
                parsed.EpisodeStart, parsed.EpisodeEnd), parsed)],
            true), ct)).Should().BeTrue();
        var assetId = (await repository.GetSnapshotAsync(ct)).Assets.Single().Id;
        await repository.ApplyAniDbIdentityAsync(
            assetId, CreateAniDbProjection(101, 1001, 1, now), ct);
        var projected = await repository.GetSnapshotAsync(ct);
        projected.Nodes.Should().Contain(node =>
            node.ExternalIds.GetValueOrDefault("anidb") == "101"
            && node.IdentityLockedProviders.Contains("anidb"));
        projected.Nodes.Should().Contain(node =>
            node.ExternalIds.GetValueOrDefault("anidb-episode") == "1001"
            && node.IdentityLockedProviders.Contains("anidb-episode"));

        await repository.ClearAllScrapeRecordsAsync(ct);

        var cleared = await repository.GetSnapshotAsync(ct);
        var unmatched = AssertResetToDistinctRootUnmatched(cleared, assetId);
        unmatched.ExternalIds.Should().NotContainKey("anidb")
            .And.NotContainKey("anidb-episode");
        unmatched.IdentityLocked.Should().BeFalse();
        unmatched.IdentityLockedProviders.Should().BeEmpty();
    }

    [Fact]
    public async Task ClearAllScrapeRecords_DoesNotProjectManualAniDbWhitelistIntoResetCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var mediaPath = Path.Combine(sourcePath, "Changed Manual Show S01E01.mkv");
        await File.WriteAllBytesAsync(mediaPath, [5], ct);
        var sourceId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 23, 15, 30, 0, TimeSpan.Zero);
        await using var repository = Create(database, Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Anime", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var parsed = new VideoFileNameParser().Parse(
            mediaPath, sourcePath, VideoLibraryMediaType.Anime);
        var generation = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, ct);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId,
            generation,
            now,
            [new VideoScanAsset(new VideoCatalogAssetUpsert(
                mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, parsed.NormalizedTitle,
                sourcePath, 1, now, now, now, VideoMediaAvailability.Available, sourceId,
                parsed.EpisodeStart, parsed.EpisodeEnd), parsed)],
            true), ct)).Should().BeTrue();
        var assetId = (await repository.GetSnapshotAsync(ct)).Assets.Single().Id;
        await repository.ApplyAniDbIdentityAsync(
            assetId, CreateAniDbProjection(101, 1001, 1, now), ct);

        await repository.ClearAllScrapeRecordsAsync(
            [new VideoManualAniDbIdentity(
                assetId,
                System.Collections.Immutable.ImmutableHashSet.Create(202),
                System.Collections.Immutable.ImmutableHashSet.Create(2002))],
            ct);

        var cleared = await repository.GetSnapshotAsync(ct);
        var unmatched = AssertResetToDistinctRootUnmatched(cleared, assetId);
        unmatched.ExternalIds.Should().BeEmpty(
            "the manual whitelist remains authoritative in the independent AniDB store only");
        unmatched.IdentityLocked.Should().BeFalse();
        unmatched.IdentityLockedProviders.Should().BeEmpty();
    }

    [Fact]
    public async Task ClearAllScrapeRecords_DoesNotCarryExactManualAidOrStaleEidIntoUnmatched()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var mediaPath = Path.Combine(temp.Path, "Mixed Identity S01E01.mkv");
        await File.WriteAllBytesAsync(mediaPath, [7], ct);
        var now = new DateTimeOffset(2026, 8, 23, 15, 45, 0, TimeSpan.Zero);
        await using var repository = Create(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            mediaPath,
            VideoMediaAssetKind.LocalFile,
            mediaPath,
            "Mixed Identity",
            temp.Path,
            1,
            now,
            now,
            now,
            VideoMediaAvailability.Available,
            EpisodeStart: 1,
            EpisodeEnd: 1), ct);
        var assetId = (await repository.GetSnapshotAsync(ct)).Assets.Single().Id;
        await repository.ApplyAniDbIdentityAsync(
            assetId, CreateAniDbProjection(101, 1001, 1, now), ct);
        var projectedEpisode = (await repository.GetSnapshotAsync(ct)).Nodes.Single(node =>
            node.ExternalIds.GetValueOrDefault("anidb-episode") == "1001");
        projectedEpisode.ExternalIds.Should().Contain("anidb", "101");

        await repository.ClearAllScrapeRecordsAsync(
            [new VideoManualAniDbIdentity(
                assetId,
                System.Collections.Immutable.ImmutableHashSet.Create(101),
                System.Collections.Immutable.ImmutableHashSet.Create(2002))],
            ct);

        var cleared = await repository.GetSnapshotAsync(ct);
        var unmatched = AssertResetToDistinctRootUnmatched(cleared, assetId);
        unmatched.ExternalIds.Should().BeEmpty();
        unmatched.IdentityLocked.Should().BeFalse();
        unmatched.IdentityLockedProviders.Should().BeEmpty();
    }

    [Fact]
    public async Task ClearAllScrapeRecords_LeavesBothManualAndAutomaticAniDbAssetsUnmatchedAndUnprojected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var manualPath = Path.Combine(sourcePath, "Shared Show S01E01.mkv");
        var automaticPath = Path.Combine(sourcePath, "Shared Show S01E02.mkv");
        await File.WriteAllBytesAsync(manualPath, [1], ct);
        await File.WriteAllBytesAsync(automaticPath, [2], ct);
        var sourceId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 23, 16, 0, 0, TimeSpan.Zero);
        await using var repository = Create(database, Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Anime", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var parsedByPath = VideoScanBundleClassifier.Parse(
            [manualPath, automaticPath], sourcePath, VideoLibraryMediaType.Anime,
            new VideoFileNameParser());
        var generation = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, ct);
        var scanAssets = new[] { manualPath, automaticPath }
            .Select(path => new VideoScanAsset(new VideoCatalogAssetUpsert(
                    path, VideoMediaAssetKind.LocalFile, path,
                    parsedByPath[path].NormalizedTitle, sourcePath, 1, now, now, now,
                    VideoMediaAvailability.Available, sourceId,
                    parsedByPath[path].EpisodeStart, parsedByPath[path].EpisodeEnd),
                parsedByPath[path]))
            .ToArray();
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId, generation, now, scanAssets, true), ct)).Should().BeTrue();
        var assets = (await repository.GetSnapshotAsync(ct)).Assets
            .ToDictionary(asset => asset.Location, StringComparer.OrdinalIgnoreCase);
        var manualAssetId = assets[manualPath].Id;
        var automaticAssetId = assets[automaticPath].Id;
        await repository.ApplyAniDbIdentityAsync(
            manualAssetId, CreateAniDbProjection(101, 1001, 1, now), ct);
        await repository.ApplyAniDbIdentityAsync(
            automaticAssetId, CreateAniDbProjection(101, 1002, 2, now), ct);

        await repository.ClearAllScrapeRecordsAsync(
            [new VideoManualAniDbIdentity(
                manualAssetId,
                System.Collections.Immutable.ImmutableHashSet.Create(101),
                System.Collections.Immutable.ImmutableHashSet.Create(1001))],
            ct);

        var cleared = await repository.GetSnapshotAsync(ct);
        var manualUnmatched = AssertResetToDistinctRootUnmatched(cleared, manualAssetId);
        manualUnmatched.ExternalIds.Should().BeEmpty(
            "manual AniDB identity survives only in the separate AniDB store until explicit re-projection");
        manualUnmatched.IdentityLocked.Should().BeFalse();
        manualUnmatched.IdentityLockedProviders.Should().BeEmpty();

        var automaticUnmatched = AssertResetToDistinctRootUnmatched(cleared, automaticAssetId);
        automaticUnmatched.Id.Should().NotBe(manualUnmatched.Id);
        automaticUnmatched.ExternalIds.Should().NotContainKey("anidb")
            .And.NotContainKey("anidb-episode");
        automaticUnmatched.IdentityLockedProviders.Should().BeEmpty();
    }

    [Fact]
    public async Task ClearAllScrapeRecords_ClearsPersistedLocalNfoProjectionAndPreservesSidecarFile()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var mediaPath = Path.Combine(sourcePath, "Show S01E01.mkv");
        var sidecarPath = Path.Combine(sourcePath, "Show S01E01.nfo");
        await File.WriteAllBytesAsync(mediaPath, [9, 8, 7, 6, 5], ct);
        await File.WriteAllBytesAsync(sidecarPath, [6, 7, 8, 9], ct);
        var mediaHash = SHA256.HashData(await File.ReadAllBytesAsync(mediaPath, ct));
        var sourceId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 23, 13, 0, 0, TimeSpan.Zero);
        await using var repository = Create(database, Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"),
            Name = "Anime",
            FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var parsed = new VideoFileNameParser().Parse(
            mediaPath, sourcePath, VideoLibraryMediaType.Anime);
        var local = LocalVideoMetadata.Empty with
        {
            Title = "Local Show",
            SeasonNumber = 4,
            EpisodeNumber = 7,
            AbsoluteEpisodeNumber = 42,
            ContainerMetadata = LocalVideoMetadataValues.Empty with { Title = "Local Show" },
            SeasonMetadata = LocalVideoMetadataValues.Empty with
            {
                Title = "Local Season 4",
                SeasonNumber = 4,
            },
            EpisodeMetadata = LocalVideoMetadataValues.Empty with
            {
                Title = "Local Episode 7",
                SeasonNumber = 4,
                EpisodeNumber = 7,
                AbsoluteEpisodeNumber = 42,
            },
        };
        var generation = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, ct);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId,
            generation,
            now,
            [new VideoScanAsset(new VideoCatalogAssetUpsert(
                mediaPath,
                VideoMediaAssetKind.LocalFile,
                mediaPath,
                parsed.NormalizedTitle,
                sourcePath,
                5,
                now,
                now,
                now,
                VideoMediaAvailability.Available,
                sourceId,
                parsed.EpisodeStart,
                parsed.EpisodeEnd), parsed, local)],
            true), ct)).Should().BeTrue();
        var before = await repository.GetSnapshotAsync(ct);
        var seasonId = before.Nodes.Single(node =>
            node.Kind == VideoCatalogNodeKind.Season && node.SeasonNumber == 4).Id;
        var episodeId = before.Nodes.Single(node =>
            node.Kind == VideoCatalogNodeKind.Episode && node.EpisodeNumber == 7).Id;

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT value FROM metadata_field_values
                WHERE node_id=$episode AND provider_id='local' AND field='localScope';
                """;
            command.Parameters.AddWithValue("$episode", episodeId.ToString("D"));
            (await command.ExecuteScalarAsync(ct)).Should().Be("episode");
            command.CommandText =
                """
                SELECT value FROM metadata_field_values
                WHERE node_id=$episode AND provider_id='local' AND field='seasonNumber';
                """;
            (await command.ExecuteScalarAsync(ct)).Should().Be("4");
            command.CommandText =
                """
                SELECT value FROM metadata_field_values
                WHERE node_id=$episode AND provider_id='local' AND field='episodeNumber';
                """;
            (await command.ExecuteScalarAsync(ct)).Should().Be("7");
            command.CommandText =
                """
                SELECT value FROM metadata_field_values
                WHERE node_id=$episode AND provider_id='local' AND field='absoluteEpisodeNumber';
                """;
            (await command.ExecuteScalarAsync(ct)).Should().Be("42");

            command.CommandText =
                """
                UPDATE catalog_nodes SET season_number=99,is_special=0 WHERE id=$season;
                UPDATE catalog_nodes
                SET season_number=99,episode_number=8,absolute_episode_number=88,is_special=0
                WHERE id=$episode;
                """;
            command.Parameters.AddWithValue("$season", seasonId.ToString("D"));
            await command.ExecuteNonQueryAsync(ct);
        }

        await repository.ClearAllScrapeRecordsAsync(ct);

        var cleared = await repository.GetSnapshotAsync(ct);
        var clearedAsset = cleared.Assets.Should().ContainSingle().Subject;
        clearedAsset.Location.Should().Be(mediaPath);
        var unmatched = AssertResetToDistinctRootUnmatched(cleared, clearedAsset.Id);
        unmatched.Id.Should().NotBe(seasonId).And.NotBe(episodeId);
        unmatched.PrimaryTitle.Should().Be(clearedAsset.Title)
            .And.NotBe("Local Show")
            .And.NotBe("Local Episode 7");
        unmatched.SeasonNumber.Should().BeNull();
        unmatched.EpisodeNumber.Should().BeNull();
        unmatched.AbsoluteEpisodeNumber.Should().BeNull();
        unmatched.Aliases.Should().BeEmpty();
        unmatched.ExternalIds.Should().BeEmpty();

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM metadata_field_values WHERE provider_id='local';";
            (await command.ExecuteScalarAsync(ct)).Should().Be(0L,
                "Local NFO values are rebuildable catalog projections");
        }

        SHA256.HashData(await File.ReadAllBytesAsync(mediaPath, ct)).Should().Equal(mediaHash);
        (await File.ReadAllBytesAsync(sidecarPath, ct)).Should().Equal(6, 7, 8, 9);
    }

    [Fact]
    public async Task ClearAllScrapeRecords_ClearsTitleOnlyLocalNfoProjectionWithoutRebuildingHierarchy()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var mediaPath = Path.Combine(sourcePath, "Title Only Show S01E01.mkv");
        await File.WriteAllBytesAsync(mediaPath, [2, 4, 6], ct);
        var sourceId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 23, 13, 30, 0, TimeSpan.Zero);
        await using var repository = Create(database, Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Anime", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var parsed = new VideoFileNameParser().Parse(
            mediaPath, sourcePath, VideoLibraryMediaType.Anime);
        var local = LocalVideoMetadata.Empty with
        {
            Title = "Local Episode Title",
            EpisodeMetadata = LocalVideoMetadataValues.Empty with
            {
                Title = "Local Episode Title",
            },
        };
        var generation = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, ct);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId,
            generation,
            now,
            [new VideoScanAsset(new VideoCatalogAssetUpsert(
                mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, parsed.NormalizedTitle,
                sourcePath, 3, now, now, now, VideoMediaAvailability.Available, sourceId,
                parsed.EpisodeStart, parsed.EpisodeEnd), parsed, local)],
            true), ct)).Should().BeTrue();

        var before = await repository.GetSnapshotAsync(ct);
        var seasonId = before.Nodes.Single(node =>
            node.Kind == VideoCatalogNodeKind.Season).Id;
        var episodeId = before.Nodes.Single(node =>
            node.Kind == VideoCatalogNodeKind.Episode).Id;
        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE catalog_nodes SET season_number=9,is_special=1 WHERE id=$season;
                UPDATE catalog_nodes
                SET season_number=9,episode_number=8,absolute_episode_number=88,is_special=1
                WHERE id=$episode;
                """;
            command.Parameters.AddWithValue("$season", seasonId.ToString("D"));
            command.Parameters.AddWithValue("$episode", episodeId.ToString("D"));
            await command.ExecuteNonQueryAsync(ct);

            command.CommandText =
                """
                SELECT COUNT(*) FROM metadata_field_values
                WHERE node_id=$episode AND provider_id='local' AND field='localScope'
                  AND value='episode';
                """;
            (await command.ExecuteScalarAsync(ct)).Should().Be(1L);
            command.CommandText =
                """
                SELECT COUNT(*) FROM metadata_field_values
                WHERE node_id=$episode AND provider_id='local'
                  AND field IN ('seasonNumber','episodeNumber','absoluteEpisodeNumber','isSpecial');
                """;
            (await command.ExecuteScalarAsync(ct)).Should().Be(0L,
                "a title-only NFO must not persist current remote node numbers as Local structure");
        }

        await repository.ClearAllScrapeRecordsAsync(ct);

        var cleared = await repository.GetSnapshotAsync(ct);
        var clearedAsset = cleared.Assets.Should().ContainSingle().Subject;
        var unmatched = AssertResetToDistinctRootUnmatched(cleared, clearedAsset.Id);
        unmatched.Id.Should().NotBe(seasonId).And.NotBe(episodeId);
        unmatched.PrimaryTitle.Should().Be(clearedAsset.Title)
            .And.NotBe("Local Episode Title");
        unmatched.SeasonNumber.Should().BeNull();
        unmatched.EpisodeNumber.Should().BeNull();
        unmatched.AbsoluteEpisodeNumber.Should().BeNull();
        unmatched.IsSpecial.Should().BeFalse();

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM metadata_field_values WHERE provider_id='local';";
            (await command.ExecuteScalarAsync(ct)).Should().Be(0L);
        }

        (await File.ReadAllBytesAsync(mediaPath, ct)).Should().Equal(2, 4, 6);
    }

    [Fact]
    public async Task ClearAllScrapeRecords_ClearsLegacyLocalProjectionWithoutKeepingItsHierarchy()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var mediaPath = Path.Combine(sourcePath, "Legacy Show S01E01.mkv");
        await File.WriteAllBytesAsync(mediaPath, [1, 3, 5], ct);
        var sourceId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 23, 14, 0, 0, TimeSpan.Zero);
        await using var repository = Create(database, Path.Combine(temp.Path, "video_library.json"));
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Anime", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var parsed = new VideoFileNameParser().Parse(
            mediaPath, sourcePath, VideoLibraryMediaType.Anime);
        var local = LocalVideoMetadata.Empty with
        {
            Title = "Legacy Local Show",
            SeasonNumber = 3,
            EpisodeNumber = 6,
            AbsoluteEpisodeNumber = 30,
            ContainerMetadata = LocalVideoMetadataValues.Empty with { Title = "Legacy Local Show" },
            SeasonMetadata = LocalVideoMetadataValues.Empty with { Title = "Legacy Season", SeasonNumber = 3 },
            EpisodeMetadata = LocalVideoMetadataValues.Empty with
            {
                Title = "Legacy Episode", SeasonNumber = 3, EpisodeNumber = 6,
                AbsoluteEpisodeNumber = 30,
            },
        };
        var generation = await repository.BeginSourceScanAsync(
            sourceId, VideoCatalogJobKind.FullScan, ct);
        (await repository.ApplyScanBatchAsync(new VideoScanBatch(
            sourceId,
            generation,
            now,
            [new VideoScanAsset(new VideoCatalogAssetUpsert(
                mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, parsed.NormalizedTitle,
                sourcePath, 3, now, now, now, VideoMediaAvailability.Available, sourceId,
                parsed.EpisodeStart, parsed.EpisodeEnd), parsed, local)],
            true), ct)).Should().BeTrue();

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM metadata_field_values
                WHERE provider_id='local'
                  AND field IN ('localScope','seasonNumber','episodeNumber',
                                'absoluteEpisodeNumber','isSpecial');
                """;
            await command.ExecuteNonQueryAsync(ct);
        }

        await repository.ClearAllScrapeRecordsAsync(ct);

        var cleared = await repository.GetSnapshotAsync(ct);
        var clearedAsset = cleared.Assets.Should().ContainSingle().Subject;
        var unmatched = AssertResetToDistinctRootUnmatched(cleared, clearedAsset.Id);
        unmatched.PrimaryTitle.Should().Be(clearedAsset.Title)
            .And.NotBe("Legacy Local Show")
            .And.NotBe("Legacy Episode");
        unmatched.SeasonNumber.Should().BeNull();
        unmatched.EpisodeNumber.Should().BeNull();
        unmatched.AbsoluteEpisodeNumber.Should().BeNull();

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM metadata_field_values WHERE provider_id='local';";
            (await command.ExecuteScalarAsync(ct)).Should().Be(0L);
        }

        (await File.ReadAllBytesAsync(mediaPath, ct)).Should().Equal(1, 3, 5);
    }

    private static VideoAniDbIdentityProjection CreateAniDbProjection(
        int animeId,
        int episodeId,
        int episodeNumber,
        DateTimeOffset now,
        string? groupId = null)
    {
        var animeIdText = animeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var details = new VideoMetadataDetails(
            "anidb", animeIdText, VideoMetadataMediaKind.Anime,
            "Shared Show", "Shared Show", null, "AniDB overview", 2026,
            null, null, null, ["Shared Show"], [], [],
            System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
                .Add("anidb", animeIdText),
            $"https://anidb.net/anime/{animeIdText}", now, now.AddDays(30));
        return new VideoAniDbIdentityProjection(
            animeId,
            animeId * 100 + episodeNumber,
            groupId ?? $"group-{animeIdText}",
            details,
            [new VideoAniDbEpisodeProjection(
                episodeId, 1, episodeNumber, $"Episode {episodeNumber}",
                $"Episode {episodeNumber}", null, 0, 100, false,
                new DateOnly(2026, 1, episodeNumber))
            {
                AnimeId = animeId,
            }]);
    }

    private static VideoCatalogNodeSnapshot AssertResetToDistinctRootUnmatched(
        VideoCatalogSnapshot snapshot,
        Guid assetId)
    {
        snapshot.Nodes.Should().HaveCount(snapshot.Assets.Length);
        snapshot.Nodes.Should().OnlyContain(node =>
            node.Kind == VideoCatalogNodeKind.Unmatched && node.ParentId == null);
        snapshot.Assets.Should().OnlyContain(asset => asset.NodeIds.Length == 1);
        snapshot.Assets.SelectMany(asset => asset.NodeIds).Distinct()
            .Should().HaveCount(snapshot.Assets.Length);

        var asset = snapshot.Assets.Single(item => item.Id == assetId);
        return snapshot.Nodes.Single(node => node.Id == asset.NodeIds.Single());
    }

    private static SQLiteVideoCatalogRepository Create(string database, string legacy) =>
        new(database, legacy, logger: NullLogger<SQLiteVideoCatalogRepository>.Instance);

    private static async Task AssertHealthyAsync(string path, CancellationToken ct)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        (await command.ExecuteScalarAsync(ct)).Should().Be("ok");
        command.CommandText = "PRAGMA foreign_key_check;";
        (await command.ExecuteScalarAsync(ct)).Should().BeNull();
        command.CommandText = "SELECT COUNT(*) FROM migration_ledger;";
        (await command.ExecuteScalarAsync(ct)).Should().Be(1L);
    }
}
