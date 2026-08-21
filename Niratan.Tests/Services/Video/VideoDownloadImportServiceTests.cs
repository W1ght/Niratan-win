using FluentAssertions;
using Moq;
using Niratan.Models.Common;
using Niratan.Models.QBittorrent;
using Niratan.Models.Video;
using Niratan.Services.Video;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Video;

public sealed class VideoDownloadImportServiceTests
{
    [Fact]
    public async Task CompletedTask_OnlyReturnsSourcesContainingContentPath()
    {
        using var temp = new TempDirectory();
        var sourcePath = Path.Combine(temp.Path, "videos");
        var outsidePath = Path.Combine(temp.Path, "other");
        Directory.CreateDirectory(sourcePath);
        Directory.CreateDirectory(outsidePath);
        var media = Path.Combine(sourcePath, "movie.mkv");
        await File.WriteAllBytesAsync(media, [1, 2, 3]);
        var source = new VideoLibrarySource { Id = "source", Name = "Videos", FolderPath = sourcePath };
        var other = new VideoLibrarySource { Id = "other", Name = "Other", FolderPath = outsidePath };
        var library = new Mock<IVideoLibraryService>();
        library.Setup(value => value.GetSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<VideoLibrarySource>>.Success([source, other]));
        var service = new VideoDownloadImportService(library.Object);

        var result = await service.GetCompatibleSourcesAsync(CreateTask(media));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle().Which.Id.Should().Be("source");
    }

    [Fact]
    public async Task Import_RefreshesExistingSourceAndNeverCreatesOrScansANewSource()
    {
        using var temp = new TempDirectory();
        var sourcePath = Path.Combine(temp.Path, "videos");
        Directory.CreateDirectory(sourcePath);
        var media = Path.Combine(sourcePath, "movie.mkv");
        await File.WriteAllBytesAsync(media, [1]);
        var source = new VideoLibrarySource { Id = "source", Name = "Videos", FolderPath = sourcePath };
        var refresh = new VideoSourceRefreshResult(source, 1, []);
        var library = new Mock<IVideoLibraryService>();
        library.Setup(value => value.GetSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<VideoLibrarySource>>.Success([source]));
        library.Setup(value => value.RefreshSourceAsync("source", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<VideoSourceRefreshResult>.Success(refresh));
        var service = new VideoDownloadImportService(library.Object);

        var result = await service.ImportCompletedTaskAsync(CreateTask(media), "source");

        result.IsSuccess.Should().BeTrue();
        library.Verify(value => value.RefreshSourceAsync("source", It.IsAny<CancellationToken>()), Times.Once);
        library.Verify(value => value.ScanFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static QbittorrentTorrent CreateTask(string path) => new(
        "hash",
        "movie",
        "UP",
        1,
        100,
        0,
        0,
        0,
        0,
        0,
        "",
        "",
        Path.GetDirectoryName(path)!,
        path,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);
}
