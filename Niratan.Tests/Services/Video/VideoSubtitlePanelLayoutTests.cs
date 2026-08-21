using FluentAssertions;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class VideoSubtitlePanelLayoutTests
{
    [Fact]
    public void Calculate_AccountsForAutomaticWrappingOfLongJapaneseCue()
    {
        const string text = "明確な医学的な根拠や基準みたいなものはないんですよ。医学的に決定されてるというよりは政治的に決定されてると。え、ちょっと待ってください。消されたりしないですよ。";

        var layout = VideoSubtitlePanelLayout.Calculate(
            text,
            requestedFontSize: 52,
            effectRadius: 10,
            canvasWidth: 1736,
            viewportHeight: 900);

        layout.FontSize.Should().Be(52);
        layout.EstimatedLineCount.Should().BeGreaterThanOrEqualTo(3);
        layout.PanelHeight.Should().BeGreaterThan(220);
    }

    [Fact]
    public void Calculate_PreservesComfortableReserveForShortCue()
    {
        var layout = VideoSubtitlePanelLayout.Calculate(
            "短い字幕",
            requestedFontSize: 52,
            effectRadius: 0,
            canvasWidth: 1200,
            viewportHeight: 900);

        layout.FontSize.Should().Be(52);
        layout.EstimatedLineCount.Should().Be(1);
        layout.PanelHeight.Should().BeApproximately(182.4, 0.001);
    }

    [Fact]
    public void Calculate_ShrinksOnlyTheEffectiveFontWhenCueWouldExceedViewport()
    {
        var text = string.Join('\n', Enumerable.Repeat("非常に長い字幕です。", 12));

        var layout = VideoSubtitlePanelLayout.Calculate(
            text,
            requestedFontSize: 52,
            effectRadius: 10,
            canvasWidth: 900,
            viewportHeight: 360);

        layout.FontSize.Should().BeLessThan(52);
        layout.PanelHeight.Should().BeLessThanOrEqualTo(360);
    }
}
