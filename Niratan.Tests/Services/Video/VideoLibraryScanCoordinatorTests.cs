using System.Collections.Concurrent;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Niratan.Models.Video;
using Niratan.Services.Storage;
using Niratan.Services.Video;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Video;

public sealed class VideoLibraryScanCoordinatorTests
{
    // CI runners do this suite's SQLite work an order of magnitude slower than a dev box:
    // single repository tests there take 12-15s, which made the old 2s/5s budgets time out
    // even though the coordinator was still making progress. These waits exist to fail a
    // deadlock, not to police throughput.
    private static readonly TimeSpan SignalWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CompletionWait = TimeSpan.FromSeconds(60);

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

    [Fact]
    public async Task FullScan_SynchronizesScopedLocalSidecarsWithoutDuplicatingOrRetainingDeletedData()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "video_library.sqlite3");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Library")).FullName;
        var seriesDirectory = Directory.CreateDirectory(Path.Combine(sourcePath, "Show")).FullName;
        var seasonDirectory = Directory.CreateDirectory(Path.Combine(seriesDirectory, "Season 01")).FullName;
        var mediaPath = Path.Combine(seasonDirectory, "Show S01E01.mkv");
        var seriesNfo = Path.Combine(seriesDirectory, "tvshow.nfo");
        var seasonNfo = Path.Combine(seasonDirectory, "season.nfo");
        var episodeNfo = Path.ChangeExtension(mediaPath, ".nfo");
        var seriesPoster = Path.Combine(seriesDirectory, "poster.jpg");
        var seasonPoster = Path.Combine(seasonDirectory, "poster.jpg");
        var episodeThumb = Path.Combine(seasonDirectory, "Show S01E01-thumb.jpg");
        await File.WriteAllBytesAsync(mediaPath, [1, 2, 3], ct);
        await File.WriteAllTextAsync(
            seriesNfo,
            "<tvshow><title>NFO Show</title><genre>Drama</genre></tvshow>",
            ct);
        await File.WriteAllTextAsync(
            seasonNfo,
            "<season><title>NFO Season</title><season>1</season></season>",
            ct);
        await File.WriteAllTextAsync(
            episodeNfo,
            "<episodedetails><title>NFO Episode</title><season>1</season><episode>1</episode></episodedetails>",
            ct);
        foreach (var path in new[] { seriesPoster, seasonPoster, episodeThumb })
            await File.WriteAllBytesAsync(path, [1], ct);
        var sourceId = Guid.NewGuid();
        await using var repository = new SQLiteVideoCatalogRepository(
            database,
            Path.Combine(temp.Path, "video_library.json"),
            logger: NullLogger<SQLiteVideoCatalogRepository>.Instance);
        await repository.InitializeAsync(ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = sourceId.ToString("D"), Name = "Library", FolderPath = sourcePath,
            MediaType = VideoLibraryMediaType.Anime,
        }, ct);
        var coordinator = new VideoLibraryScanCoordinator(
            repository,
            new VideoFileNameParser(),
            new LocalVideoMetadataProvider(),
            NullLogger<VideoLibraryScanCoordinator>.Instance);

        await coordinator.ScanSourceAsync(sourceId, fullScan: true, ct);
        await coordinator.ScanSourceAsync(sourceId, fullScan: true, ct);

        var withSidecars = (await repository.GetSnapshotAsync(ct)).Nodes.Single(node =>
            node.Kind == VideoCatalogNodeKind.Series);
        withSidecars.PrimaryTitle.Should().Be("NFO Show");
        withSidecars.Genres.Should().Equal("Drama");
        withSidecars.PosterPath.Should().Be(seriesPoster);
        var seasonWithSidecars = (await repository.GetSnapshotAsync(ct)).Nodes.Single(node =>
            node.Kind == VideoCatalogNodeKind.Season);
        seasonWithSidecars.PrimaryTitle.Should().Be("NFO Season");
        seasonWithSidecars.PosterPath.Should().Be(seasonPoster);
        var episodeWithSidecars = (await repository.GetSnapshotAsync(ct)).Nodes.Single(node =>
            node.Kind == VideoCatalogNodeKind.Episode);
        episodeWithSidecars.PrimaryTitle.Should().Be("NFO Episode");
        episodeWithSidecars.ThumbPath.Should().Be(episodeThumb);
        (await repository.GetSnapshotAsync(ct)).Assets.Single().PosterPath.Should().BeNull();
        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM artwork WHERE provider_id='local';";
            (await command.ExecuteScalarAsync(ct)).Should().Be(3L);
        }

        File.Delete(seriesNfo);
        File.Delete(seasonNfo);
        File.Delete(episodeNfo);
        await coordinator.ScanSourceAsync(sourceId, fullScan: true, ct);

        await using (var artworkOnly = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await artworkOnly.OpenAsync(ct);
            using var command = artworkOnly.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*) FROM metadata_field_values
                WHERE provider_id='local' AND field='localScope';
                """;
            (await command.ExecuteScalarAsync(ct)).Should().Be(3L,
                "each remaining Local artwork owner keeps provenance without inventing structure");
            command.CommandText =
                """
                SELECT COUNT(*) FROM metadata_field_values
                WHERE provider_id='local' AND field<>'localScope';
                """;
            (await command.ExecuteScalarAsync(ct)).Should().Be(0L);
        }

        File.Delete(seriesPoster);
        File.Delete(seasonPoster);
        File.Delete(episodeThumb);
        await coordinator.ScanSourceAsync(sourceId, fullScan: true, ct);

        var withoutSidecars = (await repository.GetSnapshotAsync(ct)).Nodes.Single(node =>
            node.Kind == VideoCatalogNodeKind.Series);
        withoutSidecars.PrimaryTitle.Should().Be("Show");
        withoutSidecars.Genres.Should().BeEmpty();
        withoutSidecars.PosterPath.Should().BeNull();
        var seasonWithoutSidecars = (await repository.GetSnapshotAsync(ct)).Nodes.Single(node =>
            node.Kind == VideoCatalogNodeKind.Season);
        seasonWithoutSidecars.PrimaryTitle.Should().Be("Season 1");
        seasonWithoutSidecars.PosterPath.Should().BeNull();
        var episodeWithoutSidecars = (await repository.GetSnapshotAsync(ct)).Nodes.Single(node =>
            node.Kind == VideoCatalogNodeKind.Episode);
        episodeWithoutSidecars.PrimaryTitle.Should().Be("Episode 1");
        episodeWithoutSidecars.ThumbPath.Should().BeNull();
        (await repository.GetSnapshotAsync(ct)).Assets.Single().PosterPath.Should().BeNull();
        await using var verify = new SqliteConnection($"Data Source={database};Pooling=False");
        await verify.OpenAsync(ct);
        using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText =
            "SELECT COUNT(*) FROM artwork WHERE provider_id='local';";
        (await verifyCommand.ExecuteScalarAsync(ct)).Should().Be(0L);
        verifyCommand.CommandText =
            "SELECT COUNT(*) FROM metadata_field_values WHERE provider_id='local';";
        (await verifyCommand.ExecuteScalarAsync(ct)).Should().Be(0L);
    }

    [Fact]
    public async Task ScanSnapshotInvalidatedBeforeBegin_DoesNotEnterReplacementGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourceId = Guid.NewGuid();
        var snapshotGeneration = 7L;
        var currentGeneration = snapshotGeneration;
        var source = new VideoCatalogSourceSnapshot(
            sourceId,
            "Anime",
            Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName,
            Path.Combine(temp.Path, "Anime").ToUpperInvariant(),
            VideoLibraryMediaType.Anime,
            "ja-JP",
            "JP",
            [],
            snapshotGeneration,
            DateTimeOffset.UtcNow,
            null,
            null);
        var snapshot = VideoCatalogSnapshot.Empty() with { Sources = [source] };
        var beginEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBegin = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new Mock<IVideoCatalogRepository>(MockBehavior.Strict);
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        repository.Setup(item => item.TryBeginSourceScanAsync(
                sourceId,
                VideoCatalogJobKind.FullScan,
                snapshotGeneration,
                It.IsAny<CancellationToken>()))
            .Returns<Guid, VideoCatalogJobKind, long, CancellationToken>(
                async (_, _, expectedGeneration, token) =>
                {
                    beginEntered.TrySetResult(true);
                    await releaseBegin.Task.WaitAsync(token);
                    return expectedGeneration == Interlocked.Read(ref currentGeneration)
                        ? expectedGeneration + 1
                        : null;
                });
        var coordinator = new VideoLibraryScanCoordinator(
            repository.Object,
            new VideoFileNameParser(),
            new FixedLocalMetadataProvider(LocalVideoMetadata.Empty),
            NullLogger<VideoLibraryScanCoordinator>.Instance);

        var scan = coordinator.ScanSourceAsync(sourceId, fullScan: true, ct);
        await beginEntered.Task.WaitAsync(SignalWait, ct);
        Interlocked.Increment(ref currentGeneration);
        releaseBegin.TrySetResult(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scan.WaitAsync(CompletionWait, ct));
        repository.Verify(item => item.TryBeginSourceScanAsync(
            sourceId,
            VideoCatalogJobKind.FullScan,
            snapshotGeneration,
            It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.ApplyScanBatchAsync(
            It.IsAny<VideoScanBatch>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(item => item.CancelSourceScanAsync(
            sourceId, It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SupersededScanCannotRemoveReplacementActiveCancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(sourcePath, "Work S01E01.mkv"), [1], ct);
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
        var local = new SupersededScanLocalMetadataProvider();
        var coordinator = new VideoLibraryScanCoordinator(
            repository,
            new VideoFileNameParser(),
            local,
            NullLogger<VideoLibraryScanCoordinator>.Instance);

        var firstScan = coordinator.ScanSourceAsync(sourceId, fullScan: true, ct);
        await local.FirstStarted.Task.WaitAsync(SignalWait, ct);
        var secondScan = coordinator.ScanSourceAsync(sourceId, fullScan: true, ct);
        await local.SecondStarted.Task.WaitAsync(SignalWait, ct);
        local.ReleaseFirst();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => firstScan.WaitAsync(CompletionWait, ct));

        await coordinator.CancelAsync(sourceId, ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => secondScan.WaitAsync(CompletionWait, ct));
        var snapshot = await repository.GetSnapshotAsync(ct);
        snapshot.Jobs.Where(job => job.Kind == VideoCatalogJobKind.FullScan)
            .Should().OnlyContain(job => job.State == VideoCatalogJobState.Cancelled);
    }

    [Fact]
    public async Task Cancel_UsesSnapshotGenerationInsteadOfBroadCancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourceId = Guid.NewGuid();
        const long expectedGeneration = 11;
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var source = new VideoCatalogSourceSnapshot(
            sourceId,
            "Anime",
            sourcePath,
            sourcePath.ToUpperInvariant(),
            VideoLibraryMediaType.Anime,
            "ja-JP",
            "JP",
            [],
            expectedGeneration,
            DateTimeOffset.UtcNow,
            null,
            null);
        var repository = new Mock<IVideoCatalogRepository>(MockBehavior.Strict);
        repository.Setup(item => item.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(VideoCatalogSnapshot.Empty() with { Sources = [source] });
        repository.Setup(item => item.CancelSourceScanAsync(
                sourceId, expectedGeneration, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var coordinator = new VideoLibraryScanCoordinator(
            repository.Object,
            new VideoFileNameParser(),
            new FixedLocalMetadataProvider(LocalVideoMetadata.Empty),
            NullLogger<VideoLibraryScanCoordinator>.Instance);

        await coordinator.CancelAsync(sourceId, ct);

        repository.Verify(item => item.CancelSourceScanAsync(
            sourceId, expectedGeneration, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.CancelSourceScanAsync(
            sourceId, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScanStartedBeforeScrapeReset_CannotQueueAniDbInReplacementGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        await File.WriteAllBytesAsync(
            Path.Combine(sourcePath, "Work S01E01.mkv"),
            [1, 2, 3],
            ct);
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
        var scrapeGeneration = 0L;
        var queued = false;
        AniDbScrapeAdmissionStamp? observedAdmission = null;
        var aniDb = new Mock<IAniDbImportService>();
        aniDb.Setup(item => item.CaptureScrapeAdmission())
            .Returns(() => new AniDbScrapeAdmissionStamp(
                Interlocked.Read(ref scrapeGeneration),
                StartedDuringReset: false));
        aniDb.Setup(item => item.QueueSourceAsync(
                sourceId,
                It.IsAny<AniDbScrapeAdmissionStamp>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, AniDbScrapeAdmissionStamp, CancellationToken>((_, admission, _) =>
            {
                observedAdmission = admission;
                queued = !admission.StartedDuringReset
                         && admission.Generation == Interlocked.Read(ref scrapeGeneration);
            })
            .Returns(Task.CompletedTask);
        var local = new BlockingLocalMetadataProvider();
        var coordinator = new VideoLibraryScanCoordinator(
            repository,
            new VideoFileNameParser(),
            local,
            NullLogger<VideoLibraryScanCoordinator>.Instance,
            aniDb.Object);

        var scan = coordinator.ScanSourceAsync(sourceId, fullScan: true, ct);
        await local.Started.Task.WaitAsync(ct);
        Interlocked.Increment(ref scrapeGeneration);
        local.Release();
        await scan;

        observedAdmission.Should().Be(new AniDbScrapeAdmissionStamp(0, false));
        queued.Should().BeFalse();
        aniDb.Verify(item => item.QueueSourceAsync(
            sourceId,
            new AniDbScrapeAdmissionStamp(0, false),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScanAllStartedBeforeScrapeReset_ReusesOuterAdmissionForEverySource()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var firstPath = Directory.CreateDirectory(Path.Combine(temp.Path, "A-Anime")).FullName;
        var secondPath = Directory.CreateDirectory(Path.Combine(temp.Path, "B-Anime")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(firstPath, "Work A S01E01.mkv"), [1], ct);
        await File.WriteAllBytesAsync(Path.Combine(secondPath, "Work B S01E01.mkv"), [2], ct);
        var firstSourceId = Guid.NewGuid();
        var secondSourceId = Guid.NewGuid();
        await using var repository = new SQLiteVideoCatalogRepository(
            Path.Combine(temp.Path, "video_library.sqlite3"),
            Path.Combine(temp.Path, "video_library.json"),
            logger: NullLogger<SQLiteVideoCatalogRepository>.Instance);
        await repository.InitializeAsync(ct);
        var createdAt = DateTimeOffset.UtcNow;
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = firstSourceId.ToString("D"),
            Name = "A-Anime",
            FolderPath = firstPath,
            MediaType = VideoLibraryMediaType.Anime,
            CreatedAt = createdAt.UtcDateTime,
        }, ct);
        await repository.UpsertSourceAsync(new VideoLibrarySource
        {
            Id = secondSourceId.ToString("D"),
            Name = "B-Anime",
            FolderPath = secondPath,
            MediaType = VideoLibraryMediaType.Anime,
            CreatedAt = createdAt.AddSeconds(1).UtcDateTime,
        }, ct);

        var scrapeGeneration = 0L;
        var resetInProgress = 0;
        var admittedSources = new ConcurrentBag<Guid>();
        var observedAdmissions = new ConcurrentBag<AniDbScrapeAdmissionStamp>();
        var aniDb = new Mock<IAniDbImportService>();
        aniDb.Setup(item => item.CaptureScrapeAdmission())
            .Returns(() => new AniDbScrapeAdmissionStamp(
                Interlocked.Read(ref scrapeGeneration),
                Volatile.Read(ref resetInProgress) != 0));
        aniDb.Setup(item => item.QueueSourceAsync(
                It.IsAny<Guid>(),
                It.IsAny<AniDbScrapeAdmissionStamp>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, AniDbScrapeAdmissionStamp, CancellationToken>((sourceId, admission, _) =>
            {
                observedAdmissions.Add(admission);
                if (!admission.StartedDuringReset
                    && admission.Generation == Interlocked.Read(ref scrapeGeneration)
                    && Volatile.Read(ref resetInProgress) == 0)
                    admittedSources.Add(sourceId);
            })
            .Returns(Task.CompletedTask);
        var local = new BlockingLocalMetadataProvider();
        var coordinator = new VideoLibraryScanCoordinator(
            repository,
            new VideoFileNameParser(),
            local,
            NullLogger<VideoLibraryScanCoordinator>.Instance,
            aniDb.Object);

        var scan = coordinator.ScanAllAsync(fullScan: true, ct);
        await local.Started.Task.WaitAsync(SignalWait, ct);
        Interlocked.Exchange(ref resetInProgress, 1);
        Interlocked.Increment(ref scrapeGeneration);
        Interlocked.Exchange(ref resetInProgress, 0);
        local.Release();
        await scan.WaitAsync(CompletionWait, ct);

        aniDb.Verify(item => item.CaptureScrapeAdmission(), Times.Once);
        observedAdmissions.Should().HaveCount(2)
            .And.OnlyContain(item => item == new AniDbScrapeAdmissionStamp(0, false));
        admittedSources.Should().BeEmpty();
    }

    [Fact]
    public async Task ScanSourceStartedDuringScrapeReset_CannotEnterReplacementGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(sourcePath, "Work S01E01.mkv"), [1], ct);
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

        var resetInProgress = 1;
        var admitted = false;
        AniDbScrapeAdmissionStamp? observedAdmission = null;
        var aniDb = new Mock<IAniDbImportService>();
        aniDb.Setup(item => item.CaptureScrapeAdmission())
            .Returns(() => new AniDbScrapeAdmissionStamp(
                1,
                Volatile.Read(ref resetInProgress) != 0));
        aniDb.Setup(item => item.QueueSourceAsync(
                sourceId,
                It.IsAny<AniDbScrapeAdmissionStamp>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, AniDbScrapeAdmissionStamp, CancellationToken>((_, admission, _) =>
            {
                observedAdmission = admission;
                admitted = !admission.StartedDuringReset
                           && admission.Generation == 1
                           && Volatile.Read(ref resetInProgress) == 0;
            })
            .Returns(Task.CompletedTask);
        var local = new BlockingLocalMetadataProvider();
        var coordinator = new VideoLibraryScanCoordinator(
            repository,
            new VideoFileNameParser(),
            local,
            NullLogger<VideoLibraryScanCoordinator>.Instance,
            aniDb.Object);

        var scan = coordinator.ScanSourceAsync(sourceId, fullScan: true, ct);
        await local.Started.Task.WaitAsync(SignalWait, ct);
        Interlocked.Exchange(ref resetInProgress, 0);
        local.Release();
        await scan.WaitAsync(CompletionWait, ct);

        observedAdmission.Should().Be(new AniDbScrapeAdmissionStamp(1, true));
        admitted.Should().BeFalse();
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

    private sealed class BlockingLocalMetadataProvider : ILocalVideoMetadataProvider
    {
        private readonly TaskCompletionSource<bool> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LocalVideoMetadata> ReadAsync(
            string mediaPath,
            string sourceRoot,
            CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            await _release.Task.WaitAsync(ct);
            return LocalVideoMetadata.Empty;
        }

        public void Release() => _release.TrySetResult(true);
    }

    private sealed class SupersededScanLocalMetadataProvider : ILocalVideoMetadataProvider
    {
        private readonly TaskCompletionSource<bool> _firstRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        public TaskCompletionSource<bool> FirstStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SecondStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LocalVideoMetadata> ReadAsync(
            string mediaPath,
            string sourceRoot,
            CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                FirstStarted.TrySetResult(true);
                await _firstRelease.Task;
                return LocalVideoMetadata.Empty;
            }

            SecondStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return LocalVideoMetadata.Empty;
        }

        public void ReleaseFirst() => _firstRelease.TrySetResult(true);
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
