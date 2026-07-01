using FluentAssertions;
using Hoshi.Models.Novel;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public sealed class ReaderSearchDocumentFactoryTests
{
    [Fact]
    public async Task Create_ReadsChapterHtmlAndMapsTocLabelsToSpineIndexes()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"hoshi-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var firstPath = Path.Combine(root, "chap1.xhtml");
        var secondPath = Path.Combine(root, "chap2.xhtml");
        await File.WriteAllTextAsync(firstPath, "<html><body>一章の猫</body></html>", ct);
        await File.WriteAllTextAsync(secondPath, "<html><body>二章の犬</body></html>", ct);
        var book = new EpubBook
        {
            ContainerDirectory = root,
            Chapters =
            [
                new EpubChapter { Href = firstPath, SpineIndex = 0 },
                new EpubChapter { Href = secondPath, SpineIndex = 1 },
            ],
            Toc =
            [
                new EpubTocItem
                {
                    Label = "第一章",
                    Href = firstPath + "#start",
                    Children =
                    [
                        new EpubTocItem { Label = "第二章", Href = secondPath },
                    ],
                },
            ],
        };

        var document = await ReaderSearchDocumentFactory.CreateAsync(
            book,
            [4, 4],
            ct);

        document.Chapters.Select(c => c.Path).Should().Equal(firstPath, secondPath);
        document.Chapters.Select(c => c.CurrentTotal).Should().Equal(0, 4);
        document.Chapters.Select(c => c.CharacterCount).Should().Equal(4, 4);
        document.HtmlByPath[firstPath].Should().Contain("一章");
        document.HtmlByPath[secondPath].Should().Contain("二章");
        document.Labels[0].Should().Be("第一章");
        document.Labels[1].Should().Be("第一章");
    }
}
