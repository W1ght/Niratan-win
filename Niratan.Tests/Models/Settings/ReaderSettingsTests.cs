using FluentAssertions;
using Niratan.Models.Settings;

namespace Niratan.Tests.Models.Settings;

public sealed class ReaderSettingsTests
{
    [Fact]
    public void Defaults_UseKleeAsReaderFont()
    {
        var settings = new ReaderSettings();

        settings.SelectedFont.Should().Be("'Klee One', 'Yu Mincho', serif");
    }

    [Fact]
    public void Defaults_EnableAllReaderDisplayItems()
    {
        var settings = new ReaderSettings();

        settings.ShowTitle.Should().BeTrue();
        settings.ShowCharacters.Should().BeTrue();
        settings.ShowPercentage.Should().BeTrue();
        settings.ShowStatisticsToggle.Should().BeTrue();
        settings.ShowReadingSpeed.Should().BeTrue();
        settings.ShowReadingTime.Should().BeTrue();
    }

    [Fact]
    public void CustomColors_OverrideReaderThemeColors()
    {
        var settings = new ReaderSettings
        {
            UseCustomColors = true,
            CustomBackgroundColor = "#123456",
            CustomTextColor = "#ABCDEF",
            CustomInfoColor = "#654321",
        };

        settings.BackgroundColor(Niratan.Enums.ThemeMode.Dark).Should().Be(0xFF123456);
        settings.TextColorCss(Niratan.Enums.ThemeMode.Dark).Should().Be("#ABCDEF");
        settings.InfoColor(Niratan.Enums.ThemeMode.Dark).Should().Be(0xFF654321);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public void TwoColumnHorizontalPages_OnlyApplyToHorizontalPagination(
        bool enabled,
        bool vertical,
        bool continuous,
        bool expected)
    {
        var settings = new ReaderSettings
        {
            TwoColumnHorizontalPages = enabled,
            VerticalWriting = vertical,
            ContinuousMode = continuous,
        };

        settings.UsesTwoColumnHorizontalPages.Should().Be(expected);
    }
}
