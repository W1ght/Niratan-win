using System.IO.Compression;
using FluentAssertions;
using Niratan.Models.Manga;
using Niratan.Services.Manga;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Manga;

public sealed class MangaPageProviderTests
{
    [Fact]
    public async Task GetPagePathAsync_FolderPathEscape_IsRejected()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "book");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(temp.Path, "outside.jpg"), [1, 2, 3]);
        var book = new MangaBook
        {
            SourcePath = root,
            ContainerKind = MangaContainerKind.ImageFolder,
            Pages = [new MangaPageDescriptor { Index = 0, Path = "../outside.jpg" }],
        };

        var action = () => new MangaPageProvider().GetPagePathAsync(
            book,
            0,
            TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*escapes*");
    }

    [Fact]
    public async Task GetPagePathAsync_Archive_ExtractsOnlyRequestedEntry()
    {
        using var temp = new TempDirectory();
        var archivePath = Path.Combine(temp.Path, "book.cbz");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            AddEntry(archive, "001.jpg", [1, 2, 3]);
            AddEntry(archive, "002.jpg", [4, 5, 6]);
        }
        var book = new MangaBook
        {
            Id = Guid.NewGuid().ToString("N"),
            SourcePath = archivePath,
            ContainerKind = MangaContainerKind.ZipArchive,
            Pages =
            [
                new MangaPageDescriptor { Index = 0, Path = "001.jpg" },
                new MangaPageDescriptor { Index = 1, Path = "002.jpg" },
            ],
        };

        var pagePath = await new MangaPageProvider(Path.Combine(temp.Path, "cache"))
            .GetPagePathAsync(book, 1, TestContext.Current.CancellationToken);

        File.ReadAllBytes(pagePath).Should().Equal(4, 5, 6);
        Path.GetFileName(pagePath).Should().Be("000001.jpg");
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("nested/id")]
    [InlineData("C:\\outside")]
    public async Task GetPagePathAsync_UnsafeBookId_IsRejectedBeforeCreatingCache(
        string bookId)
    {
        using var temp = new TempDirectory();
        var archivePath = Path.Combine(temp.Path, "book.cbz");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            AddEntry(archive, "001.jpg", [1, 2, 3]);
        var book = new MangaBook
        {
            Id = bookId,
            SourcePath = archivePath,
            ContainerKind = MangaContainerKind.ZipArchive,
            Pages = [new MangaPageDescriptor { Index = 0, Path = "001.jpg" }],
        };

        var action = () => new MangaPageProvider(Path.Combine(temp.Path, "cache"))
            .GetPagePathAsync(book, 0, TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*identity*");
    }

    private static void AddEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path);
        using var output = entry.Open();
        output.Write(content);
    }
}
