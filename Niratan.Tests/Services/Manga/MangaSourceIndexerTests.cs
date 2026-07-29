using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Niratan.Models.Manga;
using Niratan.Services.Manga;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Manga;

public sealed class MangaSourceIndexerTests
{
    [Fact]
    public async Task IndexAsync_ImageFolder_UsesImmediateNaturalPageOrder()
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Path, "10.jpg"), [10]);
        File.WriteAllBytes(Path.Combine(temp.Path, "2.jpg"), [2]);
        Directory.CreateDirectory(Path.Combine(temp.Path, "nested"));
        File.WriteAllBytes(Path.Combine(temp.Path, "nested", "1.jpg"), [1]);

        var book = await new MangaSourceIndexer().IndexAsync(
            temp.Path,
            TestContext.Current.CancellationToken);

        book.ContainerKind.Should().Be(MangaContainerKind.ImageFolder);
        book.Pages.Select(page => page.Path).Should().Equal("2.jpg", "10.jpg");
    }

    [Fact]
    public async Task IndexAsync_Cbz_FiltersMacMetadataAndUsesNaturalOrder()
    {
        using var temp = new TempDirectory();
        var archivePath = Path.Combine(temp.Path, "volume.cbz");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            AddEntry(archive, "chapter/10.png", [10]);
            AddEntry(archive, "chapter/2.png", [2]);
            AddEntry(archive, "__MACOSX/chapter/._2.png", [99]);
            AddEntry(archive, "chapter/.DS_Store", [99]);
        }

        var book = await new MangaSourceIndexer().IndexAsync(
            archivePath,
            TestContext.Current.CancellationToken);

        book.ContainerKind.Should().Be(MangaContainerKind.ZipArchive);
        book.Pages.Select(page => page.Path).Should()
            .Equal("chapter/2.png", "chapter/10.png");
    }

    [Fact]
    public async Task IndexAsync_Epub_FollowsSpineAndBodyImageReferences()
    {
        using var temp = new TempDirectory();
        var epubPath = Path.Combine(temp.Path, "manga.epub");
        using (var archive = ZipFile.Open(epubPath, ZipArchiveMode.Create))
        {
            AddText(
                archive,
                "META-INF/container.xml",
                """
                <?xml version="1.0"?>
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles>
                    <rootfile full-path="OPS/package.opf" media-type="application/oebps-package+xml"/>
                  </rootfiles>
                </container>
                """);
            AddText(
                archive,
                "OPS/package.opf",
                """
                <?xml version="1.0"?>
                <package xmlns="http://www.idpf.org/2007/opf">
                  <manifest>
                    <item id="cover" href="images/cover.jpg" media-type="image/jpeg"/>
                    <item id="p2" href="text/page2.xhtml" media-type="application/xhtml+xml"/>
                    <item id="p1" href="text/page1.xhtml" media-type="application/xhtml+xml"/>
                    <item id="i1" href="images/001.jpg" media-type="image/jpeg"/>
                    <item id="i2" href="images/002.jpg" media-type="image/jpeg"/>
                  </manifest>
                  <spine><itemref idref="p1"/><itemref idref="p2"/></spine>
                </package>
                """);
            AddText(archive, "OPS/text/page1.xhtml", """<html><body><img src="../images/001.jpg"/></body></html>""");
            AddText(archive, "OPS/text/page2.xhtml", """<html><body><img src="../images/002.jpg"/></body></html>""");
            AddEntry(archive, "OPS/images/cover.jpg", [0]);
            AddEntry(archive, "OPS/images/001.jpg", [1]);
            AddEntry(archive, "OPS/images/002.jpg", [2]);
        }

        var book = await new MangaSourceIndexer().IndexAsync(
            epubPath,
            TestContext.Current.CancellationToken);

        book.ContainerKind.Should().Be(MangaContainerKind.EpubArchive);
        book.Pages.Select(page => page.Path).Should()
            .Equal("OPS/images/001.jpg", "OPS/images/002.jpg");
    }

    [Fact]
    public async Task IndexAsync_ArchiveWithoutImages_FailsClearly()
    {
        using var temp = new TempDirectory();
        var archivePath = Path.Combine(temp.Path, "empty.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            AddText(archive, "readme.txt", "not a manga");

        var action = () => new MangaSourceIndexer().IndexAsync(
            archivePath,
            TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*no readable manga images*");
    }

    [Fact]
    public async Task IndexAsync_MokuroFile_UsesMetadataPageOrderAndNestedImages()
    {
        using var temp = new TempDirectory();
        var images = Path.Combine(temp.Path, "pages");
        Directory.CreateDirectory(images);
        File.WriteAllBytes(Path.Combine(images, "001.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(images, "002.jpg"), [2]);
        var metadataPath = Path.Combine(temp.Path, "volume.mokuro");
        File.WriteAllText(
            metadataPath,
            """
            {
              "pages": [
                { "img_path": "pages/002.jpg", "img_width": 100, "img_height": 200, "blocks": [] },
                { "img_path": "pages/001.jpg", "img_width": 100, "img_height": 200, "blocks": [] }
              ]
            }
            """);

        var book = await new MangaSourceIndexer().IndexAsync(
            metadataPath,
            TestContext.Current.CancellationToken);

        book.SourcePath.Should().Be(Path.GetFullPath(metadataPath));
        book.PageRootPath.Should().Be(Path.GetFullPath(temp.Path));
        book.MokuroMetadataPath.Should().Be(Path.GetFullPath(metadataPath));
        book.Pages.Select(page => page.Path).Should()
            .Equal("pages/002.jpg", "pages/001.jpg");
    }

    private static void AddText(ZipArchive archive, string path, string content) =>
        AddEntry(archive, path, Encoding.UTF8.GetBytes(content));

    private static void AddEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path);
        using var output = entry.Open();
        output.Write(content);
    }
}
