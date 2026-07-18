using FluentAssertions;
using Niratan.Models.Novel;
using Niratan.Services.Novels;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Novels;

public sealed class ReaderImageGalleryServiceTests
{
    [Fact]
    public async Task LoadImagesAsync_UsesSpineOrderAndRecordsReadingPosition()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var content = Directory.CreateDirectory(Path.Combine(temp.Path, "OEBPS")).FullName;
        var text = Directory.CreateDirectory(Path.Combine(content, "Text")).FullName;
        var images = Directory.CreateDirectory(Path.Combine(content, "Images")).FullName;
        var firstImage = Path.Combine(images, "first.jpg");
        var secondImage = Path.Combine(images, "second.png");
        await File.WriteAllBytesAsync(firstImage, [1], ct);
        await File.WriteAllBytesAsync(secondImage, [2], ct);

        var firstChapter = Path.Combine(text, "one.xhtml");
        var secondChapter = Path.Combine(text, "two.xhtml");
        await File.WriteAllTextAsync(
            firstChapter,
            "<html><body>前<img src='../Images/first.jpg'/>中後</body></html>",
            ct);
        await File.WriteAllTextAsync(
            secondChapter,
            "<html><body><svg><image xlink:href='../Images/second.png'/></svg>後</body></html>",
            ct);
        var book = new EpubBook
        {
            ContainerDirectory = content,
            Chapters =
            [
                new EpubChapter { Href = secondChapter, SpineIndex = 1 },
                new EpubChapter { Href = firstChapter, SpineIndex = 0 },
            ],
        };

        var result = await new ReaderImageGalleryService().LoadImagesAsync(book, ct: ct);

        result.Select(image => image.RelativePath).Should().Equal(
            "Images/first.jpg",
            "Images/second.png");
        result[0].SpineIndex.Should().Be(0);
        result[0].ChapterProgress.Should().BeApproximately(1d / 3d, 0.001);
        result[1].SpineIndex.Should().Be(1);
        result[1].ChapterProgress.Should().Be(0);
    }

    [Fact]
    public async Task LoadImagesAsync_RejectsGaijiExternalAndEscapingImagesAndDeduplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var content = Directory.CreateDirectory(Path.Combine(temp.Path, "OEBPS")).FullName;
        var images = Directory.CreateDirectory(Path.Combine(content, "Images")).FullName;
        var chapter = Path.Combine(content, "chapter.xhtml");
        await File.WriteAllBytesAsync(Path.Combine(images, "kept.jpeg"), [1], ct);
        await File.WriteAllBytesAsync(Path.Combine(images, "gaiji.png"), [2], ct);
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "outside.jpg"), [3], ct);
        await File.WriteAllTextAsync(
            chapter,
            """
            <body>
              <img src="Images/kept.jpeg" />
              <img src="Images/kept.jpeg#duplicate" />
              <img class="glyph gaiji" src="Images/gaiji.png" />
              <img src="../outside.jpg" />
              <img src="https://example.com/remote.jpg" />
              <img src="data:image/png;base64,AAAA" />
            </body>
            """,
            ct);
        var book = new EpubBook
        {
            ContainerDirectory = content,
            Chapters = [new EpubChapter { Href = chapter, SpineIndex = 0 }],
        };

        var result = await new ReaderImageGalleryService().LoadImagesAsync(book, ct: ct);

        result.Should().ContainSingle();
        result[0].RelativePath.Should().Be("Images/kept.jpeg");
    }
}
