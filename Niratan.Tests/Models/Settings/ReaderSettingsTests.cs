using FluentAssertions;
using Niratan.Enums;
using Niratan.Models.Settings;

namespace Niratan.Tests.Models.Settings;

public sealed class ReaderSettingsTests
{
    [Fact]
    public void Defaults_UseKleeAsReaderFont()
    {
        var settings = new ReaderSettings();

        settings.SelectedFont.Should().Be("'Klee One', 'Yu Mincho', serif");
        settings.FontWeight.Should().Be(400);
        settings.NormalizedFontWeight.Should().Be(400);
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(100, 100)]
    [InlineData(450, 450)]
    [InlineData(900, 900)]
    [InlineData(1000, 900)]
    public void FontWeight_IsNormalizedForReaderCss(int value, int expected)
    {
        var settings = new ReaderSettings { FontWeight = value };

        settings.NormalizedFontWeight.Should().Be(expected);
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
            Theme = ReaderTheme.Custom,
            CustomBackgroundColor = "#123456",
            CustomTextColor = "#ABCDEF",
            CustomInfoColor = "#654321",
        };

        settings.BackgroundColor(Niratan.Enums.ThemeMode.Dark).Should().Be(0xFF123456);
        settings.TextColorCss(Niratan.Enums.ThemeMode.Dark).Should().Be("#ABCDEF");
        settings.InfoColor(Niratan.Enums.ThemeMode.Dark).Should().Be(0xFF654321);
    }

    [Fact]
    public void LegacyThemeFlags_ResolveToUnifiedTheme()
    {
        new ReaderSettings { SepiaMode = true }.EffectiveTheme.Should().Be(ReaderTheme.Sepia);
        new ReaderSettings { SepiaMode = true, UseCustomColors = true }
            .EffectiveTheme.Should().Be(ReaderTheme.Custom);
    }

    [Fact]
    public void Sepia_InvertsOnlyWhenEnabledInDarkMode()
    {
        var settings = new ReaderSettings
        {
            Theme = ReaderTheme.Sepia,
            SepiaInvertInDark = true,
        };

        settings.BackgroundColor(ThemeMode.Light).Should().Be(0xFFF2E2C9);
        settings.TextColorCss(ThemeMode.Light).Should().Be("#332A1B");
        settings.BackgroundColor(ThemeMode.Dark).Should().Be(0xFF18150C);
        settings.TextColorCss(ThemeMode.Dark).Should().Be("#F2E2C9");
        settings.InfoColor(ThemeMode.Dark).Should().Be(0xFFF2E2C9);
        settings.UsesDarkInterface(ThemeMode.Dark).Should().BeTrue();
    }

    [Fact]
    public void SystemTheme_CanUseSepiaForLightContent()
    {
        var settings = new ReaderSettings
        {
            Theme = ReaderTheme.System,
            SystemLightSepia = true,
        };

        settings.BackgroundColor(ThemeMode.Light).Should().Be(0xFFF2E2C9);
        settings.TextColorCss(ThemeMode.Light).Should().Be("#332A1B");
        settings.BackgroundColor(ThemeMode.Dark).Should().Be(0xFF000000);
        settings.TextColorCss(ThemeMode.Dark).Should().Be("#fff");
    }

    [Theory]
    [InlineData(ThemeMode.System, ReaderTheme.System)]
    [InlineData(ThemeMode.Light, ReaderTheme.Light)]
    [InlineData(ThemeMode.Dark, ReaderTheme.Dark)]
    public void LegacyApplicationTheme_MigratesIntoUnifiedTheme(
        ThemeMode appTheme,
        ReaderTheme expected)
    {
        new ReaderSettings().ResolveUnifiedTheme(appTheme).Should().Be(expected);
    }

    [Fact]
    public void UnifiedTheme_ResolvesApplicationInterfaceLikeNiratan()
    {
        new ReaderSettings { Theme = ReaderTheme.Sepia }
            .ResolveInterfaceTheme(ThemeMode.Dark).Should().Be(ThemeMode.Light);
        new ReaderSettings { Theme = ReaderTheme.Sepia, SepiaInvertInDark = true }
            .ResolveInterfaceTheme(ThemeMode.Light).Should().Be(ThemeMode.System);
        new ReaderSettings
            {
                Theme = ReaderTheme.Custom,
                CustomInterfaceTheme = ThemeMode.Dark,
            }
            .ResolveInterfaceTheme(ThemeMode.Light).Should().Be(ThemeMode.Dark);
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
