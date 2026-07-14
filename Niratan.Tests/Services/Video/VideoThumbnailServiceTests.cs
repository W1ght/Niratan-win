using FluentAssertions;
using Niratan.Models;
using Niratan.Services.Storage;
using Niratan.Services.Video;
using Moq;

namespace Niratan.Tests.Services.Video;

public class VideoThumbnailServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"niratan-thumbs-{Guid.NewGuid():N}");

    public VideoThumbnailServiceTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task EnsureThumbnailAsync_ReturnsExistingPosterWithoutGenerating()
    {
        var ct = TestContext.Current.CancellationToken;
        var poster = Touch(Path.Combine(_directory, "poster.jpg"));
        var extractor = new Mock<IVideoMiningMediaExtractor>();
        var data = new Mock<IVideoDataService>();
        var sut = new VideoThumbnailService(extractor.Object, data.Object, _directory);

        var result = await sut.EnsureThumbnailAsync(
            new VideoItem { Id = "video-1", FilePath = @"D:\Video\a.mkv", PosterPath = poster },
            generateIfMissing: true,
            ct);

        result.Should().Be(poster);
        extractor.Verify(
            service => service.CaptureScreenshotAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnsureThumbnailAsync_DeduplicatesConcurrentGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        var video = Touch(Path.Combine(_directory, "episode.mkv"));
        var extractor = new Mock<IVideoMiningMediaExtractor>();
        extractor.Setup(service => service.CaptureScreenshotAsync(video, It.IsAny<string>(), TimeSpan.FromSeconds(5), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string outputPath, TimeSpan _, CancellationToken _) =>
            {
                File.WriteAllText(outputPath, "png");
                return outputPath;
            });
        var data = new Mock<IVideoDataService>();
        data.Setup(service => service.UpdateVideoThumbnailPathAsync("video-1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var sut = new VideoThumbnailService(extractor.Object, data.Object, _directory);

        var item = new VideoItem { Id = "video-1", FilePath = video, FileSizeBytes = 3, ModifiedAt = File.GetLastWriteTimeUtc(video) };
        var results = await Task.WhenAll(
            sut.EnsureThumbnailAsync(item, true, ct),
            sut.EnsureThumbnailAsync(item, true, ct));

        results.Should().OnlyContain(path => path != null && File.Exists(path));
        extractor.Verify(
            service => service.CaptureScreenshotAsync(video, It.IsAny<string>(), TimeSpan.FromSeconds(5), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EnsureThumbnailAsync_WhenSuspended_SkipsGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        var video = Touch(Path.Combine(_directory, "suspended.mkv"));
        var extractor = new Mock<IVideoMiningMediaExtractor>();
        var data = new Mock<IVideoDataService>();
        var sut = new VideoThumbnailService(extractor.Object, data.Object, _directory);
        sut.Suspend();

        var result = await sut.EnsureThumbnailAsync(
            new VideoItem { Id = "video-2", FilePath = video, FileSizeBytes = 3, ModifiedAt = File.GetLastWriteTimeUtc(video) },
            generateIfMissing: true,
            ct);

        result.Should().BeNull();
        extractor.Verify(
            service => service.CaptureScreenshotAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }
}
