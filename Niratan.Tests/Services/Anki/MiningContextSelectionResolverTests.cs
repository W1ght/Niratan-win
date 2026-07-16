using FluentAssertions;
using Niratan.Models.Anki;
using Niratan.Services.Anki;

namespace Niratan.Tests.Services.Anki;

public class MiningContextSelectionResolverTests
{
    [Fact]
    public void Apply_JoinsNovelSentencesAndRebasesTargetOffset()
    {
        var source = new AnkiMiningContext
        {
            Sentence = "二つ目の言葉。",
            SentenceOffset = 4,
            DocumentTitle = "小説",
            CoverTag = "<img src=\"cover.jpg\">",
        };
        var selection = new MiningContextSelection(
        [
            new MiningContextSentence("0", "一つ目。"),
            new MiningContextSentence("1", "二つ目の言葉。", 4),
            new MiningContextSentence("2", "三つ目。"),
        ], 1);

        var result = MiningContextSelectionResolver.Apply(
            source,
            selection,
            new MiningContextSelectionRange(0, 1));

        result.Sentence.Should().Be("一つ目。\n二つ目の言葉。");
        result.SentenceOffset.Should().Be("一つ目。\n".Length + 4);
        result.DocumentTitle.Should().Be("小説");
        result.CoverTag.Should().Be("<img src=\"cover.jpg\">");
    }

    [Fact]
    public void Apply_ExpandsVideoCueRangeAndAdjacentSubtitleContext()
    {
        var selection = new MiningContextSelection(
        [
            new MiningContextSentence("0", "前。", MediaRange: new(TimeSpan.Zero, TimeSpan.FromSeconds(1))),
            new MiningContextSentence("1", "現在。", 0, new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2))),
            new MiningContextSentence("2", "次。", MediaRange: new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3))),
            new MiningContextSentence("3", "後。", MediaRange: new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4))),
        ], 1);

        var result = MiningContextSelectionResolver.Apply(
            new AnkiMiningContext { VideoFileName = "episode.mkv" },
            selection,
            new MiningContextSelectionRange(1, 2));

        result.Sentence.Should().Be("現在。\n次。");
        result.VideoSubtitle.Should().Be("現在。\n次。");
        result.VideoCueStart.Should().Be("00:00:01.000");
        result.VideoCueEnd.Should().Be("00:00:03.000");
        result.VideoPreviousSubtitle.Should().Be("前。");
        result.VideoNextSubtitle.Should().Be("後。");
    }
}
