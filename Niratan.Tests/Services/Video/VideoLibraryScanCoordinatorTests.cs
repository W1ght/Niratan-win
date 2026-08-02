using System.Collections.Concurrent;
using System.Security.Cryptography;
using FluentAssertions;
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

    private sealed class DelayedLocalMetadataProvider : ILocalVideoMetadataProvider
    {
        private int _active;
        private int _maxConcurrency;

        public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

        public async Task<LocalVideoMetadata> ReadAsync(
            string mediaPath,
            string sourceRoot,
            CancellationToken ct = default)
        {
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
