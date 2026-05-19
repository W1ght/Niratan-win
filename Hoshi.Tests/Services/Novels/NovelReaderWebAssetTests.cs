using FluentAssertions;

namespace Hoshi.Tests.Services.Novels;

public class NovelReaderWebAssetTests
{
    private static readonly string ReaderRoot = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "Hoshi",
            "Web",
            "NovelReader"
        )
    );
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(ReaderRoot, "..", "..")
    );

    [Fact]
    public void ReaderHost_DefinesContentSecurityPolicy()
    {
        var html = File.ReadAllText(Path.Combine(ReaderRoot, "reader-host.html"));

        html.Should().Contain("Content-Security-Policy");
        html.Should().Contain("script-src 'self'");
        html.Should().Contain("frame-src blob:");
    }

    [Fact]
    public void ReaderBridge_ExposesHoshiReaderApi()
    {
        var script = File.ReadAllText(Path.Combine(ReaderRoot, "reader-bridge.js"));

        script.Should().Contain("window.hoshiReader");
        script.Should().Contain("paginate");
        script.Should().Contain("calculateProgress");
        script.Should().Contain("restoreProgress");
        script.Should().Contain("buildPaginationMetrics");
        script.Should().Contain("initialize");
    }

    [Fact]
    public void ReaderBridge_UsesHoshiStyleChapterMessages()
    {
        var script = File.ReadAllText(Path.Combine(ReaderRoot, "reader-bridge.js"));

        script.Should().Contain("postToHost(\"readerReady\"");
        script.Should().Contain("postToHost(\"chapterReady\"");
        script.Should().Contain("postToHost(\"pageChanged\"");
        script.Should().Contain("postToHost(\"restoreCompleted\"");
        script.Should().Contain("\"setChapter\"");
        script.Should().Contain("\"restoreProgress\"");
    }

    [Fact]
    public void VendorDirectory_DoesNotExist()
    {
        var vendorPath = Path.Combine(ReaderRoot, "Vendor");
        var foliatePath = Path.Combine(ReaderRoot, "Vendor", "foliate-js");
        var polyfillPath = Path.Combine(
            ReaderRoot,
            "Vendor",
            "construct-style-sheets-polyfill"
        );

        Directory.Exists(vendorPath).Should().BeFalse();
        Directory.Exists(foliatePath).Should().BeFalse();
        Directory.Exists(polyfillPath).Should().BeFalse();
    }

    [Fact]
    public void ReaderPage_LoadsChaptersDirectlyThroughVirtualHostAndNavigateToString()
    {
        var pageCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );

        pageCode.Should().Contain("hoshi-novel-book.local");
        pageCode.Should().Contain("SetVirtualHostNameToFolderMapping");
        pageCode.Should().Contain("CoreWebView2HostResourceAccessKind.Allow");
        pageCode.Should().Contain("NavigateToString");
        pageCode.Should().Contain("InjectReaderAssets");
        pageCode.Should().Contain("LoadChapter");
    }

    [Fact]
    public void ReaderPage_ServesChapterHtmlWithInjectedCssAndJs()
    {
        var pageCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );

        pageCode.Should().Contain("NovelReaderContentStyles.GenerateCss(");
        pageCode.Should().Contain("reader-bridge.js");
        pageCode.Should().Contain("<base href=");
        pageCode.Should().Contain("<style>{css}</style>");
        pageCode.Should().Contain("<script>{js}</script>");
    }

    [Fact]
    public void ReaderBridge_HandlesErrorWithDetails()
    {
        var script = File.ReadAllText(Path.Combine(ReaderRoot, "reader-bridge.js"));

        script.Should().Contain("postToHost(\"error\"");
        script.Should().Contain("window.__hoshiReaderState.error");
    }

    [Fact]
    public void ReaderBridge_NavigatesBetweenPagesAndChapters()
    {
        var script = File.ReadAllText(Path.Combine(ReaderRoot, "reader-bridge.js"));
        var pageCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );

        script.Should().Contain("window.hoshiReaderNavigate");
        script.Should().Contain("ArrowLeft");
        script.Should().Contain("ArrowRight");
        script.Should().Contain("handleNavigate");
        pageCode.Should().Contain("window.hoshiReaderNavigate");
    }

    [Fact]
    public void NovelPages_DefineStableAutomationIdsForUiAutomation()
    {
        var navigationXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NavigationPage.xaml")
        );
        var libraryXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml")
        );
        var readerXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml")
        );

        navigationXaml
            .Should()
            .Contain("AutomationProperties.AutomationId=\"NovelNavItem\"");
        libraryXaml
            .Should()
            .Contain("AutomationProperties.AutomationId=\"ImportNovelButton\"");
        libraryXaml
            .Should()
            .Contain("AutomationProperties.AutomationId=\"NovelBookGridView\"");
        libraryXaml
            .Should()
            .Contain("AutomationProperties.AutomationId=\"{x:Bind AutomationId");
        readerXaml
            .Should()
            .Contain("AutomationProperties.AutomationId=\"NovelReaderBackButton\"");
        readerXaml
            .Should()
            .Contain(
                "AutomationProperties.AutomationId=\"NovelReaderPreviousPageRegion\""
            );
        readerXaml
            .Should()
            .Contain(
                "AutomationProperties.AutomationId=\"NovelReaderNextPageRegion\""
            );
        readerXaml
            .Should()
            .Contain("AutomationProperties.AutomationId=\"NovelWebView\"");
    }

    [Fact]
    public void NovelBookCards_AreInvokableAutomationTargets()
    {
        var libraryXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml")
        );
        var libraryCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml.cs")
        );

        libraryXaml.Should().Contain("<Button");
        libraryXaml.Should().Contain("NovelBookButton_Click");
        libraryCode.Should().Contain("NovelBookButton_Click");
        libraryCode.Should().Contain("OpenNovelCommand.Execute");
    }

    [Fact]
    public void ReaderBridge_ExposesReaderDiagnosticsForAutomation()
    {
        var script = File.ReadAllText(Path.Combine(ReaderRoot, "reader-bridge.js"));

        script.Should().Contain("window.__hoshiReaderState");
        script.Should().Contain("bridgeReady");
        script.Should().Contain("hasRenderedText");
        script.Should().Contain("readerRect");
        script.Should().Contain("contentRect");
        script.Should().Contain("window.hoshiGetDiagnostics");
    }

    [Fact]
    public void ReaderBridge_RecordsHoshiStyleLayoutMetrics()
    {
        var script = File.ReadAllText(Path.Combine(ReaderRoot, "reader-bridge.js"));

        script.Should().Contain("layoutMetrics");
        script.Should().Contain("pageWidth");
        script.Should().Contain("pageHeight");
        script.Should().Contain("devicePixelRatio");
        script.Should().Contain("visualViewportWidth");
        script.Should().Contain("scrollPosition");
        script.Should().Contain("safeInline");
        script.Should().Contain("safeBlock");
        script.Should().Contain("currentPage");
        script.Should().Contain("pageIndex");
        script.Should().Contain("pageCount");
        script.Should().Contain("totalChars");
        script.Should().Contain("minScroll");
        script.Should().Contain("maxScroll");
    }

    [Fact]
    public void ReaderBridge_ReflowsFromLogicalProgressAfterResize()
    {
        var script = File.ReadAllText(Path.Combine(ReaderRoot, "reader-bridge.js"));

        script.Should().Contain("lastProgress");
        script.Should().Contain("window.addEventListener(\"resize\"");
        script.Should().Contain("window.hoshiReader.reflow(progress)");
        script.Should().Contain("currentColumnGap");
        script.Should().Contain("currentSafeInline");
        script.Should().Contain("currentSafeBlock");
        script.Should().Contain("getComputedStyle(document.body).paddingLeft");
        script.Should().Contain("getComputedStyle(document.body).paddingTop");
        script.Should().Contain("pageStep");
    }

    [Fact]
    public void ReaderBridge_AlignsPagesByViewportSizeNotColumnGap()
    {
        var script = File.ReadAllText(Path.Combine(ReaderRoot, "reader-bridge.js"));

        script.Should().Contain("pageStep: function (context)");
        script.Should().Contain("return context.pageSize;");
        script.Should().Contain("Math.floor(window.innerWidth)");
        script.Should().NotContain("window.innerWidth * (window.devicePixelRatio || 1)");
        script.Should().NotContain("context.pageSize + this.currentColumnGap()");
        script.Should().NotContain("pageSize + this.currentColumnGap()");
        script.Should().NotContain("pageSize + this.columnGap");
    }

    [Fact]
    public void ReaderBridge_DoesNotApplyVerticalBottomOverlapToHorizontalText()
    {
        var script = File.ReadAllText(Path.Combine(ReaderRoot, "reader-bridge.js"));
        var stylesCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Novels", "NovelReaderContentStyles.cs")
        );

        script.Should().Contain("bottomOverlap");
        script.Should().Contain("return this.isVertical() ? 22 : 0");
        stylesCode.Should().Contain("int bottomPadding = 0");
        stylesCode.Should().Contain("int columnGap = 0");
        stylesCode.Should().Contain("--reader-safe-inline");
        stylesCode.Should().Contain("--reader-safe-block");
        stylesCode.Should().Contain("--reader-column-gap");
        stylesCode.Should().Contain("column-width: var(--reader-content-width)");
        stylesCode.Should().Contain("column-gap: var(--reader-column-gap)");
        stylesCode.Should().Contain("padding: var(--reader-safe-block) var(--reader-safe-inline)");
    }

    [Fact]
    public void ReaderStyles_UseGridViewportLayoutWithoutFoliateParts()
    {
        var styles = File.ReadAllText(Path.Combine(ReaderRoot, "reader-styles.css"));

        styles.Should().Contain("#reader-view");
        styles.Should().Contain("overflow: hidden");
        styles.Should().Contain("min-width: 0");
        styles.Should().Contain("var(--reader-page-background)");
        styles.Should().NotContain("foliate-view");
        styles.Should().NotContain("contain: size");
    }

    [Fact]
    public void ReaderBridge_PostsChapterReadyStateForAutomation()
    {
        var script = File.ReadAllText(Path.Combine(ReaderRoot, "reader-bridge.js"));

        script.Should().Contain("postToHost(\"chapterReady\"");
        script.Should().Contain("window.__hoshiReaderState");
    }

    [Fact]
    public void ReaderPage_CapturesWebViewPreviewWhenArtifactDirectoryIsConfigured()
    {
        var pageCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );

        pageCode.Should().Contain("HOSHI_NOVEL_READER_ARTIFACT_DIR");
        pageCode.Should().Contain("CapturePreviewAsync");
        pageCode.Should().Contain("reader-state");
        pageCode.Should().Contain("webview-capture");
    }

    [Fact]
    public void NovelReaderContentStyles_GeneratesColumnWidthLayout()
    {
        var stylesCode = File.ReadAllText(
            Path.Combine(
                ProjectRoot,
                "Services",
                "Novels",
                "NovelReaderContentStyles.cs"
            )
        );

        stylesCode.Should().Contain("column-width: var(--reader-content-width)");
        stylesCode.Should().Contain("column-gap: var(--reader-column-gap)");
        stylesCode.Should().Contain("GenerateCss");
        stylesCode.Should().Contain("GenerateScriptTag");
        stylesCode.Should().Contain("writing-mode");
    }

    [Fact]
    public void EpubParserService_ExposesChaptersForNavigation()
    {
        var parserCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Novels", "EpubParserService.cs")
        );

        parserCode.Should().Contain("ParseSpine");
        parserCode.Should().Contain("ParseManifest");
        parserCode.Should().Contain("EpubChapter");
        parserCode.Should().Contain("SpineIndex");
    }

    [Fact]
    public void ReaderViewModel_TracksChapterNavigationState()
    {
        var vmCode = File.ReadAllText(
            Path.Combine(
                ProjectRoot,
                "ViewModels",
                "Pages",
                "NovelReaderPageViewModel.cs"
            )
        );

        vmCode.Should().Contain("CurrentChapterIndex");
        vmCode.Should().Contain("ChapterCount");
        vmCode.Should().Contain("CanGoNext");
        vmCode.Should().Contain("CanGoPrevious");
        vmCode.Should().Contain("SetChapter");
    }
}
