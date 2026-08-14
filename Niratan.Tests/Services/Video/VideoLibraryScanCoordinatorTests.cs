using System.Collections.Concurrent;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Niratan.Models.Video;
using Niratan.Services.Storage;
using Niratan.Services.Video;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Video;

public sealed class VideoLibraryScanCoordinatorTests
{
    [Fact]
    public async Task Scan_ReportsStagesAndAnalyzesSidecarsWithBoundedConcurrency()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var hashes = new Dictionary<string, byte[]>();
        for (var index = 1; index <= 12; index++)
        {
            var path = Path.Combine(sourcePath, $"作品 S01E{index:00}.mkv");
            await File.WriteAllBytesAsync(path, [1, 2, 3, (byte)index], ct);
            hashes[path] = SHA256.HashData(await File.ReadAllBytesAsync(path, ct));
        }

        var sourceId = Guid.NewGuid();
        await using var repository = new SQLiteVideoCatalogRepository(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"),
            logger: NullLogger<SQLiteVideoCatalogRepository>.Instance);
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"),
            Name = "Anime",
            FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var local = new DelayedLocalMetadataProvider();
        var coordinator = new VideoLibraryScanCoordinator(
            repository,
            new VideoFileNameParser(),
            local,
            NullLogger<VideoLibraryScanCoordinator>.Instance);
        var progress = new ConcurrentQueue<VideoLibraryScanProgress>();
        coordinator.ProgressChanged += (_, item) => progress.Enqueue(item);

        await coordinator.ScanSourceAsync(sourceId, fullScan: true, ct);

        progress.Should().Contain(item => item.Stage == VideoLibraryScanStage.Enumerating);
        progress.Should().Contain(item => item.Stage == VideoLibraryScanStage.Analyzing
                                          && item.TotalCount == 12
                                          && item.ProcessedCount == 12);
        progress.Should().Contain(item => item.Stage == VideoLibraryScanStage.Committing);
        progress.Last().State.Should().Be(VideoCatalogJobState.Completed);
        progress.Last().TotalCount.Should().Be(12);
        local.MaxConcurrency.Should().BeGreaterThan(1).And.BeLessThanOrEqualTo(4);
        (await repository.GetSnapshotAsync(ct)).Assets.Should().HaveCount(12);
        foreach (var pair in hashes)
            SHA256.HashData(await File.ReadAllBytesAsync(pair.Key, ct)).Should().Equal(pair.Value);
    }

    [Fact]
    public async Task IncrementalScan_ReparsesUnchangedLegacyUnmatchedEpisode()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var mediaPath = Path.Combine(
            sourcePath,
            "[Kamigami] Himouto! Umaru-chan - 08 [1920x1080 x264 AAC Sub(Chs,Cht,Jap)].mkv");
        await File.WriteAllBytesAsync(mediaPath, [1, 2, 3], ct);
        var info = new FileInfo(mediaPath);
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var sourceId = Guid.NewGuid();
        await using var repository = new SQLiteVideoCatalogRepository(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"),
            logger: NullLogger<SQLiteVideoCatalogRepository>.Instance);
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"),
            Name = "Anime",
            FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Auto,
        }, ct);
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, Path.GetFileNameWithoutExtension(mediaPath),
            "Anime", info.Length, modified, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            VideoMediaAvailability.Available, sourceId, EpisodeStart: 8, EpisodeEnd: 8), ct);
        (await repository.GetSnapshotAsync(ct)).Nodes.Should()
            .ContainSingle(node => node.Kind == VideoCatalogNodeKind.Unmatched);

        var coordinator = new VideoLibraryScanCoordinator(
            repository,
            new VideoFileNameParser(),
            new DelayedLocalMetadataProvider(),
            NullLogger<VideoLibraryScanCoordinator>.Instance);
        await coordinator.ScanSourceAsync(sourceId, fullScan: false, ct);

        var snapshot = await repository.GetSnapshotAsync(ct);
        snapshot.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Series
                                                      && node.PrimaryTitle == "Himouto! Umaru chan");
        snapshot.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Episode
                                                      && node.AbsoluteEpisodeNumber == 8);
        snapshot.Nodes.Should().NotContain(node => node.Kind == VideoCatalogNodeKind.Unmatched);
        snapshot.Assets.Single().Title.Should().Be("Himouto! Umaru chan");
    }

    [Fact]
    public async Task Scan_CollapsesAnimeReleaseBundleIntoSeasonAndSpecialFeatures()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var bundle = Directory.CreateDirectory(Path.Combine(sourcePath, "ReZero S3 bundle")).FullName;
        var pv = Directory.CreateDirectory(Path.Combine(bundle, "PV")).FullName;
        var menus = Directory.CreateDirectory(Path.Combine(bundle, "menu")).FullName;
        var shorts = Directory.CreateDirectory(Path.Combine(bundle, "迷你动画")).FullName;
        var paths = new[]
        {
            Path.Combine(bundle, "[DBD-Raws][Re Zero kara Hajimeru Isekai Seikatsu S3][01][1080P][FLACx2].mkv"),
            Path.Combine(bundle, "[DBD-Raws][Re Zero kara Hajimeru Isekai Seikatsu S3][02][1080P][FLAC].mkv"),
            Path.Combine(pv, "[DBD-Raws][Re Zero kara Hajimeru Isekai Seikatsu S3][PV][01][1080P][FLAC].mkv"),
            Path.Combine(menus, "[DBD-Raws][Re Zero kara Hajimeru Isekai Seikatsu S3][menu][01][1080P][FLAC].mkv"),
            Path.Combine(shorts, "[DBD-Raws][Re Zero Kara Hajimeru Break Time Emilia Party Struggles][01][1080P][FLAC].mkv"),
        };
        foreach (var path in paths)
            await File.WriteAllBytesAsync(path, [1, 2, 3], ct);

        var sourceId = Guid.NewGuid();
        await using var repository = new SQLiteVideoCatalogRepository(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"),
            logger: NullLogger<SQLiteVideoCatalogRepository>.Instance);
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"),
            Name = "Anime",
            FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var coordinator = new VideoLibraryScanCoordinator(
            repository,
            new VideoFileNameParser(),
            new DelayedLocalMetadataProvider(),
            NullLogger<VideoLibraryScanCoordinator>.Instance);

        await coordinator.ScanSourceAsync(sourceId, fullScan: true, ct);

        var snapshot = await repository.GetSnapshotAsync(ct);
        var series = snapshot.Nodes.Should().ContainSingle(node =>
            node.Kind == VideoCatalogNodeKind.Series
            && node.PrimaryTitle == "Re Zero kara Hajimeru Isekai Seikatsu").Subject;
        snapshot.Nodes.Should().ContainSingle(node =>
            node.ParentId == series.Id && node.Kind == VideoCatalogNodeKind.Season && node.SeasonNumber == 3);
        snapshot.Nodes.Should().ContainSingle(node =>
            node.ParentId == series.Id && node.Kind == VideoCatalogNodeKind.Season
            && node.SeasonNumber == 0 && node.IsSpecial);
        snapshot.Nodes.Count(node => node.Kind == VideoCatalogNodeKind.Episode
                                     && node.SeasonNumber == 3).Should().Be(2);
        snapshot.Nodes.Count(node => node.Kind == VideoCatalogNodeKind.Episode
                                     && node.SeasonNumber == 0 && node.IsSpecial).Should().Be(3);
        snapshot.Nodes.Should().Contain(node => node.PrimaryTitle == "PV 01" && node.IsSpecial);
        snapshot.Nodes.Should().Contain(node => node.PrimaryTitle == "Disc Menu 01" && node.IsSpecial);
        snapshot.Nodes.Should().Contain(node =>
            node.PrimaryTitle.Contains("Break Time Emilia Party Struggles") && node.IsSpecial);
    }

    [Fact]
    public async Task Scan_ReZeroStyleSixtyEightFileBundle_ProducesOneSeriesAndStableSpecialFeatures()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var bundle = Directory.CreateDirectory(Path.Combine(sourcePath, "ReZero S3 release bundle")).FullName;
        var pv = Directory.CreateDirectory(Path.Combine(bundle, "PV")).FullName;
        var menus = Directory.CreateDirectory(Path.Combine(bundle, "menu")).FullName;
        var extras = Directory.CreateDirectory(Path.Combine(bundle, "映像特典")).FullName;
        var shorts = Directory.CreateDirectory(Path.Combine(bundle, "迷你动画")).FullName;
        var paths = new List<string>();
        paths.AddRange(Enumerable.Range(1, 16).Select(number => Path.Combine(
            bundle,
            $"[DBD-Raws][Re Zero kara Hajimeru Isekai Seikatsu S3][{number:00}][1080P][FLACx2].mkv")));
        paths.AddRange(Enumerable.Range(1, 29).Select(number => Path.Combine(
            pv,
            $"[DBD-Raws][Re Zero kara Hajimeru Isekai Seikatsu S3][PV][{number:00}][1080P][FLAC].mkv")));
        paths.AddRange(Enumerable.Range(1, 5).Select(number => Path.Combine(
            menus,
            $"[DBD-Raws][Re Zero kara Hajimeru Isekai Seikatsu S3][menu][{number:00}][1080P].mkv")));
        paths.Add(Path.Combine(extras,
            "[DBD-Raws][Re Zero kara Hajimeru Isekai Seikatsu S3][NCOP][1080P].mkv"));
        paths.Add(Path.Combine(extras,
            "[DBD-Raws][Re Zero kara Hajimeru Isekai Seikatsu S3][NCED][1080P].mkv"));
        paths.AddRange(Enumerable.Range(1, 16).Select(number => Path.Combine(
            shorts,
            $"[DBD-Raws][Re Zero Kara Hajimeru Break Time Story {number:00}][{number:00}][1080P].mkv")));
        paths.Should().HaveCount(68);
        foreach (var path in paths)
            await File.WriteAllBytesAsync(path, [1], ct);

        var sourceId = Guid.NewGuid();
        await using var repository = new SQLiteVideoCatalogRepository(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"),
            logger: NullLogger<SQLiteVideoCatalogRepository>.Instance);
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Anime", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var coordinator = new VideoLibraryScanCoordinator(
            repository,
            new VideoFileNameParser(),
            new DelayedLocalMetadataProvider(),
            NullLogger<VideoLibraryScanCoordinator>.Instance);

        await coordinator.ScanSourceAsync(sourceId, fullScan: true, ct);

        var snapshot = await repository.GetSnapshotAsync(ct);
        var series = snapshot.Nodes.Should().ContainSingle(node =>
            node.Kind == VideoCatalogNodeKind.Series).Subject;
        series.PrimaryTitle.Should().Be("Re Zero kara Hajimeru Isekai Seikatsu");
        snapshot.Assets.Should().HaveCount(68)
            .And.OnlyContain(asset => asset.NodeIds.Length == 1);
        snapshot.Assets.SelectMany(asset => asset.NodeIds).Distinct().Should().HaveCount(68);
        snapshot.Nodes.Count(node => node.Kind == VideoCatalogNodeKind.Episode
                                     && node.SeasonNumber == 3 && !node.IsSpecial).Should().Be(16);
        snapshot.Nodes.Count(node => node.Kind == VideoCatalogNodeKind.Episode
                                     && node.SeasonNumber == 0 && node.IsSpecial).Should().Be(52);
        snapshot.Nodes.Should().NotContain(node => node.Kind == VideoCatalogNodeKind.Unmatched);
    }

    [Fact]
    public async Task Scan_UsesJellyfinShowSeasonAndSpecialFoldersWithoutEpisodeCollisions()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Library")).FullName;
        var show = Directory.CreateDirectory(Path.Combine(sourcePath, "作品")).FullName;
        var season = Directory.CreateDirectory(Path.Combine(show, "Season 03")).FullName;
        var specials = Directory.CreateDirectory(Path.Combine(show, "Specials")).FullName;
        var trailers = Directory.CreateDirectory(Path.Combine(show, "Trailers")).FullName;
        var paths = new[]
        {
            Path.Combine(season, "S03E01 - Departure.mkv"),
            Path.Combine(season, "S03E02 - Reunion.mkv"),
            Path.Combine(specials, "S00E01 - OVA.mkv"),
            Path.Combine(trailers, "作品 PV 01.mkv"),
        };
        foreach (var path in paths)
            await File.WriteAllBytesAsync(path, [1, 2, 3], ct);

        var sourceId = Guid.NewGuid();
        await using var repository = new SQLiteVideoCatalogRepository(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"),
            logger: NullLogger<SQLiteVideoCatalogRepository>.Instance);
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"),
            Name = "Library",
            FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Auto,
        }, ct);
        var coordinator = new VideoLibraryScanCoordinator(
            repository,
            new VideoFileNameParser(),
            new DelayedLocalMetadataProvider(),
            NullLogger<VideoLibraryScanCoordinator>.Instance);

        await coordinator.ScanSourceAsync(sourceId, fullScan: true, ct);

        var snapshot = await repository.GetSnapshotAsync(ct);
        var seriesNode = snapshot.Nodes.Should().ContainSingle(node =>
            node.Kind == VideoCatalogNodeKind.Series && node.PrimaryTitle == "作品").Subject;
        snapshot.Nodes.Should().ContainSingle(node =>
            node.ParentId == seriesNode.Id && node.Kind == VideoCatalogNodeKind.Season
                                           && node.SeasonNumber == 3);
        snapshot.Nodes.Should().ContainSingle(node =>
            node.ParentId == seriesNode.Id && node.Kind == VideoCatalogNodeKind.Season
                                           && node.SeasonNumber == 0 && node.IsSpecial);
        var regularEpisodes = snapshot.Nodes
            .Where(node => node.Kind == VideoCatalogNodeKind.Episode && node.SeasonNumber == 3)
            .OrderBy(node => node.EpisodeNumber)
            .ToList();
        regularEpisodes.Select(node => node.PrimaryTitle).Should().Equal("Departure", "Reunion");
        var specialEpisodes = snapshot.Nodes
            .Where(node => node.Kind == VideoCatalogNodeKind.Episode && node.SeasonNumber == 0)
            .OrderBy(node => node.EpisodeNumber)
            .ToList();
        specialEpisodes.Should().HaveCount(2).And.OnlyContain(node => node.IsSpecial);
        specialEpisodes.Select(node => node.EpisodeNumber).Should().OnlyHaveUniqueItems();
        specialEpisodes.Should().Contain(node => node.EpisodeNumber == 1 && node.PrimaryTitle == "OVA");
        regularEpisodes.Should().Contain(node => node.EpisodeNumber == 1);
    }

    [Fact]
    public async Task IncrementalScan_KeepsUnchangedSupplementalBindingWithoutRereadingMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Library")).FullName;
        var show = Directory.CreateDirectory(Path.Combine(sourcePath, "Show")).FullName;
        var previews = Directory.CreateDirectory(Path.Combine(show, "PV")).FullName;
        var mainPath = Path.Combine(show, "Show S01E01.mkv");
        var previewPath = Path.Combine(previews, "Show PV 01.mkv");
        await File.WriteAllBytesAsync(mainPath, [1, 2, 3], ct);
        await File.WriteAllBytesAsync(previewPath, [1, 2, 3], ct);

        var sourceId = Guid.NewGuid();
        await using var repository = new SQLiteVideoCatalogRepository(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"),
            logger: NullLogger<SQLiteVideoCatalogRepository>.Instance);
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"),
            Name = "Library",
            FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Auto,
        }, ct);
        var local = new DelayedLocalMetadataProvider();
        var coordinator = new VideoLibraryScanCoordinator(
            repository,
            new VideoFileNameParser(),
            local,
            NullLogger<VideoLibraryScanCoordinator>.Instance);

        await coordinator.ScanSourceAsync(sourceId, fullScan: true, ct);
        var first = await repository.GetSnapshotAsync(ct);
        var previewAsset = first.Assets.Single(asset =>
            string.Equals(asset.Location, previewPath, StringComparison.OrdinalIgnoreCase));
        var initialPreviewNode = first.Nodes.Single(node =>
            previewAsset.NodeIds.Contains(node.Id) && node.Kind == VideoCatalogNodeKind.Episode);
        initialPreviewNode.EpisodeNumber.Should().BeNull();
        local.ReadCount.Should().Be(2);

        var menus = Directory.CreateDirectory(Path.Combine(show, "menu")).FullName;
        var menuPath = Path.Combine(menus, "Show menu 01.mkv");
        await File.WriteAllBytesAsync(menuPath, [1, 2, 3], ct);
        await coordinator.ScanSourceAsync(sourceId, fullScan: false, ct);

        var second = await repository.GetSnapshotAsync(ct);
        previewAsset = second.Assets.Single(asset =>
            string.Equals(asset.Location, previewPath, StringComparison.OrdinalIgnoreCase));
        var menuAsset = second.Assets.Single(asset =>
            string.Equals(asset.Location, menuPath, StringComparison.OrdinalIgnoreCase));
        var previewNode = second.Nodes.Single(node =>
            previewAsset.NodeIds.Contains(node.Id) && node.Kind == VideoCatalogNodeKind.Episode);
        var menuNode = second.Nodes.Single(node =>
            menuAsset.NodeIds.Contains(node.Id) && node.Kind == VideoCatalogNodeKind.Episode);
        previewNode.Id.Should().Be(initialPreviewNode.Id);
        previewNode.Id.Should().NotBe(menuNode.Id);
        previewNode.EpisodeNumber.Should().BeNull();
        menuNode.EpisodeNumber.Should().BeNull();
        previewNode.Should().Match<VideoCatalogNodeSnapshot>(node => node.IsSpecial && node.SeasonNumber == 0);
        menuNode.Should().Match<VideoCatalogNodeSnapshot>(node => node.IsSpecial && node.SeasonNumber == 0);
        local.ReadCount.Should().Be(3);
    }

    [Fact]
    public async Task FullAndIncrementalScan_KeepMultiEpisodeFileOnOneLogicalEpisodeNode()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Library")).FullName;
        var season = Directory.CreateDirectory(Path.Combine(sourcePath, "Show", "Season 01")).FullName;
        var mediaPath = Path.Combine(season, "Show S01E01-E02.mkv");
        await File.WriteAllBytesAsync(mediaPath, [1, 2, 3], ct);

        var sourceId = Guid.NewGuid();
        await using var repository = new SQLiteVideoCatalogRepository(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"),
            logger: NullLogger<SQLiteVideoCatalogRepository>.Instance);
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"),
            Name = "Library",
            FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Auto,
        }, ct);
        var local = new DelayedLocalMetadataProvider();
        var coordinator = new VideoLibraryScanCoordinator(
            repository,
            new VideoFileNameParser(),
            local,
            NullLogger<VideoLibraryScanCoordinator>.Instance);

        await coordinator.ScanSourceAsync(sourceId, fullScan: true, ct);
        var first = await repository.GetSnapshotAsync(ct);
        var firstAsset = first.Assets.Single(asset =>
            string.Equals(asset.Location, mediaPath, StringComparison.OrdinalIgnoreCase));
        var firstEpisode = first.Nodes.Should().ContainSingle(node =>
            firstAsset.NodeIds.Contains(node.Id) && node.Kind == VideoCatalogNodeKind.Episode).Subject;
        firstEpisode.EpisodeNumber.Should().Be(1);
        firstAsset.EpisodeStart.Should().Be(1);
        firstAsset.EpisodeEnd.Should().Be(2);

        await coordinator.ScanSourceAsync(sourceId, fullScan: false, ct);

        var second = await repository.GetSnapshotAsync(ct);
        var secondAsset = second.Assets.Single(asset =>
            string.Equals(asset.Location, mediaPath, StringComparison.OrdinalIgnoreCase));
        var secondEpisode = second.Nodes.Should().ContainSingle(node =>
            secondAsset.NodeIds.Contains(node.Id) && node.Kind == VideoCatalogNodeKind.Episode).Subject;
        secondEpisode.Id.Should().Be(firstEpisode.Id);
        secondEpisode.EpisodeNumber.Should().Be(1);
        secondAsset.EpisodeEnd.Should().Be(2);
        local.ReadCount.Should().Be(1);
    }

    [Fact]
    public async Task IncrementalScan_PreservesUnchangedLocalNfoNumberingWithoutRereadingSidecar()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Library")).FullName;
        var mediaPath = Path.Combine(sourcePath, "Show S01E01.mkv");
        await File.WriteAllBytesAsync(mediaPath, [1, 2, 3], ct);
        var sourceId = Guid.NewGuid();
        await using var repository = new SQLiteVideoCatalogRepository(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"),
            logger: NullLogger<SQLiteVideoCatalogRepository>.Instance);
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Library", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Auto,
        }, ct);
        var local = new FixedLocalMetadataProvider(LocalVideoMetadata.Empty with
        {
            Title = "NFO Episode Two",
            SeasonNumber = 1,
            EpisodeNumber = 2,
        });
        var coordinator = new VideoLibraryScanCoordinator(
            repository,
            new VideoFileNameParser(),
            local,
            NullLogger<VideoLibraryScanCoordinator>.Instance);

        await coordinator.ScanSourceAsync(sourceId, fullScan: true, ct);
        var first = await repository.GetSnapshotAsync(ct);
        var firstEpisode = first.Nodes.Should().ContainSingle(node =>
            node.Kind == VideoCatalogNodeKind.Episode).Subject;
        firstEpisode.EpisodeNumber.Should().Be(2);
        firstEpisode.PrimaryTitle.Should().Be("NFO Episode Two");

        await coordinator.ScanSourceAsync(sourceId, fullScan: false, ct);

        var secondEpisode = (await repository.GetSnapshotAsync(ct)).Nodes.Should().ContainSingle(node =>
            node.Kind == VideoCatalogNodeKind.Episode).Subject;
        secondEpisode.Id.Should().Be(firstEpisode.Id);
        secondEpisode.EpisodeNumber.Should().Be(2);
        secondEpisode.PrimaryTitle.Should().Be("NFO Episode Two");
        local.ReadCount.Should().Be(1);
    }

    [Fact]
    public async Task CompatibilityReparse_UsesNfoOnlyEpisodeEvidenceFromLegacyDirectSeriesBinding()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Library")).FullName;
        var mediaPath = Path.Combine(sourcePath, "Show.mkv");
        await File.WriteAllBytesAsync(mediaPath, [1, 2, 3], ct);
        var info = new FileInfo(mediaPath);
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var sourceId = Guid.NewGuid();
        await using var repository = new SQLiteVideoCatalogRepository(
            database,
            Path.Combine(temp.Path, "video_library.json"),
            logger: NullLogger<SQLiteVideoCatalogRepository>.Instance);
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Library", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Auto,
        }, ct);
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "Show", "Library", info.Length,
            modified, modified, modified, VideoMediaAvailability.Available, sourceId), ct);
        var initial = await repository.GetSnapshotAsync(ct);
        var assetId = initial.Assets.Single().Id;
        var unmatchedId = initial.Nodes.Single().Id;
        var legacySeriesId = Guid.NewGuid();
        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM node_assets WHERE asset_id=$asset;
                DELETE FROM catalog_nodes WHERE id=$unmatched;
                INSERT INTO catalog_nodes(id,parent_id,kind,primary_title,is_special,identity_locked,created_at,updated_at)
                VALUES($series,NULL,'series','Show',0,0,$now,$now);
                INSERT INTO catalog_aliases(node_id,provider_id,alias,normalized_alias)
                VALUES($series,'filename','Show','show');
                INSERT INTO node_assets(node_id,asset_id,is_preferred,ordinal)
                VALUES($series,$asset,1,0);
                UPDATE media_assets SET modified_at=NULL WHERE id=$asset;
                """;
            command.Parameters.AddWithValue("$asset", assetId.ToString("D"));
            command.Parameters.AddWithValue("$unmatched", unmatchedId.ToString("D"));
            command.Parameters.AddWithValue("$series", legacySeriesId.ToString("D"));
            command.Parameters.AddWithValue("$now", modified.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }
        var local = new FixedLocalMetadataProvider(LocalVideoMetadata.Empty with
        {
            Title = "NFO Episode Two",
            SeasonNumber = 1,
            AbsoluteEpisodeNumber = 2,
        });
        var coordinator = new VideoLibraryScanCoordinator(
            repository,
            new VideoFileNameParser(),
            local,
            NullLogger<VideoLibraryScanCoordinator>.Instance);

        await coordinator.ScanSourceAsync(sourceId, fullScan: false, ct);

        var repaired = await repository.GetSnapshotAsync(ct);
        repaired.Nodes.Should().ContainSingle(node => node.Kind == VideoCatalogNodeKind.Series);
        var season = repaired.Nodes.Should().ContainSingle(node =>
            node.Kind == VideoCatalogNodeKind.Season && node.SeasonNumber == 1).Subject;
        var episode = repaired.Nodes.Should().ContainSingle(node =>
            node.Kind == VideoCatalogNodeKind.Episode && node.ParentId == season.Id).Subject;
        episode.EpisodeNumber.Should().Be(2);
        episode.AbsoluteEpisodeNumber.Should().Be(2);
        episode.PrimaryTitle.Should().Be("NFO Episode Two");
        repaired.Assets.Single().NodeIds.Should().Equal(episode.Id);
    }

    [Fact]
    public async Task CompatibilityReparse_DemotesSafeLegacyHierarchyInExplicitMovieSource()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Movies")).FullName;
        var mediaPath = Path.Combine(sourcePath, "OVA The Movie (2020).mkv");
        await File.WriteAllBytesAsync(mediaPath, [1, 2, 3], ct);
        var info = new FileInfo(mediaPath);
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        var sourceId = Guid.NewGuid();
        await using var repository = new SQLiteVideoCatalogRepository(
            database,
            Path.Combine(temp.Path, "video_library.json"),
            logger: NullLogger<SQLiteVideoCatalogRepository>.Instance);
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Movies", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Movie,
        }, ct);
        await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
            mediaPath, VideoMediaAssetKind.LocalFile, mediaPath, "OVA The Movie", "Movies", info.Length,
            modified, modified, modified, VideoMediaAvailability.Available, sourceId), ct);
        var initial = await repository.GetSnapshotAsync(ct);
        var assetId = initial.Assets.Single().Id;
        var unmatchedId = initial.Nodes.Single().Id;
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
                DELETE FROM catalog_nodes WHERE id=$unmatched;
                INSERT INTO catalog_nodes(id,parent_id,kind,primary_title,season_number,episode_number,is_special,identity_locked,created_at,updated_at)
                VALUES($series,NULL,'series','OVA The Movie',NULL,NULL,0,0,$now,$now),
                      ($season,$series,'season','Specials',0,NULL,1,0,$now,$now),
                      ($episode,$season,'episode','OVA The Movie',0,1,1,0,$now,$now);
                INSERT INTO node_assets(node_id,asset_id,is_preferred,ordinal)
                VALUES($episode,$asset,1,0);
                UPDATE media_assets SET modified_at=NULL WHERE id=$asset;
                """;
            command.Parameters.AddWithValue("$asset", assetId.ToString("D"));
            command.Parameters.AddWithValue("$unmatched", unmatchedId.ToString("D"));
            command.Parameters.AddWithValue("$series", seriesId.ToString("D"));
            command.Parameters.AddWithValue("$season", seasonId.ToString("D"));
            command.Parameters.AddWithValue("$episode", episodeId.ToString("D"));
            command.Parameters.AddWithValue("$now", modified.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }
        var staleMetadataJob = await repository.BeginMetadataRefreshAsync(sourceId, 1, ct);
        await repository.UpdateMetadataRefreshAsync(
            staleMetadataJob, VideoCatalogJobState.Completed, 1, null, ct);
        var local = new FixedLocalMetadataProvider(LocalVideoMetadata.Empty);
        var coordinator = new VideoLibraryScanCoordinator(
            repository,
            new VideoFileNameParser(),
            local,
            NullLogger<VideoLibraryScanCoordinator>.Instance);

        await coordinator.ScanSourceAsync(sourceId, fullScan: false, ct);

        var repaired = await repository.GetSnapshotAsync(ct);
        var standalone = repaired.Nodes.Should().ContainSingle().Subject;
        standalone.Id.Should().Be(episodeId);
        standalone.Kind.Should().Be(VideoCatalogNodeKind.Unmatched);
        standalone.ParentId.Should().BeNull();
        standalone.IsSpecial.Should().BeFalse();
        repaired.Assets.Single().NodeIds.Should().Equal(episodeId);
        repaired.Assets.Single().ModifiedAt.Should().Be(modified);
        repaired.Jobs.Single(job => job.Id == staleMetadataJob).State
            .Should().Be(VideoCatalogJobState.Cancelled);
        local.ReadCount.Should().Be(1);
    }

    private sealed class FixedLocalMetadataProvider(LocalVideoMetadata metadata)
        : ILocalVideoMetadataProvider
    {
        private int _readCount;
        public int ReadCount => Volatile.Read(ref _readCount);

        public Task<LocalVideoMetadata> ReadAsync(
            string mediaPath,
            string sourceRoot,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _readCount);
            return Task.FromResult(metadata);
        }
    }

    private sealed class DelayedLocalMetadataProvider : ILocalVideoMetadataProvider
    {
        private int _active;
        private int _maxConcurrency;
        private int _readCount;

        public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);
        public int ReadCount => Volatile.Read(ref _readCount);

        public async Task<LocalVideoMetadata> ReadAsync(
            string mediaPath,
            string sourceRoot,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _readCount);
            var active = Interlocked.Increment(ref _active);
            var observed = Volatile.Read(ref _maxConcurrency);
            while (active > observed)
            {
                var original = Interlocked.CompareExchange(ref _maxConcurrency, active, observed);
                if (original == observed)
                    break;
                observed = original;
            }
            try
            {
                await Task.Delay(25, ct);
                return LocalVideoMetadata.Empty;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
