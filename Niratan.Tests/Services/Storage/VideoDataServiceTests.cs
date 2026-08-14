using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using Moq;
using Niratan.Models;
using Niratan.Models.Video;
using Niratan.Services.Storage;
using Niratan.Services.Video;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Storage;

public sealed class VideoDataServiceTests
{
    [Fact]
    public async Task CatalogProjection_PrefersUserTitleAndKeepsSeriesOwnerMetadataSeparate()
    {
        var ct = TestContext.Current.CancellationToken;
        var seriesId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var plainAssetId = Guid.NewGuid();
        var titledAssetId = Guid.NewGuid();
        var snapshot = VideoCatalogSnapshot.Empty() with
        {
            Nodes =
            [
                new VideoCatalogNodeSnapshot(
                    seriesId, null, VideoCatalogNodeKind.Series, "作品", "シリーズ原題", "シリーズ副題", "シリーズ概要",
                    2024, null, null, null, false, false, [],
                    ImmutableDictionary<string, string>.Empty),
                new VideoCatalogNodeSnapshot(
                    episodeId, seriesId, VideoCatalogNodeKind.Episode, "第4話", "単話原題", "単話副題", "単話概要",
                    2025, 1, 4, null, false, false, [],
                    ImmutableDictionary<string, string>.Empty),
            ],
            Assets =
            [
                Asset(plainAssetId, @"D:\Anime\plain.mkv", episodeId, displayTitle: null),
                Asset(titledAssetId, @"D:\Anime\titled.mkv", episodeId, displayTitle: "ユーザー題"),
            ],
        };
        var catalog = new Mock<IVideoCatalogRepository>();
        catalog.Setup(repository => repository.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        var history = new Mock<IVideoPlaybackHistoryStore>();
        history.Setup(store => store.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoPlaybackHistoryEntry(
                new VideoPlaybackState(0, 0, VideoSubtitleSelection.None()), null, false));
        var service = new VideoDataService(
            catalog.Object,
            history.Object,
            Mock.Of<IVideoFileNameParser>());

        var videos = await service.GetVideosAsync(ct: ct);

        var plainVideo = videos.Single(video => video.CatalogAssetId == plainAssetId);
        plainVideo.Title.Should().Be("第4話");
        plainVideo.OriginalTitle.Should().Be("単話原題");
        plainVideo.Overview.Should().Be("単話概要");
        plainVideo.ReleaseYear.Should().Be(2025);
        plainVideo.CatalogSeriesTitle.Should().Be("作品");
        plainVideo.CatalogSeriesOriginalTitle.Should().Be("シリーズ原題");
        plainVideo.CatalogSeriesOverview.Should().Be("シリーズ概要");
        plainVideo.CatalogSeriesReleaseYear.Should().Be(2024);
        plainVideo.EpisodeEnd.Should().Be(5);
        plainVideo.CatalogNumberingText.Should().Be("S01E04–E05");
        videos.Single(video => video.CatalogAssetId == titledAssetId).Title.Should().Be("ユーザー題");
    }

    [Fact]
    public async Task VideoDataService_PersistsCatalogAndPlaybackInSeparateNiratanFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var catalogPath = Path.Combine(temp.Path, "video_library.json");
        var historyPath = Path.Combine(temp.Path, "video_playback_history.json");
        var videoPath = Path.Combine(temp.Path, "Season 1", "Episode 01.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(videoPath)!);
        await File.WriteAllTextAsync(videoPath, "video", ct);

        var service = new VideoDataService(catalogPath, historyPath);
        await service.UpsertVideoAsync(new VideoItem
        {
            Title = "Episode 01",
            FilePath = videoPath,
            FileSizeBytes = 5,
            ModifiedAt = DateTime.UtcNow,
            ImportedAt = DateTime.UtcNow,
        }, ct);
        await service.SaveVideoPlaybackStateAsync(
            videoPath,
            new VideoPlaybackState(
                25,
                100,
                VideoSubtitleSelection.EmbeddedTrack(3, "Japanese"),
                SubtitleDelayMilliseconds: 250,
                PlaybackSpeed: 1.2,
                AudioDelaySeconds: -0.1,
                AudioSelection: new VideoAudioSelection(
                    VideoAudioSelectionKind.EmbeddedTrack,
                    TrackId: 2,
                    FfIndex: 1,
                    Title: "Main",
                    Language: "ja",
                    Codec: "aac")),
            ct);

        File.Exists(catalogPath).Should().BeFalse();
        File.Exists(historyPath).Should().BeTrue();
        File.Exists(Path.ChangeExtension(catalogPath, ".sqlite3")).Should().BeTrue();
        File.Exists(Path.Combine(temp.Path, "niratan.db")).Should().BeFalse();

        var reloaded = new VideoDataService(catalogPath, historyPath);
        var item = await reloaded.GetVideoAsync(videoPath, ct);
        item.Should().NotBeNull();
        item!.Id.Should().Be(Path.GetFullPath(videoPath));
        item.LastPositionSeconds.Should().Be(25);
        item.DurationSeconds.Should().Be(100);
        item.PlaybackSpeed.Should().Be(1.2);
        item.SubtitleDelayMilliseconds.Should().Be(250);
        item.AudioSelectionFfIndex.Should().Be(1);
        item.SubtitleSelectionTrackId.Should().Be(3);
    }

    [Fact]
    public async Task VideoDataService_WritesSQLiteCatalogAndSourceConfiguration()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var catalogPath = Path.Combine(temp.Path, "video_library.json");
        var historyPath = Path.Combine(temp.Path, "video_playback_history.json");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var source = new VideoLibrarySource
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Anime",
            FolderPath = sourcePath,
            CreatedAt = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc),
            LastScannedAt = new DateTime(2026, 7, 28, 1, 0, 0, DateTimeKind.Utc),
            MediaType = VideoLibraryMediaType.Anime,
            Language = "ja-JP",
            Region = "JP",
            ProviderOrder = ["anilist", "anidb", "bangumi", "tmdb"],
        };

        var service = new VideoDataService(catalogPath, historyPath);
        await service.UpsertVideoLibrarySourceAsync(source, ct);

        var databasePath = Path.ChangeExtension(catalogPath, ".sqlite3");
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT folder_path || '|' || media_type || '|' || language || '|' || region FROM library_sources;";
        (await command.ExecuteScalarAsync(ct)).Should().Be($"{Path.GetFullPath(sourcePath)}|anime|ja-JP|JP");
        command.CommandText = "SELECT group_concat(provider_id, ',') FROM source_provider_routes ORDER BY ordinal;";
        (await command.ExecuteScalarAsync(ct)).Should().Be("anilist,anidb,bangumi,tmdb");
        command.CommandText = "PRAGMA user_version;";
        (await command.ExecuteScalarAsync(ct)).Should().Be(1L);
    }

    [Fact]
    public async Task VideoDataService_StoresCollectionMembershipByMediaIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var catalogPath = Path.Combine(temp.Path, "video_library.json");
        var historyPath = Path.Combine(temp.Path, "video_playback_history.json");
        var videoPath = Path.Combine(temp.Path, "Movie.mkv");
        await File.WriteAllTextAsync(videoPath, "video", ct);
        var collection = new VideoCollection
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Favorites",
            Kind = VideoCollectionKind.Manual,
        };

        var service = new VideoDataService(catalogPath, historyPath);
        await service.UpsertVideoAsync(new VideoItem
        {
            FilePath = videoPath,
            Title = "Movie",
            ImportedAt = DateTime.UtcNow,
        }, ct);
        await service.UpsertVideoCollectionAsync(collection, ct);
        await service.SetVideoCollectionItemsAsync(collection.Id, [videoPath], ct);

        var reloaded = new VideoDataService(catalogPath, historyPath);
        var stored = (await reloaded.GetVideoCollectionsAsync(ct)).Single();
        stored.ItemIds.Should().Equal(Path.GetFullPath(videoPath));

        File.Exists(catalogPath).Should().BeFalse();
    }

    [Fact]
    public async Task RemovingSource_MarksCatalogItemUnavailableAndPreservesPlaybackHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var catalogPath = Path.Combine(temp.Path, "video_library.json");
        var historyPath = Path.Combine(temp.Path, "video_playback_history.json");
        var sourcePath = Directory.CreateDirectory(Path.Combine(temp.Path, "Anime")).FullName;
        var videoPath = Path.Combine(sourcePath, "Episode.mkv");
        await File.WriteAllTextAsync(videoPath, "video", ct);
        var source = new VideoLibrarySource
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Anime",
            FolderPath = sourcePath,
        };

        var service = new VideoDataService(catalogPath, historyPath);
        await service.UpsertVideoLibrarySourceAsync(source, ct);
        await service.UpsertVideoAsync(new VideoItem
        {
            FilePath = videoPath,
            Title = "Episode",
            SourceId = source.Id,
            SourceFolderPath = sourcePath,
            ImportedAt = DateTime.UtcNow,
        }, ct);
        await service.SaveVideoPlaybackStateAsync(
            videoPath,
            new VideoPlaybackState(30, 100, VideoSubtitleSelection.None()),
            ct);

        await service.DeleteVideoLibrarySourceAsync(source.Id, ct);
        var unavailable = await service.GetVideoAsync(videoPath, ct);
        unavailable.Should().NotBeNull();
        unavailable!.IsAvailable.Should().BeFalse();

        using var history = JsonDocument.Parse(await File.ReadAllTextAsync(historyPath, ct));
        history.RootElement.GetProperty("playbackStates")
            .TryGetProperty(Path.GetFullPath(videoPath), out _)
            .Should().BeTrue();

        await service.UpsertVideoLibrarySourceAsync(source, ct);
        await service.UpsertVideoAsync(new VideoItem
        {
            FilePath = videoPath,
            Title = "Episode",
            SourceId = source.Id,
            SourceFolderPath = sourcePath,
            ImportedAt = DateTime.UtcNow,
        }, ct);
        (await service.GetVideoAsync(videoPath, ct))!.LastPositionSeconds.Should().Be(30);
    }

    [Fact]
    public async Task PlaybackState_UsesNiratanStartAndFinishBoundaries()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var catalogPath = Path.Combine(temp.Path, "video_library.json");
        var historyPath = Path.Combine(temp.Path, "video_playback_history.json");
        var videoPath = Path.Combine(temp.Path, "Boundary.mkv");
        await File.WriteAllTextAsync(videoPath, "video", ct);
        var service = new VideoDataService(catalogPath, historyPath);
        await service.UpsertVideoAsync(new VideoItem
        {
            FilePath = videoPath,
            Title = "Boundary",
            ImportedAt = DateTime.UtcNow,
        }, ct);

        await service.SaveVideoPlaybackStateAsync(
            videoPath,
            new VideoPlaybackState(1.9, 100, VideoSubtitleSelection.Off()),
            ct);
        var beforeStart = await service.GetVideoAsync(videoPath, ct);
        beforeStart!.LastPositionSeconds.Should().Be(0);
        beforeStart.IsWatched.Should().BeFalse();
        beforeStart.SubtitleSelectionKind.Should().Be(VideoSubtitleSelectionKind.Off);

        await service.SaveVideoPlaybackStateAsync(
            videoPath,
            new VideoPlaybackState(95, 100, VideoSubtitleSelection.Off()),
            ct);
        var finished = await service.GetVideoAsync(videoPath, ct);
        finished!.LastPositionSeconds.Should().Be(100);
        finished.IsWatched.Should().BeTrue();

        await service.SaveVideoPlaybackStateAsync(
            videoPath,
            new VideoPlaybackState(50, 100, VideoSubtitleSelection.Off()),
            ct);
        var resumed = await service.GetVideoAsync(videoPath, ct);
        resumed!.LastPositionSeconds.Should().Be(50);
        resumed.IsWatched.Should().BeFalse();
    }

    [Fact]
    public async Task RemoteVideo_RoundTripsDurableIdentityWithoutSignedStreams()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var catalogPath = Path.Combine(temp.Path, "video_library.json");
        var historyPath = Path.Combine(temp.Path, "video_playback_history.json");
        var service = new VideoDataService(catalogPath, historyPath);
        var key = "remote://youtube/abc123";

        await service.UpsertVideoAsync(new VideoItem
        {
            Id = key,
            FilePath = key,
            Title = "Remote",
            ProviderId = "youtube",
            RemoteId = "abc123",
            OriginalUrl = "https://youtu.be/abc123",
            CanonicalUrl = "https://www.youtube.com/watch?v=abc123",
            RemoteThumbnailUrl = "https://i.ytimg.com/abc123.jpg",
            RemoteSubtitleLanguage = "ja",
            DurationSeconds = 120,
            ImportedAt = DateTime.UtcNow,
        }, ct);

        var reloaded = new VideoDataService(catalogPath, historyPath);
        var item = await reloaded.GetVideoAsync(key, ct);
        item.Should().NotBeNull();
        item!.Id.Should().Be(key);
        item.ProviderId.Should().Be("youtube");
        item.RemoteId.Should().Be("abc123");
        item.CanonicalUrl.Should().Contain("youtube.com");
        item.RemoteSubtitleLanguage.Should().Be("ja");
    }

    [Fact]
    public async Task InvalidCatalog_IsPreservedAndRejectedInsteadOfOverwritten()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var catalogPath = Path.Combine(temp.Path, "video_library.json");
        var historyPath = Path.Combine(temp.Path, "video_playback_history.json");
        const string invalid = "{ invalid";
        await File.WriteAllTextAsync(catalogPath, invalid, ct);
        var service = new VideoDataService(catalogPath, historyPath);

        var databasePath = Path.ChangeExtension(catalogPath, ".sqlite3");
        await using var repository = new SQLiteVideoCatalogRepository(
            databasePath,
            catalogPath,
            logger: NullLogger<SQLiteVideoCatalogRepository>.Instance);
        var initialized = await repository.InitializeAsync(ct);
        initialized.Mode.Should().Be(VideoCatalogMode.LegacyReadOnly);
        initialized.MigrationError.Should().NotBeNullOrWhiteSpace();
        File.Exists(databasePath).Should().BeFalse();
        (await File.ReadAllTextAsync(catalogPath, ct)).Should().Be(invalid);
    }

    private static VideoCatalogAssetSnapshot Asset(
        Guid id,
        string identity,
        Guid nodeId,
        string? displayTitle) => new(
        id,
        identity,
        VideoMediaAssetKind.LocalFile,
        identity,
        "作品",
        "Anime",
        1,
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        VideoMediaAvailability.Available,
        4,
        5,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        displayTitle,
        false,
        [],
        null,
        null,
        null,
        [],
        [nodeId],
        []);
}
