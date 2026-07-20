using FluentAssertions;
using Niratan.Models;
using Niratan.Models.Video;
using Niratan.Services.Storage;
using Niratan.Services.Video;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Niratan.Tests.Services.Video;

public class VideoLibraryServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"niratan-video-library-{Guid.NewGuid():N}");

    public VideoLibraryServiceTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task ScanFolderAsync_RecursivelyImportsSupportedVideosOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var nested = Directory.CreateDirectory(Path.Combine(_directory, "Season 1")).FullName;
        var firstVideo = Touch(Path.Combine(_directory, "Episode 01.mkv"), "first");
        var secondVideo = Touch(Path.Combine(nested, "Episode 02.mp4"), "second");
        Touch(Path.Combine(nested, "notes.txt"));

        var data = new Mock<IVideoDataService>();
        var savedVideos = new List<VideoItem>();
        data
            .Setup(service => service.UpsertVideoAsync(It.IsAny<VideoItem>(), It.IsAny<CancellationToken>()))
            .Callback<VideoItem, CancellationToken>((video, _) => savedVideos.Add(video))
            .Returns(Task.CompletedTask);
        var sut = new VideoLibraryService(data.Object, NullLogger<VideoLibraryService>.Instance);

        var result = await sut.ScanFolderAsync(_directory, ct);

        result.IsSuccess.Should().BeTrue();
        result.Value!.ImportedCount.Should().Be(2);
        savedVideos.Select(video => video.FilePath).Should().BeEquivalentTo(firstVideo, secondVideo);
        savedVideos.Should().OnlyContain(video => video.SourceFolderPath != null);
        savedVideos.Should().OnlyContain(video => video.FileSizeBytes > 0);
        savedVideos.Should().OnlyContain(video => video.ModifiedAt.HasValue);
        savedVideos.Select(video => video.CollectionName).Should().Contain("niratan-video-library-" + Path.GetFileName(_directory).Split('-').Last());
        savedVideos.Should().OnlyContain(video => !string.IsNullOrWhiteSpace(video.SourceId));
        savedVideos.Should().OnlyContain(video => video.LastSeenAt.HasValue);
        data.Verify(service => service.UpsertVideoLibrarySourceAsync(
            It.Is<VideoLibrarySource>(source => source.FolderPath == _directory),
            It.IsAny<CancellationToken>()), Times.Once);
        data.Verify(service => service.DeleteSourceVideosExceptAsync(
            It.IsAny<string>(),
            It.Is<IReadOnlyList<string>>(paths => paths.Count == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ImportVideoAsync_PopulatesFileSizeAndModifiedTimestamp()
    {
        var ct = TestContext.Current.CancellationToken;
        var modifiedAt = new DateTime(2026, 7, 9, 5, 30, 0, DateTimeKind.Utc);
        var videoPath = Touch(Path.Combine(_directory, "Episode 04.mkv"), "video-bytes", modifiedAt);
        var data = new Mock<IVideoDataService>();
        VideoItem? saved = null;
        data
            .Setup(service => service.UpsertVideoAsync(It.IsAny<VideoItem>(), It.IsAny<CancellationToken>()))
            .Callback<VideoItem, CancellationToken>((video, _) => saved = video)
            .Returns(Task.CompletedTask);
        var sut = new VideoLibraryService(data.Object, NullLogger<VideoLibraryService>.Instance);

        var result = await sut.ImportVideoAsync(videoPath, ct);

        result.IsSuccess.Should().BeTrue();
        saved.Should().NotBeNull();
        saved!.FileSizeBytes.Should().Be(new FileInfo(videoPath).Length);
        saved.ModifiedAt.Should().Be(modifiedAt);
    }

    [Fact]
    public void FindPosterImage_PrefersSameStemPosterBeforeFolderPoster()
    {
        var mediaPath = Touch(Path.Combine(_directory, "Episode 03.mkv"));
        var folderPoster = Touch(Path.Combine(_directory, "cover.jpg"));
        var sameStemPoster = Touch(Path.Combine(_directory, "Episode 03.png"));

        VideoLibraryService.FindPosterImage(mediaPath).Should().Be(sameStemPoster);

        File.Delete(sameStemPoster);
        VideoLibraryService.FindPosterImage(mediaPath).Should().Be(folderPoster);
    }

    [Fact]
    public async Task MarkWatchedAsync_DelegatesToDataService()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = new Mock<IVideoDataService>();
        data
            .Setup(service => service.MarkVideoWatchedAsync("video-1", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var sut = new VideoLibraryService(data.Object, NullLogger<VideoLibraryService>.Instance);

        var result = await sut.MarkWatchedAsync("video-1", ct);

        result.IsSuccess.Should().BeTrue();
        data.Verify(
            service => service.MarkVideoWatchedAsync("video-1", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ClearProgressAsync_DelegatesToDataService()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = new Mock<IVideoDataService>();
        data
            .Setup(service => service.ClearVideoProgressAsync("video-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var sut = new VideoLibraryService(data.Object, NullLogger<VideoLibraryService>.Instance);

        var result = await sut.ClearProgressAsync("video-1", ct);

        result.IsSuccess.Should().BeTrue();
        data.Verify(
            service => service.ClearVideoProgressAsync("video-1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetCollectionsAsync_ReturnsCollectionsFromDataService()
    {
        var ct = TestContext.Current.CancellationToken;
        var collections = new[]
        {
            new VideoCollection { Id = "collection-1", Name = "Favorites", Kind = VideoCollectionKind.Manual },
        };
        var data = new Mock<IVideoDataService>();
        data
            .Setup(service => service.GetVideoCollectionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(collections);
        var sut = new VideoLibraryService(data.Object, NullLogger<VideoLibraryService>.Instance);

        var result = await sut.GetCollectionsAsync(ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(collections);
    }

    [Fact]
    public async Task CreateManualCollectionAsync_PersistsNormalizedManualCollection()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = new Mock<IVideoDataService>();
        VideoCollection? saved = null;
        IReadOnlyList<string>? savedItemIds = null;
        data
            .Setup(service => service.UpsertVideoCollectionAsync(It.IsAny<VideoCollection>(), It.IsAny<CancellationToken>()))
            .Callback<VideoCollection, CancellationToken>((collection, _) => saved = collection)
            .Returns(Task.CompletedTask);
        data
            .Setup(service => service.SetVideoCollectionItemsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, CancellationToken>((_, itemIds, _) => savedItemIds = itemIds)
            .Returns(Task.CompletedTask);
        var sut = new VideoLibraryService(data.Object, NullLogger<VideoLibraryService>.Instance);

        var result = await sut.CreateManualCollectionAsync("   ", ["video-1", "", "video-2", "video-1"], ct);

        result.IsSuccess.Should().BeTrue();
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("Untitled Collection");
        saved.Kind.Should().Be(VideoCollectionKind.Manual);
        saved.ItemIds.Should().Equal("video-1", "video-2");
        saved.SmartRules.Should().BeEmpty();
        savedItemIds.Should().Equal("video-1", "video-2");
    }

    [Fact]
    public async Task CreateSmartCollectionAsync_PersistsNormalizedSmartCollection()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = new Mock<IVideoDataService>();
        VideoCollection? saved = null;
        data
            .Setup(service => service.UpsertVideoCollectionAsync(It.IsAny<VideoCollection>(), It.IsAny<CancellationToken>()))
            .Callback<VideoCollection, CancellationToken>((collection, _) => saved = collection)
            .Returns(Task.CompletedTask);
        var sut = new VideoLibraryService(data.Object, NullLogger<VideoLibraryService>.Instance);

        var result = await sut.CreateSmartCollectionAsync(
            "  Umaru  ",
            [
                new VideoSmartRule { Field = VideoSmartRuleField.FileName, Value = " umaru " },
                new VideoSmartRule { Field = VideoSmartRuleField.Tag, Match = VideoSmartRuleMatch.Contains, Value = "   " },
                new VideoSmartRule { Field = VideoSmartRuleField.HasBoundSubtitle, Match = VideoSmartRuleMatch.IsTrue },
            ],
            ct);

        result.IsSuccess.Should().BeTrue();
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("Umaru");
        saved.Kind.Should().Be(VideoCollectionKind.Smart);
        saved.SmartRules.Should().HaveCount(2);
        saved.SmartRules[0].Value.Should().Be("umaru");
        saved.SmartRules[1].Field.Should().Be(VideoSmartRuleField.HasBoundSubtitle);
        saved.SmartRules[1].Value.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteCollectionAsync_DelegatesToDataService()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = new Mock<IVideoDataService>();
        data
            .Setup(service => service.DeleteVideoCollectionAsync("collection-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var sut = new VideoLibraryService(data.Object, NullLogger<VideoLibraryService>.Instance);

        var result = await sut.DeleteCollectionAsync("collection-1", ct);

        result.IsSuccess.Should().BeTrue();
        data.Verify(
            service => service.DeleteVideoCollectionAsync("collection-1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SetFavoriteAsync_DelegatesToDataService()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = new Mock<IVideoDataService>();
        data
            .Setup(service => service.UpdateVideoFavoriteAsync("video-1", true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var sut = new VideoLibraryService(data.Object, NullLogger<VideoLibraryService>.Instance);

        var result = await sut.SetFavoriteAsync("video-1", true, ct);

        result.IsSuccess.Should().BeTrue();
        data.Verify(
            service => service.UpdateVideoFavoriteAsync("video-1", true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static string Touch(string path, string contents = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static string Touch(string path, string contents, DateTime modifiedAt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        File.SetLastWriteTimeUtc(path, modifiedAt);
        return path;
    }
}
