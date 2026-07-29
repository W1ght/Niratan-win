using FluentAssertions;
using Niratan.Services.Anki;

namespace Niratan.Tests.Services.Anki;

public sealed class AnkiMangaMiningContextTests
{
    [Fact]
    public void CreateMangaPageMediaFilename_UsesContentHashInsteadOfPageBasename()
    {
        var first = AnkiService.CreateMangaPageMediaFilename(
            @"D:\cache\book-a\000001.jpg",
            [1, 2, 3]);
        var second = AnkiService.CreateMangaPageMediaFilename(
            @"D:\cache\book-b\000001.jpg",
            [4, 5, 6]);

        first.Should().StartWith("hoshi_manga_page_").And.EndWith(".jpg");
        second.Should().StartWith("hoshi_manga_page_").And.EndWith(".jpg");
        first.Should().NotBe(second);
    }

    [Fact]
    public void CreateMangaPageMediaFilename_IsStableForSamePageBytes()
    {
        var first = AnkiService.CreateMangaPageMediaFilename(
            @"D:\cache\book\page.webp",
            [1, 2, 3]);
        var second = AnkiService.CreateMangaPageMediaFilename(
            @"E:\different-cache\renamed.webp",
            [1, 2, 3]);

        first.Should().Be(second);
    }
}
