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
    public void ReaderPage_LoadsChaptersDirectlyThroughVirtualHost()
    {
        var pageCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );

        pageCode.Should().Contain("hoshi-novel-book.local");
        pageCode.Should().Contain("SetVirtualHostNameToFolderMapping");
        pageCode.Should().Contain("CoreWebView2HostResourceAccessKind.Allow");
        pageCode.Should().Contain("NovelWebView.CoreWebView2.Navigate(url)");
        pageCode.Should().Contain("OnDomContentLoaded");
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
        pageCode.Should().Contain("selection.js");
        pageCode.Should().Contain("GenerateScriptTag");
        pageCode.Should().Contain("ExecuteScriptAsync(_readerJs)");
        pageCode.Should().Contain("ExecuteScriptAsync(_selectionJs)");
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
        script.Should().Contain("window.__hoshiReaderShortcutBindings");
        script.Should().Contain("shortcutActionForKeyboardEvent");
        script.Should().Contain("BracketLeft");
        script.Should().Contain("BracketRight");
        script.Should().Contain("\"reader.previousPage\"");
        script.Should().Contain("\"reader.nextPage\"");
        script.Should().Contain("handleNavigate");
        script.Should().Contain("postToHost(\"shortcut\"");
        pageCode.Should().Contain("BuildReaderWebShortcutBindingsJson");
        pageCode.Should().Contain("window.__hoshiReaderShortcutBindings");
        pageCode.Should().Contain("case \"shortcut\":");
        pageCode.Should().Contain("result == \"limit\"");
        pageCode.Should().Contain("LoadChapter(ViewModel.CurrentChapterIndex + 1)");
        pageCode.Should().Contain("LoadChapter(ViewModel.CurrentChapterIndex - 1)");
    }

    [Fact]
    public void ReaderPage_TreatsSasayakiAutoScrollAsPageTurnForStatistics()
    {
        var script = File.ReadAllText(Path.Combine(ReaderRoot, "reader-bridge.js"));
        var pageCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );

        script.Should().Contain("var beforeProgress = window.hoshiReader.calculateProgress()");
        script.Should().Contain("var afterProgress = window.hoshiReader.calculateProgress()");
        script.Should().Contain("return Math.abs(afterProgress - beforeProgress) > 0.0001 ? afterProgress : null");

        pageCode.Should().Contain("TryApplySasayakiAutoScrollProgress");
        pageCode.Should().Contain("StartStatisticsForAutostart(StatisticsAutostartMode.PageTurn)");
        pageCode.Should().Contain("LoadChapterForSasayakiAutoScroll");
        pageCode.Should().Contain("if (CurrentSasayakiSettings.AutoScroll)");
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
            .Contain("AutomationProperties.AutomationId=\"NovelShelfSectionsControl\"");
        libraryXaml
            .Should()
            .Contain("AutomationProperties.AutomationId=\"NovelUnshelvedBooksRepeater\"");
        libraryXaml
            .Should()
            .Contain("AutomationProperties.AutomationId=\"{x:Bind AutomationId");
        readerXaml
            .Should()
            .Contain("AutomationProperties.AutomationId=\"NovelReaderBackButton\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderChapterButton\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderAppearanceButton\"");
        readerXaml.Should().NotContain("NovelReaderPreviousPageRegion");
        readerXaml.Should().NotContain("NovelReaderNextPageRegion");
        readerXaml
            .Should()
            .Contain("AutomationProperties.AutomationId=\"NovelWebView\"");
    }

    [Fact]
    public void NavigationPage_ExposesLogsAsSettingsLevelFooterItems()
    {
        var navigationXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NavigationPage.xaml")
        );

        navigationXaml.Should().Contain("IsSettingsVisible=\"False\"");
        navigationXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsNavItem\"");
        navigationXaml.Should().Contain("Tag=\"Hoshi.Views.Pages.SettingsPage\"");
        navigationXaml.Should().Contain("AutomationProperties.AutomationId=\"NormalLogsNavItem\"");
        navigationXaml.Should().Contain("AutomationProperties.AutomationId=\"ErrorLogsNavItem\"");
        navigationXaml.Should().Contain("Tag=\"Hoshi.Views.Pages.NormalLogsPage\"");
        navigationXaml.Should().Contain("Tag=\"Hoshi.Views.Pages.ErrorLogsPage\"");
    }

    [Fact]
    public void DictionaryPopupOverlay_IsHiddenUntilDetailsAreOpened()
    {
        var popupCss = File.ReadAllText(
            Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.css")
        );

        popupCss.Should().Contain(".overlay");
        popupCss.Should().Contain("display: none");
        popupCss.Should().Contain(".overlay-close");
    }

    [Fact]
    public void DictionaryPopup_UsesPanningIndicatorScrollbarChrome()
    {
        var popupCss = File.ReadAllText(
            Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.css")
        );
        var popupJs = File.ReadAllText(
            Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.js")
        );

        popupCss.Should().Contain("--popup-scrollbar-size: var(--popup-space-8)");
        popupCss.Should().Contain("::-webkit-scrollbar-button");
        popupCss.Should().Contain("display: none");
        popupCss.Should().Contain("::-webkit-scrollbar-track");
        popupCss.Should().Contain("background: transparent");
        popupCss.Should().Contain("::-webkit-scrollbar-thumb");
        popupCss.Should().Contain("background-color: transparent");
        popupCss.Should().Contain("html.popup-scroll-active::-webkit-scrollbar-thumb");
        popupCss.Should().Contain(".overlay.popup-scroll-active::-webkit-scrollbar-thumb");
        popupCss.Should().Contain("--popup-scrollbar-thumb-active");

        popupJs.Should().Contain("setPopupScrollIndicatorActive(event.target)");
        popupJs.Should().Contain("popup-scroll-active");
        popupJs.Should().Contain("capture: true");
    }

    [Fact]
    public void DictionaryPopup_WebDocumentUsesOpaqueHostSurface()
    {
        var popupCss = File.ReadAllText(
            Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.css")
        );
        popupCss.Should().MatchRegex(
            @"(?s)html,\s*body\s*\{[^}]*background-color:\s*var\(--background-color\)\s*!important;");
        popupCss.Should().NotContain("background-color: transparent !important");
        popupCss.Should().MatchRegex(
            @"(?s)#popup-viewport\s*\{[^}]*overflow-y:\s*auto;[^}]*background-color:\s*var\(--background-color\);");
        popupCss.Should().MatchRegex(
            @"(?s)::-webkit-scrollbar\s*\{[^}]*background:\s*transparent;");
        popupCss.Should().MatchRegex(
            @"(?s)::-webkit-scrollbar-track,[^{]*::-webkit-scrollbar-corner\s*\{[^}]*background:\s*transparent;");
    }

    [Fact]
    public void DictionaryPopup_UsesNiratanStyleTwoColumnDictionaryCards()
    {
        var popupCss = File.ReadAllText(
            Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.css")
        );
        var popupJs = File.ReadAllText(
            Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.js")
        );

        popupCss.Should().Contain("--popup-dictionary-card-gap");
        popupCss.Should().Contain(".glossary-sections");
        popupCss.Should().Contain(".glossary-sections:not(.single-section)");
        popupCss.Should().Contain(".glossary-sections.single-section");
        popupCss.Should().Contain("border: 1px solid var(--popup-card-border-color)");
        popupCss.Should().Contain("box-shadow");

        popupJs.Should().Contain("function layoutDictionaryColumns()");
        popupJs.Should().Contain("ResizeObserver");
        popupJs.Should().Contain("var glossarySections = el('div', { className: 'glossary-sections' });");
        popupJs.Should().Contain("glossarySections.classList.add('single-section')");
        popupJs.Should().Contain("glossarySections.appendChild(createGlossarySection(");
        popupJs.Should().Contain("layoutDictionaryColumns();");
    }

    [Fact]
    public void DictionaryPopup_UsesDesktopReferenceDictionaryCards()
    {
        var popupCss = File.ReadAllText(
            Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.css")
        );

        popupCss.Should().Contain("--popup-card-border-color: rgba(0, 0, 0, 0.14);");
        popupCss.Should().Contain("--popup-card-inner-highlight: rgba(255, 255, 255, 0.34);");
        popupCss.Should().Contain("--popup-card-shadow-color: rgba(0, 0, 0, 0.10);");
        popupCss.Should().MatchRegex(
            @"(?s)\.glossary-group\s*\{[^}]*border:\s*1px solid var\(--popup-card-border-color\);[^}]*border-radius:\s*var\(--popup-space-8\);");
        popupCss.Should().Contain("--popup-card-border-color: rgba(255, 255, 255, 0.18);");
        popupCss.Should().Contain("inset 0 0 0 1px var(--popup-card-inner-highlight)");
        popupCss.Should().Contain("0 1px 2px var(--popup-card-shadow-color)");
        popupCss.Should().MatchRegex(
            @"(?s)html\[data-hoshi-color-scheme=""light""\],\s*html\[data-hoshi-color-scheme=""light""\] body\s*\{[^}]*--popup-card-border-color:\s*rgba\(0, 0, 0, 0\.14\);[^}]*--popup-card-inner-highlight:\s*rgba\(255, 255, 255, 0\.34\);[^}]*--popup-card-shadow-color:\s*rgba\(0, 0, 0, 0\.10\);[^}]*\}");
    }

    [Fact]
    public void DictionaryPopupActionBar_UsesNiratanHistoryContract()
    {
        var popupCode = File.ReadAllText(Path.Combine(
            ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs"));
        var overlayCode = File.ReadAllText(Path.Combine(
            ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs"));
        var popupJs = File.ReadAllText(Path.Combine(
            ProjectRoot, "Web", "DictionaryPopup", "popup.js"));

        popupCode.Should().Contain("new CommandBar");
        popupCode.Should().Contain("PopupActionBar");
        popupCode.Should().Contain("NavigateBackAsync");
        popupCode.Should().Contain("NavigateForwardAsync");
        popupCode.Should().Contain("case \"navigationState\"");
        overlayCode.Should().Contain("DictionaryPopupRedirectMode.InPlace");
        popupJs.Should().Contain("window.hoshiRedirectResults");
        popupJs.Should().Contain("postNavigationState");
        popupJs.Should().Contain("canGoBack: backStack.length > 0");
        popupJs.Should().Contain("canGoForward: forwardStack.length > 0");
    }

    [Fact]
    public void DictionaryPopupHide_KeepsWebView2InVisualTree()
    {
        var popupCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs")
        );
        popupCode.Should().NotContain("VisualRoot.Visibility = Visibility.Collapsed;");
        popupCode.Should().NotContain("HideWebContent(");
        popupCode.Should().Contain("VisualRoot.Opacity = 0;");
        popupCode.Should().Contain("VisualRoot.IsHitTestVisible = false;");
    }

    [Fact]
    public void GlobalLookupPopupWindow_ReusesDictionaryPopupOverlayWithoutManualSearchUi()
    {
        var popupXamlPath = Path.Combine(ProjectRoot, "Views", "Dictionary", "GlobalLookupPopupWindow.xaml");
        var popupCodePath = Path.Combine(ProjectRoot, "Views", "Dictionary", "GlobalLookupPopupWindow.xaml.cs");
        var serviceCodePath = Path.Combine(ProjectRoot, "Services", "Dictionary", "GlobalLookupPopupService.cs");
        var appCode = File.ReadAllText(Path.Combine(ProjectRoot, "App.xaml.cs"));

        File.Exists(popupXamlPath).Should().BeTrue();
        File.Exists(popupCodePath).Should().BeTrue();
        File.Exists(serviceCodePath).Should().BeTrue();

        var popupXaml = File.ReadAllText(popupXamlPath);
        var popupCode = File.ReadAllText(popupCodePath);
        var serviceCode = File.ReadAllText(serviceCodePath);

        popupXaml.Should().Contain("x:Class=\"Hoshi.Views.Dictionary.GlobalLookupPopupWindow\"");
        popupXaml.Should().Contain("x:Name=\"DictionaryOverlayCanvas\"");
        popupXaml.Should().NotContain("<TextBox");
        popupXaml.Should().NotContain("LookupQueryBox");
        popupXaml.Should().NotContain("GlobalLookupSearchButton");
        popupXaml.Should().NotContain("<ListView");
        popupXaml.Should().NotContain("<ListBox");
        popupXaml.Should().NotContain("ItemsRepeater");

        popupCode.Should().Contain("DictionaryPopupOverlay");
        popupCode.Should().Contain("UseCanvas(DictionaryOverlayCanvas)");
        popupCode.Should().Contain("ShowLookupAsync(");
        popupCode.Should().Contain("Hoshi Lookup Popup");
        popupCode.Should().Contain("VirtualKey.Escape");
        popupCode.Should().Contain("Activated +=");
        popupCode.Should().Contain("WindowActivationState.Deactivated");

        serviceCode.Should().Contain("DictionaryPopupRequestService");
        serviceCode.Should().Contain("CreateAsync(query, traceId: $\"global-popup-{Guid.NewGuid():N}\"");
        serviceCode.Should().Contain("GlobalLookupPopupWindow");

        appCode.Should().Contain("IGlobalLookupPopupService, GlobalLookupPopupService");
    }

    [Fact]
    public void GlobalLookupPopupWindow_UsesBorderlessPopupSizedHostForNakedPopup()
    {
        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs")
        );
        var popupCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "GlobalLookupPopupWindow.xaml.cs")
        );
        var serviceCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Dictionary", "GlobalLookupPopupService.cs")
        );
        var placementCodePath = Path.Combine(
            ProjectRoot,
            "Services",
            "Dictionary",
            "GlobalLookupPopupWindowPlacement.cs"
        );

        File.Exists(placementCodePath).Should().BeTrue();

        overlayCode.Should().Contain("SetRootReadyOpacity(");
        overlayCode.Should().Contain("GetRootPopupBounds()");
        overlayCode.Should().Contain("MoveRootPopupToOrigin()");
        popupCode.Should().Contain("SetRootReadyOpacity(1)");
        popupCode.Should().Contain("ApplyPopupSizedHostSurface(request.Theme)");
        popupCode.Should().Contain("UseCanvas(DictionaryOverlayCanvas)");
        popupCode.Should().Contain("UseStandaloneWindowVisuals()");
        popupCode.Should().Contain("overlay.ShowLookupAsync(");
        popupCode.Should().Contain("anchorPoint");
        popupCode.Should().Contain("GetRootPopupBounds()");
        popupCode.Should().Contain("MoveRootPopupToOrigin()");
        popupCode.Should().Contain("RasterizationScale");
        popupCode.Should().NotContain("UseNakedFloatingWindowVisuals()");
        popupCode.Should().NotContain("ApplyHostSurface(request.Theme)");
        popupCode.Should().Contain("ExtendsContentIntoTitleBar = true");
        popupCode.Should().Contain("_desktopAcrylicThinBackdrop = DictionaryPopupMaterial.TryApplyDesktopAcrylicThin(this, RootGrid)");
        popupCode.Should().NotContain("SystemBackdrop = null");
        popupCode.Should().Contain("ApplyBorderlessHostChrome(");
        popupCode.Should().Contain("ApplyNativeBorderlessHostStyles(");
        popupCode.Should().Contain("ApplyDwmBorderlessChrome(");
        popupCode.Should().Contain("ApplyRoundedHostRegion(");
        popupCode.Should().Contain("ClearHostRegion(");
        popupCode.Should().Contain("DwmSetWindowAttribute");
        popupCode.Should().Contain("DwmwaNcRenderingPolicy");
        popupCode.Should().Contain("DwmNcRenderingPolicyDisabled");
        popupCode.Should().Contain("DwmwaWindowCornerPreference");
        popupCode.Should().Contain("DwmWindowCornerPreferenceDoNotRound");
        popupCode.Should().Contain("DwmwaBorderColor");
        popupCode.Should().Contain("ToColorRef(hostSurfaceColor)");
        popupCode.Should().Contain("GwlStyle");
        popupCode.Should().Contain("WsBorder");
        popupCode.Should().Contain("WsDlgFrame");
        popupCode.Should().Contain("WsPopup");
        popupCode.Should().Contain("WsExClientEdge");
        popupCode.Should().Contain("WsExStaticEdge");
        popupCode.Should().Contain("WsExToolWindow");
        popupCode.Should().Contain("WsExNoActivate");
        popupCode.Should().Contain("PopupCornerRadiusDip = 8");
        popupCode.Should().Contain("CreateRoundRectRgn(");
        popupCode.Should().Contain("SetWindowRgn(");
        popupCode.Should().Contain("SetWindowPos(");
        popupCode.Should().Contain("SwpFrameChanged");
        popupCode.Should().NotContain("DwmColorNone");

        serviceCode.Should().Contain("StagingSize = new(720, 560)");
        serviceCode.Should().Contain("ResolveStagingRect(workArea, StagingSize)");
        serviceCode.Should().Contain("window.ClearHostRegion()");
        serviceCode.Should().Contain("window.AppWindow.MoveAndResize(stagingRect)");
        serviceCode.Should().Contain("window.Activate()");
        serviceCode.Should().Contain("ApplyBorderlessHostChrome()");
        serviceCode.Should().Contain("cursorPoint.X - workArea.X");
        serviceCode.Should().Contain("cursorPoint.Y - workArea.Y");
        serviceCode.Should().Contain("window.GetRootPopupBounds()");
        serviceCode.Should().Contain("window.RasterizationScale");
        serviceCode.Should().Contain("ResolveFinalRect(");
        serviceCode.Should().Contain("window.MoveRootPopupToOrigin()");
        serviceCode.Should().Contain("window.AppWindow.MoveAndResize(finalRect)");
        serviceCode.Should().NotContain("AppWindow.ResizeClient");
        serviceCode.Should().Contain("window.ApplyRoundedHostRegion()");
        serviceCode.Should().NotContain("GetDpiScaleForPoint(cursorPoint)");
        serviceCode.Should().Contain("ReferenceEquals(window, _window)");
    }

    [Fact]
    public void DictionaryPopupOverlay_GuardsConcurrentPrewarmForGlobalPopupWindow()
    {
        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs")
        );
        var popupCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "GlobalLookupPopupWindow.xaml.cs")
        );

        overlayCode.Should().Contain("Task? _prewarmTask");
        overlayCode.Should().Contain("PrewarmCoreAsync(");
        overlayCode.Should().Contain("await _prewarmTask");

        popupCode.Should().NotContain("_ = overlay.PrewarmAsync(RootGrid.XamlRoot);");
    }

    [Fact]
    public void SettingsPage_UsesVisibilityConvertersForBooleanVisibilityBindings()
    {
        var settingsXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "SettingsPage.xaml")
        );
        var settingsCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "SettingsPage.xaml.cs")
        );
        var appearancePageXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "ReaderAppearanceSettingsPage.xaml")
        );
        var appearanceContentXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Controls", "ReaderAppearanceSettingsContent.xaml")
        );
        var appearanceDialogXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dialogs", "ReaderAppearanceDialog.xaml")
        );
        var appCode = File.ReadAllText(Path.Combine(ProjectRoot, "App.xaml.cs"));
        var readerSettingsCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Models", "Settings", "ReaderSettings.cs")
        );
        var settingsViewModelCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "ViewModels", "Pages", "SettingsPageViewModel.cs")
        );
        var readerPageCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var bridgeScript = File.ReadAllText(Path.Combine(ReaderRoot, "reader-bridge.js"));
        var enResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw")
        );
        var zhResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw")
        );

        settingsXaml.Should().Contain("x:Name=\"SettingsSecondaryNavigationView\"");
        settingsXaml.Should().Contain("x:Name=\"SettingsContentFrame\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsAppearanceNavItem\"");
        settingsXaml.Should().NotContain("Visibility=\"{x:Bind ViewModel.IsDictionaryListEmpty, Mode=OneWay}\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsDictionariesNavItem\"");
        settingsXaml.Should().NotContain("Shift Hover Delay");
        settingsXaml.Should().Contain("PaneTitle=\"Settings\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsReportIssueNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsProfilesNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsVideoNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsKeyboardShortcutsNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsGameControllerNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsSyncNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsBackupNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsAudioNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsSasayakiNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsStatisticsNavItem\"");
        settingsXaml.Should().Contain("Tag=\"Hoshi.Views.Pages.ReaderAppearanceSettingsPage\"");
        settingsCode.Should().Contain("SettingsContentFrame.Navigate(pageType, SettingsNavigationMode.Embedded)");
        settingsCode.Should().Contain("SettingsNavigationMode.Embedded");
        settingsCode.Should().NotContain("Navigate(typeof(AdvancedSettingsPage))");

        appearancePageXaml.Should().Contain("AutomationProperties.AutomationId=\"ReaderAppearanceBackButton\"");
        appearancePageXaml.Should().Contain("ReaderAppearanceSettingsContent");
        appearanceDialogXaml.Should().Contain("ReaderAppearanceSettingsContent");
        appearanceDialogXaml.Should().Contain("x:Uid=\"ReaderAppearanceDialog\"");
        appearanceDialogXaml.Should().NotContain("Title=\"Reader Appearance\"");
        appearanceDialogXaml.Should().NotContain("PrimaryButtonText=\"Done\"");
        appearanceContentXaml.Should().Contain("BooleanToVisibilityConverter");
        appearanceContentXaml.Should().NotContain("Shift Hover Delay");
        appearanceContentXaml.Should().NotContain("Visibility=\"{x:Bind ViewModel.IsSystemSepiaLightVisible, Mode=OneWay}\"");
        appearanceContentXaml.Should().NotContain("Visibility=\"{x:Bind ViewModel.IsSepiaInvertVisible, Mode=OneWay}\"");
        appearanceContentXaml.Should().NotContain("Visibility=\"{x:Bind ViewModel.IsSwipeDistanceVisible, Mode=OneWay}\"");
        appearanceContentXaml.Should().NotContain("Visibility=\"{x:Bind ViewModel.IsLineHeightVisible, Mode=OneWay}\"");
        appearanceContentXaml.Should().NotContain("Visibility=\"{x:Bind ViewModel.IsCharacterSpacingVisible, Mode=OneWay}\"");
        appearanceContentXaml.Should().NotContain("Visibility=\"{x:Bind ViewModel.IsProgressPositionVisible, Mode=OneWay}\"");
        appearanceContentXaml.Should().Contain("x:Uid=\"ReaderMouseWheelPageTurnCard\"");
        appearanceContentXaml.Should().Contain("AutomationProperties.AutomationId=\"ReaderMouseWheelPageTurnToggle\"");
        appearanceContentXaml.Should().Contain("IsOn=\"{x:Bind ViewModel.MouseWheelPageTurn, Mode=TwoWay}\"");
        readerSettingsCode.Should().Contain("bool MouseWheelPageTurn { get; set; } = true");
        settingsViewModelCode.Should().Contain("MouseWheelPageTurn");
        settingsViewModelCode.Should().Contain("ApplyReaderSetting(s => s.MouseWheelPageTurn, value)");
        readerPageCode.Should().Contain("registerWheelNavigation");
        readerPageCode.Should().Contain("!readerSettings.Current.ContinuousMode && readerSettings.Current.MouseWheelPageTurn");
        bridgeScript.Should().Contain("registerWheelNavigation: function");
        bridgeScript.Should().Contain("hoshiWheelNavigationEnabled");
        bridgeScript.Should().Contain("event.deltaY > 0 ? \"forward\" : \"backward\"");
        bridgeScript.Should().Contain("handleNavigate(direction)");
        bridgeScript.Should().Contain("isIgnoredWheelTarget");
        bridgeScript.Should().Contain("window.getSelection()?.isCollapsed === false");
        enResources.Should().Contain("ReaderMouseWheelPageTurnCard.Header");
        zhResources.Should().Contain("ReaderMouseWheelPageTurnCard.Header");
        foreach (var resources in new[] { enResources, zhResources })
        {
            resources.Should().Contain("SettingsReaderSectionHeader.Content");
            resources.Should().Contain("SettingsLibrarySectionHeader.Content");
            resources.Should().Contain("SettingsShortcutsControlsSectionHeader.Content");
            resources.Should().Contain("SettingsSyncDataSectionHeader.Content");
            resources.Should().Contain("SettingsSupportSectionHeader.Content");
            resources.Should().NotContain("SettingsReaderSectionHeader.Text");
            resources.Should().NotContain("SettingsSupportSectionHeader.Text");
        }

        appCode.Should().NotContain("ReaderAppearanceViewModel");

        var dictionaryXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "DictionarySettingsPage.xaml")
        );
        dictionaryXaml.Should().Contain("AutomationProperties.AutomationId=\"ImportDictionaryButton\"");
        dictionaryXaml.Should().Contain("AutomationProperties.Name=\"Import Dictionary\"");
        dictionaryXaml.Should().Contain("AutomationProperties.AutomationId=\"ScanNonJapaneseTextToggle\"");
        dictionaryXaml.Should().Contain("IsOn=\"{x:Bind ViewModel.ScanNonJapaneseText, Mode=TwoWay}\"");
        dictionaryXaml.Should().Contain("AutomationProperties.AutomationId=\"DictionaryTypeTabs\"");
        dictionaryXaml.Should().NotContain("ItemsSource=\"{x:Bind ViewModel.AvailableDictionaryTypes}\"");
        dictionaryXaml.Should().Contain("DictionaryType_Checked");
        dictionaryXaml.Should().Contain("AutomationProperties.AutomationId=\"DictionaryMaxResultsIncreaseButton\"");
        dictionaryXaml.Should().Contain("AutomationProperties.AutomationId=\"DictionaryScanLengthIncreaseButton\"");
        dictionaryXaml.Should().Contain("AutomationProperties.AutomationId=\"DictionaryCollapseModeComboBox\"");
        dictionaryXaml.Should().Contain("AutomationProperties.AutomationId=\"DictionaryCompactGlossariesToggle\"");
        dictionaryXaml.Should().Contain("AutomationProperties.AutomationId=\"DictionaryShowExpressionTagsToggle\"");
        dictionaryXaml.Should().Contain("AutomationProperties.AutomationId=\"DictionaryHarmonicFrequencyToggle\"");
        dictionaryXaml.Should().Contain("AutomationProperties.AutomationId=\"DictionaryDeduplicatePitchToggle\"");
        dictionaryXaml.Should().Contain("AutomationProperties.AutomationId=\"DictionaryCompactPitchToggle\"");
        dictionaryXaml.Should().Contain("AutomationProperties.AutomationId=\"InstalledDictionaryList\"");
        dictionaryXaml.Should().Contain("CanDragItems=\"True\"");
        dictionaryXaml.Should().Contain("CanReorderItems=\"True\"");
        dictionaryXaml.Should().Contain("DragItemsCompleted=\"DictionaryList_DragItemsCompleted\"");
        dictionaryXaml.Should().Contain("CornerRadius=\"8\"");
        dictionaryXaml.Should().NotContain("Header=\"Dictionary Order\"");
    }

    [Fact]
    public void PopupAppearanceSettings_AreOwnedByAppearanceAndMatchNiratanControls()
    {
        var appearanceXaml = File.ReadAllText(Path.Combine(
            ProjectRoot, "Views", "Controls", "ReaderAppearanceSettingsContent.xaml"));
        var dictionaryXaml = File.ReadAllText(Path.Combine(
            ProjectRoot, "Views", "Pages", "DictionarySettingsPage.xaml"));
        var appearanceViewModel = File.ReadAllText(Path.Combine(
            ProjectRoot, "ViewModels", "Pages", "SettingsPageViewModel.cs"));
        var dictionaryViewModel = File.ReadAllText(Path.Combine(
            ProjectRoot, "ViewModels", "Pages", "DictionarySettingsPageViewModel.cs"));
        var enResources = File.ReadAllText(Path.Combine(
            ProjectRoot, "Strings", "en-US", "Resources.resw"));
        var zhResources = File.ReadAllText(Path.Combine(
            ProjectRoot, "Strings", "zh-CN", "Resources.resw"));

        appearanceXaml.Should().Contain("x:Uid=\"PopupAppearanceSectionHeader\"");
        appearanceXaml.Should().Contain("Minimum=\"100\" Maximum=\"1400\"");
        appearanceXaml.Should().Contain("Minimum=\"100\" Maximum=\"800\"");
        appearanceXaml.Should().Contain("Minimum=\"0.8\" Maximum=\"1.5\"");
        appearanceXaml.Should().Contain("AutomationProperties.AutomationId=\"PopupActionBarToggle\"");
        appearanceXaml.Should().Contain("AutomationProperties.AutomationId=\"PopupFullWidthToggle\"");
        appearanceViewModel.Should().Contain("partial double PopupScale");
        appearanceViewModel.Should().Contain("current with { PopupFullWidth = value }");

        dictionaryXaml.Should().NotContain("DictionaryPopupMaxWidthCard");
        dictionaryXaml.Should().NotContain("DictionaryPopupMaxHeightCard");
        dictionaryViewModel.Should().NotContain("PopupMaxWidth");
        dictionaryViewModel.Should().NotContain("PopupMaxHeight");

        foreach (var key in new[]
        {
            "PopupAppearanceSectionHeader.Text",
            "PopupWidthCard.Header",
            "PopupWidthCard.Description",
            "PopupHeightCard.Header",
            "PopupHeightCard.Description",
            "PopupScaleCard.Header",
            "PopupScaleCard.Description",
            "PopupActionBarCard.Header",
            "PopupActionBarCard.Description",
            "PopupFullWidthCard.Header",
            "PopupFullWidthCard.Description",
        })
        {
            enResources.Should().Contain(key);
            zhResources.Should().Contain(key);
        }
    }

    [Fact]
    public void SettingsPage_ExposesMacAlignedAdvancedSettingsHome()
    {
        var advancedXamlPath = Path.Combine(ProjectRoot, "Views", "Pages", "AdvancedSettingsPage.xaml");
        var advancedCodePath = Path.Combine(ProjectRoot, "Views", "Pages", "AdvancedSettingsPage.xaml.cs");
        var settingsXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "SettingsPage.xaml")
        );
        var appPageCode = File.ReadAllText(Path.Combine(ProjectRoot, "Enums", "AppPage.cs"));
        var navigationCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "UI", "NavigationService.cs")
        );
        var enResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw")
        );
        var zhResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw")
        );

        File.Exists(advancedXamlPath).Should().BeTrue();
        File.Exists(advancedCodePath).Should().BeTrue();

        var advancedXaml = File.ReadAllText(advancedXamlPath);
        var advancedCode = File.ReadAllText(advancedCodePath);

        settingsXaml.Should().NotContain("AutomationProperties.AutomationId=\"SettingsAdvancedNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsProfilesNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsVideoNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsKeyboardShortcutsNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsGameControllerNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsSyncNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsBackupNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsReportIssueNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsAudioNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsSasayakiNavItem\"");
        settingsXaml.Should().Contain("AutomationProperties.AutomationId=\"SettingsStatisticsNavItem\"");
        settingsXaml.Should().Contain("NavigationViewItemHeader");

        advancedXaml.Should().Contain("x:Class=\"Hoshi.Views.Pages.AdvancedSettingsPage\"");
        advancedXaml.Should().Contain("AutomationProperties.AutomationId=\"AdvancedSettingsBackButton\"");
        advancedXaml.Should().Contain("x:Uid=\"AdvancedSettingsPageTitle\"");
        advancedXaml.Should().Contain("x:Uid=\"AdvancedReaderSectionHeader\"");
        advancedXaml.Should().Contain("x:Uid=\"AdvancedAudioCard\"");
        advancedXaml.Should().Contain("AutomationProperties.AutomationId=\"AudioSettingsButton\"");
        advancedXaml.Should().Contain("x:Uid=\"AdvancedStatisticsCard\"");
        advancedXaml.Should().Contain("AutomationProperties.AutomationId=\"StatisticsSettingsButton\"");
        advancedXaml.Should().Contain("x:Uid=\"AdvancedSasayakiCard\"");
        advancedXaml.Should().Contain("AutomationProperties.AutomationId=\"SasayakiSettingsButton\"");
        advancedXaml.Should().Contain("x:Uid=\"AdvancedVideoSectionHeader\"");
        advancedXaml.Should().Contain("x:Uid=\"AdvancedVideoCard\"");
        advancedXaml.Should().Contain("AutomationProperties.AutomationId=\"VideoSettingsButton\"");
        advancedXaml.Should().Contain("x:Uid=\"AdvancedShortcutsControlsSectionHeader\"");
        advancedXaml.Should().Contain("x:Uid=\"AdvancedKeyboardShortcutsCard\"");
        advancedXaml.Should().Contain("AutomationProperties.AutomationId=\"KeyboardShortcutsSettingsButton\"");
        advancedXaml.Should().Contain("x:Uid=\"AdvancedGameControllerCard\"");
        advancedXaml.Should().Contain("AutomationProperties.AutomationId=\"GameControllerSettingsButton\"");
        advancedXaml.Should().Contain("x:Uid=\"AdvancedSyncDataSectionHeader\"");
        advancedXaml.Should().Contain("x:Uid=\"AdvancedSyncCard\"");
        advancedXaml.Should().Contain("AutomationProperties.AutomationId=\"SyncSettingsButton\"");
        advancedXaml.Should().Contain("x:Uid=\"AdvancedBackupCard\"");
        advancedXaml.Should().Contain("AutomationProperties.AutomationId=\"BackupSettingsButton\"");

        advancedCode.Should().Contain("AudioSettings_Click");
        advancedCode.Should().Contain("NavigateSettingsSubpage(typeof(AudioSettingsPage))");
        advancedCode.Should().Contain("StatisticsSettings_Click");
        advancedCode.Should().Contain("NavigateSettingsSubpage(typeof(StatisticsSettingsPage))");
        advancedCode.Should().Contain("SasayakiSettings_Click");
        advancedCode.Should().Contain("NavigateSettingsSubpage(typeof(SasayakiSettingsPage))");

        appPageCode.Should().Contain("AdvancedSettingsPage");
        navigationCode.Should().Contain("typeof(AdvancedSettingsPage) => AppPage.AdvancedSettingsPage");

        foreach (var key in new[]
        {
            "SettingsAudioNavItem.Content",
            "SettingsStatisticsNavItem.Content",
            "SettingsSasayakiNavItem.Content",
            "SettingsReportIssueNavItem.Content",
            "SettingsReportIssueLink.Content",
            "AdvancedSettingsPageTitle.Text",
            "AdvancedReaderSectionHeader.Text",
            "AdvancedVideoSectionHeader.Text",
            "AdvancedShortcutsControlsSectionHeader.Text",
            "AdvancedSyncDataSectionHeader.Text",
            "AdvancedAudioCard.Header",
            "AdvancedStatisticsCard.Header",
            "AdvancedSasayakiCard.Header",
            "AdvancedVideoCard.Header",
            "AdvancedKeyboardShortcutsCard.Header",
            "AdvancedGameControllerCard.Header",
            "AdvancedSyncCard.Header",
            "AdvancedBackupCard.Header",
        })
        {
            enResources.Should().Contain(key);
            zhResources.Should().Contain(key);
        }
    }

    [Fact]
    public void AudioSettingsPage_UsesLocalizedStringsForVisibleText()
    {
        var audioXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "AudioSettingsPage.xaml")
        );
        var audioViewModelCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "ViewModels", "Pages", "AudioSettingsPageViewModel.cs")
        );
        var enResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw")
        );
        var zhResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw")
        );

        foreach (var uid in new[]
        {
            "AudioSettingsBackButton",
            "AudioSettingsPageTitle",
            "AudioSourcesSectionHeader",
            "AudioMoveSourceUpButton",
            "AudioMoveSourceDownButton",
            "AudioDeleteSourceButton",
            "AudioEnableSourceToggle",
            "AudioAddSourceSectionHeader",
            "AudioSourceNameCard",
            "AudioSourceNameTextBox",
            "AudioSourceUrlCard",
            "AudioSourceUrlTextBox",
            "AudioAddSourceButton",
            "AudioAddSourceButtonText",
            "AudioYomitanJsonHint",
            "AudioPlaybackSectionHeader",
            "AudioAutoplayCard",
            "AudioAutoplayToggle",
            "AudioPlaybackModeCard",
            "AudioPlaybackModeComboBox",
            "AudioLocalAudioSectionHeader",
            "AudioLocalAudioEnabledCard",
            "AudioLocalAudioToggle",
            "AudioAndroidAudioDatabaseCard",
            "AudioImportLocalAudioButton",
            "AudioImportLocalAudioButtonText",
            "AudioDeleteLocalAudioButton",
            "AudioDeleteLocalAudioButtonText",
            "AudioLocalAudioDatabaseHelpText",
        })
        {
            audioXaml.Should().Contain($"x:Uid=\"{uid}\"");
        }

        audioXaml.Should().Contain("Header=\"{x:Bind DisplayName, Mode=OneWay}\"");
        audioXaml.Should().Contain("DisplayMemberPath=\"Label\"");
        audioXaml.Should().Contain("SelectedValuePath=\"Mode\"");
        audioXaml.Should().NotContain("Text=\"Audio\"");
        audioXaml.Should().NotContain("Header=\"Enable Autoplay\"");
        audioXaml.Should().NotContain("Header=\"Playback Mode\"");
        audioXaml.Should().NotContain("Text=\"Import .db\"");
        audioXaml.Should().NotContain("Text=\"Delete\"");

        audioViewModelCode.Should().Contain("AudioPlaybackModeOption");
        audioViewModelCode.Should().Contain("ResourceStringHelper.GetString");

        foreach (var key in new[]
        {
            "AudioSettingsBackButton.AutomationProperties.Name",
            "AudioSettingsPageTitle.Text",
            "AudioSourcesSectionHeader.Text",
            "AudioMoveSourceUpButton.AutomationProperties.Name",
            "AudioMoveSourceUpButton.ToolTipService.ToolTip",
            "AudioMoveSourceDownButton.AutomationProperties.Name",
            "AudioMoveSourceDownButton.ToolTipService.ToolTip",
            "AudioDeleteSourceButton.AutomationProperties.Name",
            "AudioDeleteSourceButton.ToolTipService.ToolTip",
            "AudioEnableSourceToggle.AutomationProperties.Name",
            "AudioAddSourceSectionHeader.Text",
            "AudioSourceNameCard.Header",
            "AudioSourceNameTextBox.AutomationProperties.Name",
            "AudioSourceNameTextBox.PlaceholderText",
            "AudioSourceUrlCard.Header",
            "AudioSourceUrlCard.Description",
            "AudioSourceUrlTextBox.AutomationProperties.Name",
            "AudioAddSourceButton.AutomationProperties.Name",
            "AudioAddSourceButtonText.Text",
            "AudioYomitanJsonHint.Text",
            "AudioPlaybackSectionHeader.Text",
            "AudioAutoplayCard.Header",
            "AudioAutoplayCard.Description",
            "AudioAutoplayToggle.AutomationProperties.Name",
            "AudioPlaybackModeCard.Header",
            "AudioPlaybackModeCard.Description",
            "AudioPlaybackModeComboBox.AutomationProperties.Name",
            "AudioLocalAudioSectionHeader.Text",
            "AudioLocalAudioEnabledCard.Header",
            "AudioLocalAudioEnabledCard.Description",
            "AudioLocalAudioToggle.AutomationProperties.Name",
            "AudioAndroidAudioDatabaseCard.Header",
            "AudioImportLocalAudioButton.AutomationProperties.Name",
            "AudioImportLocalAudioButtonText.Text",
            "AudioDeleteLocalAudioButton.AutomationProperties.Name",
            "AudioDeleteLocalAudioButtonText.Text",
            "AudioLocalAudioDatabaseHelpText.Text",
            "AudioPlaybackModeInterrupt",
            "AudioPlaybackModeDuck",
            "AudioPlaybackModeMix",
            "AudioDefaultSourceName",
            "AudioLocalSourceName",
            "AudioLocalAudioImportingStatus",
            "AudioLocalAudioImportedStatus",
            "AudioLocalAudioMissingStatus",
            "AudioNotificationTitle",
            "AudioLocalAudioImportedNotification",
            "AudioImportErrorTitle",
            "AudioLocalAudioDeletedNotification",
            "AudioDeleteErrorTitle",
        })
        {
            enResources.Should().Contain(key);
            zhResources.Should().Contain(key);
        }
    }

    [Fact]
    public void SettingsPage_ExposesMacAlignedSasayakiSettings()
    {
        var advancedXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "AdvancedSettingsPage.xaml")
        );
        var advancedCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "AdvancedSettingsPage.xaml.cs")
        );
        var sasayakiXamlPath = Path.Combine(ProjectRoot, "Views", "Pages", "SasayakiSettingsPage.xaml");
        var sasayakiCodePath = Path.Combine(ProjectRoot, "Views", "Pages", "SasayakiSettingsPage.xaml.cs");
        var sasayakiViewModelPath = Path.Combine(ProjectRoot, "ViewModels", "Pages", "SasayakiSettingsPageViewModel.cs");
        var appCode = File.ReadAllText(Path.Combine(ProjectRoot, "App.xaml.cs"));
        var enResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw")
        );
        var zhResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw")
        );

        advancedXaml.Should().Contain("x:Uid=\"AdvancedSasayakiCard\"");
        advancedXaml.Should().Contain("AutomationProperties.AutomationId=\"SasayakiSettingsButton\"");
        advancedCode.Should().Contain("SasayakiSettings_Click");
        advancedCode.Should().Contain("NavigateSettingsSubpage(typeof(SasayakiSettingsPage))");

        File.Exists(sasayakiXamlPath).Should().BeTrue();
        File.Exists(sasayakiCodePath).Should().BeTrue();
        File.Exists(sasayakiViewModelPath).Should().BeTrue();

        var sasayakiXaml = File.ReadAllText(sasayakiXamlPath);
        var sasayakiViewModel = File.ReadAllText(sasayakiViewModelPath);

        sasayakiXaml.Should().Contain("x:Class=\"Hoshi.Views.Pages.SasayakiSettingsPage\"");
        sasayakiXaml.Should().Contain("helpers:BooleanToVisibilityConverter");
        sasayakiXaml.Should().Contain("AutomationProperties.AutomationId=\"SasayakiEnableToggle\"");
        sasayakiXaml.Should().Contain("x:Name=\"SasayakiEnabledSettingsPanel\"");
        sasayakiXaml.Should().Contain("Visibility=\"{x:Bind ViewModel.EnableSasayaki, Mode=OneWay, Converter={StaticResource BooleanToVisibilityConverter}}\"");
        sasayakiXaml.Should().Contain("AutomationProperties.AutomationId=\"SasayakiReaderToggleSwitch\"");
        sasayakiXaml.Should().Contain("AutomationProperties.AutomationId=\"SasayakiAutoScrollToggle\"");
        sasayakiXaml.Should().Contain("AutomationProperties.AutomationId=\"SasayakiAutoPauseToggle\"");
        sasayakiXaml.Should().Contain("AutomationProperties.AutomationId=\"SasayakiSkipControlsToggle\"");
        sasayakiXaml.Should().Contain("x:Uid=\"SasayakiSyncSectionHeader\"");
        sasayakiXaml.Should().Contain("AutomationProperties.AutomationId=\"SasayakiEnableSyncToggle\"");
        sasayakiXaml.Should().Contain("AutomationProperties.AutomationId=\"SasayakiSearchWindowNumberBox\"");
        sasayakiXaml.Should().Contain("AutomationProperties.AutomationId=\"SasayakiLightTextColorPicker\"");
        sasayakiXaml.Should().Contain("AutomationProperties.AutomationId=\"SasayakiDarkBackgroundColorPicker\"");
        sasayakiXaml.Should().Contain("IsOn=\"{x:Bind ViewModel.EnableSasayaki, Mode=TwoWay}\"");
        sasayakiXaml.Should().Contain("IsOn=\"{x:Bind ViewModel.AutoPauseOnLookup, Mode=TwoWay}\"");
        sasayakiXaml.Should().Contain("IsOn=\"{x:Bind ViewModel.EnableSync, Mode=TwoWay}\"");

        sasayakiViewModel.Should().Contain("EnableSasayaki");
        sasayakiViewModel.Should().Contain("ReaderShowSasayakiToggle");
        sasayakiViewModel.Should().Contain("AutoPauseOnLookup");
        sasayakiViewModel.Should().Contain("ShowSkipControls");
        sasayakiViewModel.Should().Contain("EnableSync");
        sasayakiViewModel.Should().Contain("SearchWindowSize");
        sasayakiViewModel.Should().Contain("SasayakiSettings");

        appCode.Should().Contain("AddTransient<SasayakiSettingsPageViewModel>");

        foreach (var key in new[]
        {
            "AdvancedSasayakiCard.Header",
            "SasayakiSettingsPageTitle.Text",
            "SasayakiEnableToggle.Header",
            "SasayakiReaderToggleSwitch.Header",
            "SasayakiAutoScrollToggle.Header",
            "SasayakiAutoPauseToggle.Header",
            "SasayakiSkipControlsToggle.Header",
            "SasayakiSyncSectionHeader.Text",
            "SasayakiEnableSyncToggle.Header",
            "SasayakiSearchWindowNumberBox.Header",
        })
        {
            enResources.Should().Contain(key);
            zhResources.Should().Contain(key);
        }
    }

    [Fact]
    public void SettingsPage_ExposesMacAlignedStatisticsSettings()
    {
        var advancedXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "AdvancedSettingsPage.xaml")
        );
        var advancedCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "AdvancedSettingsPage.xaml.cs")
        );
        var statisticsXamlPath = Path.Combine(ProjectRoot, "Views", "Pages", "StatisticsSettingsPage.xaml");
        var statisticsCodePath = Path.Combine(ProjectRoot, "Views", "Pages", "StatisticsSettingsPage.xaml.cs");
        var statisticsViewModelPath = Path.Combine(ProjectRoot, "ViewModels", "Pages", "StatisticsSettingsPageViewModel.cs");
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var readerXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml")
        );
        var appCode = File.ReadAllText(Path.Combine(ProjectRoot, "App.xaml.cs"));
        var appSettingsCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Models", "Settings", "AppSettings.cs")
        );
        var enResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw")
        );
        var zhResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw")
        );

        advancedXaml.Should().Contain("x:Uid=\"AdvancedStatisticsCard\"");
        advancedXaml.Should().Contain("AutomationProperties.AutomationId=\"StatisticsSettingsButton\"");
        advancedCode.Should().Contain("StatisticsSettings_Click");
        advancedCode.Should().Contain("NavigateSettingsSubpage(typeof(StatisticsSettingsPage))");

        File.Exists(statisticsXamlPath).Should().BeTrue();
        File.Exists(statisticsCodePath).Should().BeTrue();
        File.Exists(statisticsViewModelPath).Should().BeTrue();

        var statisticsXaml = File.ReadAllText(statisticsXamlPath);
        var statisticsViewModel = File.ReadAllText(statisticsViewModelPath);

        statisticsXaml.Should().Contain("x:Class=\"Hoshi.Views.Pages.StatisticsSettingsPage\"");
        statisticsXaml.Should().Contain("AutomationProperties.AutomationId=\"StatisticsEnableToggle\"");
        statisticsXaml.Should().Contain("AutomationProperties.AutomationId=\"StatisticsAutostartModeComboBox\"");
        statisticsXaml.Should().Contain("AutomationProperties.AutomationId=\"StatisticsDailyTargetTypeComboBox\"");
        statisticsXaml.Should().Contain("AutomationProperties.AutomationId=\"StatisticsDailyCharacterTargetNumberBox\"");
        statisticsXaml.Should().Contain("AutomationProperties.AutomationId=\"StatisticsWeeklyTargetDaysNumberBox\"");
        statisticsXaml.Should().Contain("IsOn=\"{x:Bind ViewModel.EnableStatistics, Mode=TwoWay}\"");
        statisticsViewModel.Should().Contain("NovelStatisticsSettings");
        statisticsViewModel.Should().Contain("StatisticsAutostartMode");
        statisticsViewModel.Should().Contain("DailyCharacterTarget");
        statisticsViewModel.Should().Contain("WeeklyTargetDays");

        appCode.Should().Contain("AddTransient<StatisticsSettingsPageViewModel>");
        appSettingsCode.Should().Contain("NovelStatisticsSettings StatisticsSettings");
        readerXaml.Should().Contain("x:Name=\"NovelReaderStatisticsButton\"");
        readerCode.Should().Contain("UpdateStatisticsButtonVisibility");
        readerCode.Should().Contain("StartStatisticsForAutostart(StatisticsAutostartMode.On)");
        readerCode.Should().Contain("StartStatisticsForAutostart(StatisticsAutostartMode.PageTurn)");

        foreach (var key in new[]
        {
            "AdvancedStatisticsCard.Header",
            "StatisticsSettingsPageTitle.Text",
            "StatisticsEnableToggle.Header",
            "StatisticsAutostartModeComboBox.Header",
            "StatisticsDailyTargetTypeComboBox.Header",
            "StatisticsDailyCharacterTargetNumberBox.Header",
            "StatisticsWeeklyTargetDaysNumberBox.Header",
        })
        {
            enResources.Should().Contain(key);
            zhResources.Should().Contain(key);
        }
    }

    [Fact]
    public void SettingsPage_AssignsViewModelBeforeXBindInitializes()
    {
        var settingsCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "SettingsPage.xaml.cs")
        );

        settingsCode
            .Replace("\r\n", "\n")
            .Should()
            .Contain("ViewModel = App.GetService<SettingsPageViewModel>();\n        InitializeComponent();");
    }

    [Fact]
    public void DictionaryLookupPopup_IsAttachedToReaderXamlRootBeforeOpening()
    {
        var popupCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs")
        );
        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs")
        );
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );

        overlayCode.Should().Contain("_currentXamlRoot = xamlRoot;");
        overlayCode.Should().Contain("PrewarmAsync");
        popupCode.Should().Contain("WebView2");
        popupCode.Should().Contain("PopupHtmlGenerator");
        popupCode.Should().Contain("EnsureCoreWebView2Async");
        popupCode.Should().Contain("WarmAsync");
        readerCode.Should().Contain("ShowLookupAsync");
        readerCode.Should().Contain("PrewarmAsync");
        readerCode.Should().Contain("_lookupSemaphore.WaitAsync()");
    }

    [Fact]
    public void WebView2Hosts_UseWritableAppDataUserDataFolder()
    {
        var helperCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Helpers", "WebView2EnvironmentHelper.cs")
        );
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var popupCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs")
        );
        var subtitleCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.SubtitleOverlay.cs")
        );

        helperCode.Should().Contain("AppDataHelper.GetWebView2UserDataPath()");
        helperCode.Should().Contain("CoreWebView2Environment.CreateWithOptionsAsync");

        readerCode.Should().Contain("WebView2EnvironmentHelper.GetOrCreateAsync()");
        readerCode.Should().Contain("NovelWebView.EnsureCoreWebView2Async(environment)");
        readerCode.Should().NotContain("NovelWebView.EnsureCoreWebView2Async();");

        popupCode.Should().Contain("WebView2EnvironmentHelper.GetOrCreateAsync()");
        popupCode.Should().Contain("_contentWebView.EnsureCoreWebView2Async(environment)");
        popupCode.Should().NotContain("_contentWebView.EnsureCoreWebView2Async();");

        subtitleCode.Should().Contain("WebView2EnvironmentHelper.GetOrCreateAsync()");
        subtitleCode.Should().Contain("SubtitleWebView.EnsureCoreWebView2Async(environment)");
        subtitleCode.Should().NotContain("SubtitleWebView.EnsureCoreWebView2Async();");
    }

    [Fact]
    public void DictionaryLookupPopup_ReusesWarmShellForNestedLookup()
    {
        var popupCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs")
        );
        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs")
        );

        popupCode.Should().Contain("_shellReadyCompletion");
        popupCode.Should().Contain("WaitForShellReadyAsync");
        popupCode.Should().Contain("await EnsureWebViewAsync();");
        popupCode.Should().Contain("GenerateInjectionScript");
        popupCode.Should().NotContain("public async Task ShowResultsNavigatedAsync");
        popupCode.Should().NotContain("GenerateHtml(results");
        popupCode.Should().NotContain("WaitForContentReadyAsync");
        popupCode.Should().Contain("TapOutsideRequested");
        popupCode.Should().Contain("PrepareForPendingContent");
        popupCode.Should().Contain("ShowReadyContent");
        popupCode.Should().Contain("_pendingContentGeneration");
        popupCode.Should().Contain("IsCurrentContentReady");
        popupCode.Should().NotContain("HideWebContent");
        popupCode.Should().NotContain("_contentWebView.Visibility = Visibility.Collapsed");
        popupCode.Should().NotContain("VisualRoot.Visibility = Visibility.Collapsed;");
        popupCode.Should().Contain("VisualRoot.Opacity = 0");
        popupCode.Should().Contain("double _readyOpacity = 1");
        popupCode.Should().Contain("SetReadyOpacity(double opacity)");
        popupCode.Should().Contain("VisualRoot.Opacity = _readyOpacity");
        popupCode.Should().Contain("VisualRoot.IsHitTestVisible = false");
        popupCode.Should().Contain("VisualRoot.IsHitTestVisible = true");
        overlayCode.Should().Contain("await child.ShowResultsWarmAsync");
    }

    [Fact]
    public void DictionaryLookupPopup_UsesFluentFloatingCardShell()
    {
        var popupCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs")
        );
        var materialPath = Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupMaterial.cs");

        File.Exists(materialPath).Should().BeTrue();
        var materialCode = File.ReadAllText(materialPath);

        popupCode.Should().Contain("private readonly SolidColorBrush _surfaceBrush;");
        popupCode.Should().Contain("private readonly SolidColorBrush _outlineBrush;");
        popupCode.Should().Contain("private double _popupCornerRadius = 8;");
        popupCode.Should().Contain("DictionaryPopupMaterial.GetOpaqueSurfaceColor(ThemeMode.System)");
        popupCode.Should().Contain("DictionaryPopupMaterial.GetOutlineColor(ThemeMode.System)");
        popupCode.Should().Contain("DefaultBackgroundColor = initialSurfaceColor");
        popupCode.Should().Contain("Background = _surfaceBrush");
        popupCode.Should().Contain("BorderBrush = _outlineBrush");
        popupCode.Should().Contain("BorderThickness = new Thickness(1)");
        popupCode.Should().Contain("DictionaryPopupCornerGuard.CalculateInset(_popupCornerRadius)");
        popupCode.Should().Contain("_surfaceRoot.Margin = new Thickness(guardInset);");
        popupCode.Should().Contain("VisualRoot.CornerRadius = new CornerRadius(_popupCornerRadius);");
        popupCode.Should().Contain("ApplySurfaceTheme(themeMode);");
        popupCode.Should().Contain("_outlineBrush.Color = DictionaryPopupMaterial.GetOutlineColor(themeMode);");
        materialCode.Should().Contain("Windows.UI.Color.FromArgb(0xFF, 0xD1, 0xD1, 0xD6)");
        materialCode.Should().Contain("Windows.UI.Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3C)");
        popupCode.Should().Contain("_contentWebView.DefaultBackgroundColor = surfaceColor;");
        popupCode.Should().Contain("private double _readyOpacity = 1;");
        popupCode.Should().NotContain("CompositionGeometricClip");
        popupCode.Should().NotContain("CompositionRoundedRectangleGeometry");
        popupCode.Should().NotContain("_surfaceVisual.Clip");
        popupCode.Should().NotContain("DefaultBackgroundColor = Colors.Transparent");
        popupCode.Should().NotContain("_contentWebView.Margin = new Thickness(-1);\n        SetPopupCornerRadius(8);");
        popupCode.Should().NotContain("AcrylicBrush");
        popupCode.Should().NotContain("DictionaryPopupMaterial.CreateInAppAcrylicThinBrush");
        popupCode.Should().NotContain("DictionaryPopupMaterial.ApplyTheme");
        popupCode.Should().NotContain("_contentWebView.DefaultBackgroundColor = isDark ? Colors.Black : Colors.White;");
        popupCode.Should().NotContain("SetTheme(");
        popupCode.Should().Contain("await EnsureWebViewAsync();");
        popupCode.Should().MatchRegex(
            @"(?s)await _contentWebView\.EnsureCoreWebView2Async\(environment\);\s*_contentWebView\.DefaultBackgroundColor = _surfaceBrush\.Color;");
        popupCode.Should().Contain("ApplyPopupCornerRadiusToWebViewAsync");
        popupCode.Should().NotContain("_snapshotAcrylicImage");
        popupCode.Should().NotContain("SetSnapshotAcrylicBackgroundAsync");
        popupCode.Should().NotContain("DictionaryPopupSnapshotAcrylicRenderer.RenderPngAsync");
        popupCode.Should().NotContain("ApplySnapshotAcrylicWebBackgroundAsync");
        popupCode.Should().NotContain("has-snapshot-acrylic");
        materialCode.Should().Contain("AcrylicBrush");
        materialCode.Should().Contain("CreateInAppAcrylicThinBrush");
        materialCode.Should().Contain("DesktopAcrylicController");
        materialCode.Should().Contain("DesktopAcrylicKind.Thin");
        materialCode.Should().Contain("AlwaysUseFallback = false");
        materialCode.Should().Contain("TintOpacity = isDark ? 0.12 : 0.78");
        materialCode.Should().Contain("TintLuminosityOpacity = isDark ? 0.18 : 0.62");
        materialCode.Should().Contain("TintLuminosityOpacity");
        materialCode.Should().Contain("Windows.UI.Color.FromArgb(0x58");
        materialCode.Should().Contain("Windows.UI.Color.FromArgb(0xDC");
        materialCode.Should().Contain("DispatcherQueue.GetForCurrentThread()?.EnsureSystemDispatcherQueue()");
        materialCode.Should().Contain("Windows.UI.Color.FromArgb(0x18");
        materialCode.Should().Contain("Windows.UI.Color.FromArgb(0x22");
        materialCode.Should().Contain("Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00)");
        materialCode.Should().Contain("Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)");
        popupCode.Should().NotContain("ThemeShadow");
        popupCode.Should().NotContain("VisualRoot.Shadow");
        popupCode.Should().NotContain("VisualRoot.Translation");
        popupCode.Should().NotContain("ApplyThemeShadow");
        popupCode.Should().NotContain("using System.Numerics;");

        var popupCss = File.ReadAllText(
            Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.css")
        );
        popupCss.Should().Contain("--popup-corner-radius: var(--popup-space-12)");
        popupCss.Should().Contain("border-radius: var(--popup-corner-radius)");
        popupCss.Should().Contain("#popup-viewport");
        popupCss.Should().Contain("overflow-y: auto");
        popupCss.Should().Contain("background-color: var(--background-color) !important");
        popupCss.Should().Contain("position: absolute;");
        popupCss.Should().NotContain("html.has-snapshot-acrylic");
        popupCss.Should().NotContain("--snapshot-acrylic-background-image");

        var popupHtmlGeneratorCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Dictionary", "PopupHtmlGenerator.cs")
        );
        popupHtmlGeneratorCode.Should().Contain("<div id=\"\"popup-viewport\"\">");

        var popupJs = File.ReadAllText(
            Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.js")
        );
        popupJs.Should().Contain("function getPopupScrollElement()");
        popupJs.Should().NotContain("document.scrollingElement.scrollTop");

        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs")
        );
        overlayCode.Should().NotContain("ApplySnapshotAcrylicBackground(_rootHost)");
        overlayCode.Should().NotContain("ApplySnapshotAcrylicBackground(child)");
        overlayCode.Should().NotContain("_currentMiningContext.VideoScreenshotPath");
        overlayCode.Should().Contain("PrewarmAsync(XamlRoot xamlRoot, ThemeMode themeMode");
        overlayCode.Should().Contain("PrewarmCoreAsync(xamlRoot, themeMode)");
        overlayCode.Should().Contain("private double _rootReadyOpacity = 1;");

        var videoCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.SubtitleOverlay.cs")
        );
        videoCode.Should().Contain("PrewarmVideoDictionaryPopupAsync");
        videoCode.Should().Contain("await overlay.PrewarmAsync(");
    }

    [Fact]
    public void NovelLookupPage_EmbedsPopupOverlayCanvasForNestedLookup()
    {
        var lookupXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLookupPage.xaml")
        );
        var lookupCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLookupPage.xaml.cs")
        );

        lookupXaml.Should().Contain("x:Name=\"DictionaryOverlayCanvas\"");
        lookupCode.Should().Contain("_popupOverlay.UseCanvas(DictionaryOverlayCanvas)");
        lookupCode.Should().Contain("DictionaryOverlayCanvas");
        lookupCode.Should().Contain("_popupOverlay.Dismissed += OnPopupOverlayDismissed");
        lookupCode.Should().Contain("DictionaryPanelRoot.Visibility = Visibility.Visible");
        lookupCode.Should().Contain("DictionaryPanelRoot.Visibility = Visibility.Collapsed");
    }

    [Fact]
    public void PopupScript_SupportsClickAndShiftNestedLookupInsideLookupWindow()
    {
        var popupJs = File.ReadAllText(
            Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.js")
        );

        popupJs.Should().Contain("container.addEventListener('click'");
        popupJs.Should().Contain("lookupAtPopupPoint(e.clientX, e.clientY, true");
        popupJs.Should().Contain("document.addEventListener('mousemove'");
        popupJs.Should().Contain("lookupAtPopupPoint(e.clientX, e.clientY, false, 'shift')");
        popupJs.Should().Contain("postPopupMessage('lookupRedirect', {");
        popupJs.Should().Contain("var rect = window.hoshiSelection?.getSelectionRect?.(x, y) || null");
        popupJs.Should().Contain("rectMs: rectMs");
    }

    [Fact]
    public void DictionaryPopupOverlay_SerializesRedirectLookupBeforeNativeQuery()
    {
        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs")
        );

        var waitIndex = overlayCode.IndexOf("await _redirectSemaphore.WaitAsync()", StringComparison.Ordinal);
        var lookupIndex = overlayCode.IndexOf("var results = await _lookupService.LookupAsync", StringComparison.Ordinal);

        waitIndex.Should().BeGreaterThanOrEqualTo(0);
        lookupIndex.Should().BeGreaterThanOrEqualTo(0);
        waitIndex.Should().BeLessThan(lookupIndex);
        overlayCode.Should().Contain("if (redirectVersion != Volatile.Read(ref _redirectVersion))");
    }

    [Fact]
    public void DictionaryPopupOverlay_DeduplicatesRepeatedRedirectQueryFromSameParent()
    {
        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs")
        );

        overlayCode.Should().Contain("_lastRedirectQuery");
        overlayCode.Should().Contain("_lastRedirectParent");
        overlayCode.Should().Contain("ReferenceEquals(parent, _lastRedirectParent)");
        overlayCode.Should().Contain("string.Equals(query, _lastRedirectQuery, StringComparison.Ordinal)");
        overlayCode.Should().Contain("ResetRedirectDeduplication");
    }

    [Fact]
    public void NovelReaderLookup_SerializesRootLookupBeforeNativeQuery()
    {
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );

        var waitIndex = readerCode.IndexOf("await _lookupSemaphore.WaitAsync()", StringComparison.Ordinal);
        var lookupIndex = readerCode.IndexOf("var results = await lookupService.LookupAsync", StringComparison.Ordinal);

        waitIndex.Should().BeGreaterThanOrEqualTo(0);
        lookupIndex.Should().BeGreaterThanOrEqualTo(0);
        waitIndex.Should().BeLessThan(lookupIndex);
        readerCode.Should().Contain("if (requestVersion != Volatile.Read(ref _lookupRequestVersion))");
    }

    [Fact]
    public void DictionaryPopup_DoesNotPipeEveryJsConsoleMessageThroughDevTools()
    {
        var popupCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs")
        );
        var popupJs = File.ReadAllText(
            Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.js")
        );

        popupCode.Should().NotContain("Runtime.consoleAPICalled");
        popupCode.Should().NotContain("[DictPopup] JS console");
        popupJs.Should().NotContain("[Bridge] postPopupMessage");
    }

    [Fact]
    public void SelectionScript_UsesImmediateShiftHoverLookup()
    {
        var script = File.ReadAllText(Path.Combine(ReaderRoot, "selection.js"));
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var settingsCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Models", "Settings", "ReaderSettings.cs")
        );

        script.Should().NotContain("getShiftHoverDelayMs");
        script.Should().NotContain("shiftHoverDelayMs");
        script.Should().NotContain("setTimeout(() =>");
        script.Should().Contain("lookupAtPoint(e.clientX, e.clientY)");
        script.Should().Contain("window.__hoshiLookupPopupActive === true");
        script.Should().Contain("postToHost('lookupDismiss'");
        readerCode.Should().Contain("window.__hoshiLookupSettings");
        readerCode.Should().Contain("case \"lookupDismiss\":");
        readerCode.Should().Contain("SetLookupPopupActiveAsync(true)");
        readerCode.Should().Contain("SetLookupPopupActiveAsync(false)");
        settingsCode.Should().NotContain("ShiftHoverLookupDelayMs");
    }

    [Fact]
    public void SelectionScript_AllowsNonJapaneseLookupWhenSettingIsEnabled()
    {
        var script = File.ReadAllText(Path.Combine(ReaderRoot, "selection.js"));
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );

        script.Should().Contain("window.scanNonJapaneseText === false");
        script.Should().Contain("!this.isCodePointJapanese(char.codePointAt(0))");
        script.Should().Contain("getScanLength");
        script.Should().Contain("window.__hoshiLookupSettings?.scanLength");
        script.Should().NotContain("||\n        !isCodePointJapanese(char.codePointAt(0));");
        readerCode.Should().Contain("scanNonJapaneseText");
        readerCode.Should().Contain("maxResults");
        readerCode.Should().Contain("scanLength");
        readerCode.Should().Contain("dictionaryDisplaySettings.MaxResults");
        readerCode.Should().Contain("dictionaryDisplaySettings.ScanLength");
        readerCode.Should().Contain("Current.DictionaryDisplaySettings");
    }

    [Fact]
    public void SelectionScript_HighlightsMatchedLookupTextInReader()
    {
        var script = File.ReadAllText(Path.Combine(ReaderRoot, "selection.js"));
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var stylesCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Novels", "NovelReaderContentStyles.cs")
        );

        script.Should().Contain("highlightSelection(charCount)");
        script.Should().Contain("selectionCharacterRanges(charCount)");
        script.Should().Contain("CSS.highlights?.set('hoshi-selection', new Highlight(...highlights))");
        script.Should().Contain("CSS.highlights?.get('hoshi-selection')?.clear()");
        readerCode.Should().Contain("HighlightLookupSelectionAsync(results[0].Matched)");
        readerCode.Should().Contain("window.hoshiSelection.highlightSelection");
        stylesCode.Should().Contain("::highlight(hoshi-selection)");
        stylesCode.Should().Contain("background-color: rgba(160, 160, 160, 0.4) !important;");
    }

    [Fact]
    public void NavigationPage_ExposesNovelLookupPage()
    {
        var navigationXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NavigationPage.xaml")
        );
        var appCode = File.ReadAllText(Path.Combine(ProjectRoot, "App.xaml.cs"));
        var lookupXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLookupPage.xaml")
        );

        navigationXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelLookupNavItem\"");
        navigationXaml.Should().Contain("Tag=\"Hoshi.Views.Pages.NovelLookupPage\"");
        appCode.Should().Contain("NovelLookupPageViewModel");
        lookupXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelLookupQueryBox\"");
        lookupXaml.Should().NotContain("AutomationProperties.AutomationId=\"NovelLookupResultsList\"");
        lookupXaml.Should().Contain("DictionaryPanelRoot");
        var lookupCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLookupPage.xaml.cs")
        );
        lookupCode.Should().Contain("DictionaryPopupOverlay");
        lookupCode.Should().Contain("ShowLookupAsync");
        lookupCode.Should().Contain("PrewarmAsync");
        lookupCode.Should().Contain("EmbedRoot");
    }

    [Fact]
    public void GlobalLookupWindow_UsesSharedPopupOverlayAndNoCustomResultsList()
    {
        var windowXamlPath = Path.Combine(ProjectRoot, "Views", "Dictionary", "GlobalLookupWindow.xaml");
        var windowCodePath = Path.Combine(ProjectRoot, "Views", "Dictionary", "GlobalLookupWindow.xaml.cs");

        File.Exists(windowXamlPath).Should().BeTrue();
        File.Exists(windowCodePath).Should().BeTrue();

        var windowXaml = File.ReadAllText(windowXamlPath);
        var windowCode = File.ReadAllText(windowCodePath);

        windowXaml.Should().Contain("x:Class=\"Hoshi.Views.Dictionary.GlobalLookupWindow\"");
        windowXaml.Should().Contain("AutomationProperties.AutomationId=\"GlobalLookupQueryBox\"");
        windowXaml.Should().Contain("AutomationProperties.AutomationId=\"GlobalLookupSearchButton\"");
        windowXaml.Should().Contain("AutomationProperties.AutomationId=\"GlobalLookupPasteButton\"");
        windowXaml.Should().Contain("x:Name=\"DictionaryPanelRoot\"");
        windowXaml.Should().Contain("x:Name=\"DictionaryOverlayCanvas\"");
        windowXaml.Should().NotContain("ListView");
        windowXaml.Should().NotContain("ItemsRepeater");

        windowCode.Should().Contain("DictionaryPopupOverlay");
        windowCode.Should().Contain("UseCanvas(DictionaryOverlayCanvas)");
        windowCode.Should().Contain("EmbedRoot(DictionaryOverlayCanvas)");
        windowCode.Should().Contain("LookupReady +=");
        windowCode.Should().Contain("ShowLookupAsync");
        windowCode.Should().Contain("Clipboard.GetContent()");
        windowCode.Should().NotContain("IDictionaryLookupService");
    }

    [Fact]
    public void GlobalLookup_IsRegisteredDisabledByDefaultWithoutManualTitleBarWindow()
    {
        var projectCode = File.ReadAllText(Path.Combine(ProjectRoot, "Hoshi.csproj"));
        var appCode = File.ReadAllText(Path.Combine(ProjectRoot, "App.xaml.cs"));
        var appSettingsCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Models", "Settings", "AppSettings.cs")
        );
        var globalSettingsCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Models", "Settings", "GlobalLookupSettings.cs")
        );
        var navigationXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NavigationPage.xaml")
        );
        var navigationCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NavigationPage.xaml.cs")
        );
        var dictionarySettingsXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "DictionarySettingsPage.xaml")
        );
        var dictionarySettingsViewModel = File.ReadAllText(
            Path.Combine(ProjectRoot, "ViewModels", "Pages", "DictionarySettingsPageViewModel.cs")
        );
        var enResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw")
        );
        var zhResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw")
        );

        appSettingsCode.Should().Contain("GlobalLookupSettings GlobalLookup");
        globalSettingsCode.Should().Contain("bool Enabled { get; set; }");
        globalSettingsCode.Should().Contain("DefaultHotKey = \"Ctrl+Alt+D\"");
        globalSettingsCode.Should().NotContain("Enabled { get; set; } = true");

        appCode.Should().Contain("IDictionaryPopupRequestService, DictionaryPopupRequestService");
        appCode.Should().Contain("GlobalLookupWindowViewModel");
        appCode.Should().Contain("IGlobalLookupWindowService, GlobalLookupWindowService");
        appCode.Should().Contain("IGlobalSelectionLookupService, GlobalSelectionLookupService");
        appCode.Should().Contain("Win32GlobalLookupHotKeyRegistrar");
        projectCode.Should().Contain("Microsoft.WindowsDesktop.App");
        appCode.Should().Contain("UIAutomationSelectedTextReader");
        appCode.Should().Contain("Win32FocusedEditSelectedTextReader");
        appCode.Should().Contain("CascadingSelectedTextReader");
        appCode.Should().NotContain("ClipboardFallbackSelectedTextReader");
        appCode.Should().NotContain("ICopyShortcutSender");
        var normalizedAppCode = appCode.Replace("\r\n", "\n");
        normalizedAppCode.Should().Contain(
            "MainWindow.NavigateToShell();\n            await GetService<IGlobalSelectionLookupService>().InitializeAsync();");

        navigationXaml.Should().NotContain("AutomationProperties.AutomationId=\"GlobalLookupButton\"");
        navigationXaml.Should().NotContain("x:Uid=\"GlobalLookupButton\"");
        navigationCode.Should().NotContain("OpenGlobalLookup_Click");
        navigationCode.Should().NotContain("IGlobalLookupWindowService");

        dictionarySettingsXaml.Should().Contain("x:Uid=\"GlobalLookupEnabledCard\"");
        dictionarySettingsXaml.Should().Contain("AutomationProperties.AutomationId=\"GlobalLookupEnabledToggle\"");
        dictionarySettingsXaml.Should().Contain("IsOn=\"{x:Bind ViewModel.GlobalLookupEnabled, Mode=TwoWay}\"");
        dictionarySettingsViewModel.Should().Contain("GlobalLookupEnabled");
        dictionarySettingsViewModel.Should().Contain("GlobalLookup.Enabled = value");
        dictionarySettingsViewModel.Should().Contain("IGlobalSelectionLookupService");

        var globalLookupServiceCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Dictionary", "GlobalSelectionLookupService.cs")
        );
        globalLookupServiceCode.Should().Contain("GlobalLookup] {StatusText}");
        globalLookupServiceCode.Should().Contain("Global lookup hotkey message received");
        globalLookupServiceCode.Should().Contain("Hotkey handler failed");
        globalLookupServiceCode.Should().Contain("class UIAutomationSelectedTextReader");
        globalLookupServiceCode.Should().Contain("s_readGate");
        globalLookupServiceCode.Should().Contain("AutomationElement.FocusedElement");
        globalLookupServiceCode.Should().Contain("TextPattern.Pattern");
        globalLookupServiceCode.Should().Contain("GetSelection()");
        globalLookupServiceCode.Should().Contain("AutomationElement.FromHandle");
        globalLookupServiceCode.Should().Contain("FindAll(TreeScope.Descendants");
        globalLookupServiceCode.Should().Contain("IsTextPatternAvailableProperty");
        globalLookupServiceCode.Should().Contain("class CascadingSelectedTextReader");
        globalLookupServiceCode.Should().Contain("class Win32FocusedEditSelectedTextReader");
        globalLookupServiceCode.Should().Contain("EM_GETSEL");
        globalLookupServiceCode.Should().Contain("EM_GETSELTEXT");
        globalLookupServiceCode.Should().Contain("WM_GETTEXT");
        globalLookupServiceCode.Should().Contain("GetGUIThreadInfo");
        globalLookupServiceCode.Should().Contain("SendMessageTimeout");
        globalLookupServiceCode.Should().NotContain("ClipboardFallbackSelectedTextReader");
        globalLookupServiceCode.Should().NotContain("keybd_event");
        File.ReadAllText(
                Path.Combine(ProjectRoot, "Services", "Dictionary", "GlobalLookupPopupService.cs")
            )
            .Should()
            .Contain("Global lookup popup requested")
            .And.Contain("ResolveStagingRect(workArea, StagingSize)")
            .And.Contain("window.AppWindow.MoveAndResize(finalRect)")
            .And.Contain("cursorPoint.X - workArea.X")
            .And.NotContain("GetDpiForMonitor");
        var globalLookupPopupWindowCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "GlobalLookupPopupWindow.xaml.cs")
        );
        var globalLookupWindowCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "GlobalLookupWindow.xaml.cs")
        );
        globalLookupPopupWindowCode.Should().Contain("ApplyPopupSizedHostSurface");
        globalLookupPopupWindowCode.Should().Contain("_desktopAcrylicThinBackdrop = DictionaryPopupMaterial.TryApplyDesktopAcrylicThin(this, RootGrid)");
        globalLookupPopupWindowCode.Should().Contain("DictionaryPopupMaterial.CreateTransparentBrush()");
        globalLookupPopupWindowCode.Should().Contain("DictionaryPopupMaterial.GetOpaqueSurfaceColor(themeMode)");
        globalLookupPopupWindowCode.Should().Contain("_desktopAcrylicThinBackdrop?.SetTheme(themeMode)");
        globalLookupPopupWindowCode.Should().Contain("UseStandaloneWindowVisuals");
        globalLookupPopupWindowCode.Should().Contain("SetRootReadyOpacity(1)");
        globalLookupPopupWindowCode.Should().NotContain("UseNakedFloatingWindowVisuals");
        globalLookupPopupWindowCode.Should().Contain("ApplyNativeBorderlessHostStyles");
        globalLookupPopupWindowCode.Should().Contain("ApplyDwmBorderlessChrome");
        globalLookupPopupWindowCode.Should().Contain("ApplyRoundedHostRegion");
        globalLookupPopupWindowCode.Should().Contain("ClearHostRegion");
        globalLookupPopupWindowCode.Should().Contain("DwmSetWindowAttribute");
        globalLookupPopupWindowCode.Should().Contain("SetWindowRgn");
        globalLookupPopupWindowCode.Should().NotContain("ApplyHostSurface(request.Theme)");
        globalLookupWindowCode.Should().Contain("_desktopAcrylicThinBackdrop = DictionaryPopupMaterial.TryApplyDesktopAcrylicThin(this, RootGrid)");
        globalLookupWindowCode.Should().Contain("RootGrid.Background = DictionaryPopupMaterial.CreateWindowFallbackBrush");
        var dictionaryLookupPopupCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs")
        );
        var popupCss = File.ReadAllText(
            Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.css")
        );
        dictionaryLookupPopupCode.Should().Contain("UseStandaloneWindowVisuals");
        dictionaryLookupPopupCode.Should().Contain("UseNakedFloatingWindowVisuals");
        dictionaryLookupPopupCode.Should().NotContain("ThemeShadow");
        dictionaryLookupPopupCode.Should().NotContain("VisualRoot.Shadow");
        dictionaryLookupPopupCode.Should().NotContain("VisualRoot.Translation");
        dictionaryLookupPopupCode.Should().NotContain("using System.Numerics;");
        dictionaryLookupPopupCode.Should().NotContain("VisualRoot.BorderThickness");
        dictionaryLookupPopupCode.Should().Contain("VisualRoot.CornerRadius = new CornerRadius(_popupCornerRadius)");
        dictionaryLookupPopupCode.Should().Contain("Background = _surfaceBrush");
        dictionaryLookupPopupCode.Should().Contain("DictionaryPopupCornerGuard.CalculateInset(_popupCornerRadius)");
        dictionaryLookupPopupCode.Should().Contain("DefaultBackgroundColor = initialSurfaceColor");
        dictionaryLookupPopupCode.Should().Contain("_contentWebView.Margin = new Thickness(-1)");
        dictionaryLookupPopupCode.Should().Contain("IsTabStop = false");
        dictionaryLookupPopupCode.Should().Contain("UseSystemFocusVisuals = false");
        popupCss.Should().Contain("html:focus");
        popupCss.Should().Contain("body:focus-visible");
        popupCss.Should().Contain("outline: none");
        File.ReadAllText(
                Path.Combine(ProjectRoot, "Services", "Dictionary", "GlobalLookupWindowService.cs")
            )
            .Should()
            .Contain("Manual lookup window requested");

        foreach (var key in new[]
        {
            "GlobalLookupWindow.Title",
            "GlobalLookupQueryBox.PlaceholderText",
            "GlobalLookupSearchButtonText.Text",
            "GlobalLookupPasteButton.AutomationProperties.Name",
            "GlobalLookupEnabledCard.Header",
            "GlobalLookupEnabledCard.Description",
            "PopupAppearanceSectionHeader.Text",
            "PopupWidthCard.Header",
            "PopupWidthCard.Description",
            "PopupHeightCard.Header",
            "PopupHeightCard.Description",
            "PopupScaleCard.Header",
            "PopupScaleCard.Description",
            "PopupActionBarCard.Header",
            "PopupActionBarCard.Description",
            "PopupFullWidthCard.Header",
            "PopupFullWidthCard.Description",
        })
        {
            enResources.Should().Contain(key);
            zhResources.Should().Contain(key);
        }
    }

    [Fact]
    public void GlobalLookupWindow_DoesNotAssignWindowTitleFromXaml()
    {
        var windowXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "GlobalLookupWindow.xaml")
        );
        var windowCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "GlobalLookupWindow.xaml.cs")
        );

        windowXaml.Should().NotContain("x:Uid=\"GlobalLookupWindow\"");
        windowXaml.Should().NotContain("Title=\"Global Lookup\"");
        windowCode.Should().Contain("Title =");
    }

    [Fact]
    public void DictionaryDisplaySettings_MatchAndroidLookupDefaults()
    {
        var settingsCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Models", "Settings", "DictionaryDisplaySettings.cs")
        );
        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs")
        );
        var popupCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Dictionary", "PopupHtmlGenerator.cs")
        );

        settingsCode.Should().Contain("int MaxResults = 16");
        settingsCode.Should().Contain("int ScanLength = 16");
        settingsCode.Should().Contain("int PopupMaxWidth = 320");
        settingsCode.Should().Contain("int PopupMaxHeight = 250");
        settingsCode.Should().Contain("double PopupScale = 1.0");
        settingsCode.Should().Contain("bool PopupActionBar = false");
        settingsCode.Should().Contain("bool PopupFullWidth = false");
        settingsCode.Should().Contain("bool DictionaryTabDefault = false");
        settingsCode.Should().Contain("DictionaryCollapseMode CollapseMode = DictionaryCollapseMode.ExpandAll");
        settingsCode.Should().Contain("bool ExpandFirstDictionary = false");
        overlayCode.Should().Contain("LookupAsync(");
        overlayCode.Should().Contain("_displaySettings.MaxResults");
        overlayCode.Should().Contain("_displaySettings.ScanLength");
        overlayCode.Should().Contain("_displaySettings.PopupMaxWidth");
        overlayCode.Should().Contain("_displaySettings.PopupMaxHeight");
        overlayCode.Should().Contain("_displaySettings.PopupFullWidth");
        overlayCode.Should().NotContain("ChildPopupMaxWidth");
        overlayCode.Should().NotContain("ChildPopupMaxHeight");
        overlayCode.Should().NotContain("HardMaxPopupWidth");
        overlayCode.Should().NotContain("HardMaxPopupHeight");
        popupCode.Should().Contain("window.maxResults");
        popupCode.Should().Contain("window.scanLength");
    }

    [Fact]
    public void UserFacingStrings_ArePreparedForWinUiResourceLocalization()
    {
        var projectRoot = ProjectRoot;
        var appCode = File.ReadAllText(Path.Combine(projectRoot, "App.xaml.cs"));
        var agents = File.ReadAllText(Path.Combine(projectRoot, "..", "agents.md"));
        var verification = File.ReadAllText(Path.Combine(projectRoot, "..", "docs", "VERIFICATION.md"));
        var settingsXaml = File.ReadAllText(Path.Combine(projectRoot, "Views", "Pages", "SettingsPage.xaml"));
        var appearanceXaml = File.ReadAllText(Path.Combine(projectRoot, "Views", "Controls", "ReaderAppearanceSettingsContent.xaml"));
        var dialogXaml = File.ReadAllText(Path.Combine(projectRoot, "Views", "Dialogs", "ReaderAppearanceDialog.xaml"));
        var readerXaml = File.ReadAllText(Path.Combine(projectRoot, "Views", "Pages", "NovelReaderPage.xaml"));

        File.Exists(Path.Combine(projectRoot, "Strings", "en-US", "Resources.resw")).Should().BeTrue();
        File.Exists(Path.Combine(projectRoot, "Strings", "zh-CN", "Resources.resw")).Should().BeTrue();
        appCode.Should().NotContain("PrimaryLanguageOverride = \"en-US\"");
        settingsXaml.Should().NotContain("Header=\"Application Theme\"");
        appearanceXaml.Should().NotContain("Header=\"Color Theme\"");
        dialogXaml.Should().NotContain("Title=\"Reader Appearance\"");
        dialogXaml.Should().NotContain("PrimaryButtonText=\"Done\"");
        readerXaml.Should().Contain("x:Uid=\"NovelReaderAppearanceButton\"");
        readerXaml.Should().NotContain("AutomationProperties.Name=\"Reader Appearance\"");
        verification.Should().Contain("新增用户可见功能必须同步 i18n");
    }

    [Fact]
    public void DictionaryLookupPopup_UsesOverlayCanvasBoundsForPopupPlacement()
    {
        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs")
        );
        var readerXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml")
        );
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );

        overlayCode.Should().Contain("GetOverlaySize()");
        overlayCode.Should().Contain("_canvas.ActualWidth");
        overlayCode.Should().Contain("_canvas.Width = double.NaN");
        overlayCode.Should().Contain("_canvas.Visibility = Visibility.Collapsed");
        overlayCode.Should().Contain("DictionaryPopupLayoutCalculator.Resolve");
        overlayCode.Should().Contain("Canvas.SetLeft");
        overlayCode.Should().Contain("Canvas.SetTop");
        overlayCode.Should().NotContain("App.MainWindow");
        overlayCode.Should().NotContain("window.Bounds");
        readerXaml.Should().Contain("x:Name=\"DictionaryOverlayCanvas\"");
        readerXaml.Should().Contain("Grid.Row=\"1\"");
        readerXaml.Should().NotContain("Grid.RowSpan=\"2\"\r\n                HorizontalAlignment=\"Stretch\"\r\n                VerticalAlignment=\"Stretch\"\r\n                Background=\"Transparent\"\r\n                Visibility=\"Collapsed\"\r\n                AutomationProperties.AutomationId=\"DictionaryOverlayCanvas\"");
        readerCode.Should().Contain("NovelWebView.TransformToVisual(DictionaryOverlayCanvas)");
        readerCode.Should().NotContain("NovelWebView.TransformToVisual(null)");
    }

    [Fact]
    public void DictionaryPopupOverlay_UsesHoshiReaderMacPopupPlacement()
    {
        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs")
        );
        var popupJs = File.ReadAllText(
            Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.js")
        );
        var layoutCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupLayoutCalculator.cs")
        );

        overlayCode.Should().Contain("aligned with Hoshi Reader Mac PopupLayout");
        overlayCode.Should().Contain("DictionaryPopupLayoutCalculator.Resolve");
        layoutCode.Should().Contain("public const double ScreenBorderPadding = 6;");
        layoutCode.Should().Contain("public const double PopupPadding = 4;");
        layoutCode.Should().Contain("ShowOnRight(selection.X, selection.Width, screenWidth, maxWidth)");
        layoutCode.Should().Contain("SpaceRight(x, w, screenWidth) >= SpaceLeft(x) || SpaceRight(x, w, screenWidth) >= popupWidth");
        layoutCode.Should().Contain("ShowBelow(selection.Y, selection.Height, screenHeight, maxHeight)");
        layoutCode.Should().Contain("SpaceBelow(y, h, screenHeight) >= SpaceAbove(y) || SpaceBelow(y, h, screenHeight) >= popupHeight");
        layoutCode.Should().Contain("var availableHeight = showBelow ? spaceBelow : spaceAbove");
        layoutCode.Should().Contain("height = ClampPopupExtent(availableHeight - ScreenBorderPadding, maxHeight)");
        layoutCode.Should().Contain("height = maxHeight");
        layoutCode.Should().Contain("selection.X + selection.Width / 2");
        layoutCode.Should().Contain("if (isFullWidth)");
        layoutCode.Should().Contain("centerX = screenWidth / 2");
        layoutCode.Should().Contain("centerY = screenHeight - ScreenBorderPadding - height / 2");
        overlayCode.Should().NotContain("centerX = screenWidth / 2");
        overlayCode.Should().Contain("_displaySettings.PopupFullWidth");
        overlayCode.Should().Contain("PositionChildHost(child");
        overlayCode.Should().Contain("parentLeft + x");
        overlayCode.Should().NotContain("PositionHostAboveOrBelowParent");
        overlayCode.Should().Contain("GetHostBounds(parentHost)");
        overlayCode.Should().Contain("ClearChildrenAfter(host)");
        overlayCode.Should().Contain("CloseChildrenOfParent(parent)");
        overlayCode.Should().Contain("OnRootTapOutsideRequested");
        overlayCode.Should().Contain("OnChildTapOutsideRequested");
        overlayCode.Should().Contain("public void Dismiss()");
        overlayCode.Should().Contain("private void RemoveChild");
        overlayCode.Should().Contain("for (var i = _childHosts.Count - 1; i > parentIndex; i--)");
        overlayCode.Should().Contain("OnOverlayPointerPressed");
        overlayCode.Should().Contain("_canvas.IsHitTestVisible = true");
        overlayCode.Should().Contain("_canvas.IsHitTestVisible = false");
        overlayCode.Should().Contain("Dismissed?.Invoke");
        popupJs.Should().Contain("query: selected");
        popupJs.Should().Contain("getSelectionRect");
        popupJs.Should().Contain("generation !== (window.popupRenderGeneration || 0)");
        popupJs.Should().Contain("document.documentElement.style.visibility = 'hidden'");
        popupJs.Should().Contain("document.documentElement.style.visibility = 'visible'");
        popupJs.Should().Contain("postPopupMessage('contentReady', { generation: generation })");
        popupJs.Split(
                "postPopupMessage('contentReady', { generation: generation });",
                StringSplitOptions.None)
            .Should()
            .HaveCount(2);
        popupJs.Should().Contain("function commitFirstFrame(generation, entryDiv)");
        popupJs.Should().Contain("function renderAvailableEntries()");
        popupJs.Should().Contain("postPopupMessage('tapOutside', null);");
    }

    [Fact]
    public void DictionaryPopupOverlay_AnchorsChildPopupToParentSelectionRect()
    {
        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs")
        );
        var popupCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs")
        );
        var popupGenerator = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Dictionary", "PopupHtmlGenerator.cs")
        );

        overlayCode.Should().Contain("HighlightPopupSelectionAsync(parent, results[0].Matched)");
        overlayCode.Should().Contain("var anchorX = parentLeft + x;");
        overlayCode.Should().Contain("var anchorY = parentTop + y;");
        overlayCode.Should().Contain("var anchorWidth = request.Width.GetValueOrDefault(1);");
        overlayCode.Should().Contain("var anchorHeight = request.Height.GetValueOrDefault(1);");
        overlayCode.Should().Contain("PositionHost(host, anchorX, anchorY, anchorWidth, anchorHeight");
        overlayCode.Should().NotContain("PositionHostAboveOrBelowParent(host, parentHost, parentLeft + x)");
        popupCode.Should().Contain("public async Task HighlightSelectionAsync(string matchedText)");
        popupCode.Should().Contain("window.hoshiSelection.highlightSelection");
        popupGenerator.Should().Contain("highlightSelection: function (charCount)");
        popupGenerator.Should().Contain("selectionCharacterRanges: function (charCount)");
        popupGenerator.Should().Contain("CSS.highlights?.set('hoshi-selection', new Highlight(...highlights))");
    }

    [Fact]
    public void DictionaryPopupOverlay_StacksChildPopupsAboveTheirParents()
    {
        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs")
        );

        overlayCode.Should().Contain("RootPopupZIndex");
        overlayCode.Should().Contain("ChildPopupZIndexBase");
        overlayCode.Should().Contain("PopupZIndexStep");
        overlayCode.Should().Contain("ApplyPopupZOrder()");
        overlayCode.Should().Contain("Canvas.SetZIndex(_rootHost.VisualRoot, RootPopupZIndex)");
        overlayCode.Should().Contain("Canvas.SetZIndex(_childHosts[i].VisualRoot, ChildPopupZIndexBase + i * PopupZIndexStep)");
        overlayCode.Should().Contain("EnsureHostOnCanvas(child)");
        overlayCode.Should().NotContain("if (!_canvas.Children.Contains(child.VisualRoot))\r\n                _canvas.Children.Add(child.VisualRoot);");
    }

    [Fact]
    public void DictionaryPopupOverlay_DoesNotCapRecursiveChildPopupDepth()
    {
        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs")
        );

        overlayCode.Should().NotContain("MaxChildPopups");
        overlayCode.Should().NotContain("_childHosts.Count >= MaxChildPopups");
        overlayCode.Should().NotContain("_childHosts.RemoveAt(0)");
        overlayCode.Should().Contain("CloseChildrenOfParent(parent)");
        overlayCode.Should().Contain("ClearChildrenAfter(parent)");
        overlayCode.Should().Contain("_childHosts.Add(child)");
    }

    [Fact]
    public void DictionaryPopup_AutoPlaysFirstEntryWhenAudioAutoplayIsEnabled()
    {
        var popupJs = File.ReadAllText(
            Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.js")
        );

        popupJs.Should().Contain("window.audioEnableAutoplay && (window.audioSources || []).length && idx === 0");
        popupJs.Should().Contain("var autoplayEntryIndex = idx");
        popupJs.Should().Contain("var autoplayAudioTraceId = nextAudioTraceId()");
        popupJs.Should().Contain("postAudioTrace(autoplayAudioTraceId, 'autoplay-scheduled'");
        popupJs.Should().Contain("setTimeout(function () {");
        popupJs.Should().Contain("postAudioTrace(autoplayAudioTraceId, 'autoplay-fired'");
        popupJs.Should().Contain("playEntryAudio(autoplayEntryIndex, autoplayAudioTraceId, { deferResolutionToNative: true });");
        popupJs.Should().Contain("fetch-url-deferred-to-native");
    }

    [Fact]
    public void DictionaryLookupAudioPath_EmitsEndToEndLatencyTraceLogs()
    {
        var selectionJs = File.ReadAllText(Path.Combine(ReaderRoot, "selection.js"));
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs")
        );
        var popupCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs")
        );
        var popupJs = File.ReadAllText(
            Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.js")
        );
        var lookupServiceCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Dictionary", "DictionaryLookupService.cs")
        );
        var lookupInterfaceCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Dictionary", "IDictionaryLookupService.cs")
        );
        var audioServiceCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Audio", "AudioService.cs")
        );
        var audioInterfaceCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Audio", "IAudioService.cs")
        );

        selectionJs.Should().Contain("nextLookupTraceId()");
        selectionJs.Should().Contain("traceId");
        selectionJs.Should().Contain("clientNow: performance.now()");

        readerCode.Should().Contain("[LookupTrace] trace={TraceId} received");
        readerCode.Should().Contain("[LookupTrace] trace={TraceId} native lookup finished");
        readerCode.Should().Contain("[LookupTrace] trace={TraceId} styles loaded");
        readerCode.Should().Contain("[LookupTrace] trace={TraceId} popup show completed");
        readerCode.Should().Contain("lookupService.LookupAsync(");
        readerCode.Should().Contain("traceId: traceId");
        overlayCode.Should().Contain("string? traceId = null");
        overlayCode.Should().Contain("[LookupTrace] trace={TraceId} root popup content injected");
        popupCode.Should().Contain("string? traceId = null");
        popupCode.Should().Contain("_currentTraceId = traceId");
        popupCode.Should().Contain("[LookupTrace] trace={TraceId} popup initial ExecuteScriptAsync finished");
        popupCode.Should().Contain("[AudioTrace] lookup={TraceId} audio={AudioTraceId} popup-js stage={Stage}");
        popupCode.Should().Contain("[AudioTrace] lookup={TraceId} audio={AudioTraceId} native message received");
        popupCode.Should().Contain("[AudioTrace] lookup={TraceId} audio={AudioTraceId} audio service returned");
        popupCode.Should().Contain("\"https://hoshi-audio-resolver.local/*\"");
        popupCode.Should().Contain("HandleAudioResolverRequestAsync");
        popupCode.Should().Contain("IsAllowedAudioResolverUrl");
        popupCode.Should().Contain("NormalizeAudioSourceUrl");
        popupCode.Should().Contain("using var response = await s_audioResolveHttpClient.GetAsync");
        popupCode.Should().Contain("Access-Control-Allow-Origin: *");
        popupJs.Should().Contain("nextAudioTraceId()");
        popupJs.Should().Contain("postAudioTrace(");
        popupJs.Should().Contain("audioTraceId");
        popupJs.Should().Contain("lookupTraceId");
        popupJs.Should().Contain("window.audioRequestEndpoint || ''");
        popupJs.Should().Contain("fetch-url-proxy-resolved");
        popupJs.Should().Contain("fetchAudioUrl(entry.expression, entry.reading || entry.expression");
        lookupInterfaceCode.Should().Contain("string? traceId = null");
        lookupServiceCode.Should().Contain("[LookupTrace] trace={TraceId} ensure-index completed");
        lookupServiceCode.Should().Contain("[LookupTrace] trace={TraceId} native lookup completed");
        lookupServiceCode.Should().Contain("[LookupTrace] trace={TraceId} deserialize/display completed");
        audioInterfaceCode.Should().Contain("string? traceId = null");
        audioInterfaceCode.Should().Contain("string? audioTraceId = null");
        audioServiceCode.Should().Contain("[AudioTrace] lookup={TraceId} audio={AudioTraceId} download completed");
        audioServiceCode.Should().Contain("[AudioTrace] lookup={TraceId} audio={AudioTraceId} playback started");
        audioServiceCode.Should().Contain("[AudioTrace] lookup={TraceId} audio={AudioTraceId} playback ended");
    }

    [Fact]
    public void DictionaryPopupOverlay_ReusesChildWebViewHostsDuringRedirectStorms()
    {
        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs")
        );
        var popupCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs")
        );
        var popupJs = File.ReadAllText(
            Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.js")
        );

        overlayCode.Should().Contain("_redirectSemaphore");
        overlayCode.Should().Contain("_redirectVersion");
        overlayCode.Should().Contain("Interlocked.Increment(ref _redirectVersion)");
        overlayCode.Should().Contain("PrewarmedChildHostCount = 1");
        overlayCode.Should().Contain("await PrewarmChildHostPoolAsync(PrewarmedChildHostCount, themeMode)");
        overlayCode.Should().Contain("await child.WarmAsync(themeMode)");
        overlayCode.Should().Contain("NestedLookupMaxResults = 1");
        overlayCode.Should().Contain("redirectMode == DictionaryPopupRedirectMode.InPlace");
        overlayCode.Should().Contain("Math.Min(_displaySettings.MaxResults, NestedLookupMaxResults)");
        overlayCode.Should().Contain("redirectMaxResults,");
        overlayCode.Should().Contain("GetReusableChildHost");
        overlayCode.Should().Contain("HideChildHost");
        overlayCode.Should().Contain("host.VisualRoot.Opacity <= 0");
        overlayCode.Should().Contain("await child.ShowResultsWarmAsync");
        overlayCode.Should().NotContain("ShowResultsNavigatedAsync");
        popupCode.Should().Contain("WaitForShellReadyAsync");
        popupCode.Should().NotContain("public async Task ShowResultsNavigatedAsync");
        popupCode.Should().NotContain("GenerateHtml(results");
        overlayCode.Should().NotContain("child.Dispose();");
        overlayCode.Should().NotContain("oldest.Dispose();");
        popupJs.Should().Contain("lastShiftLookupQuery");
        popupJs.Should().Contain("lastShiftLookupAt");
    }

    [Fact]
    public void DictionaryPopupMineButton_IgnoresClicksWhilePending()
    {
        var popupJs = File.ReadAllText(
            Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.js")
        );

        popupJs.Should().Contain("if (slot.dataset.state === 'pending' || slot.dataset.enabled === 'false') return;");
        popupJs.Should().Contain("if (mineSlot && mineSlot.dataset.state === 'pending') return;");
        popupJs.Should().Contain("let miningRequestPending = false;");
        popupJs.Should().Contain("if (miningRequestPending) return;");
        popupJs.Should().Contain("miningRequestPending = true;");
        popupJs.Should().Contain("if (kind === 'mine' && miningRequestPending) return;");
        popupJs.Should().Contain("finally {");
        popupJs.Should().Contain("if (!submitted) miningRequestPending = false;");
        popupJs.Replace("\r\n", "\n").Should().Contain("window.onMineComplete = function (success) {\n  miningRequestPending = false;");
    }

    [Fact]
    public void ReaderShiftLookup_ReplacesExistingRootPopup()
    {
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs")
        );

        readerCode.Should().Contain("Interlocked.Increment(ref _lookupRequestVersion)");
        readerCode.Should().Contain("_popupOverlay?.Dismiss();");
        overlayCode.Should().Contain("public void Dismiss()");
        overlayCode.Should().Contain("_canvas.Visibility = Visibility.Visible");
        overlayCode.Should().NotContain("IsLightDismissEnabled = true");
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
    public void NovelLibraryPage_ExposesSasayakiMatchAction()
    {
        var libraryXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml")
        );
        var libraryViewModel = File.ReadAllText(
            Path.Combine(ProjectRoot, "ViewModels", "Pages", "NovelLibraryPageViewModel.cs")
        );
        var appCode = File.ReadAllText(Path.Combine(ProjectRoot, "App.xaml.cs"));
        var enResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw")
        );
        var zhResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw")
        );

        libraryXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelBookMatchSasayakiMenuItem\"");
        libraryXaml.Should().Contain("x:Uid=\"NovelBookMatchSasayakiMenuItem\"");
        libraryXaml.Should().Contain("MatchSasayakiCommand");
        libraryViewModel.Should().Contain("ISasayakiMatchService");
        libraryViewModel.Should().Contain("MatchSasayakiAsync");
        libraryViewModel.Should().Contain("OpenFilePickerAsync(\".mp3\", \".m4b\"");
        appCode.Should().Contain("ISasayakiMatchService, SasayakiMatchService");
        enResources.Should().Contain("NovelBookMatchSasayakiMenuItem.Text");
        zhResources.Should().Contain("NovelBookMatchSasayakiMenuItem.Text");
    }

    [Fact]
    public void NovelLibraryPage_ExposesMacAlignedDropImportAndSortControls()
    {
        var libraryXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml")
        );
        var libraryCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml.cs")
        );
        var libraryViewModel = File.ReadAllText(
            Path.Combine(ProjectRoot, "ViewModels", "Pages", "NovelLibraryPageViewModel.cs")
        );
        var libraryService = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Novels", "INovelLibraryService.cs")
        );
        var bookStorageService = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Novels", "INovelBookStorageService.cs")
        );
        var appSettings = File.ReadAllText(
            Path.Combine(ProjectRoot, "Models", "Settings", "AppSettings.cs")
        );
        var databaseMigrator = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Storage", "DatabaseMigrator.cs")
        );

        libraryXaml.Should().Contain("AllowDrop=\"True\"");
        libraryXaml.Should().Contain("DragOver=\"NovelLibrary_DragOver\"");
        libraryXaml.Should().Contain("Drop=\"NovelLibrary_Drop\"");
        libraryXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelLibrarySortComboBox\"");
        libraryXaml.Should().Contain("SelectedValue=\"{x:Bind ViewModel.SelectedSortOption, Mode=TwoWay}\"");
        libraryXaml.Should().Contain("x:Name=\"NovelShelfSectionsControl\"");
        libraryXaml.Should().Contain("x:Name=\"NovelUnshelvedBooksRepeater\"");
        libraryXaml.Should().Contain("<UniformGridLayout");
        libraryXaml.Should().Contain("Click=\"MoveNovelToShelfMenuItem_Click\"");

        libraryCode.Should().Contain("GetStorageItemsAsync");
        libraryCode.Should().Contain("StorageFile");
        libraryCode.Should().Contain("ImportDroppedNovelsCommand");
        libraryCode.Should().Contain("MoveBookCommand");

        libraryViewModel.Should().Contain("NovelLibrarySortOption");
        libraryViewModel.Should().Contain("SelectedSortOption");
        libraryViewModel.Should().Contain("ImportDroppedNovelsAsync");
        libraryViewModel.Should().Contain("MoveNovelBeforeAsync");
        libraryViewModel.Should().Contain("SaveCurrentManualOrderAsync");
        libraryViewModel.Should().Contain("ReorderShelfBookAsync");

        libraryService.Should().Contain("SaveNovelBookOrderAsync");
        bookStorageService.Should().Contain("SaveBookOrderAsync");
        appSettings.Should().Contain("NovelLibrarySortOption");
        databaseMigrator.Should().Contain("new Migration_008()");
    }

    [Fact]
    public void NovelLibraryPage_ExposesStatisticsDashboard()
    {
        var libraryXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml")
        );
        var libraryViewModel = File.ReadAllText(
            Path.Combine(ProjectRoot, "ViewModels", "Pages", "NovelLibraryPageViewModel.cs")
        );
        var appCode = File.ReadAllText(Path.Combine(ProjectRoot, "App.xaml.cs"));
        var dashboardServicePath = Path.Combine(
            ProjectRoot,
            "Services",
            "Novels",
            "NovelStatisticsDashboardService.cs");

        File.Exists(dashboardServicePath).Should().BeTrue();
        var dashboardService = File.ReadAllText(dashboardServicePath);

        libraryXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelLibraryStatisticsButton\"");
        libraryXaml.Should().Contain("IsChecked=\"{x:Bind ViewModel.ShowStatisticsDashboard, Mode=TwoWay}\"");
        libraryXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelLibraryStatisticsDashboard\"");
        libraryXaml.Should().Contain("NovelLibraryTodayStatisticsText");
        libraryXaml.Should().Contain("NovelLibraryWeekStatisticsText");
        libraryXaml.Should().Contain("NovelLibraryByBookStatisticsList");

        libraryViewModel.Should().Contain("INovelStatisticsDashboardService");
        libraryViewModel.Should().Contain("ShowStatisticsDashboard");
        libraryViewModel.Should().Contain("StatisticsTodayText");
        libraryViewModel.Should().Contain("StatisticsWeekText");
        libraryViewModel.Should().Contain("StatisticsDistributionRows");

        dashboardService.Should().Contain("NovelStatisticsDashboardCalculator");
        dashboardService.Should().Contain("LoadSnapshotAsync");
        appCode.Should().Contain("INovelStatisticsDashboardService, NovelStatisticsDashboardService");
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

    [Fact]
    public void ReaderPage_ExposesBookSearchPanelAndResultJump()
    {
        var readerXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml")
        );
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );

        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderSearchButton\"");
        readerXaml.Should().Contain("x:Uid=\"NovelReaderSearchButton\"");
        readerXaml.Should().Contain("x:Name=\"ReaderSearchPanelDialog\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderSearchPanelDialog\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderSearchQueryBox\"");
        readerXaml.Should().Contain("x:Uid=\"ReaderSearchQueryBox\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderSearchResultsList\"");
        readerXaml.Should().Contain("x:Name=\"ReaderSearchStatusPanel\"");
        readerXaml.Should().Contain("x:Name=\"ReaderSearchLoadingRing\"");
        readerXaml.Should().Contain("x:Name=\"ReaderSearchPromptText\"");
        readerXaml.Should().Contain("x:Name=\"ReaderSearchLoadingText\"");
        readerXaml.Should().Contain("x:Name=\"ReaderSearchNoMatchesText\"");
        readerXaml.Should().Contain("x:Name=\"ReaderSearchFailedText\"");
        readerXaml.Should().Contain("SnippetBeforeMatch");
        readerXaml.Should().Contain("SnippetMatch");
        readerXaml.Should().Contain("SnippetAfterMatch");
        readerXaml.Should().Contain("AccentTextFillColorPrimaryBrush");
        readerXaml.Should().NotContain("Text=\"{x:Bind Snippet}\"");
        var enResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw")
        );
        var zhResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw")
        );
        enResources.Should().Contain("NovelReaderSearchButton.AutomationProperties.Name");
        enResources.Should().Contain("ReaderSearchPanelDialog.Title");
        enResources.Should().Contain("ReaderSearchPanelDialog.CloseButtonText");
        enResources.Should().Contain("ReaderSearchQueryBox.PlaceholderText");
        enResources.Should().Contain("ReaderSearchPromptText.Text");
        enResources.Should().Contain("ReaderSearchLoadingText.Text");
        enResources.Should().Contain("ReaderSearchNoMatchesText.Text");
        enResources.Should().Contain("ReaderSearchFailedText.Text");
        zhResources.Should().Contain("NovelReaderSearchButton.AutomationProperties.Name");
        zhResources.Should().Contain("ReaderSearchPanelDialog.Title");
        zhResources.Should().Contain("ReaderSearchPanelDialog.CloseButtonText");
        zhResources.Should().Contain("ReaderSearchQueryBox.PlaceholderText");
        zhResources.Should().Contain("ReaderSearchPromptText.Text");
        zhResources.Should().Contain("ReaderSearchLoadingText.Text");
        zhResources.Should().Contain("ReaderSearchNoMatchesText.Text");
        zhResources.Should().Contain("ReaderSearchFailedText.Text");
        readerCode.Should().Contain("ReaderSearchDocumentFactory.CreateAsync");
        readerCode.Should().Contain("new ReaderSearchEngine");
        readerCode.Should().Contain("ReaderSearchPanelStatus.Prompt");
        readerCode.Should().Contain("ReaderSearchPanelStatus.Loading");
        readerCode.Should().Contain("ReaderSearchPanelStatus.NoMatches");
        readerCode.Should().Contain("ReaderSearchPanelStatus.Failed");
        readerCode.Should().Contain("SetReaderSearchStatus");
        readerCode.Should().Contain("SearchResult_ItemClick");
        readerCode.Should().Contain("LoadChapter(result.ChapterIndex, result.ChapterProgress)");
    }

    [Fact]
    public void ReaderPage_LoadsPersistedHighlightsIntoReaderWebView()
    {
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var bridgeScript = File.ReadAllText(Path.Combine(ReaderRoot, "reader-bridge.js"));
        var appCode = File.ReadAllText(Path.Combine(ProjectRoot, "App.xaml.cs"));
        var highlightsPath = Path.Combine(ReaderRoot, "highlights.js");

        File.Exists(highlightsPath).Should().BeTrue();
        var highlightsScript = File.ReadAllText(highlightsPath);
        highlightsScript.Should().Contain("window.hoshiHighlights");
        highlightsScript.Should().Contain("applyHighlights");
        highlightsScript.Should().Contain("removeHighlight");
        highlightsScript.Should().Contain("collectSegments");
        highlightsScript.Should().Contain("hoshi-highlight-yellow");
        readerCode.Should().Contain("_highlightsJs");
        readerCode.Should().Contain("highlights.js");
        readerCode.Should().Contain("ViewModel.LoadHighlightsAsync");
        readerCode.Should().Contain("ViewModel.GetCurrentChapterHighlightsJson()");
        readerCode.Should().Contain("window.__hoshiChapterHighlights");
        bridgeScript.Should().Contain("window.hoshiHighlights.applyHighlights(window.__hoshiChapterHighlights || [])");
        appCode.Should().Contain("IReaderHighlightService, ReaderHighlightService");
    }

    [Fact]
    public void ReaderPage_WritesMacCompatibleBookInfoSidecarAfterCountingChapters()
    {
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var viewModelCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "ViewModels", "Pages", "NovelReaderPageViewModel.cs")
        );
        var appCode = File.ReadAllText(Path.Combine(ProjectRoot, "App.xaml.cs"));

        readerCode.Should().Contain("ViewModel.SetChapterCharacterCounts(_chapterCharacterCounts)");
        readerCode.Should().Contain("ViewModel.SaveBookInfoSidecarAsync");
        readerCode.Should().Contain("_epubBook.Chapters");
        readerCode.Should().Contain("_epubBook.ContainerDirectory");
        viewModelCode.Should().Contain("SaveBookInfoSidecarAsync");
        viewModelCode.Should().NotContain("SaveBookmarkSidecarAsync");
        viewModelCode.Should().Contain("_novelLibraryService.SaveProgressAsync");
        appCode.Should().Contain("INovelBookSidecarService, NovelBookSidecarService");
    }

    [Fact]
    public void ReaderPage_ExposesHighlightListPanelAndActions()
    {
        var readerXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml")
        );
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var viewModelCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "ViewModels", "Pages", "NovelReaderPageViewModel.cs")
        );
        var enResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw")
        );
        var zhResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw")
        );

        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderHighlightsButton\"");
        readerXaml.Should().Contain("x:Uid=\"NovelReaderHighlightsButton\"");
        readerXaml.Should().Contain("x:Name=\"ReaderHighlightsPanelDialog\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderHighlightsPanelDialog\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderHighlightsList\"");
        readerXaml.Should().Contain("ReaderHighlightDeleteMenuItem_Click");
        readerXaml.Should().Contain("SolidColorBrush Color=\"{x:Bind SwatchColor}\"");
        readerXaml.Should().NotContain("Background=\"{ThemeResource SystemAccentColor}\"");
        readerCode.Should().Contain("HighlightsButton_Click");
        readerCode.Should().Contain("RefreshHighlightList");
        readerCode.Should().Contain("Highlight_ItemClick");
        readerCode.Should().Contain("DeleteHighlightAsync");
        readerCode.Should().Contain("RemoveHighlight");
        viewModelCode.Should().Contain("GetHighlightListItems");
        viewModelCode.Should().Contain("DeleteHighlightAsync");
        enResources.Should().Contain("NovelReaderHighlightsButton.AutomationProperties.Name");
        enResources.Should().Contain("ReaderHighlightsPanelDialog.Title");
        enResources.Should().Contain("ReaderHighlightsPanelDialog.CloseButtonText");
        enResources.Should().Contain("ReaderHighlightsPanelTitle.Text");
        zhResources.Should().Contain("NovelReaderHighlightsButton.AutomationProperties.Name");
        zhResources.Should().Contain("ReaderHighlightsPanelDialog.Title");
        zhResources.Should().Contain("ReaderHighlightsPanelDialog.CloseButtonText");
        zhResources.Should().Contain("ReaderHighlightsPanelTitle.Text");
    }

    [Fact]
    public void ReaderPage_ExposesStatisticsPanelAndTrackingActions()
    {
        var readerXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml")
        );
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var viewModelCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "ViewModels", "Pages", "NovelReaderPageViewModel.cs")
        );
        var enResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw")
        );
        var zhResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw")
        );

        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderStatisticsButton\"");
        readerXaml.Should().Contain("x:Uid=\"NovelReaderStatisticsButton\"");
        readerXaml.Should().Contain("x:Name=\"ReaderStatisticsPanelDialog\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderStatisticsPanelDialog\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderStatisticsStartStopButton\"");
        readerXaml.Should().Contain("StatisticsButton_Click");
        readerXaml.Should().Contain("StatisticsStartStopButton_Click");
        readerCode.Should().Contain("ViewModel.LoadStatisticsAsync");
        readerCode.Should().Contain("StatisticsButton_Click");
        readerCode.Should().Contain("StatisticsStartStopButton_Click");
        readerCode.Should().Contain("RefreshStatisticsPanel");
        viewModelCode.Should().Contain("StartStatisticsTracking");
        viewModelCode.Should().Contain("StopStatisticsTrackingAsync");
        viewModelCode.Should().Contain("FlushStatisticsAsync");
        viewModelCode.Should().Contain("INovelStatisticsSidecarService");
        enResources.Should().Contain("NovelReaderStatisticsButton.AutomationProperties.Name");
        enResources.Should().Contain("ReaderStatisticsPanelDialog.Title");
        enResources.Should().Contain("ReaderStatisticsPanelDialog.CloseButtonText");
        enResources.Should().Contain("ReaderStatisticsPanelTitle.Text");
        enResources.Should().Contain("ReaderStatisticsSessionHeader.Text");
        zhResources.Should().Contain("NovelReaderStatisticsButton.AutomationProperties.Name");
        zhResources.Should().Contain("ReaderStatisticsPanelDialog.Title");
        zhResources.Should().Contain("ReaderStatisticsPanelDialog.CloseButtonText");
        zhResources.Should().Contain("ReaderStatisticsPanelTitle.Text");
        zhResources.Should().Contain("ReaderStatisticsSessionHeader.Text");
    }

    [Fact]
    public void ReaderAppearance_ExposesMacAlignedStatisticsChromeSettings()
    {
        var appearanceXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Controls", "ReaderAppearanceSettingsContent.xaml")
        );
        var settingsViewModel = File.ReadAllText(
            Path.Combine(ProjectRoot, "ViewModels", "Pages", "SettingsPageViewModel.cs")
        );
        var readerSettings = File.ReadAllText(
            Path.Combine(ProjectRoot, "Models", "Settings", "ReaderSettings.cs")
        );
        var readerXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml")
        );
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var enResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw")
        );
        var zhResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw")
        );

        readerSettings.Should().Contain("ShowStatisticsToggle");
        readerSettings.Should().Contain("ShowReadingSpeed");
        readerSettings.Should().Contain("ShowReadingTime");

        appearanceXaml.Should().Contain("x:Uid=\"ReaderShowStatisticsToggleCard\"");
        appearanceXaml.Should().Contain("AutomationProperties.AutomationId=\"ReaderShowStatisticsToggleSwitch\"");
        appearanceXaml.Should().Contain("IsOn=\"{x:Bind ViewModel.ShowStatisticsToggle, Mode=TwoWay}\"");
        appearanceXaml.Should().Contain("x:Uid=\"ReaderShowReadingSpeedCard\"");
        appearanceXaml.Should().Contain("AutomationProperties.AutomationId=\"ReaderShowReadingSpeedToggle\"");
        appearanceXaml.Should().Contain("IsOn=\"{x:Bind ViewModel.ShowReadingSpeed, Mode=TwoWay}\"");
        appearanceXaml.Should().Contain("x:Uid=\"ReaderShowReadingTimeCard\"");
        appearanceXaml.Should().Contain("AutomationProperties.AutomationId=\"ReaderShowReadingTimeToggle\"");
        appearanceXaml.Should().Contain("IsOn=\"{x:Bind ViewModel.ShowReadingTime, Mode=TwoWay}\"");

        settingsViewModel.Should().Contain("ShowStatisticsToggle");
        settingsViewModel.Should().Contain("ShowReadingSpeed");
        settingsViewModel.Should().Contain("ShowReadingTime");
        settingsViewModel.Should().Contain("ApplyReaderSetting(s => s.ShowStatisticsToggle");
        settingsViewModel.Should().Contain("ApplyReaderSetting(s => s.ShowReadingSpeed");
        settingsViewModel.Should().Contain("ApplyReaderSetting(s => s.ShowReadingTime");

        readerXaml.Should().Contain("x:Name=\"NovelReaderStatisticsText\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderStatisticsText\"");
        readerCode.Should().Contain("RefreshReaderStatisticsChrome");
        readerCode.Should().Contain("readerSettings.Current.ShowStatisticsToggle");
        readerCode.Should().Contain("readerSettings.Current.ShowReadingSpeed");
        readerCode.Should().Contain("readerSettings.Current.ShowReadingTime");
        readerCode.Should().Contain("NovelReaderStatisticsText.Text");

        foreach (var key in new[]
        {
            "ReaderShowStatisticsToggleCard.Header",
            "ReaderShowStatisticsToggleSwitch.AutomationProperties.Name",
            "ReaderShowReadingSpeedCard.Header",
            "ReaderShowReadingSpeedToggle.AutomationProperties.Name",
            "ReaderShowReadingTimeCard.Header",
            "ReaderShowReadingTimeToggle.AutomationProperties.Name",
        })
        {
            enResources.Should().Contain(key);
            zhResources.Should().Contain(key);
        }
    }

    [Fact]
    public void ReaderPage_AppliesMacAlignedReaderChromeDisplaySettings()
    {
        var readerXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml")
        );
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );

        readerXaml.Should().Contain("x:Name=\"NovelReaderTitleText\"");
        readerXaml.Should().Contain("x:Name=\"NovelReaderTopProgressText\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderProgressText\"");
        readerXaml.Should().Contain("x:Name=\"NovelReaderBottomProgressText\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderBottomProgressText\"");
        readerXaml.Should().NotContain("Text=\"{x:Bind ViewModel.ReaderProgressText, Mode=OneWay}\"");

        readerCode.Should().Contain("RefreshReaderDisplayChrome");
        readerCode.Should().Contain("BuildReaderProgressText");
        readerCode.Should().Contain("readerSettings.Current.ShowTitle");
        readerCode.Should().Contain("readerSettings.Current.ShowCharacters");
        readerCode.Should().Contain("readerSettings.Current.ShowPercentage");
        readerCode.Should().Contain("readerSettings.Current.ShowProgressTop");
        readerCode.Should().Contain("NovelReaderTitleText.Visibility");
        readerCode.Should().Contain("NovelReaderTopProgressText.Text");
        readerCode.Should().Contain("NovelReaderTopProgressText.Visibility");
        readerCode.Should().Contain("NovelReaderBottomProgressText.Text");
        readerCode.Should().Contain("NovelReaderBottomProgressText.Visibility");
        readerCode.Should().Contain("nameof(ViewModel.ReaderProgressText)");
    }

    [Fact]
    public void ReaderPage_ExposesNiratanAlignedReaderKeyboardShortcuts()
    {
        var readerXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml")
        );
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var shortcutModels = File.ReadAllText(
            Path.Combine(ProjectRoot, "Models", "Shortcuts", "ReaderShortcutModels.cs")
        );

        readerXaml.Should().Contain("x:Name=\"ReaderTopChrome\"");
        readerXaml.Should().Contain("x:Name=\"ReaderBottomChrome\"");
        readerXaml.Should().NotContain("<Page.KeyboardAccelerators>");
        readerXaml.Should().Contain("CharacterReceived=\"NovelReaderPage_CharacterReceived\"");
        readerXaml.Should().Contain("x:Name=\"NovelReaderBackButton\"");
        readerXaml.Should().Contain("x:Name=\"NovelReaderSearchButton\"");

        shortcutModels.Should().Contain("ReaderKeyboardShortcut");
        shortcutModels.Should().Contain("ReaderShortcutActions");
        shortcutModels.Should().Contain("reader.previousPage");
        shortcutModels.Should().Contain("reader.nextPage");
        shortcutModels.Should().Contain("reader.close");
        shortcutModels.Should().Contain("reader.toggleFocusMode");
        shortcutModels.Should().Contain("reader.toggleStatistics");
        shortcutModels.Should().Contain("reader.toggleLyricsMode");

        readerCode.Should().Contain("IShortcutService");
        readerCode.Should().Contain("_shortcutService = App.GetService<IShortcutService>();");
        readerCode.Should().Contain("_shortcutService.ShortcutsChanged += OnReaderShortcutsChanged;");
        readerCode.Should().Contain("_shortcutService.TryResolve(ShortcutScope.Reader");
        readerCode.Should().Contain("ApplyReaderShortcutLabels");
        readerCode.Should().Contain("_shortcutService.GetBinding(shortcutAction).Label");
        readerCode.Should().NotContain("ReaderShortcutActions.Close.DefaultShortcut.Label");
        readerCode.Should().NotContain("ReaderShortcutActions.ToggleStatistics.DefaultShortcut.Label");
        readerCode.Should().Contain("ToolTipService.SetToolTip");
        readerCode.Should().Contain("AutomationProperties.SetHelpText");
        readerCode.Should().Contain("RegisterReaderKeyboardAccelerators");
        readerCode.Should().Contain("ReaderKeyboardAccelerator_Invoked");
        readerCode.Should().Contain("NovelReaderPage_KeyDown");
        readerCode.Should().Contain("AddHandler(UIElement.KeyDownEvent");
        readerCode.Should().Contain("IsReaderKeyDownFallbackBinding");
        readerCode.Should().Contain("KeyboardShortcutBinding.FromVirtualKey");
        readerCode.Should().Contain("WasRecentlyHandledByReaderKeyDownFallback");
        readerCode.Should().Contain("HandleReaderShortcutActionAsync");
        readerCode.Should().Contain("ReaderShortcutActions.PreviousPage.Id");
        readerCode.Should().Contain("NavigateReaderPageAsync(\"backward\")");
        readerCode.Should().Contain("ReaderShortcutActions.NextPage.Id");
        readerCode.Should().Contain("NavigateReaderPageAsync(\"forward\")");
        readerCode.Should().Contain("ViewModel.BackToLibraryCommand.Execute");
        readerCode.Should().Contain("ToggleReaderFocusMode");
        readerCode.Should().Contain("_readerFocusMode");
        readerCode.Should().Contain("ReaderTopChrome.Visibility");
        readerCode.Should().Contain("ReaderBottomChrome.Visibility");
        readerCode.Should().Contain("CloseReaderPanels();");
        readerCode.Should().Contain("ToggleStatisticsTrackingAsync");
        readerCode.Should().Contain("ReaderShortcutActions.ToggleLyricsMode.Id");
        readerCode.Should().Contain("ToggleReaderLyricsModeShortcutAsync");
        readerCode.Should().Contain("CurrentStatisticsSettings.EnableStatistics");
    }

    [Fact]
    public void ReaderPage_ExposesNiratanAlignedSasayakiKeyboardShortcuts()
    {
        var readerXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml")
        );
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var shortcutModels = File.ReadAllText(
            Path.Combine(ProjectRoot, "Models", "Shortcuts", "ReaderShortcutModels.cs")
        );

        shortcutModels.Should().Contain("SasayakiShortcutActions");
        shortcutModels.Should().Contain("sasayaki.previousCue");
        shortcutModels.Should().Contain("sasayaki.playPause");
        shortcutModels.Should().Contain("sasayaki.nextCue");
        shortcutModels.Should().Contain("sasayaki.replayCue");
        shortcutModels.Should().Contain("sasayaki.jumpCue");

        readerXaml.Should().Contain("CharacterReceived=\"NovelReaderPage_CharacterReceived\"");
        readerXaml.Should().NotContain("SasayakiPlayPauseKeyboardAccelerator_Invoked");
        readerXaml.Should().NotContain("SasayakiReplayCueKeyboardAccelerator_Invoked");
        readerXaml.Should().NotContain("SasayakiJumpCueKeyboardAccelerator_Invoked");

        readerXaml.Should().Contain("x:Name=\"SasayakiPreviousCueMenuItem\"");
        readerXaml.Should().Contain("x:Name=\"SasayakiPlayPauseMenuItem\"");
        readerXaml.Should().Contain("x:Name=\"SasayakiNextCueMenuItem\"");

        readerCode.Should().Contain("_shortcutService.TryResolve(ShortcutScope.Sasayaki");
        readerCode.Should().NotContain("SasayakiShortcutActions.PreviousCue.DefaultShortcut.Label");
        readerCode.Should().NotContain("SasayakiShortcutActions.PlayPause.DefaultShortcut.Label");
        readerCode.Should().NotContain("SasayakiShortcutActions.NextCue.DefaultShortcut.Label");
        readerCode.Should().Contain("NovelReaderPage_CharacterReceived");
        readerCode.Should().Contain("KeyboardShortcutBindingFromCharacter");
        readerCode.Should().Contain("TryHandleReaderShortcutBindingAsync");
        readerCode.Should().Contain("IsReaderKeyDownFallbackBinding");
        readerCode.Should().Contain("HandleSasayakiShortcutActionAsync");
        readerCode.Should().NotContain("case '[':");
        readerCode.Should().NotContain("case ']':");
        readerCode.Should().Contain("GoToPreviousSasayakiCueAsync");
        readerCode.Should().Contain("GoToNextSasayakiCueAsync");
        readerCode.Should().Contain("ReplayCurrentSasayakiCueAsync");
        readerCode.Should().Contain("JumpToCurrentSasayakiCueAsync");
        readerCode.Should().Contain("ResolveSasayakiJumpTarget");
    }

    [Fact]
    public void ReaderPage_DoesNotExposeUnusedSessionNavigationHistoryControls()
    {
        var readerXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml")
        );
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var enResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw")
        );
        var zhResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw")
        );

        readerXaml.Should().NotContain("NovelReaderHistoryBackButton");
        readerXaml.Should().NotContain("NovelReaderHistoryForwardButton");
        readerXaml.Should().NotContain("ReaderHistoryBackButton_Click");
        readerXaml.Should().NotContain("ReaderHistoryForwardButton_Click");

        readerCode.Should().NotContain("ReaderNavigationHistoryEntry");
        readerCode.Should().NotContain("_readerBackHistory");
        readerCode.Should().NotContain("_readerForwardHistory");
        readerCode.Should().NotContain("RecordReaderHistoryEntry");
        readerCode.Should().NotContain("RestoreReaderHistoryEntryAsync");
        readerCode.Should().NotContain("ReaderHistoryBackButton_Click");
        readerCode.Should().NotContain("ReaderHistoryForwardButton_Click");
        readerCode.Should().NotContain("UpdateReaderHistoryButtons");

        enResources.Should().NotContain("NovelReaderHistoryBackButton.AutomationProperties.Name");
        enResources.Should().NotContain("NovelReaderHistoryForwardButton.AutomationProperties.Name");
        zhResources.Should().NotContain("NovelReaderHistoryBackButton.AutomationProperties.Name");
        zhResources.Should().NotContain("NovelReaderHistoryForwardButton.AutomationProperties.Name");
    }

    [Fact]
    public void ReaderChapterPanel_ExposesMacAlignedCharacterJump()
    {
        var chapterContentXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Controls", "ReaderChapterListContent.xaml")
        );
        var chapterContentCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Controls", "ReaderChapterListContent.xaml.cs")
        );
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var enResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw")
        );
        var zhResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw")
        );

        chapterContentXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderCharacterJumpNumberBox\"");
        chapterContentXaml.Should().Contain("x:Uid=\"NovelReaderCharacterJumpNumberBox\"");
        chapterContentXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderCharacterJumpButton\"");
        chapterContentXaml.Should().Contain("x:Uid=\"NovelReaderCharacterJumpButton\"");
        chapterContentXaml.Should().Contain("Click=\"CharacterJumpButton_Click\"");

        chapterContentCode.Should().Contain("CharacterJumpRequested");
        chapterContentCode.Should().Contain("TotalCharacterCount");
        chapterContentCode.Should().Contain("CharacterJumpButton_Click");
        chapterContentCode.Should().Contain("Math.Clamp");

        readerCode.Should().Contain("ReaderChapterListContent.CharacterJumpRequested -= OnCharacterJumpRequested");
        readerCode.Should().Contain("ReaderChapterListContent.CharacterJumpRequested += OnCharacterJumpRequested");
        readerCode.Should().Contain("OnCharacterJumpRequested");
        readerCode.Should().Contain("JumpToCharacterAsync");
        readerCode.Should().Contain("ResolveCharacterJumpTarget");

        foreach (var key in new[]
        {
            "NovelReaderCharacterJumpNumberBox.PlaceholderText",
            "NovelReaderCharacterJumpButton.AutomationProperties.Name",
            "NovelReaderCharacterJumpButtonText.Text",
            "NovelReaderCharacterJumpStatus.Text",
        })
        {
            enResources.Should().Contain(key);
            zhResources.Should().Contain(key);
        }
    }

    [Fact]
    public void ReaderPage_UsesMacCompatibleSasayakiSidecarService()
    {
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var sidecarContract = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Sasayaki", "ISasayakiSidecarService.cs")
        );
        var appCode = File.ReadAllText(Path.Combine(ProjectRoot, "App.xaml.cs"));

        sidecarContract.Should().Contain("sasayaki_match.json");
        sidecarContract.Should().Contain("sasayaki_playback.json");
        sidecarContract.Should().Contain("sasayaki.json");
        appCode.Should().Contain("ISasayakiSidecarService, SasayakiSidecarService");
        readerCode.Should().Contain("App.GetService<ISasayakiSidecarService>()");
        readerCode.Should().Contain("LoadMatchAsync");
        readerCode.Should().Contain("SaveMatchAsync");
        readerCode.Should().Contain("LoadPlaybackAsync");
        readerCode.Should().Contain("SavePlaybackAsync");
        readerCode.Should().Contain("SaveSasayakiPlaybackAsync");
        readerCode.Should().NotContain("\"sasayaki.json\"");
        readerCode.Should().NotContain("GetSasayakiSidecarPath");
    }

    [Fact]
    public void ReaderPage_ExposesLocalizedSasayakiControlsForAutomation()
    {
        var readerXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml")
        );
        var enResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw")
        );
        var zhResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw")
        );

        readerXaml.Should().NotContain("AutomationProperties.AutomationId=\"NovelReaderSasayakiBar\"");
        readerXaml.Should().NotContain("AutomationProperties.AutomationId=\"NovelReaderSasayakiPositionSlider\"");
        readerXaml.Should().NotContain("AutomationProperties.AutomationId=\"NovelReaderSasayakiPlaybackRateComboBox\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderSasayakiButton\"");
        readerXaml.Should().Contain("x:Uid=\"NovelReaderSasayakiButton\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderSasayakiLoadAudioMenuItem\"");
        readerXaml.Should().Contain("x:Uid=\"SasayakiLoadAudioMenuItem\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderSasayakiSkipBackMenuItem\"");
        readerXaml.Should().Contain("x:Uid=\"SasayakiSkipBackMenuItem\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderSasayakiPreviousCueMenuItem\"");
        readerXaml.Should().Contain("x:Uid=\"SasayakiPreviousCueMenuItem\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderSasayakiPlayPauseMenuItem\"");
        readerXaml.Should().Contain("x:Uid=\"SasayakiPlayPauseMenuItem\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderSasayakiNextCueMenuItem\"");
        readerXaml.Should().Contain("x:Uid=\"SasayakiNextCueMenuItem\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderSasayakiSkipForwardMenuItem\"");
        readerXaml.Should().Contain("x:Uid=\"SasayakiSkipForwardMenuItem\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderSasayakiReplayCueMenuItem\"");
        readerXaml.Should().Contain("x:Uid=\"SasayakiReplayCueMenuItem\"");
        readerXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderSasayakiJumpCueMenuItem\"");
        readerXaml.Should().Contain("x:Uid=\"SasayakiJumpCueMenuItem\"");
        readerXaml.Should().NotContain("AutomationProperties.Name=\"Load Audiobook\"");
        readerXaml.Should().NotContain("AutomationProperties.Name=\"Skip Back 15 Seconds\"");
        readerXaml.Should().NotContain("AutomationProperties.Name=\"Previous Cue\"");
        readerXaml.Should().NotContain("AutomationProperties.Name=\"Play/Pause\"");
        readerXaml.Should().NotContain("AutomationProperties.Name=\"Next Cue\"");
        readerXaml.Should().NotContain("AutomationProperties.Name=\"Skip Forward 15 Seconds\"");
        readerXaml.Should().NotContain("AutomationProperties.Name=\"Playback Position\"");
        readerXaml.Should().NotContain("AutomationProperties.Name=\"Playback Rate\"");

        foreach (var key in new[]
        {
            "NovelReaderSasayakiButton.AutomationProperties.Name",
            "SasayakiLoadAudioMenuItem.Text",
            "SasayakiSkipBackMenuItem.Text",
            "SasayakiPreviousCueMenuItem.Text",
            "SasayakiPlayPauseMenuItem.Text",
            "SasayakiNextCueMenuItem.Text",
            "SasayakiSkipForwardMenuItem.Text",
            "SasayakiReplayCueMenuItem.Text",
            "SasayakiJumpCueMenuItem.Text",
            "NovelReaderSasayakiPanelDialog.Title",
            "NovelReaderSasayakiPanelDialog.CloseButtonText",
            "NovelReaderSasayakiAudioSectionHeader.Text",
            "NovelReaderSasayakiPlaybackSectionHeader.Text",
            "NovelReaderSasayakiSettingsSectionHeader.Text",
            "NovelReaderSasayakiThemeSectionHeader.Text",
            "NovelReaderSasayakiPanelLoadAudioButton.AutomationProperties.Name",
            "NovelReaderSasayakiPanelSkipBackButton.AutomationProperties.Name",
            "NovelReaderSasayakiPanelPreviousCueButton.AutomationProperties.Name",
            "NovelReaderSasayakiPanelPlayPauseButton.AutomationProperties.Name",
            "NovelReaderSasayakiPanelNextCueButton.AutomationProperties.Name",
            "NovelReaderSasayakiPanelSkipForwardButton.AutomationProperties.Name",
            "NovelReaderSasayakiDelayLabel.Text",
            "NovelReaderSasayakiSpeedLabel.Text",
            "NovelReaderSasayakiShowToggleSwitch.Header",
            "NovelReaderSasayakiAutoScrollToggleSwitch.Header",
            "NovelReaderSasayakiAutoPauseToggleSwitch.Header",
            "NovelReaderSasayakiLightTextColorLabel.Text",
            "NovelReaderSasayakiLightBackgroundColorLabel.Text",
            "NovelReaderSasayakiDarkTextColorLabel.Text",
            "NovelReaderSasayakiDarkBackgroundColorLabel.Text",
        })
        {
            enResources.Should().Contain(key);
            zhResources.Should().Contain(key);
        }
    }

    [Fact]
    public void ReaderPage_OrdersSasayakiMenuLikeNiratanWithoutLyricsMode()
    {
        var readerXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml")
        );

        int IndexOf(string value) => readerXaml.IndexOf(value, StringComparison.Ordinal);

        var skipBack = IndexOf("x:Name=\"SasayakiSkipBackMenuItem\"");
        var previousCue = IndexOf("x:Name=\"SasayakiPreviousCueMenuItem\"");
        var playPause = IndexOf("x:Name=\"SasayakiPlayPauseMenuItem\"");
        var nextCue = IndexOf("x:Name=\"SasayakiNextCueMenuItem\"");
        var skipForward = IndexOf("x:Name=\"SasayakiSkipForwardMenuItem\"");
        var replayCue = IndexOf("x:Name=\"SasayakiReplayCueMenuItem\"");
        var jumpCue = IndexOf("x:Name=\"SasayakiJumpCueMenuItem\"");
        var loadAudio = IndexOf("x:Name=\"SasayakiLoadAudioMenuItem\"");

        skipBack.Should().BeGreaterThanOrEqualTo(0);
        previousCue.Should().BeGreaterThan(skipBack);
        playPause.Should().BeGreaterThan(previousCue);
        nextCue.Should().BeGreaterThan(playPause);
        skipForward.Should().BeGreaterThan(nextCue);
        replayCue.Should().BeGreaterThan(skipForward);
        jumpCue.Should().BeGreaterThan(replayCue);
        loadAudio.Should().BeGreaterThan(jumpCue);

        readerXaml.Should().NotContain("NovelReaderLyricsModeMenuItem");
        readerXaml.Should().NotContain("Lyrics Mode");
    }

    [Fact]
    public void ReaderPage_DefinesNiratanStyleSasayakiPanelWithoutLyricsMode()
    {
        var readerXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml")
        );

        foreach (var requiredToken in new[]
        {
            "NovelReaderSasayakiPanelDialog",
            "NovelReaderSasayakiAudioSection",
            "NovelReaderSasayakiPlaybackSection",
            "NovelReaderSasayakiSettingsSection",
            "NovelReaderSasayakiThemeSection",
            "NovelReaderSasayakiPanelSkipBackButton",
            "NovelReaderSasayakiPanelPreviousCueButton",
            "NovelReaderSasayakiPanelPlayPauseButton",
            "NovelReaderSasayakiPanelNextCueButton",
            "NovelReaderSasayakiPanelSkipForwardButton",
            "NovelReaderSasayakiPanelLoadAudioButton",
            "NovelReaderSasayakiDelaySlider",
            "Minimum=\"-2\"",
            "Maximum=\"2\"",
            "StepFrequency=\"0.05\"",
            "NovelReaderSasayakiSpeedSlider",
            "Minimum=\"0.5\"",
            "Maximum=\"1.5\"",
            "NovelReaderSasayakiShowToggleSwitch",
            "NovelReaderSasayakiAutoScrollToggleSwitch",
            "NovelReaderSasayakiAutoPauseToggleSwitch",
            "NovelReaderSasayakiLightTextColorPicker",
            "NovelReaderSasayakiLightBackgroundColorPicker",
            "NovelReaderSasayakiDarkTextColorPicker",
            "NovelReaderSasayakiDarkBackgroundColorPicker",
        })
        {
            readerXaml.Should().Contain(requiredToken);
        }

        readerXaml.Should().NotContain("NovelReaderLyricsModeMenuItem");
        readerXaml.Should().NotContain("Lyrics Mode");
    }

    [Fact]
    public void ReaderPage_GuardsSasayakiPanelSliderInitialization()
    {
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );

        readerCode.Should().Contain("private bool _isRefreshingSasayakiPanel = true;");
        var initializeIndex = readerCode.IndexOf("InitializeComponent();", StringComparison.Ordinal);
        var guardOffIndex = readerCode.IndexOf(
            "_isRefreshingSasayakiPanel = false;",
            initializeIndex,
            StringComparison.Ordinal);

        initializeIndex.Should().BeGreaterThanOrEqualTo(0);
        guardOffIndex.Should().BeGreaterThan(initializeIndex);
    }

    [Fact]
    public void ReaderPage_UsesSheetDialogsForReaderToolbarPanels()
    {
        var readerXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml")
        );
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );

        foreach (var requiredToken in new[]
        {
            "ReaderChapterPanelDialog",
            "ReaderSearchPanelDialog",
            "ReaderHighlightsPanelDialog",
            "ReaderStatisticsPanelDialog",
            "ReaderAppearancePanelDialog",
            "AutomationProperties.AutomationId=\"NovelReaderChapterPanelDialog\"",
            "AutomationProperties.AutomationId=\"NovelReaderSearchPanelDialog\"",
            "AutomationProperties.AutomationId=\"NovelReaderHighlightsPanelDialog\"",
            "AutomationProperties.AutomationId=\"NovelReaderStatisticsPanelDialog\"",
            "AutomationProperties.AutomationId=\"NovelReaderAppearancePanelDialog\"",
        })
        {
            readerXaml.Should().Contain(requiredToken);
        }

        foreach (var oldPopup in new[]
        {
            "ReaderChapterPanelPopup",
            "ReaderSearchPanelPopup",
            "ReaderHighlightsPanelPopup",
            "ReaderStatisticsPanelPopup",
            "ReaderAppearancePanelPopup",
        })
        {
            readerXaml.Should().NotContain(oldPopup);
            readerCode.Should().NotContain(oldPopup);
        }

        readerCode.Should().Contain("ShowReaderPanelDialogAsync");
        readerCode.Should().NotContain("OpenReaderPanel(");
        readerCode.Should().NotContain("PositionReaderPanel");
    }

    [Fact]
    public void ReaderPage_UsesWideSheetDialogContentForReaderPanels()
    {
        var readerXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml")
        );
        var appearanceContentXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Controls", "ReaderAppearanceSettingsContent.xaml")
        );

        readerXaml.Should().Contain("<Grid Width=\"1120\"");
        readerXaml.Should().Contain("<controls:ReaderAppearanceSettingsContent Width=\"1280\"");
        readerXaml.Should().Contain("<StackPanel Width=\"1120\"");
        readerXaml.Should().Contain("MaxWidth=\"1120\"");
        (readerXaml.Split("<x:Double x:Key=\"ContentDialogMaxWidth\">1600</x:Double>").Length - 1)
            .Should().Be(6);
        (readerXaml.Split("<x:Double x:Key=\"ContentDialogMinWidth\">1120</x:Double>").Length - 1)
            .Should().Be(5);
        readerXaml.Should().Contain("<x:Double x:Key=\"ContentDialogMinWidth\">1280</x:Double>");
        appearanceContentXaml.Should().Contain("MaxWidth=\"1280\"");

        readerXaml.Should().NotContain("Width=\"560\"");
        readerXaml.Should().NotContain("MaxWidth=\"560\"");
        readerXaml.Should().NotContain("Width=\"640\"");
        appearanceContentXaml.Should().NotContain("MaxWidth=\"1000\"");
    }

    [Fact]
    public void ReaderPage_AppliesSasayakiSettingsToPlaybackUiAndHighlighting()
    {
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var bridgeScript = File.ReadAllText(Path.Combine(ReaderRoot, "reader-bridge.js"));

        readerCode.Should().Contain("CurrentSasayakiSettings");
        readerCode.Should().Contain("settings.EnableSasayaki");
        readerCode.Should().Contain("settings.ReaderShowSasayakiToggle");
        readerCode.Should().Contain("AutoPauseOnLookup");
        readerCode.Should().Contain("SasayakiSkipBackMenuItem.IsEnabled");
        readerCode.Should().Contain("SasayakiSkipForwardMenuItem.IsEnabled");
        readerCode.Should().NotContain("SasayakiSkipBackMenuItem.Visibility = settings.ShowSkipControls");
        readerCode.Should().NotContain("SasayakiSkipForwardMenuItem.Visibility = settings.ShowSkipControls");
        readerCode.Should().Contain("SasayakiSkipBack_Click");
        readerCode.Should().Contain("SasayakiSkipForward_Click");
        readerCode.Should().NotContain("if (CurrentSasayakiSettings.ShowSkipControls)");
        readerCode.Should().Contain("GetMatchedCueIndexBefore");
        readerCode.Should().Contain("GetMatchedCueIndexAfter");
        readerCode.Should().Contain("SkipSasayakiAsync(-15)");
        readerCode.Should().Contain("SkipSasayakiAsync(15)");
        readerCode.Should().Contain("settings.AutoScroll");
        readerCode.Should().Contain("window.hoshiSasayaki.setColors");
        readerCode.Should().Contain("settings.DarkBackgroundColor");
        bridgeScript.Should().Contain("setColors: function");
        bridgeScript.Should().Contain("--hoshi-sasayaki-background-color");
        bridgeScript.Should().Contain("autoScroll !== false");
    }

    [Fact]
    public void ReaderSasayakiHighlight_UsesContinuousRangeWithoutActiveOutline()
    {
        var bridgeScript = File.ReadAllText(Path.Combine(ReaderRoot, "reader-bridge.js"));

        bridgeScript.Should().Contain("_createHighlightRanges: function");
        bridgeScript.Should().Contain("CSS.highlights.set(\"hoshi-sasayaki\"");
        bridgeScript.Should().Contain("new Highlight(...ranges)");
        bridgeScript.Should().Contain("window.hoshiSelection?.clearSelection?.()");
        bridgeScript.Should().Contain("window.getSelection()?.removeAllRanges?.()");
        bridgeScript.Should().Contain("runningCount + nodeLen >= targetCodePoint");
        bridgeScript.Should().Contain("_wrapHighlightRanges: function");
        bridgeScript.Should().Contain("return text.length;");
        bridgeScript.Should().NotContain("hoshi-sasayaki-highlight-active");
        bridgeScript.Should().NotContain("outline: 2px");
        bridgeScript.Should().NotContain("for (var i = slices.length - 1; i >= 0; i--)");
        bridgeScript.Should().NotContain("return fallbackOffset;");
        bridgeScript.Should().NotContain("range.intersectsNode");
    }

    [Fact]
    public void ReaderSasayakiHighlight_IgnoresStaleAsyncRequests()
    {
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );

        readerCode.Should().Contain("_sasayakiHighlightGeneration");
        readerCode.Should().Contain("Interlocked.Increment(ref _sasayakiHighlightGeneration)");
        readerCode.Should().Contain("window.__hoshiSasayakiHighlightGeneration");
        readerCode.Should().Contain("currentGeneration > generation");
    }

    [Fact]
    public void ReaderLookup_ProvidesNovelSasayakiMiningContext()
    {
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs")
        );
        var popupCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs")
        );
        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs")
        );
        var miningContext = File.ReadAllText(
            Path.Combine(ProjectRoot, "Models", "Anki", "AnkiMiningPayload.cs")
        );

        readerCode.Should().Contain("CreateReaderAnkiMiningContext");
        readerCode.Should().Contain("TryFindSasayakiMatchAtOffset");
        readerCode.Should().Contain("RequestSasayakiMiningAudioAsync");
        readerCode.Should().Contain("SasayakiAudioProvider");
        readerCode.Should().Contain("SasayakiPopupControls");
        readerCode.Should().Contain("ReplaySasayakiMatchAsync");
        readerCode.Should().Contain("JumpToSasayakiMatchAsync");
        readerCode.Should().Contain("JumpToAndPlaySasayakiMatchAsync");
        readerCode.Should().Contain("PlaySasayakiMatchFromCueAsync");
        readerCode.Should().Contain("await PlaySasayakiMatchFromCueAsync(match);");
        readerCode.Should().NotContain("await JumpToSasayakiMatchAsync(match);\r\n        await ReplaySasayakiMatchAsync(match);");
        readerCode.Should().NotContain("await JumpToSasayakiMatchAsync(match);\n        await ReplaySasayakiMatchAsync(match);");
        readerCode.Should().Contain("JumpToCueAsync: () => JumpToAndPlaySasayakiMatchAsync(match)");
        readerCode.Should().Contain("sentenceOffset");
        readerCode.Should().Contain("normalizedOffset");
        readerCode.Should().Contain("DocumentTitle = book?.Title");
        readerCode.Should().Contain("CoverPath = book?.CoverPath");
        readerCode.Should().Contain("audioSettings");
        readerCode.Should().Contain("ankiSettings");

        miningContext.Should().Contain("SasayakiPopupControls");
        miningContext.Should().Contain("TogglePlaybackAsync");
        miningContext.Should().Contain("ReplayCueAsync");
        miningContext.Should().Contain("JumpToCueAsync");

        popupCode.Should().Contain("NovelReaderPopupSasayakiControls");
        popupCode.Should().Contain("NovelReaderPopupSasayakiPlayPauseButton");
        popupCode.Should().Contain("NovelReaderPopupSasayakiReplayCueButton");
        popupCode.Should().Contain("NovelReaderPopupSasayakiJumpCueButton");
        popupCode.Should().Contain("HandleSasayakiPopupPlayPauseAsync");
        popupCode.Should().Contain("HandleSasayakiPopupReplayCueAsync");
        popupCode.Should().Contain("HandleSasayakiPopupJumpCueAsync");
        popupCode.Should().Contain("DismissRequested");
        popupCode.Should().Contain("DismissRequested?.Invoke(this, EventArgs.Empty)");

        overlayCode.Should().Contain("DismissRequested += OnPopupDismissRequested");
        overlayCode.Should().Contain("DismissRequested -= OnPopupDismissRequested");
        overlayCode.Should().Contain("private void OnPopupDismissRequested");
        overlayCode.Should().Contain("Dismiss();");
    }

    [Fact]
    public void NovelLibraryPage_ExposesNativeShelfSurfaceAndManagementDialog()
    {
        var libraryXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml"));
        var dialogXaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dialogs", "NovelShelfManagementDialog.xaml"));
        var dialogCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dialogs", "NovelShelfManagementDialog.xaml.cs"));
        var enResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw"));
        var zhResources = File.ReadAllText(
            Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw"));

        libraryXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelLibraryCommandBar\"");
        libraryXaml.Should().Contain("<SymbolIcon Symbol=\"Document\" />");
        libraryXaml.Should().NotContain("Icon=\"ReportDocument\"");
        libraryXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelShelfSectionsControl\"");
        libraryXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelShelfManagementButton\"");
        libraryXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelStorageWarningInfoBar\"");
        libraryXaml.Should().Contain("ItemsSource=\"{x:Bind ViewModel.UnshelvedBooks, Mode=OneWay}\"");
        dialogXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelShelfList\"");
        dialogXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelShelfCreateButton\"");
        dialogXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelShelfDeleteConfirmationFlyout\"");
        dialogCode.Should().NotContain("new ContentDialog");

        foreach (var key in new[]
                 {
                     "NovelShelfReadingLabel.Text",
                     "NovelShelfUnshelvedLabel.Text",
                     "NovelShelfManageButton.Label",
                     "NovelShelfCreateButton.Content",
                     "NovelShelfRenameButton.Content",
                     "NovelShelfDeleteButton.Content",
                     "NovelShelfConfirmDeleteButton.Content",
                     "NovelBookMoveToShelfMenuItem.Text",
                 })
        {
            enResources.Should().Contain($"name=\"{key}\"");
            zhResources.Should().Contain($"name=\"{key}\"");
        }
    }
}
