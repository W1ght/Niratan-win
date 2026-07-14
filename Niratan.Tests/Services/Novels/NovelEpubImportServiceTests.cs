using System.IO.Compression;
using FluentAssertions;
using Niratan.Models.Novel;
using Microsoft.Extensions.Logging.Abstractions;
using Niratan.Services.Novels;
using Moq;

namespace Niratan.Tests.Services.Novels;

public class NovelEpubImportServiceTests
{
    private static NovelEpubImportService CreateSut()
    {
        var parser = new EpubParserService(NullLogger<EpubParserService>.Instance);
        return new NovelEpubImportService(
            parser,
            new NovelBookSidecarService(),
            NullLogger<NovelEpubImportService>.Instance
        );
    }

    [Fact]
    public async Task ImportAsync_ReturnsFailure_WhenFileIsNotEpub()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "not epub", ct);
        var sut = CreateSut();

        var result = await sut.ImportAsync(path, ct);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain(".epub");
    }

    [Fact]
    public async Task ImportAsync_ReturnsFailure_WhenEpubIsMissingContainerXml()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.epub");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var mimetype = archive.CreateEntry("mimetype");
            await using var mimeStream = mimetype.Open();
            await using var writer = new StreamWriter(mimeStream);
            await writer.WriteAsync("application/epub+zip");
        }

        var sut = CreateSut();

        var result = await sut.ImportAsync(path, ct);

        result.IsSuccess.Should().BeFalse();
        result.ErrorTitle.Should().Be("EPUB import failed");
    }

    [Fact]
    public async Task ImportAsync_CopiesEpubIntoPrivateBookDirectoryBeforeParsing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Path.Combine(temp.Path, "source.epub");
        await File.WriteAllTextAsync(sourcePath, "epub", ct);
        var booksRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "Novels")).FullName;
        var parser = new Mock<IEpubParserService>();
        parser
            .Setup(service => service.Parse(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string privateEpubPath, string outputDirectory) =>
            {
                File.Exists(privateEpubPath).Should().BeTrue();
                Path.GetDirectoryName(privateEpubPath).Should().Be(outputDirectory);
                return new EpubBook
                {
                    Title = "星",
                    Language = "ja",
                    ContainerDirectory = outputDirectory,
                };
            });
        var sut = new NovelEpubImportService(
            parser.Object,
            new NovelBookSidecarService(),
            NullLogger<NovelEpubImportService>.Instance,
            id => Path.Combine(booksRoot, id));

        var result = await sut.ImportAsync(sourcePath, ct);

        result.IsSuccess.Should().BeTrue(result.Error);
        var book = result.Value!.Book;
        book.FilePath.Should().Be(Path.Combine(book.ExtractedPath!, book.Id + ".epub"));
        File.Exists(book.FilePath).Should().BeTrue();
        book.OriginalTitle.Should().Be("星");
        book.Folder.Should().Be(book.Id);
        File.Exists(sourcePath).Should().BeTrue();
    }

    [Fact]
    public async Task ImportAsync_SavesBookInfoBeforeReturningSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourcePath = Path.Combine(temp.Path, "source.epub");
        await File.WriteAllTextAsync(sourcePath, "epub", ct);
        var booksRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "Novels")).FullName;
        var parser = new Mock<IEpubParserService>();
        parser
            .Setup(service => service.Parse(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string _, string outputDirectory) =>
            {
                var first = Path.Combine(outputDirectory, "chapter-1.xhtml");
                var second = Path.Combine(outputDirectory, "chapter-2.xhtml");
                File.WriteAllText(first, "<body><ruby>星<rt>ほし</rt></ruby>A!</body>");
                File.WriteAllText(second, "<body>読む。</body>");
                return new EpubBook
                {
                    Title = "星を読む",
                    ContainerDirectory = outputDirectory,
                    Chapters =
                    [
                        new EpubChapter { Href = first, SpineIndex = 0 },
                        new EpubChapter { Href = second, SpineIndex = 1 },
                    ],
                };
            });
        var sidecars = new NovelBookSidecarService();
        var sut = new NovelEpubImportService(
            parser.Object,
            sidecars,
            NullLogger<NovelEpubImportService>.Instance,
            id => Path.Combine(booksRoot, id));

        var result = await sut.ImportAsync(sourcePath, ct);

        result.IsSuccess.Should().BeTrue(result.Error);
        var bookInfo = await sidecars.LoadBookInfoAsync(result.Value!.Book.ExtractedPath!, ct);
        bookInfo.Should().NotBeNull();
        bookInfo!.CharacterCount.Should().Be(4);
        bookInfo.ChapterInfo["chapter-1.xhtml"].Should().Be(
            new NovelBookInfoChapter(SpineIndex: 0, CurrentTotal: 0, ChapterCount: 2));
        bookInfo.ChapterInfo["chapter-2.xhtml"].Should().Be(
            new NovelBookInfoChapter(SpineIndex: 1, CurrentTotal: 2, ChapterCount: 2));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
