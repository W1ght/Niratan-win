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
        await repository.SetSourceScanPausedAsync(sourceId, true, ct);
        var running = await repository.GetSnapshotAsync(ct);

        running.Jobs.Single(job => job.Id == metadataJobId).State.Should().Be(VideoCatalogJobState.Running);
        running.Jobs.Single(job => job.Id == metadataJobId).ProcessedCount.Should().Be(5);
        running.Jobs.Single(job => job.Kind == VideoCatalogJobKind.IncrementalScan).State
            .Should().Be(VideoCatalogJobState.Paused);

        await repository.UpdateMetadataRefreshAsync(
            metadataJobId, VideoCatalogJobState.Completed, 12, null, ct);
        (await repository.GetSnapshotAsync(ct)).Jobs.Single(job => job.Id == metadataJobId).State
            .Should().Be(VideoCatalogJobState.Completed);
    }

    [Fact]
    public async Task ExistingStructuredSeries_InvalidatesStaleNegativeMetadataJobOnce()
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
                .Should().Be(VideoCatalogJobState.Cancelled);
        }
        await using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM migration_audit WHERE category='series-rich-details-routing-v5';";
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

        await repository.ApplyMetadataMatchAsync(assetId, candidate, details, false, ct);
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

        await repository.ApplyMetadataMatchAsync(assetId, candidate, null, false, ct);
        var after = await repository.GetSnapshotAsync(ct);

        after.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Series);
        after.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Episode);
        after.Nodes.Single(node => node.Kind == VideoCatalogNodeKind.Series).ExternalIds
            .Should().Contain("anilist", "20987");
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

        await repository.ApplyMetadataMatchAsync(assetId, candidate, details, false, ct);
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
