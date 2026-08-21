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
            "SELECT COUNT(*) FROM migration_audit WHERE category IN ('anilist-null-id-search-v9','jellyfin-folder-hierarchy-v11');";
        (await command.ExecuteScalarAsync(ct)).Should().Be(2L);
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
