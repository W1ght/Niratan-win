using FluentAssertions;
using Hoshi.Models.Novel;
using Hoshi.Models.Sasayaki;
using Hoshi.Services.Sasayaki;

namespace Hoshi.Tests.Services.Sasayaki;

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
                Id = 1,
                StartTime = 0,
                EndTime = 1,
                Text = "本文",
            },
        };

        var data = await new SasayakiMatcher().MatchAsync(
            book,
            cues,
            "book",
            "audio.m4b",
            "audio.srt");

        data.Matches.Should().ContainSingle().Which.StartCodePoint.Should().Be(0);
    }

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
