using System.Text.Json;
using FluentAssertions;
using Niratan.Models;
using Niratan.Models.Video;
using Niratan.Services.Storage;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Storage;

public sealed class VideoDataServiceTests
{
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

        File.Exists(catalogPath).Should().BeTrue();
        File.Exists(historyPath).Should().BeTrue();
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
    public async Task VideoDataService_WritesNiratanCatalogShapeAndMacAbsoluteDates()
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
        };

        var service = new VideoDataService(catalogPath, historyPath);
        await service.UpsertVideoLibrarySourceAsync(source, ct);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(catalogPath, ct));
        var root = document.RootElement;
        var storedSource = root.GetProperty("sources")[0];
        storedSource.GetProperty("path").GetString().Should().Be(Path.GetFullPath(sourcePath));
        storedSource.GetProperty("bookmark").GetString().Should().Be("");
        storedSource.GetProperty("createdAt").ValueKind.Should().Be(JsonValueKind.Number);
        root.TryGetProperty("items", out _).Should().BeTrue();
        root.TryGetProperty("remoteItems", out _).Should().BeTrue();
        root.TryGetProperty("itemMetadataByPath", out _).Should().BeTrue();
        root.TryGetProperty("collections", out _).Should().BeTrue();
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

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(catalogPath, ct));
        document.RootElement.GetProperty("collections")[0]
            .GetProperty("itemPaths")[0]
            .GetString()
            .Should().Be(Path.GetFullPath(videoPath));
    }

    [Fact]
    public async Task RemovingSource_RemovesCatalogItemButPreservesPlaybackHistory()
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
        (await service.GetVideoAsync(videoPath, ct)).Should().BeNull();

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

        var action = () => service.GetVideosAsync(ct: ct);
        await action.Should().ThrowAsync<InvalidDataException>();
        (await File.ReadAllTextAsync(catalogPath, ct)).Should().Be(invalid);
    }
}
