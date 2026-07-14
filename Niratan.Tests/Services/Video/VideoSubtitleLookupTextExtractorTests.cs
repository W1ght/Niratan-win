using FluentAssertions;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public class VideoSubtitleLookupTextExtractorTests
{
    [Fact]
    public void GetQueryAtCharacter_StartsAtClickedJapaneseCharacter()
    {
        var text = "解説しだした\n开始解说了";
        var index = text.IndexOf("し", StringComparison.Ordinal);

        var query = VideoSubtitleLookupTextExtractor.GetQueryAtCharacter(text, index);

        query.Should().Be("しだした\n开始解说了");
    }

    [Fact]
    public void GetQueryAtCharacter_DoesNotFallBackToSentenceStartForLaterWords()
    {
        var text = "見てください 既に大勢人が並んでます";
        var index = text.IndexOf("大勢", StringComparison.Ordinal);

        var query = VideoSubtitleLookupTextExtractor.GetQueryAtCharacter(text, index);

        query.Should().StartWith("大勢人");
        query.Should().NotStartWith("見て");
    }

    [Fact]
    public void GetQueryAtCharacter_SkipsWhitespaceToNextLookupCharacter()
    {
        var text = "見てください 既に大勢人が並んでます";
        var index = text.IndexOf(' ');

        var query = VideoSubtitleLookupTextExtractor.GetQueryAtCharacter(text, index);

        query.Should().StartWith("既に");
    }

    [Fact]
    public void GetQueryAtInsertionOffset_UsesCharacterBeforeRichTextInsertionPoint()
    {
        var text = "年越しって言ったらコーラだよ";
        var clickedCharacter = text.IndexOf("越", StringComparison.Ordinal);
        var richTextInsertionOffset = clickedCharacter + 1;

        var query = VideoSubtitleLookupTextExtractor.GetQueryAtInsertionOffset(text, richTextInsertionOffset);
        var offset = VideoSubtitleLookupTextExtractor.GetLookupOffsetAtInsertionOffset(text, richTextInsertionOffset);

        query.Should().StartWith("越しって");
        offset.Should().Be(clickedCharacter);
    }
}
