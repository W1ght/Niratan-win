using FluentAssertions;
using Hoshi.Models;
using Hoshi.Models.Novel;
using Hoshi.Models.Sasayaki;
using Hoshi.Services.Novels;
using Hoshi.Services.Sasayaki;
using Hoshi.Tests.TestUtils;
using Moq;

namespace Hoshi.Tests.Services.Sasayaki;

public sealed class SasayakiMatchServiceTests
{
    [Fact]
    public async Task MatchAsync_PreservesExistingPlaybackSidecar()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sidecar = new SasayakiSidecarService();
        var playback = new SasayakiPlaybackData
        {
            LastPosition = 21_448.206,
            Delay = 0.35,
            Rate = 1.25,
            AudioBookmark = 42,
        };
        await sidecar.SavePlaybackAsync(temp.Path, playback, ct);
        var sut = await CreateSutAsync(temp, sidecar, ct);

        await sut.MatchAsync(
            CreateBook(temp.Path),
            Path.Combine(temp.Path, "audio.m4b"),
            Path.Combine(temp.Path, "audio.srt"),
            200,
            ct);

        var loaded = await sidecar.LoadPlaybackAsync(temp.Path, ct);
        loaded.LastPosition.Should().Be(21_448.206);
        loaded.Delay.Should().Be(0.35);
        loaded.Rate.Should().Be(1.25);
        loaded.AudioBookmark.Should().Be(42);
    }

    [Fact]
    public async Task MatchAsync_DoesNotCreatePlaybackSidecarWhenNoneExists()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sidecar = new SasayakiSidecarService();
        var sut = await CreateSutAsync(temp, sidecar, ct);

        await sut.MatchAsync(
            CreateBook(temp.Path),
            Path.Combine(temp.Path, "audio.m4b"),
            Path.Combine(temp.Path, "audio.srt"),
            200,
            ct);

        File.Exists(Path.Combine(temp.Path, ISasayakiSidecarService.PlaybackFileName))
            .Should().BeFalse();
        var loaded = await sidecar.LoadPlaybackAsync(temp.Path, ct);
        loaded.LastPosition.Should().Be(0);
        loaded.Delay.Should().Be(0);
        loaded.Rate.Should().Be(1);
        loaded.AudioBookmark.Should().Be(-1);
    }

    private static async Task<SasayakiMatchService> CreateSutAsync(
        TempDirectory temp,
        ISasayakiSidecarService sidecar,
        CancellationToken cancellationToken)
    {
        var chapterPath = Path.Combine(temp.Path, "chapter.xhtml");
        await File.WriteAllTextAsync(
            chapterPath,
            "<html><body><p>星を読む</p></body></html>",
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "audio.srt"),
            "1\n00:00:01,000 --> 00:00:02,000\n星を読む\n",
            cancellationToken);

        var epub = new EpubBook
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
        var parser = new Mock<IEpubParserService>();
        parser.Setup(service => service.Parse("book.epub", temp.Path)).Returns(epub);
        return new SasayakiMatchService(parser.Object, sidecar);
    }

    private static NovelBook CreateBook(string rootPath) => new()
    {
        Id = "book-1",
        Title = "星を読む",
        FilePath = "book.epub",
        ExtractedPath = rootPath,
    };
}
