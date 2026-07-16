using FluentAssertions;
using Niratan.Models.Novel;
using Niratan.Models.Sasayaki;
using Niratan.Services.Sasayaki;

namespace Niratan.Tests.Services.Sasayaki;

public sealed class SasayakiMatcherTests
{
    [Fact]
    public void ToCodePoints_UsesReaderMatchableCharacters()
    {
        var codePoints = SasayakiMatcher.ToCodePoints("「か……は」　体積の大きい頭の方、");
        var normalized = string.Concat(codePoints.Select(char.ConvertFromUtf32));

        normalized.Should().Be("かは体積の大きい頭の方");
    }

    [Fact]
    public async Task MatchAsync_CountsBodyTextOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var chapterPath = Path.Combine(temp.Path, "chapter.xhtml");
        await File.WriteAllTextAsync(
            chapterPath,
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
            <head><title>化物語　（上）</title></head>
            <body><p>本文</p></body>
            </html>
            """,
            ct);

        var book = new EpubBook
        {
            Chapters =
            [
                new EpubChapter
                {
                    Href = chapterPath,
                    MediaType = "application/xhtml+xml",
                },
            ],
        };
        var cues = new List<SasayakiCue>
        {
            new()
            {
                Id = "0",
                StartTime = 0,
                EndTime = 1,
                Text = "本文",
            },
        };

        var data = await new SasayakiMatcher().MatchAsync(
            book,
            cues);

        var match = data.Matches.Should().ContainSingle().Which;
        match.Id.Should().Be("0");
        match.StartTime.Should().Be(0);
        match.EndTime.Should().Be(1);
        match.Text.Should().Be("本文");
        match.Start.Should().Be(0);
    }

    [Fact]
    public async Task MatchAsync_SkipsShortStarCueBeforeMatchingFollowingSentence()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var chapterPath = Path.Combine(temp.Path, "chapter.xhtml");
        await File.WriteAllTextAsync(
            chapterPath,
            """
            <html><body><p>正しい始まり次の正しい文章あ終わり</p></body></html>
            """,
            ct);

        var data = await new SasayakiMatcher().MatchAsync(
            CreateBook(chapterPath),
            [
                new SasayakiCue { Id = "0", Text = "正しい始まり" },
                new SasayakiCue { Id = "1", Text = "＊あ……" },
                new SasayakiCue { Id = "2", Text = "次の正しい文章" },
            ]);

        data.Matches.Select(match => match.Id).Should().Equal("0", "2");
        data.Unmatched.Should().Be(1);
        data.RequiresMatcherRefresh.Should().BeFalse();
    }

    [Fact]
    public void MatchData_RequiresRefreshWhenLegacyMatchContainsShortStarCue()
    {
        var data = new SasayakiMatchData
        {
            Matches = [new SasayakiMatch { Id = "0", Text = "＊あ……", Length = 1 }],
        };

        data.RequiresMatcherRefresh.Should().BeTrue();
    }

    [Fact]
    public async Task MatchAsync_AnchorsInitialShortHeadingsToBodyCopy()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var contentsPath = Path.Combine(temp.Path, "contents.xhtml");
        var bodyPath = Path.Combine(temp.Path, "body.xhtml");
        const string heading = "第七章青少年期中堅冒険者編プロローグ";
        await File.WriteAllTextAsync(contentsPath, $"<html><body>{heading}</body></html>", ct);
        await File.WriteAllTextAsync(bodyPath, $"<html><body>{heading}森が広がっていた。</body></html>", ct);

        var data = await new SasayakiMatcher().MatchAsync(
            CreateBook(contentsPath, bodyPath),
            [
                new SasayakiCue { Id = "0", Text = "第七章" },
                new SasayakiCue { Id = "1", Text = "青少年期" },
                new SasayakiCue { Id = "2", Text = "中堅冒険者編" },
                new SasayakiCue { Id = "3", Text = "プロローグ" },
                new SasayakiCue { Id = "4", Text = "森が広がっていた。" },
            ]);

        data.Matches.Should().HaveCount(5);
        data.Matches.Should().OnlyContain(match => match.ChapterIndex == 1);
    }

    private static EpubBook CreateBook(params string[] chapterPaths) => new()
    {
        Chapters = chapterPaths
            .Select(path => new EpubChapter
            {
                Href = path,
                MediaType = "application/xhtml+xml",
            })
            .ToList(),
    };

    private sealed class TempBookDirectory : IDisposable
    {
        public TempBookDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
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
