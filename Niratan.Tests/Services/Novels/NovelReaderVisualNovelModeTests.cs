using System.Text.Json;
using FluentAssertions;
using Niratan.Enums;
using Niratan.Models.Settings;
using Niratan.Services.Novels;

namespace Niratan.Tests.Services.Novels;

public sealed class NovelReaderVisualNovelModeTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", ".."));

    [Fact]
    public void ReaderSettings_DefaultToPaginatedAndNormalizeVisualNovelControls()
    {
        var defaults = new ReaderSettings();
        defaults.VisualNovelMode.Should().BeFalse();
        defaults.VisualNovelRevealSpeed.Should().Be(45);
        defaults.VisualNovelScreenMode.Should().Be(VisualNovelScreenMode.Block);
        defaults.VisualNovelSentencesPerScreen.Should().Be(1);

        var invalid = new ReaderSettings
        {
            VisualNovelRevealSpeed = 900,
            VisualNovelSentencesPerScreen = -4,
        };
        invalid.NormalizedVisualNovelRevealSpeed.Should().Be(120);
        invalid.NormalizedVisualNovelSentencesPerScreen.Should().Be(1);
    }

    [Fact]
    public void VisualNovelMode_DisablesHorizontalSpreadAndGeneratesCenteredStageCss()
    {
        var settings = new ReaderSettings
        {
            VisualNovelMode = true,
            VerticalWriting = false,
            TwoColumnHorizontalPages = true,
        };

        settings.UsesTwoColumnHorizontalPages.Should().BeFalse();
        var css = NovelReaderContentStyles.GenerateCss(settings, ThemeMode.Light);
        css.Should().Contain(".niratan-vn-stage");
        css.Should().Contain(".niratan-vn-screen");
        css.Should().Contain("[data-niratan-vn-unrevealed]");
        css.Should().Contain("column-count: auto !important");
    }

    [Fact]
    public void ReaderAssets_InjectTypedVisualNovelSettingsAndBlankSpaceAdvance()
    {
        var page = File.ReadAllText(Path.Combine(
            ProjectRoot, "Niratan", "Views", "Pages", "NovelReaderPage.xaml.cs"));
        var appearance = File.ReadAllText(Path.Combine(
            ProjectRoot, "Niratan", "Views", "Controls", "ReaderAppearanceSettingsContent.xaml"));
        var bridge = File.ReadAllText(Path.Combine(
            ProjectRoot, "Niratan", "Web", "NovelReader", "reader-bridge.js"));
        var selection = File.ReadAllText(Path.Combine(
            ProjectRoot, "Niratan", "Web", "NovelReader", "selection.js"));
        var visualNovel = File.ReadAllText(Path.Combine(
            ProjectRoot, "Niratan", "Web", "NovelReader", "reader-visual-novel.js"));

        page.Should().Contain("window.__niratanVisualNovelSettings");
        page.Should().Contain("reader-visual-novel.js");
        appearance.Should().Contain("ViewModel.VisualNovelMode");
        appearance.Should().Contain("ViewModel.VisualNovelRevealSpeed");
        bridge.Should().Contain("setVisualNovelRevealSpeed");
        selection.Should().Contain("window.niratanVisualNovel.clickAdvance");
        visualNovel.Should().Contain("buildSentenceScreens");
        visualNovel.Should().Contain("completeReveal");
        visualNovel.Should().Contain("patchHighlights");
    }

    [Fact]
    public void RevealSpeedBridgeMessage_IsVersionedAndBounded()
    {
        using var document = JsonDocument.Parse(
            NovelReaderBridgeMessageFactory.CreateSetVisualNovelRevealSpeedMessage(80));
        var root = document.RootElement;

        root.GetProperty("version").GetInt32().Should().Be(1);
        root.GetProperty("type").GetString().Should().Be("setVisualNovelRevealSpeed");
        root.GetProperty("payload").GetProperty("charactersPerSecond").GetInt32().Should().Be(80);
        var action = () => NovelReaderBridgeMessageFactory
            .CreateSetVisualNovelRevealSpeedMessage(121);
        action.Should().Throw<ArgumentOutOfRangeException>();
    }
}
