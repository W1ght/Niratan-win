using FluentAssertions;
using Niratan.Models.Settings;

namespace Niratan.Tests.Models.Settings;

public sealed class GalGameSettingsTests
{
    [Fact]
    public void Defaults_MatchTheOverlayEditor()
    {
        var appearance = new GalGameOverlayAppearanceSettings();

        appearance.FontFamily.Should().Be("Yu Gothic UI");
        appearance.FontSize.Should().Be(30);
        appearance.Bold.Should().BeTrue();
        appearance.HorizontalAlignment.Should().Be(GalGameOverlayHorizontalAlignment.Center);
        appearance.VerticalAlignment.Should().Be(GalGameOverlayVerticalAlignment.Center);
        appearance.TextColor.Should().Be("#FFFFFFFF");
        appearance.BackgroundOpacity.Should().Be(0);
        appearance.OutlineWidth.Should().Be(1.6);
        appearance.Padding.Should().Be(20);
        appearance.CornerRadius.Should().Be(14);
    }

    [Fact]
    public void Normalize_ClampsNumbersAndCanonicalizesColors()
    {
        var appearance = new GalGameOverlayAppearanceSettings
        {
            FontFamily = "  Meiryo  ",
            FontSize = 999,
            LetterSpacing = -999,
            LineHeight = double.NaN,
            HorizontalAlignment = (GalGameOverlayHorizontalAlignment)99,
            VerticalAlignment = (GalGameOverlayVerticalAlignment)99,
            TextColor = "112233",
            BackgroundColor = "invalid",
            BackgroundOpacity = double.PositiveInfinity,
            OutlineColor = "#aabbccdd",
            OutlineWidth = 99,
            Padding = -10,
            CornerRadius = 99,
        }.Normalize();

        appearance.FontFamily.Should().Be("Meiryo");
        appearance.FontSize.Should().Be(GalGameOverlayAppearanceSettings.FontSizeMax);
        appearance.LetterSpacing.Should().Be(GalGameOverlayAppearanceSettings.LetterSpacingMin);
        appearance.LineHeight.Should().Be(1);
        appearance.HorizontalAlignment.Should().Be(GalGameOverlayHorizontalAlignment.Center);
        appearance.VerticalAlignment.Should().Be(GalGameOverlayVerticalAlignment.Center);
        appearance.TextColor.Should().Be("#FF112233");
        appearance.BackgroundColor.Should().Be("#FF000000");
        appearance.BackgroundOpacity.Should().Be(0);
        appearance.OutlineColor.Should().Be("#AABBCCDD");
        appearance.OutlineWidth.Should().Be(GalGameOverlayAppearanceSettings.OutlineWidthMax);
        appearance.Padding.Should().Be(GalGameOverlayAppearanceSettings.PaddingMin);
        appearance.CornerRadius.Should().Be(GalGameOverlayAppearanceSettings.CornerRadiusMax);
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var original = new GalGameOverlayAppearanceSettings { FontFamily = "Meiryo" };
        var clone = original.Clone();

        clone.FontFamily = "Yu Gothic UI";

        original.FontFamily.Should().Be("Meiryo");
    }
}
