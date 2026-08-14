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

    [Fact]
    public void Parse_DoesNotTreatTrailerInsideARegularSeriesNameAsSupplemental()
    {
        var parsed = _parser.Parse(@"D:\fixture\I Live in a Trailer S01E01.mkv");

        parsed.NormalizedTitle.Should().Be("I Live in a Trailer");
        parsed.SpecialKind.Should().Be(ParsedVideoSpecialKind.None);
        parsed.SeasonNumber.Should().Be(1);
        parsed.EpisodeStart.Should().Be(1);
    }

    [Fact]
    public void Parse_RecognizesAnimeReleaseSeasonSuffixAndCompositeAudioTag()
    {
        var parsed = _parser.Parse(
            @"D:\fixture\[DBD-Raws][Re Zero kara Hajimeru Isekai Seikatsu S3][01][1080P][BDRip][HEVC-10bit][FLACx2].mkv",
            @"D:\fixture",
            VideoLibraryMediaType.Anime);

        parsed.NormalizedTitle.Should().Be("Re Zero kara Hajimeru Isekai Seikatsu");
        parsed.SeasonNumber.Should().Be(3);
        parsed.EpisodeStart.Should().Be(1);
        parsed.RemovedReleaseTags.Should().Contain("FLACx2");
    }

    [Theory]
    [InlineData("PV", "[DBD-Raws][作品 S3][PV][01].mkv", ParsedVideoSpecialKind.Preview)]
    [InlineData("menu", "[DBD-Raws][作品 S3][menu][01].mkv", ParsedVideoSpecialKind.Menu)]
    [InlineData("迷你动画", "[DBD-Raws][作品 Break Time][01][1080P][FLAC].mkv", ParsedVideoSpecialKind.Short)]
    public void Parse_UsesReleaseBundleFolderSemantics(
        string folder,
        string name,
        ParsedVideoSpecialKind expected)
    {
        var parsed = _parser.Parse(
            Path.Combine(@"D:\fixture\作品第三季", folder, name),
            @"D:\fixture",
            VideoLibraryMediaType.Anime);

        parsed.SpecialKind.Should().Be(expected);
    }

    [Fact]
    public void BundleClassifier_AssignsNestedExtrasToCanonicalSeriesAndSpecialsSeason()
    {
        const string root = @"D:\fixture";
        var bundle = Path.Combine(root, "ReZero S3 bundle");
        var main = Path.Combine(bundle,
            "[DBD-Raws][Re Zero kara Hajimeru Isekai Seikatsu S3][01][1080P][FLACx2].mkv");
        var preview = Path.Combine(bundle, "PV",
            "[DBD-Raws][Re Zero kara Hajimeru Isekai Seikatsu S3][PV][01][1080P][FLAC].mkv");
        var shortVideo = Path.Combine(bundle, "迷你动画",
            "[DBD-Raws][Re Zero Kara Hajimeru Break Time Emilia Party Struggles][01][1080P][FLAC].mkv");

        var parsed = VideoScanBundleClassifier.Parse(
            [main, preview, shortVideo], root, VideoLibraryMediaType.Anime, _parser);

        parsed[main].NormalizedTitle.Should().Be("Re Zero kara Hajimeru Isekai Seikatsu");
        parsed[main].SeasonNumber.Should().Be(3);
        parsed[preview].NormalizedTitle.Should().Be(parsed[main].NormalizedTitle);
        parsed[preview].SeasonNumber.Should().Be(0);
        parsed[preview].EpisodeTitle.Should().Be("PV 01");
        parsed[shortVideo].NormalizedTitle.Should().Be(parsed[main].NormalizedTitle);
        parsed[shortVideo].SeasonNumber.Should().Be(0);
        parsed[shortVideo].EpisodeTitle.Should().Contain("Break Time Emilia Party Struggles");
        parsed[preview].EpisodeStart.Should().BeNull();
        parsed[shortVideo].EpisodeStart.Should().BeNull();
        parsed[preview].EpisodeTitle.Should().NotBe(parsed[shortVideo].EpisodeTitle);
    }

    [Fact]
    public void BundleClassifier_KeepsSpecialLookingMovieTitleStandalone()
    {
        const string root = @"D:\fixture\Movies";
        var movie = Path.Combine(root, "OVA The Movie (2020).mkv");

        var parsed = VideoScanBundleClassifier.Parse(
            [movie], root, VideoLibraryMediaType.Movie, _parser)[movie];

        parsed.NormalizedTitle.Should().Be("OVA The Movie");
        parsed.Year.Should().Be(2020);
        parsed.SpecialKind.Should().Be(ParsedVideoSpecialKind.None);
        parsed.HasEpisodeEvidence.Should().BeFalse();
        parsed.SeasonNumber.Should().BeNull();
        parsed.EpisodeStart.Should().BeNull();
    }

    [Fact]
    public void BundleClassifier_OrganizesNewShokoRenamedFolderWithSameRules()
    {
        const string root = @"D:\Shoko\Library";
        var show = Path.Combine(root, "Show Name");
        var episode = Path.Combine(show, "Show Name - 01 [anidbid-123].mkv");
        var preview = Path.Combine(show, "Extras", "Show Name PV 01.mkv");

        var parsed = VideoScanBundleClassifier.Parse(
            [episode, preview], root, VideoLibraryMediaType.Anime, _parser);

        parsed[episode].NormalizedTitle.Should().Be("Show Name");
        parsed[episode].EpisodeStart.Should().Be(1);
        parsed[episode].ExternalIds.Should().Contain("anidb", "123");
        parsed[preview].NormalizedTitle.Should().Be("Show Name");
        parsed[preview].SeasonNumber.Should().Be(0);
        parsed[preview].EpisodeStart.Should().BeNull();
        parsed[preview].SpecialKind.Should().Be(ParsedVideoSpecialKind.Preview);
    }

    [Theory]
    [InlineData("作品 S01E02 - 約束 [1080p].mkv", "作品", "約束")]
    [InlineData("作品 S01E02.mkv", "作品", null)]
    public void Parse_SeparatesEpisodeSubtitleFromSeriesIdentity(
        string name,
        string expectedSeries,
        string? expectedEpisodeTitle)
    {
        var parsed = _parser.Parse(Path.Combine(@"D:\fixture", name));

        parsed.NormalizedTitle.Should().Be(expectedSeries);
        parsed.EpisodeTitle.Should().Be(expectedEpisodeTitle);
    }

    [Fact]
    public void Parse_PreservesExplicitSeasonZeroAsSpecial()
    {
        var parsed = _parser.Parse(@"D:\fixture\作品 S00E03 - Bonus.mkv");

        parsed.SeasonNumber.Should().Be(0);
        parsed.EpisodeStart.Should().Be(3);
        parsed.SpecialKind.Should().Be(ParsedVideoSpecialKind.Special);
        parsed.EpisodeTitle.Should().Be("Bonus");
    }

    [Fact]
    public void BundleClassifier_UsesJellyfinShowAndSeasonFolders()
    {
        const string root = @"D:\fixture\Library";
        var episode1 = Path.Combine(root, "作品", "Season 03", "S03E01 - Departure.mkv");
        var episode2 = Path.Combine(root, "作品", "Season 03", "S03E02 - Reunion.mkv");
        var explicitSpecial = Path.Combine(root, "作品", "Specials", "S00E01 - OVA.mkv");
        var preview = Path.Combine(root, "作品", "Trailers", "[Group][作品][PV][01][1080P].mkv");

        var parsed = VideoScanBundleClassifier.Parse(
            [episode1, episode2, explicitSpecial, preview],
            root,
            VideoLibraryMediaType.Auto,
            _parser);

        parsed.Values.Should().OnlyContain(item => item.NormalizedTitle == "作品");
        parsed[episode1].SeasonNumber.Should().Be(3);
        parsed[episode1].EpisodeTitle.Should().Be("Departure");
        parsed[episode2].EpisodeTitle.Should().Be("Reunion");
        parsed[explicitSpecial].SeasonNumber.Should().Be(0);
        parsed[explicitSpecial].EpisodeStart.Should().Be(1);
        parsed[explicitSpecial].EpisodeTitle.Should().Be("OVA");
        parsed[preview].SeasonNumber.Should().Be(0);
        parsed[preview].EpisodeStart.Should().BeNull();
        parsed[preview].SpecialKind.Should().Be(ParsedVideoSpecialKind.Preview);
    }

    [Fact]
    public void BundleClassifier_AttachesDistinctBreakTimeTitlesToOneFolderOwner()
    {
        const string root = @"D:\fixture\Library";
        var main = Path.Combine(root, "作品", "Season 01", "作品 S01E01.mkv");
        var emilia = Path.Combine(root, "作品", "迷你动画",
            "[DBD-Raws][作品 Break Time Emilia Party][01][1080P].mkv");
        var beatrice = Path.Combine(root, "作品", "迷你动画",
            "[DBD-Raws][作品 Break Time Beatrice Lesson][02][1080P].mkv");

        var parsed = VideoScanBundleClassifier.Parse(
            [main, emilia, beatrice], root, VideoLibraryMediaType.Anime, _parser);

        parsed.Values.Should().OnlyContain(item => item.NormalizedTitle == "作品");
        parsed[emilia].EpisodeStart.Should().BeNull();
        parsed[beatrice].EpisodeStart.Should().BeNull();
        parsed[emilia].EpisodeTitle.Should().Contain("Emilia Party");
        parsed[beatrice].EpisodeTitle.Should().Contain("Beatrice Lesson");
        parsed[emilia].EpisodeTitle.Should().NotBe(parsed[beatrice].EpisodeTitle);
    }

    [Fact]
    public void BundleClassifier_DoesNotInventAnEpisodeNumberForSupplementalOnlyFolder()
    {
        const string root = @"D:\fixture\Library";
        var preview = Path.Combine(root, "作品", "Trailers", "作品 PV 01.mkv");

        var parsed = VideoScanBundleClassifier.Parse(
            [preview], root, VideoLibraryMediaType.Auto, _parser);

        parsed[preview].NormalizedTitle.Should().Be("作品");
        parsed[preview].SpecialKind.Should().Be(ParsedVideoSpecialKind.Preview);
        parsed[preview].SeasonNumber.Should().Be(0);
        parsed[preview].EpisodeStart.Should().BeNull();
        parsed[preview].EpisodeEnd.Should().BeNull();
        parsed[preview].AbsoluteEpisodeNumber.Should().BeNull();
        parsed[preview].EpisodeTitle.Should().Be("PV 01");
    }

    [Fact]
    public void BundleClassifier_UsesSourceRootAsShowWhenItContainsSeasonFolders()
    {
        const string root = @"D:\fixture\Root Show";
        var episode1 = Path.Combine(root, "Season 01", "S01E01 - Pilot.mkv");
        var episode2 = Path.Combine(root, "Season 01", "S01E02 - Next.mkv");

        var parsed = VideoScanBundleClassifier.Parse(
            [episode1, episode2], root, VideoLibraryMediaType.Auto, _parser);

        parsed.Values.Should().OnlyContain(item => item.NormalizedTitle == "Root Show");
        parsed.Values.Should().OnlyContain(item => item.SeasonNumber == 1);
    }

    [Fact]
    public void BundleClassifier_DoesNotAttachSiblingExtrasToRootShow()
    {
        const string root = @"D:\fixture\Library";
        var rootEpisode = Path.Combine(root, "Root Show S01E01.mkv");
        var siblingEpisode = Path.Combine(root, "Show B", "Season 01", "Show B S01E01.mkv");
        var siblingPreview = Path.Combine(root, "Show B", "Trailers", "Show B PV 01.mkv");

        var parsed = VideoScanBundleClassifier.Parse(
            [rootEpisode, siblingEpisode, siblingPreview],
            root,
            VideoLibraryMediaType.Auto,
            _parser);

        parsed[rootEpisode].NormalizedTitle.Should().Be("Root Show");
        parsed[siblingEpisode].NormalizedTitle.Should().Be("Show B");
        parsed[siblingPreview].NormalizedTitle.Should().Be("Show B");
        parsed[siblingPreview].NormalizedTitle.Should().NotBe(parsed[rootEpisode].NormalizedTitle);
    }

    [Fact]
    public void BundleClassifier_DoesNotAttachFlatMinoritySupplementalToDominantShow()
    {
        const string root = @"D:\fixture\Library";
        var showA1 = Path.Combine(root, "Show A S01E01.mkv");
        var showA2 = Path.Combine(root, "Show A S01E02.mkv");
        var showA3 = Path.Combine(root, "Show A S01E03.mkv");
        var showB = Path.Combine(root, "Show B S01E01.mkv");
        var showBPreview = Path.Combine(root, "[Group][Show B][PV][01][1080P].mkv");

        var parsed = VideoScanBundleClassifier.Parse(
            [showA1, showA2, showA3, showB, showBPreview],
            root,
            VideoLibraryMediaType.Auto,
            _parser);

        parsed[showA1].NormalizedTitle.Should().Be("Show A");
        parsed[showB].NormalizedTitle.Should().Be("Show B");
        parsed[showBPreview].NormalizedTitle.Should().Be("Show B");
        parsed[showBPreview].EpisodeStart.Should().BeNull();
    }
}
