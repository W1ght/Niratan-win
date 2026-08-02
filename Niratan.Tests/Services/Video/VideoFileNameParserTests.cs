using FluentAssertions;
using Niratan.Models.Video;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class VideoFileNameParserTests
{
    private readonly VideoFileNameParser _parser = new();

    [Theory]
    [InlineData("葬送のフリーレン S01E02.mkv", 1, 2, 2)]
    [InlineData("アンナチュラル 1x03.mp4", 1, 3, 3)]
    [InlineData("薬屋のひとりごと 第１２話.mkv", null, 12, 12)]
    [InlineData("作品 S02E03-E05 [1080p] [HEVC].mkv", 2, 3, 5)]
    public void Parse_RecognizesSeasonEpisodeAndFullWidthPatterns(
        string name,
        int? season,
        int start,
        int end)
    {
        var parsed = _parser.Parse(Path.Combine("D:\\fixture", name));

        parsed.SeasonNumber.Should().Be(season);
        parsed.EpisodeStart.Should().Be(start);
        parsed.EpisodeEnd.Should().Be(end);
        parsed.HasEpisodeEvidence.Should().BeTrue();
    }

    [Theory]
    [InlineData("作品 OVA 01.mkv", ParsedVideoSpecialKind.Ova)]
    [InlineData("作品 OAD.mkv", ParsedVideoSpecialKind.Oad)]
    [InlineData("作品 NCOP.mkv", ParsedVideoSpecialKind.NcOp)]
    [InlineData("ドラマ SP.mkv", ParsedVideoSpecialKind.Special)]
    public void Parse_RecognizesJapaneseSpecialSemantics(string name, ParsedVideoSpecialKind kind) =>
        _parser.Parse(Path.Combine("D:\\fixture", name)).SpecialKind.Should().Be(kind);

    [Fact]
    public void Parse_PreservesExplicitIdsAndMovieEvidence()
    {
        var parsed = _parser.Parse(
            "D:\\fixture\\七人の侍 (1954) [tmdbid-346] [anilistid-123].mkv",
            mediaType: VideoLibraryMediaType.Movie);

        parsed.Year.Should().Be(1954);
        parsed.ExternalIds.Should().Contain("tmdb", "346").And.Contain("anilist", "123");
        parsed.OriginalName.Should().Contain("七人の侍");
    }

    [Fact]
    public void Parse_RemovesReleaseGroupAndCompositeTechnicalBracket()
    {
        var parsed = _parser.Parse(
            @"D:\fixture\[Kamigami] Himouto! Umaru-chan - 08 [1920x1080 x264 AAC Sub(Chs,Cht,Jap)].mkv");

        parsed.NormalizedTitle.Should().Be("Himouto! Umaru chan");
        parsed.AbsoluteEpisodeNumber.Should().Be(8);
        parsed.RemovedReleaseTags.Should().Contain("Kamigami");
        parsed.RemovedReleaseTags.Should().Contain("1920x1080 x264 AAC Sub(Chs,Cht,Jap)");
    }

    [Fact]
    public void Parse_PreservesUnknownBracketContent()
    {
        var parsed = _parser.Parse(@"D:\fixture\作品 S01E02 [Director Cut].mkv");

        parsed.NormalizedTitle.Should().Contain("[Director Cut]");
    }
}
