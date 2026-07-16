using System.Xml.Linq;
using FluentAssertions;

namespace Niratan.Tests.Views.Pages;

public sealed class LookupPageShellContractTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "Niratan"));

    private static string ReadProjectFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([ProjectRoot, .. parts]));

    [Fact]
    public void ProfilesSettingsPage_UsesLocalizedActiveProfileComboBoxWithoutInstalledSection()
    {
        var profilesXaml = ReadProjectFile("Views", "Pages", "ProfilesSettingsPage.xaml");
        var chineseResources = ReadProjectFile("Strings", "zh-CN", "Resources.resw");

        XDocument.Parse(profilesXaml);
        XDocument.Parse(chineseResources);
        profilesXaml.Should().Contain("Header=\"Active Profile\"");
        profilesXaml.Should().Contain("AutomationProperties.AutomationId=\"GlobalActiveProfileComboBox\"");
        profilesXaml.Should().Contain("x:Uid=\"ProfilesActiveProfileCard\"");
        profilesXaml.Should().Contain("x:Uid=\"ProfilesSettingsPageTitle\"");
        profilesXaml.Should().Contain("Dictionary, Reader appearance and Anki mining settings follow the active profile.");
        profilesXaml.Should().NotContain("Text=\"Installed\"");
        profilesXaml.Should().NotContain("AutomationProperties.AutomationId=\"ProfilesListView\"");
        chineseResources.Should().Contain("name=\"ProfilesActiveProfileCard.Header\"");
        chineseResources.Should().Contain("<value>当前配置档案</value>");
        chineseResources.Should().Contain("name=\"ProfilesLanguageJapanese\"");
        chineseResources.Should().Contain("name=\"ProfilesLanguageEnglish\"");
    }

    [Fact]
    public void LookupSurfaces_ReactivateTheirNiratanProfileContextWhenFocused()
    {
        var navigationCode = ReadProjectFile("Views", "Pages", "NavigationPage.xaml.cs");
        var readerCode = ReadProjectFile("Views", "Pages", "NovelReaderPage.xaml.cs");
        var videoService = ReadProjectFile("Services", "Video", "VideoPlayerWindowService.cs");

        navigationCode.Should().Contain("mainWindow.Activated += MainWindow_Activated");
        navigationCode.Should().Contain("ViewModel.ActivateGlobalProfileAsync()");
        readerCode.Should().Contain("mainWindow.Activated += MainWindow_Activated");
        readerCode.Should().Contain("ViewModel.ActivateCurrentProfileAsync()");
        videoService.Should().Contain("_window.Activated += OnWindowActivated");
        videoService.Should().Contain("_profileRuntime.ActivateForVideoAsync(video)");
    }

    [Fact]
    public void NavigationPage_DoesNotExposeManualLookupWindowCommandInTitleBar()
    {
        var navigationXaml = ReadProjectFile("Views", "Pages", "NavigationPage.xaml");
        var navigationCode = ReadProjectFile("Views", "Pages", "NavigationPage.xaml.cs");

        XDocument.Parse(navigationXaml);

        navigationXaml.Should().NotContain("AutomationProperties.AutomationId=\"GlobalLookupButton\"");
        navigationXaml.Should().NotContain("x:Uid=\"GlobalLookupButton\"");
        navigationXaml.Should().NotContain("Click=\"OpenGlobalLookup_Click\"");
        navigationCode.Should().NotContain("OpenGlobalLookup_Click");
        navigationCode.Should().NotContain("IGlobalLookupWindowService");

        var zhResources = ReadProjectFile("Strings", "zh-CN", "Resources.resw");
        var enResources = ReadProjectFile("Strings", "en-US", "Resources.resw");
        zhResources.Should().NotContain("GlobalLookupButton.AutomationProperties.Name");
        enResources.Should().NotContain("GlobalLookupButton.AutomationProperties.Name");
    }

    [Fact]
    public void NovelLookupPage_IsNamedLookupWindowInLocalizedShell()
    {
        var lookupPageXaml = ReadProjectFile("Views", "Pages", "NovelLookupPage.xaml");
        var navigationXaml = ReadProjectFile("Views", "Pages", "NavigationPage.xaml");
        var zhResources = ReadProjectFile("Strings", "zh-CN", "Resources.resw");
        var enResources = ReadProjectFile("Strings", "en-US", "Resources.resw");

        XDocument.Parse(lookupPageXaml);
        XDocument.Parse(navigationXaml);

        navigationXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelLookupNavItem\"");
        lookupPageXaml.Should().Contain("x:Uid=\"NovelLookupPageTitle\"");
        zhResources.Should().Contain("<data name=\"NovelLookupNavItem.Content\" xml:space=\"preserve\"><value>查词</value></data>");
        zhResources.Should().Contain("<data name=\"NovelLookupPageTitle.Text\" xml:space=\"preserve\"><value>查词</value></data>");
        enResources.Should().Contain("<data name=\"NovelLookupNavItem.Content\" xml:space=\"preserve\"><value>Lookup Window</value></data>");
        enResources.Should().Contain("<data name=\"NovelLookupPageTitle.Text\" xml:space=\"preserve\"><value>Lookup Window</value></data>");
    }

    [Fact]
    public void NovelLookupPage_PreservesEmbeddedResultsAcrossPagePointerPresses()
    {
        var lookupPageCode = ReadProjectFile("Views", "Pages", "NovelLookupPage.xaml.cs");

        lookupPageCode.Should().NotContain("AddHandler(PointerPressedEvent");
        lookupPageCode.Should().NotContain("RemoveHandler(PointerPressedEvent");
        lookupPageCode.Should().NotContain("OnPagePointerPressed");
    }

    [Fact]
    public void DictionaryPopupOverlay_EnablesCanvasHitTestingWhenRootContentCommits()
    {
        var overlayCode = ReadProjectFile("Views", "Dictionary", "DictionaryPopupOverlay.cs");
        var handlerStart = overlayCode.IndexOf(
            "private void OnRootContentCommitted(",
            StringComparison.Ordinal);
        var handlerEnd = overlayCode.IndexOf(
            "private void OnRootContentCommitAborted(",
            handlerStart,
            StringComparison.Ordinal);

        handlerStart.Should().BeGreaterThanOrEqualTo(0);
        handlerEnd.Should().BeGreaterThan(handlerStart);

        var commitHandler = overlayCode[handlerStart..handlerEnd].ReplaceLineEndings("\n");
        commitHandler.Should().Contain(
            "_rootVisible = true;\n        _canvas.IsHitTestVisible = true;\n        if (_embeddedPanel != null)");
    }
}
