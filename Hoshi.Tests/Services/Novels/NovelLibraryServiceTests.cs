using FluentAssertions;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Models.DTO;
using Hoshi.Models.Novel;
using Hoshi.Services.Novels;
using Hoshi.Tests.TestUtils;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Hoshi.Tests.Services.Novels;

public sealed class NovelLibraryServiceTests
{
    [Fact]
    public async Task GetNovelBooksAsync_ReturnsFileCatalogSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = new NovelBookCatalogSnapshot(
            [new NovelBook { Id = "a", Title = "星" }],
            ["broken/metadata.json"]);
        var storage = new Mock<INovelBookStorageService>();
        storage.Setup(service => service.LoadSnapshotAsync("星", ct)).ReturnsAsync(expected);
        var sut = CreateSut(storage: storage);

        var result = await sut.GetNovelBooksAsync("星", ct);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Should().BeSameAs(expected);
        storage.VerifyAll();
    }

    [Fact]
    public async Task SaveProgressAsync_WritesOneCanonicalBookmarkSidecar()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Mock<INovelBookStorageService>();
        storage.Setup(service => service.ResolveRootPath("book-a")).Returns(@"D:\Books\book-a");
        var sidecars = new Mock<INovelBookSidecarService>();
        sidecars
            .Setup(service => service.SaveBookmarkAsync(
                @"D:\Books\book-a",
                It.Is<NovelBookmark>(bookmark =>
                    bookmark.ChapterIndex == 2
                    && bookmark.Progress == 0.25
                    && bookmark.CharacterCount == 1234
                    && bookmark.LastModified != null),
                ct))
            .Returns(Task.CompletedTask);
        var sut = CreateSut(storage, sidecars);

        var result = await sut.SaveProgressAsync("book-a", 2, 0.25, 1234, 9000, ct);

        result.IsSuccess.Should().BeTrue(result.Error);
        sidecars.VerifyAll();
        sidecars.Verify(
            service => service.SaveBookmarkAsync(
                It.IsAny<string>(),
                It.IsAny<NovelBookmark>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MarkReadAsync_WritesNiratanCompatibleFinalBookmark()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Mock<INovelBookStorageService>();
        storage.Setup(service => service.ResolveRootPath("book-a"))
            .Returns(@"D:\Books\book-a");
        var sidecars = new Mock<INovelBookSidecarService>();
        sidecars.Setup(service => service.LoadBookInfoAsync(@"D:\Books\book-a", ct))
            .ReturnsAsync(new NovelBookInfo(
                9000,
                new Dictionary<string, NovelBookInfoChapter>
                {
                    ["chapter-a"] = new(1, 100, 100),
                    ["chapter-b"] = new(null, 200, 200),
                    ["chapter-c"] = new(7, 300, 300),
                }));
        sidecars.Setup(service => service.SaveBookmarkAsync(
                @"D:\Books\book-a",
                It.Is<NovelBookmark>(bookmark =>
                    bookmark.ChapterIndex == 7
                    && bookmark.Progress == 1
                    && bookmark.CharacterCount == 9000
                    && bookmark.LastModified != null),
                ct))
            .Returns(Task.CompletedTask);
        var sut = CreateSut(storage, sidecars);

        var result = await sut.MarkReadAsync("book-a", ct);

        result.IsSuccess.Should().BeTrue(result.Error);
        sidecars.VerifyAll();
        sidecars.Verify(service => service.SaveBookmarkAsync(
            It.IsAny<string>(),
            It.IsAny<NovelBookmark>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkReadAsync_NoSpineIndicesUsesFirstChapterIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Mock<INovelBookStorageService>();
        storage.Setup(service => service.ResolveRootPath("book-a"))
            .Returns(@"D:\Books\book-a");
        var sidecars = new Mock<INovelBookSidecarService>();
        sidecars.Setup(service => service.LoadBookInfoAsync(@"D:\Books\book-a", ct))
            .ReturnsAsync(new NovelBookInfo(
                500,
                new Dictionary<string, NovelBookInfoChapter>
                {
                    ["chapter-a"] = new(null, 500, 500),
                }));
        sidecars.Setup(service => service.SaveBookmarkAsync(
                @"D:\Books\book-a",
                It.Is<NovelBookmark>(bookmark => bookmark.ChapterIndex == 0),
                ct))
            .Returns(Task.CompletedTask);
        var sut = CreateSut(storage, sidecars);

        var result = await sut.MarkReadAsync("book-a", ct);

        result.IsSuccess.Should().BeTrue(result.Error);
        sidecars.VerifyAll();
    }

    [Fact]
    public async Task MarkReadAsync_MissingBookInfoReturnsSuccessWithoutWriting()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Mock<INovelBookStorageService>();
        storage.Setup(service => service.ResolveRootPath("book-a"))
            .Returns(@"D:\Books\book-a");
        var sidecars = new Mock<INovelBookSidecarService>();
        sidecars.Setup(service => service.LoadBookInfoAsync(@"D:\Books\book-a", ct))
            .ReturnsAsync((NovelBookInfo?)null);
        var sut = CreateSut(storage, sidecars);

        var result = await sut.MarkReadAsync("book-a", ct);

        result.IsSuccess.Should().BeTrue(result.Error);
        sidecars.VerifyAll();
        sidecars.Verify(service => service.SaveBookmarkAsync(
            It.IsAny<string>(),
            It.IsAny<NovelBookmark>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ImportEpubAsync_PersistsMetadataBeforeReturningSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        var book = new NovelBook { Id = "a", Title = "星", Folder = "a" };
        var import = new Mock<INovelEpubImportService>();
        import.Setup(service => service.ImportAsync("book.epub", ct))
            .ReturnsAsync(Result<NovelImportResult>.Success(new NovelImportResult(book)));
        var storage = new Mock<INovelBookStorageService>();
        storage.Setup(service => service.SaveMetadataAsync(book, ct)).Returns(Task.CompletedTask);
        var sut = CreateSut(storage: storage, import: import);

        var result = await sut.ImportEpubAsync("book.epub", ct);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Should().BeSameAs(book);
        import.VerifyAll();
        storage.VerifyAll();
    }

    [Fact]
    public async Task SaveNovelBookOrderAsync_ReadOnlyRecoveryDoesNotMutateFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Mock<INovelBookStorageService>(MockBehavior.Strict);
        var accessState = new Mock<INovelStorageAccessState>();
        accessState.SetupGet(state => state.IsReadOnly).Returns(true);
        accessState.SetupGet(state => state.ErrorMessage).Returns("repair metadata first");
        var sut = CreateSut(storage: storage, accessState: accessState);

        var result = await sut.SaveNovelBookOrderAsync(["a"], ct);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("repair metadata first");
        storage.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteNovelAsync_ShelfCleanupFailurePreservesBookDirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Mock<INovelBookStorageService>();
        storage.Setup(service => service.LoadAsync("a", ct))
            .ReturnsAsync(new NovelBook { Id = "a", Title = "星" });
        var shelves = new Mock<INovelShelfService>();
        shelves.Setup(service => service.RemoveBookAsync("a", ct))
            .ReturnsAsync(Result<NovelShelfState>.Failure("shelves.json is invalid"));
        var sut = CreateSut(storage: storage, shelves: shelves);

        var result = await sut.DeleteNovelAsync("a", ct);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("shelves.json is invalid");
        storage.Verify(service => service.DeleteAsync(
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExportEpubAsync_CopiesPrivateEpubBytesExactly()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "private.epub");
        var destination = Path.Combine(temp.Path, "exported.epub");
        var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0xFF };
        await File.WriteAllBytesAsync(source, bytes, ct);
        var storage = new Mock<INovelBookStorageService>();
        storage.Setup(service => service.LoadAsync("book-a", ct))
            .ReturnsAsync(new NovelBook { Id = "book-a", FilePath = source });
        var sut = CreateSut(storage: storage);

        var result = await sut.ExportEpubAsync("book-a", destination, ct);

        result.IsSuccess.Should().BeTrue(result.Error);
        (await File.ReadAllBytesAsync(destination, ct)).Should().Equal(bytes);
        (await File.ReadAllBytesAsync(source, ct)).Should().Equal(bytes);
    }

    [Fact]
    public async Task ExportEpubAsync_SourceEqualDestinationFailsWithoutChangingSource()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "private.epub");
        var bytes = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(source, bytes, ct);
        var storage = new Mock<INovelBookStorageService>();
        storage.Setup(service => service.LoadAsync("book-a", ct))
            .ReturnsAsync(new NovelBook { Id = "book-a", FilePath = source });
        var sut = CreateSut(storage: storage);

        var result = await sut.ExportEpubAsync("book-a", source, ct);

        result.IsSuccess.Should().BeFalse();
        result.ErrorTitle.Should().Be("EPUB export failed");
        (await File.ReadAllBytesAsync(source, ct)).Should().Equal(bytes);
    }

    [Fact]
    public async Task ExportEpubAsync_MissingPrivateEpubReturnsFileNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var storage = new Mock<INovelBookStorageService>();
        storage.Setup(service => service.LoadAsync("book-a", ct))
            .ReturnsAsync(new NovelBook
            {
                Id = "book-a",
                FilePath = Path.Combine(temp.Path, "missing.epub"),
            });
        var sut = CreateSut(storage: storage);

        var result = await sut.ExportEpubAsync(
            "book-a",
            Path.Combine(temp.Path, "exported.epub"),
            ct);

        result.IsSuccess.Should().BeFalse();
        result.ErrorTitle.Should().Be("EPUB file not found");
    }

    [Fact]
    public async Task ExportEpubAsync_MissingCatalogBookReturnsExportFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var storage = new Mock<INovelBookStorageService>();
        storage.Setup(service => service.LoadAsync("missing", ct))
            .ReturnsAsync((NovelBook?)null);
        var sut = CreateSut(storage: storage);

        var result = await sut.ExportEpubAsync(
            "missing",
            Path.Combine(temp.Path, "exported.epub"),
            ct);

        result.IsSuccess.Should().BeFalse();
        result.ErrorTitle.Should().Be("EPUB export failed");
        result.Error.Should().Contain("Book not found");
    }

    [Fact]
    public async Task ExportEpubAsync_NonEpubDestinationFailsBeforeOpeningSource()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new Mock<INovelBookStorageService>(MockBehavior.Strict);
        var sut = CreateSut(storage: storage);

        var result = await sut.ExportEpubAsync("book-a", "exported.zip", ct);

        result.IsSuccess.Should().BeFalse();
        result.ErrorTitle.Should().Be("EPUB export failed");
        storage.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExportEpubAsync_RemainsAvailableWhenLibraryIsReadOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "private.epub");
        var destination = Path.Combine(temp.Path, "exported.epub");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 }, ct);
        var storage = new Mock<INovelBookStorageService>();
        storage.Setup(service => service.LoadAsync("book-a", ct))
            .ReturnsAsync(new NovelBook { Id = "book-a", FilePath = source });
        var accessState = new Mock<INovelStorageAccessState>();
        accessState.SetupGet(state => state.IsReadOnly).Returns(true);
        var sut = CreateSut(storage: storage, accessState: accessState);

        var result = await sut.ExportEpubAsync("book-a", destination, ct);

        result.IsSuccess.Should().BeTrue(result.Error);
        File.Exists(destination).Should().BeTrue();
    }

    private static NovelLibraryService CreateSut(
        Mock<INovelBookStorageService>? storage = null,
        Mock<INovelBookSidecarService>? sidecars = null,
        Mock<INovelStorageAccessState>? accessState = null,
        Mock<INovelEpubImportService>? import = null,
        Mock<INovelShelfService>? shelves = null) =>
        new(
            storage?.Object ?? Mock.Of<INovelBookStorageService>(),
            sidecars?.Object ?? Mock.Of<INovelBookSidecarService>(),
            accessState?.Object ?? CreateWritableAccessState().Object,
            import?.Object ?? Mock.Of<INovelEpubImportService>(),
            shelves?.Object ?? CreateSuccessfulShelfService().Object,
            NullLogger<NovelLibraryService>.Instance);

    private static Mock<INovelStorageAccessState> CreateWritableAccessState()
    {
        var state = new Mock<INovelStorageAccessState>();
        state.SetupGet(value => value.IsReadOnly).Returns(false);
        return state;
    }

    private static Mock<INovelShelfService> CreateSuccessfulShelfService()
    {
        var shelves = new Mock<INovelShelfService>();
        shelves.Setup(service => service.RemoveBookAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<NovelShelfState>.Success(new NovelShelfState([], [])));
        return shelves;
    }
}
