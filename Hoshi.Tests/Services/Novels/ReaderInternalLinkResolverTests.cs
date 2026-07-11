using FluentAssertions;
using Hoshi.Models.Novel;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public sealed class ReaderInternalLinkResolverTests
{
    [Fact]
    public void Resolve_MapsEncodedVirtualHostPathAndFragmentToSpine()
    {
        var root = Path.Combine(Path.GetTempPath(), "hoshi link resolver");
        var chapters = new[]
        {
            Chapter(0, Path.Combine(root, "Text", "chapter 1.xhtml")),
            Chapter(1, Path.Combine(root, "Text", "chapter 2.xhtml")),
        };

        var result = ReaderInternalLinkResolver.Resolve(
            root,
            chapters,
            "https://hoshi-novel-book.local/Text/chapter%202.xhtml#section%202",
            "hoshi-novel-book.local");

        result.Should().Be(new ReaderInternalLinkTarget(1, "section 2"));
    }

    [Theory]
    [InlineData("https://example.com/Text/chapter.xhtml")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://hoshi-novel-book.local/../outside.xhtml")]
    public void Resolve_RejectsExternalUnsafeOrNonSpineTargets(string href)
    {
        var root = Path.Combine(Path.GetTempPath(), "hoshi-link-resolver");
        var chapters = new[] { Chapter(0, Path.Combine(root, "Text", "chapter.xhtml")) };

        ReaderInternalLinkResolver.Resolve(
                root,
                chapters,
                href,
                "hoshi-novel-book.local")
            .Should().BeNull();
    }

    private static EpubChapter Chapter(int index, string href) => new()
    {
        Id = $"chapter-{index}",
        Href = href,
        MediaType = "application/xhtml+xml",
        SpineIndex = index,
    };
}
