using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Niratan.Models;
using Niratan.Models.Common;
using Niratan.Models.ZLibrary;
using Niratan.Services.Novels;
using Niratan.Services.ZLibrary;

namespace Niratan.Tests.Services.ZLibrary;

public sealed class ZLibraryServiceTests
{
    [Fact]
    public async Task DownloadAndImportAsync_ValidatesEpubAndImportsThroughNovelLibrary()
    {
        var bytes = CreateEpub();
        var credentials = new ZLibraryCredentials(
            "https://books.example",
            "reader@example.com",
            "secret");
        var session = new ZLibrarySession(new Uri("https://books.example/"), "42", "key");
        var sourceBook = CreateBook();
        var importedBook = new NovelBook { Id = "novel-1", Title = sourceBook.Title };
        var client = new Mock<IZLibraryClient>();
        client.Setup(value => value.LoginAsync(credentials, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        client.Setup(value => value.DownloadEpubAsync(
                session,
                sourceBook,
                It.IsAny<Stream>(),
                It.IsAny<CancellationToken>()))
            .Returns<ZLibrarySession, ZLibraryBook, Stream, CancellationToken>(
                async (_, _, destination, ct) => await destination.WriteAsync(bytes, ct));
        var store = new Mock<IZLibraryCredentialStore>();
        store.Setup(value => value.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(credentials);
        var library = new Mock<INovelLibraryService>();
        library.Setup(value => value.ImportEpubAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((path, _) =>
            {
                File.Exists(path).Should().BeTrue();
                return Task.FromResult(Result<NovelBook>.Success(importedBook));
            });
        var sut = new ZLibraryService(
            client.Object,
            store.Object,
            library.Object,
            NullLogger<ZLibraryService>.Instance);

        var result = await sut.DownloadAndImportAsync(
            sourceBook,
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(importedBook);
        library.Verify(value => value.ImportEpubAsync(
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DownloadAndImportAsync_RejectsNonEpubPayloadBeforeImport()
    {
        var credentials = new ZLibraryCredentials(
            "https://books.example",
            "reader@example.com",
            "secret");
        var session = new ZLibrarySession(new Uri("https://books.example/"), "42", "key");
        var sourceBook = CreateBook();
        var client = new Mock<IZLibraryClient>();
        client.Setup(value => value.LoginAsync(credentials, It.IsAny<CancellationToken>()))
            .ReturnsAsync(session);
        client.Setup(value => value.DownloadEpubAsync(
                session,
                sourceBook,
                It.IsAny<Stream>(),
                It.IsAny<CancellationToken>()))
            .Returns<ZLibrarySession, ZLibraryBook, Stream, CancellationToken>(
                async (_, _, destination, ct) =>
                    await destination.WriteAsync(Encoding.UTF8.GetBytes("not an epub"), ct));
        var store = new Mock<IZLibraryCredentialStore>();
        store.Setup(value => value.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(credentials);
        var library = new Mock<INovelLibraryService>();
        var sut = new ZLibraryService(
            client.Object,
            store.Object,
            library.Object,
            NullLogger<ZLibraryService>.Instance);

        var result = await sut.DownloadAndImportAsync(
            sourceBook,
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not an EPUB archive");
        library.Verify(value => value.ImportEpubAsync(
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ZLibraryBook CreateBook() => new(
        "12", "abc", "Test book", "Author", "EPUB", "1 KB", null,
        "English", null, null);

    private static byte[] CreateEpub()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var mimetype = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(mimetype.Open(), new UTF8Encoding(false)))
                writer.Write("application/epub+zip");
            var container = archive.CreateEntry("META-INF/container.xml");
            using (var writer = new StreamWriter(container.Open(), new UTF8Encoding(false)))
                writer.Write("<container/>");
        }

        return output.ToArray();
    }
}
