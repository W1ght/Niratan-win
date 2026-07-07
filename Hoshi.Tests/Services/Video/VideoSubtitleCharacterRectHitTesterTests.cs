using FluentAssertions;
using Hoshi.Services.Video;

namespace Hoshi.Tests.Services.Video;

public class VideoSubtitleCharacterRectHitTesterTests
{
    [Fact]
    public void GetTextPointerOffsetForCharacterIndex_TargetsCharacterBeforeForwardRect()
    {
        var text = "見てください 既に大勢人が並んでます";
        var daiIndex = text.IndexOf("大", StringComparison.Ordinal);

        VideoSubtitleCharacterRectHitTester.GetTextPointerOffsetForCharacterIndex(daiIndex)
            .Should()
            .Be(daiIndex);
    }

    [Fact]
    public void TryResolveCharacterIndex_PrefersRectUnderPointerOverOvershotInsertionOffset()
    {
        var text = "見てください 既に大勢人が並んでます";
        var daiIndex = text.IndexOf("大", StringComparison.Ordinal);
        var seiIndex = text.IndexOf("勢", StringComparison.Ordinal);
        var rects = new[]
        {
            new VideoSubtitleCharacterRect(daiIndex, 100, 0, 36, 42),
            new VideoSubtitleCharacterRect(seiIndex, 136, 0, 36, 42),
        };

        var index = VideoSubtitleCharacterRectHitTester.TryResolveCharacterIndex(
            rects,
            pointX: 118,
            pointY: 21);

        index.Should().Be(daiIndex);
    }

    [Fact]
    public void CreateCharacterHitRectsFromLeadingEdges_MakesEveryCharacterCenterClickable()
    {
        var text = "見てください 既に大勢人が並んでます";
        var leadingEdges = text
            .Select((_, index) => new VideoSubtitleCharacterRect(index, 100 + index * 24, 8, 1, 42))
            .ToArray();

        var hitRects = VideoSubtitleCharacterRectHitTester.CreateCharacterHitRectsFromLeadingEdges(leadingEdges);

        for (var index = 0; index < text.Length; index++)
        {
            var resolved = VideoSubtitleCharacterRectHitTester.TryResolveCharacterIndex(
                hitRects,
                pointX: 100 + index * 24 + 12,
                pointY: 29);

            resolved.Should().Be(index, $"the center of '{text[index]}' should hit character index {index}");
        }
    }

    [Fact]
    public void CreateCharacterHitRectsFromCharacterRects_KeepsActualCharacterOwnership()
    {
        var text = "見てください 既に大勢人が並んでます";
        var daiIndex = text.IndexOf("大", StringComparison.Ordinal);
        var seiIndex = text.IndexOf("勢", StringComparison.Ordinal);
        var characterRects = new[]
        {
            new VideoSubtitleCharacterRect(daiIndex, 100, 8, 24, 42),
            new VideoSubtitleCharacterRect(seiIndex, 124, 8, 24, 42),
        };

        var hitRects = VideoSubtitleCharacterRectHitTester.CreateCharacterHitRectsFromCharacterRects(characterRects);

        VideoSubtitleCharacterRectHitTester.TryResolveCharacterIndex(hitRects, 112, 29)
            .Should()
            .Be(daiIndex);
        VideoSubtitleCharacterRectHitTester.TryResolveCharacterIndex(hitRects, 136, 29)
            .Should()
            .Be(seiIndex);
    }

    [Fact]
    public void CreateCharacterHitRectsFromCharacterRects_StartsNextCharacterAtItsLeadingEdge()
    {
        var text = "見てください 既に大勢人が並んでます";
        var sudeIndex = text.IndexOf("既", StringComparison.Ordinal);
        var niIndex = text.IndexOf("に", sudeIndex, StringComparison.Ordinal);
        var characterRects = new[]
        {
            new VideoSubtitleCharacterRect(sudeIndex, 100, 8, 32, 42),
            new VideoSubtitleCharacterRect(niIndex, 124, 8, 32, 42),
        };

        var hitRects = VideoSubtitleCharacterRectHitTester.CreateCharacterHitRectsFromCharacterRects(
            characterRects,
            containerHeight: 96);

        VideoSubtitleCharacterRectHitTester.TryResolveCharacterIndex(hitRects, 127, 48)
            .Should()
            .Be(niIndex);
        VideoSubtitleCharacterRectHitTester.TryResolveCharacterIndex(hitRects, 123, 48)
            .Should()
            .Be(sudeIndex);
    }

    [Fact]
    public void CreateCharacterHitRectsFromCharacterRects_ExpandsSingleLineToSubtitleBoxHeight()
    {
        var text = "見てください 既に大勢人が並んでます";
        var daiIndex = text.IndexOf("大", StringComparison.Ordinal);
        var characterRects = new[]
        {
            new VideoSubtitleCharacterRect(daiIndex, 264, 0, 36, 42),
        };

        var hitRects = VideoSubtitleCharacterRectHitTester.CreateCharacterHitRectsFromCharacterRects(
            characterRects,
            containerHeight: 96);

        VideoSubtitleCharacterRectHitTester.TryResolveCharacterIndex(hitRects, 282, 48)
            .Should()
            .Be(daiIndex);
    }
}
