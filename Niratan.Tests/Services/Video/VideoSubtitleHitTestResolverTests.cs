using FluentAssertions;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public class VideoSubtitleHitTestResolverTests
{
    [Fact]
    public void ResolveCharacterIndex_UsesVisualPositionWhenRichTextInsertionOffsetOvershootsKanji()
    {
        var text = "見てください 既に大勢人が並んでます";
        var clickedIndex = text.IndexOf("大", StringComparison.Ordinal);
        var richTextInsertionOffset = text.IndexOf("人", StringComparison.Ordinal);
        var characterWidth = 24d;
        var pointX = (clickedIndex + 0.5) * characterWidth;

        var index = VideoSubtitleHitTestResolver.ResolveCharacterIndex(
            text,
            richTextInsertionOffset,
            pointX,
            pointY: 18,
            textWidth: text.Length * characterWidth,
            textHeight: 36);

        index.Should().Be(clickedIndex);
        VideoSubtitleLookupTextExtractor.GetQueryAtCharacter(text, index).Should().StartWith("大勢人");
    }

    [Fact]
    public void ResolveCharacterIndex_KeepsInsertionResultWhenVisualPositionHitsWhitespace()
    {
        var text = "見てください 既に大勢人が並んでます";
        var whitespaceIndex = text.IndexOf(' ');
        var nextLookupIndex = text.IndexOf("既", StringComparison.Ordinal);
        var characterWidth = 24d;
        var pointX = (whitespaceIndex + 0.5) * characterWidth;

        var index = VideoSubtitleHitTestResolver.ResolveCharacterIndex(
            text,
            nextLookupIndex,
            pointX,
            pointY: 18,
            textWidth: text.Length * characterWidth,
            textHeight: 36);

        index.Should().Be(nextLookupIndex);
    }
}
