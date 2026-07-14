using FluentAssertions;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public class SubtitleParserServiceTests
{
    [Fact]
    public void Parse_DetectsSrtCuesAndContext()
    {
        var parser = new SubtitleParserService();
        var subtitle = """
            1
            00:00:01,250 --> 00:00:03,000
            星がきれいですね。

            2
            00:00:03,500 --> 00:00:05,000
            本当に。
            """;

        var document = parser.Parse(subtitle, ".srt");

        document.Cues.Should().HaveCount(2);
        document.Cues[0].Text.Should().Be("星がきれいですね。");
        document.FindCueAt(TimeSpan.FromMilliseconds(1400))!.Index.Should().Be(0);
        document.GetContext(document.Cues[1]).PreviousText.Should().Be("星がきれいですね。");
    }

    [Fact]
    public void FindPreviousAndNextCue_NavigatesAroundCurrentPosition()
    {
        var parser = new SubtitleParserService();
        var subtitle = """
            1
            00:00:01,000 --> 00:00:02,000
            一つ目。

            2
            00:00:03,000 --> 00:00:04,000
            二つ目。

            3
            00:00:05,000 --> 00:00:06,000
            三つ目。
            """;

        var document = parser.Parse(subtitle, ".srt");

        document.FindPreviousCue(TimeSpan.FromMilliseconds(3500))!.Text.Should().Be("一つ目。");
        document.FindNextCue(TimeSpan.FromMilliseconds(3500))!.Text.Should().Be("三つ目。");
        document.FindNextCue(TimeSpan.FromMilliseconds(4500))!.Text.Should().Be("三つ目。");
    }

    [Fact]
    public void FindCuesAt_ReturnsAllOverlappingCues()
    {
        var parser = new SubtitleParserService();
        var subtitle = """
            1
            00:00:00,000 --> 00:00:10,000
            長い字幕

            2
            00:00:03,000 --> 00:00:04,000
            重なる字幕

            3
            00:00:12,000 --> 00:00:13,000
            後の字幕
            """;

        var document = parser.Parse(subtitle, ".srt");

        document.FindCuesAt(TimeSpan.FromSeconds(3.5))
            .Select(cue => cue.Text)
            .Should()
            .Equal("長い字幕", "重なる字幕");
        document.FindCuesAt(TimeSpan.FromSeconds(11)).Should().BeEmpty();
    }

    [Fact]
    public void Parse_DetectsWebVttCues()
    {
        var parser = new SubtitleParserService();
        var subtitle = """
            WEBVTT

            intro
            00:00:10.000 --> 00:00:11.500 align:start
            もう始まってる。
            """;

        var document = parser.Parse(subtitle, ".vtt");

        document.Cues.Should().ContainSingle();
        document.Cues[0].Start.Should().Be(TimeSpan.FromSeconds(10));
        document.Cues[0].End.Should().Be(TimeSpan.FromMilliseconds(11500));
        document.Cues[0].Text.Should().Be("もう始まってる。");
    }

    [Fact]
    public void Parse_DetectsAssDialogueAndStripsOverrideTags()
    {
        var parser = new SubtitleParserService();
        var subtitle = """
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:01:02.34,0:01:04.56,Default,,0,0,0,,{\i1}行こう\N今すぐ
            """;

        var document = parser.Parse(subtitle, ".ass");

        document.Cues.Should().ContainSingle();
        document.Cues[0].Start.Should().Be(TimeSpan.FromMilliseconds(62340));
        document.Cues[0].End.Should().Be(TimeSpan.FromMilliseconds(64560));
        document.Cues[0].Text.Should().Be("行こう\n今すぐ");
    }

    [Fact]
    public void Parse_DetectsSsaDialogue()
    {
        var parser = new SubtitleParserService();
        var subtitle = """
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,SSA字幕
            """;

        var document = parser.Parse(subtitle, ".ssa");

        document.Cues.Should().ContainSingle();
        document.Cues[0].Text.Should().Be("SSA字幕");
    }

    [Fact]
    public void Parse_AllowsEmptySubtitleFiles()
    {
        var parser = new SubtitleParserService();

        var document = parser.Parse("  \r\n\t", ".srt");

        document.Cues.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ThrowsForMalformedNonEmptySubtitle()
    {
        var parser = new SubtitleParserService();

        var act = () => parser.Parse("not a subtitle", ".srt");

        act.Should().Throw<InvalidDataException>();
    }
}
