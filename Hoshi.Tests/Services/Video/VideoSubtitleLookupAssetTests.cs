using FluentAssertions;
using System.Xml.Linq;

namespace Hoshi.Tests.Services.Video;

public class VideoSubtitleLookupAssetTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "Hoshi"));

    private static string ReadVideoPlayerWindowMainCode() =>
        File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml.cs"));

    private static string ReadVideoPlayerWindowCode()
    {
        var videoViewsPath = Path.Combine(ProjectRoot, "Views", "Video");
        return string.Join(
            Environment.NewLine,
            Directory.GetFiles(videoViewsPath, "VideoPlayerWindow*.cs")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));
    }

    private static string ReadProjectFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([ProjectRoot, .. parts]));

    [Fact]
    public void VideoSubtitleLookup_UsesOneCanvasForRenderingAndHitTesting()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var code = ReadVideoPlayerWindowCode();
        var script = File.ReadAllText(Path.Combine(ProjectRoot, "Web", "VideoSubtitle", "subtitle-overlay.js"));
        var rendererPath = Path.Combine(ProjectRoot, "Services", "Video", "VideoSubtitleCanvasRenderer.cs");

        xaml.Should().Contain("x:Name=\"SubtitleCanvas\"");
        xaml.Should().Contain("PointerPressed=\"SubtitleCanvas_PointerPressed\"");
        xaml.Should().Contain("PointerMoved=\"SubtitleCanvas_PointerMoved\"");
        xaml.Should().Contain("x:Name=\"SubtitleWebView\"");
        xaml.Should().NotContain("x:Name=\"SubtitleTextBox\"");
        xaml.Should().NotContain("x:Name=\"SubtitleTextRun\"");
        code.Should().Contain("WebMessageReceived += OnSubtitleWebMessageReceived");
        code.Should().Contain("UpdateSubtitleWebViewAsync");
        code.Should().Contain("LookupSubtitleAtCanvasPointAsync");
        code.Should().NotContain("VideoSubtitleHitTestResolver.ResolveCharacterIndex");
        code.Should().Contain("SubtitleCanvas.TransformToVisual(PopupOverlayCanvas)");
        script.Should().Contain("lookupAtOffset:");
        script.Should().Contain("selectTextAtOffset");
        script.Should().NotContain("document.addEventListener('click'");
        File.Exists(rendererPath).Should().BeTrue();
    }

    [Fact]
    public void VideoSubtitleLookup_UsesFloatingDictionaryPopup()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var code = ReadVideoPlayerWindowCode();
        var popupCode = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs"));
        var overlayCode = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs"));

        xaml.Should().Contain("x:Name=\"VideoDictionaryPanelChrome\"");
        xaml.Should().Contain("HorizontalAlignment=\"Stretch\"");
        xaml.Should().Contain("VerticalAlignment=\"Stretch\"");
        xaml.Should().Contain("BorderThickness=\"0\"");
        xaml.Should().Contain("Visibility=\"Collapsed\"");
        xaml.Should().Contain("x:Name=\"PopupOverlayCanvas\"");
        xaml.Should().Contain("SizeChanged=\"PopupOverlayCanvas_SizeChanged\"");
        code.Should().Contain("_popupOverlay = new DictionaryPopupOverlay();");
        code.Should().Contain("DictionaryPopupCanvasInputMode.VisibleHostsOnly");
        code.Should().NotContain("_popupOverlay.EmbedRoot(PopupOverlayCanvas)");
        code.Should().NotContain("IGlobalLookupPopupService");
        code.Should().NotContain("GlobalLookupPopupWindow");
        code.Should().NotContain("_globalLookupPopupService");
        code.Should().Contain("SubtitleCanvas.TransformToVisual(PopupOverlayCanvas)");
        code.Should().Contain("VideoDictionaryPanelChrome.Visibility = Visibility.Visible");
        code.Should().Contain("EnsureVideoDictionaryOverlaySurfaceVisible(lookupOverlay);");
        code.Should().Contain("EnsureVideoDictionaryOverlaySurfaceVisible(EnsurePopupOverlay());");
        code.Should().Contain("await lookupOverlay.ShowLookupAsync(");
        code.Should().Contain("VideoDictionaryPanelChrome.Visibility = Visibility.Collapsed");
        code.Should().Contain("_popupOverlay?.UpdateRootSize(e.NewSize.Width, e.NewSize.Height)");
        code.Should().Contain("TryDismissLookupPopupFromOutsidePointer");
        code.Should().Contain("IsDescendantOf(source, VideoDictionaryPanelChrome)");
        popupCode.Should().NotContain("SetSnapshotAcrylicBackgroundAsync");
        popupCode.Should().NotContain("has-snapshot-acrylic");
        overlayCode.Should().NotContain("_currentMiningContext.VideoScreenshotPath");
    }

    [Fact]
    public void VideoSubtitleLookup_PopupEmptySpacePassesThrough()
    {
        var xaml = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var code = ReadVideoPlayerWindowCode();
        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs"));

        code.Should().Contain("DictionaryPopupCanvasInputMode.VisibleHostsOnly");
        overlayCode.Should().Contain("RootContentCommitted");
        xaml.Should().NotContain(
            "x:Name=\"VideoDictionaryPanelChrome\"\r\n" +
            "                        Canvas.ZIndex=\"100\"\r\n" +
            "                        HorizontalAlignment=\"Stretch\"\r\n" +
            "                        VerticalAlignment=\"Stretch\"\r\n" +
            "                        Margin=\"16,16,16,120\"\r\n" +
            "                        Background=\"Transparent\"");
    }

    [Fact]
    public void VideoSubtitleLookup_UsesLatestRequestWinsCoordination()
    {
        var code = ReadVideoPlayerWindowCode();
        var normalizedCode = code.Replace("\r\n", "\n", StringComparison.Ordinal);
        var overlayCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs"));

        code.Should().Contain("_subtitleLookupCoordinator.BeginRequest()");
        code.Should().Contain("_subtitleLookupCoordinator.IsCurrent(lookupRequest)");
        code.Should().Contain("lookupRequest.CancellationToken");
        code.Should().Contain("cancellationToken: lookupRequest.CancellationToken");
        code.Should().Contain("ResolvePopupShowCancellation(");
        normalizedCode.Should().Contain("if (!IsCurrentSubtitleLookup(lookupRequest))");
        code.Should().Contain("stale request version={RequestVersion} failed after supersession");
        code.Should().Contain("popupRequest.TraceId");
        code.Should().Contain("[VideoLookup] request version={RequestVersion}");
        code.Should().NotContain("_isSubtitlePointerLookupRunning");
        overlayCode.Should().Contain("CancellationToken cancellationToken = default");
        overlayCode.Should().Contain("cancellationToken.ThrowIfCancellationRequested();");
        overlayCode.Should().Contain("cancellationToken: cancellationToken");
        overlayCode.Should().Contain(
            "public DictionaryPopupShowCancellationResult CancelShow(string? traceId)");
        overlayCode.Should().Contain("var contentCancelled = _rootHost.CancelPendingContent(");
        overlayCode.Should().Contain("if (contentCancelled)");
        overlayCode.Should().Contain("contentCancellationSucceeded: true");
        overlayCode.Should().Contain("RootContentAborted?.Invoke(this, e)");
    }

    [Fact]
    public void VideoSubtitleLookup_ReplacesVisiblePopupWithoutPredismiss()
    {
        var code = ReadVideoPlayerWindowCode();
        var mainCode = ReadVideoPlayerWindowMainCode()
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var missStart = mainCode.IndexOf(
            "if (popupRequest == null)",
            StringComparison.Ordinal);
        var missEnd = mainCode.IndexOf(
            "lookupOverlay = EnsurePopupOverlay();",
            missStart,
            StringComparison.Ordinal);
        var missCode = mainCode[missStart..missEnd];

        code.Should().Contain(
            "RootContentCommitted += PopupOverlay_RootContentCommitted");
        code.Should().Contain(
            "RootContentCommitted -= PopupOverlay_RootContentCommitted");
        code.Should().Contain("StagePopupCommit(");
        code.Should().Contain("TryTakePopupCommit(");
        mainCode.Should().Contain("CreatePopupCommitIdentity(");
        mainCode.Should().Contain("popupRequest.TraceId);");
        mainCode.Should().Contain("traceId: lookupCommitId");
        code.Should().Contain("!_isLookupPopupVisible");
        code.Should().Contain("HasPopupCommitCandidates");
        code.Should().Contain("ViewModel.StatusText = \"No dictionary results\"");
        missCode.Should().NotContain("Dismiss(");
        mainCode.Should().NotContain(
            "await HighlightSubtitleCanvasSelectionAsync(sentenceOffset");
        mainCode.Should().Contain(
            "_subtitleLookupCoordinator.CancelCurrentRequest();\n" +
            "            ResolvePopupShowCancellation(lookupOverlay, lookupCommitId);");
        code.Should().Contain(
            "RootContentAborted += PopupOverlay_RootContentAborted");
        code.Should().Contain(
            "RootContentAborted -= PopupOverlay_RootContentAborted");
        code.Should().Contain("MarkPopupCommitAccepted(commitIdentity)");
        code.Should().Contain("CancelPopupCommit(commitIdentity)");
    }

    [Fact]
    public void VideoSubtitleLookup_ClickAndShiftHoverShareTheSameRequestPath()
    {
        var code = ReadVideoPlayerWindowCode();

        code.Should().Contain(
            "await LookupSubtitleAtCanvasPointAsync(point, isHoverLookup: false)");
        code.Should().Contain(
            "_ = LookupSubtitleAtCanvasPointAsync(point, isHoverLookup: true)");
        code.Should().Contain("await StartSubtitleLookupAsync(");
    }

    [Fact]
    public void VideoSubtitleLookup_QueuedShowDropReleasesExactCandidate()
    {
        var code = ReadVideoPlayerWindowCode();
        var overlayCode = ReadProjectFile(
            "Views", "Dictionary", "DictionaryPopupOverlay.cs");
        var popupCode = ReadProjectFile(
            "Views", "Dictionary", "DictionaryLookupPopup.cs");

        popupCode.Should().Contain("QueuedShowDropped");
        popupCode.Should().Contain("NotifyQueuedShowDropped(");
        popupCode.Should().Contain("request.State.TryDropBeforeGeneration()");
        popupCode.Should().Contain("request.State.TryStartGeneration()");
        popupCode.Should().Contain("queuedRequest.CancellationToken.IsCancellationRequested");
        overlayCode.Should().Contain("RootShowDropped");
        overlayCode.Should().Contain("OnRootShowDropped");
        code.Should().Contain("RootShowDropped += PopupOverlay_RootShowDropped");
        code.Should().Contain("RootShowDropped -= PopupOverlay_RootShowDropped");
        code.Should().Contain("PopupOverlay_RootShowDropped(");
        code.Should().Contain("CancelPopupCommit(e.TraceId)");
    }

    [Fact]
    public void DictionaryPopup_GenerationStartedAlwaysEndsWithCommitOrAbort()
    {
        var popupCode = ReadProjectFile(
            "Views", "Dictionary", "DictionaryLookupPopup.cs")
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var overlayCode = ReadProjectFile(
            "Views", "Dictionary", "DictionaryPopupOverlay.cs")
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var cancelSucceededIndex = overlayCode.IndexOf(
            "if (contentCancelled)",
            StringComparison.Ordinal);
        var retainedOwnershipIndex = overlayCode.IndexOf(
            "retainedGeneration == generation",
            cancelSucceededIndex,
            StringComparison.Ordinal);
        var prepareIndex = popupCode.IndexOf(
            "var generation = PrepareForPendingContent(",
            StringComparison.Ordinal);
        var startStateIndex = popupCode.IndexOf(
            "request.State.TryStartGeneration()",
            prepareIndex,
            StringComparison.Ordinal);
        var terminalTryIndex = popupCode.IndexOf(
            "try\n        {",
            startStateIndex,
            StringComparison.Ordinal);
        var generationCallbackIndex = popupCode.IndexOf(
            "request.GenerationStarted?.Invoke(generation);",
            startStateIndex,
            StringComparison.Ordinal);

        popupCode.Should().Contain("_displayTransaction.TryCancelPending(");
        popupCode.Should().Contain("out var aborted");
        popupCode.Should().Contain("ContentCommitAborted?.Invoke(");
        popupCode.Should().Contain(
            "catch\n        {\n            CancelPendingContent(generation, request.TraceId);\n            throw;");
        popupCode.Should().Contain("if (CancelPendingContent(pendingGeneration, traceId))");
        startStateIndex.Should().BeGreaterThan(prepareIndex);
        terminalTryIndex.Should().BeGreaterThan(startStateIndex);
        generationCallbackIndex.Should().BeGreaterThan(terminalTryIndex);
        cancelSucceededIndex.Should().BeGreaterThanOrEqualTo(0);
        retainedOwnershipIndex.Should().BeGreaterThan(cancelSucceededIndex);
        overlayCode.Should().Contain(
            "return DictionaryPopupShowCancellationResult.Cancelled;");
        overlayCode.Should().Contain(
            "return DictionaryPopupShowCancellationResult.NoOwnership;");
    }

    [Fact]
    public void VideoSubtitleLookup_EmptyCanvasHitCancelsThePendingRequest()
    {
        var code = ReadVideoPlayerWindowCode().Replace("\r\n", "\n", StringComparison.Ordinal);

        code.Should().Contain("ClearSubtitleLookupFromPointer");
        code.Should().Contain("_subtitleLookupCoordinator.CancelCurrent();");
        code.Should().Contain("_popupOverlay?.Dismiss();");
        code.Should().Contain("ClearSubtitleCanvasSelection();");
    }

    [Fact]
    public void VideoSubtitleLookup_PrewarmsPopupWhenSubtitleWebViewIsReady()
    {
        var code = ReadVideoPlayerWindowCode();
        var readyIndex = code.IndexOf("case \"ready\":", StringComparison.Ordinal);
        var prewarmIndex = code.IndexOf(
            "PrewarmVideoDictionaryPopupAsync",
            readyIndex,
            StringComparison.Ordinal);

        readyIndex.Should().BeGreaterThanOrEqualTo(0);
        prewarmIndex.Should().BeGreaterThan(readyIndex);
        code.Should().Contain("await overlay.PrewarmAsync(");
        code.Should().Contain("RootGrid.XamlRoot");
        code.Should().Contain("[VideoLookup] Popup prewarm");
    }

    [Fact]
    public void VideoSubtitleAppearance_AppliesInspectorValuesToCanvas()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var code = ReadVideoPlayerWindowCode();
        var rendererPath = Path.Combine(ProjectRoot, "Services", "Video", "VideoSubtitleCanvasRenderer.cs");

        xaml.Should().Contain("x:Name=\"SubtitleCanvas\"");
        xaml.Should().Contain("win2d:CanvasControl");
        xaml.Should().Contain("x:Name=\"SubtitleWebView\"");
        xaml.Should().NotContain("x:Name=\"SubtitleVisibleText\"");
        xaml.Should().NotContain("x:Name=\"SubtitleShadowText");
        xaml.Should().NotContain("x:Name=\"SubtitleBlurTextLayer\"");
        xaml.Should().NotContain("x:Name=\"SubtitleNativeBlurTextLayer\"");
        xaml.Should().NotContain("x:Name=\"SubtitleMaskBlurImage\"");
        code.Should().Contain("fontSize = ViewModel.SubtitleFontSize");
        code.Should().Contain("shadowRadius = ViewModel.SubtitleShadowRadius");
        code.Should().Contain("blurRadius = ViewModel.CalculateSubtitleMaskBlurRadius");
        code.Should().Contain("UpdateSubtitleCanvasAppearance");
        code.Should().Contain("SubtitleCanvas.Invalidate()");
        code.Should().Contain("VideoSubtitleCanvasRenderer.Draw(");
        code.Should().Contain("SubtitleCanvas_Draw");
        code.Should().NotContain("new CanvasImageSource(");
        code.Should().NotContain("ApplySubtitleTextBlockStyle");
        code.Should().NotContain("UpdateSubtitleMaskBlurImageAsync");
        code.Should().NotContain("SubtitleNativeBlurTextLayer");
        code.Should().NotContain("SubtitleWebView.Opacity = 0.01");
        code.Should().NotContain("VideoSubtitleBlurLayout");
        code.Should().NotContain("VideoSubtitleMaskBlurLayout");
        code.Should().NotContain("VideoSubtitleMaskBitmapRenderer.RenderPngAsync");
        code.Should().Contain("subtitleColor = ViewModel.SubtitleColorHex");
        File.Exists(rendererPath).Should().BeTrue();
    }

    [Fact]
    public void VideoSubtitleLookup_UsesOnlyOneVisibleInteractiveTextLayer()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));

        xaml.Should().Contain("x:Name=\"SubtitleCanvas\"");
        xaml.Should().Contain("IsHitTestVisible=\"True\"");
        xaml.Should().Contain("x:Name=\"SubtitleWebView\"");
        xaml.Should().Contain("IsHitTestVisible=\"False\"");
        xaml.Should().Contain("Opacity=\"0\"");
    }

    [Fact]
    public void VideoSubtitleLookup_DoesNotStealFocusBeforeTheDomClickCompletes()
    {
        var code = ReadVideoPlayerWindowCode();

        code.Should().NotContain("SubtitlePanelBorder.AddHandler(UIElement.PointerPressedEvent");
        code.Should().NotContain("SubtitleWebView.AddHandler(UIElement.PointerPressedEvent");
        code.Should().NotContain("SubtitleWebView.GotFocus += SubtitleWebView_GotFocus");
        code.Should().NotContain("SubtitlePanelBorder_PointerPressed");
        code.Should().NotContain("SubtitleWebView_PointerPressed");
        code.Should().NotContain("SubtitleWebView_GotFocus");
        code.Should().Contain("SubtitleCanvas_PointerPressed");
    }

    [Fact]
    public void VideoSubtitleAppearance_UsesOneNiratanSoftShadowAndCanvasSelection()
    {
        var rendererPath = Path.Combine(ProjectRoot, "Services", "Video", "VideoSubtitleCanvasRenderer.cs");
        File.Exists(rendererPath).Should().BeTrue();
        var renderer = File.Exists(rendererPath) ? File.ReadAllText(rendererPath) : "";
        var code = ReadVideoPlayerWindowCode();

        renderer.Should().Contain("VideoSubtitleShadowLayout.Create(");
        renderer.Should().Contain("new GaussianBlurEffect");
        renderer.Should().Contain("GetCharacterRegions(");
        renderer.Should().Contain("SetColor(");
        renderer.Should().Contain("FillRectangle(");
        renderer.Should().Contain("LineSpacingBaseline = fontSize");
        renderer.Should().NotContain("CreateOffsets");
        code.Should().Contain("HighlightSubtitleCanvasSelection");
        code.Should().Contain("ClearSubtitleCanvasSelection");
    }

    [Fact]
    public void VideoSubtitleAppearance_DoesNotRenderSubtitleBackgroundLayer()
    {
        var code = ReadVideoPlayerWindowCode();
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));

        xaml.Should().NotContain("SubtitleBackgroundOpacitySlider");
        xaml.Should().NotContain("SubtitleBackgroundDisabledToggle");
        xaml.Should().NotContain("VideoInspectorBackgroundOpacityText");
        xaml.Should().NotContain("VideoInspectorNoBackgroundToggle");
        code.Should().NotContain("SubtitleBackgroundOpacitySlider");
        code.Should().NotContain("SubtitleBackgroundDisabledToggle");
        xaml.Should().Contain("x:Name=\"SubtitleWebView\"");
        xaml.Should().Contain("Opacity=\"0\"");
        code.Should().Contain("SubtitlePanelBorder.Background");
    }

    [Fact]
    public void VideoPlayerWindow_MaximizesAndPreservesSourceAspectRatioWhenOpeningVideoForTesting()
    {
        var windowCode = ReadVideoPlayerWindowCode();
        var mpvCode = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "Video", "MpvPlaybackEngine.cs"));

        windowCode.Should().Contain("MaximizeVideoWindowForTesting();");
        windowCode.Should().Contain("OverlappedPresenter");
        windowCode.Should().Contain("presenter.Maximize();");
        mpvCode.Should().Contain("MpvNative.SetOptionStringChecked(_handle, \"panscan\", \"0.0\")");
        mpvCode.Should().NotContain("MpvNative.SetOptionStringChecked(_handle, \"panscan\", \"1.0\")");
        mpvCode.Should().Contain("MpvNative.SetOptionStringChecked(_handle, \"sub-visibility\", \"no\")");
    }

    [Fact]
    public void VideoPlayerWindow_RestoresShortcutFocusFromVideoBottomChromeAndSubtitleClicks()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var code = ReadVideoPlayerWindowCode();

        xaml.Should().Contain("PointerPressed=\"VideoSurface_PointerPressed\"");
        code.Should().Contain("BottomChrome.AddHandler(UIElement.PointerPressedEvent");
        code.Should().NotContain("SubtitlePanelBorder.AddHandler(UIElement.PointerPressedEvent");
        code.Should().NotContain("SubtitleWebView.AddHandler(UIElement.PointerPressedEvent");
        code.Should().NotContain("SubtitleWebView.GotFocus += SubtitleWebView_GotFocus");
        code.Should().NotContain("SubtitleWebView_PointerPressed");
        code.Should().NotContain("SubtitlePanelBorder_PointerPressed");
        code.Should().Contain("RestoreVideoKeyboardFocus");
        code.Should().Contain("RestoreVideoKeyboardFocusAfterSubtitleInteraction");
        code.Should().Contain("SetWindowSubclass(_videoHwnd");
        code.Should().Contain("RemoveWindowSubclass(_videoHwnd");
        code.Should().Contain("VideoHostSubclassProc");
        code.Should().Contain("WM_LBUTTONDOWN");
        code.Should().Contain("WM_SETFOCUS");
        code.Should().Contain("SetForegroundWindow(_parentHwnd");
        code.Should().Contain("SetFocus(_parentHwnd");
    }

    [Fact]
    public void VideoPlayerWindow_PlaybackButtonStaysAtSameHorizontalHeightAsTransportButtons()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));

        xaml.Should().Contain("<Style x:Key=\"VideoTransportButtonStyle\" TargetType=\"AppBarButton\">");
        xaml.Should().Contain("<Setter Property=\"Width\" Value=\"40\" />");
        xaml.Should().Contain("<Setter Property=\"Height\" Value=\"40\" />");
        xaml.Should().Contain("<Setter Property=\"VerticalAlignment\" Value=\"Center\" />");
        xaml.Should().Contain("x:Key=\"VideoPrimaryTransportButtonStyle\"");
        xaml.Should().Contain("BasedOn=\"{StaticResource VideoTransportButtonStyle}\"");
        xaml.Should().Contain("Style=\"{StaticResource VideoPrimaryTransportButtonStyle}\"");
        xaml.Should().NotContain("<Setter Property=\"Width\" Value=\"48\" />");
        xaml.Should().NotContain("<Setter Property=\"Height\" Value=\"48\" />");
    }

    [Fact]
    public void VideoPlayerWindow_VideoLeftClickTogglesPlayPause()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var code = ReadVideoPlayerWindowCode();

        code.Should().Contain("TogglePlayPauseFromVideoClickAsync");
        xaml.Should().Contain("PointerPressed=\"BottomChromePopupRoot_PointerPressed\"");
        code.Should().Contain("VideoSurface_PointerPressed");
        code.Should().Contain("point.Properties.IsLeftButtonPressed");
        code.Should().Contain("RunVideoSurfaceSingleClick();");
        code.Should().Contain("case WM_LBUTTONDOWN:");
        code.Should().Contain("DispatcherQueue.TryEnqueue(RunVideoSurfaceSingleClick)");
        code.Should().Contain("IsVideoOverlayInteractiveSource");
        code.Should().NotContain("Task.Delay((int)GetDoubleClickTime())");
    }

    [Fact]
    public void VideoPlayerWindow_VideoLeftDoubleClickTogglesFullScreen()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var code = ReadVideoPlayerWindowCode();

        xaml.Should().Contain("DoubleTapped=\"BottomChromePopupRoot_DoubleTapped\"");
        code.Should().Contain("ToggleFullScreenFromVideoDoubleClick");
        code.Should().Contain("WM_LBUTTONDBLCLK");
        code.Should().Contain("case WM_LBUTTONDBLCLK:");
        code.Should().Contain("ToggleFullScreenFromVideoDoubleClick();");
        code.Should().NotContain("GetDoubleClickTime()");
        code.Should().NotContain("_videoSurfaceClickGeneration");
        code.Should().NotContain("TryConsumeVideoSurfaceDoubleClick");
    }

    [Fact]
    public void VideoPlayerWindow_DocksInspectorPanelBesideVideoSurface()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var code = ReadVideoPlayerWindowCode();

        xaml.Should().Contain("<ColumnDefinition Width=\"*\" />");
        xaml.Should().Contain("<ColumnDefinition Width=\"Auto\" />");
        xaml.Should().Contain("x:Name=\"VideoSurface\"");
        xaml.Should().Contain("Grid.Column=\"0\"");
        xaml.Should().Contain("x:Name=\"BottomChromePopup\"");
        xaml.Should().Contain("x:Name=\"BottomChrome\"");
        xaml.Should().Contain("x:Name=\"InspectorPanel\"");
        xaml.Should().Contain("Grid.Column=\"1\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoInspectorRightDockPanel\"");

        var popupIndex = xaml.IndexOf("x:Name=\"BottomChromePopup\"", StringComparison.Ordinal);
        var popupCloseIndex = xaml.IndexOf("</Popup>", popupIndex, StringComparison.Ordinal);
        var inspectorIndex = xaml.IndexOf("x:Name=\"InspectorPanel\"", StringComparison.Ordinal);
        inspectorIndex.Should().BeGreaterThan(popupCloseIndex);
        xaml.Substring(popupIndex, popupCloseIndex - popupIndex)
            .Should()
            .NotContain("x:Name=\"InspectorPanel\"");

        xaml.Should().Contain("x:Name=\"BottomChromePopupRoot\"");
        xaml.Should().Contain("Background=\"Transparent\"");
        xaml.Should().Contain("PointerPressed=\"BottomChromePopupRoot_PointerPressed\"");
        xaml.Should().Contain("DoubleTapped=\"BottomChromePopupRoot_DoubleTapped\"");
        xaml.Should().Contain("PointerWheelChanged=\"BottomChromePopupRoot_PointerWheelChanged\"");
        code.Should().Contain("BottomChromePopupRoot.Width = VideoSurface.ActualWidth");
        code.Should().Contain("BottomChromePopupRoot.Height = VideoSurface.ActualHeight");
        code.Should().Contain("PositionVideoHost();");
        code.Should().Contain("RefreshVideoLayoutAfterInspectorChanged");
        code.Should().Contain("DispatcherQueue.TryEnqueue(() =>");
    }

    [Fact]
    public void VideoPlayerWindow_AutoHidesBottomChromeAfterPointerIdleOrLeave()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var code = ReadVideoPlayerWindowCode();

        xaml.Should().Contain("PointerMoved=\"BottomChromePopupRoot_PointerMoved\"");
        xaml.Should().Contain("PointerExited=\"BottomChromePopupRoot_PointerExited\"");
        code.Should().Contain("VideoBottomChromeAutoHideState");
        code.Should().Contain("_bottomChromeAutoHideTimer.Interval = VideoBottomChromeAutoHideState.DefaultHideDelay;");
        code.Should().Contain("_bottomChromeAutoHideTimer.Tick += BottomChromeAutoHideTimer_Tick;");
        code.Should().Contain("ShowBottomChromeForPointerActivity();");
        code.Should().Contain("HideBottomChromeForPointerLeave();");
        code.Should().Contain("HideBottomChromeForInactivity();");
        code.Should().Contain("BottomChrome.Visibility = Visibility.Collapsed;");
    }

    [Fact]
    public void VideoPlayerWindow_BottomOverlayAndNativeHostPreserveWindowCornerResize()
    {
        var code = ReadVideoPlayerWindowCode();

        code.Should().Contain("RootGrid.AddHandler(UIElement.PointerPressedEvent");
        code.Should().Contain("TryGetBottomCornerResizeDirection");
        code.Should().Contain("BeginWindowResize");
        code.Should().Contain("VideoWindowResizeDirection.BottomLeft");
        code.Should().Contain("VideoWindowResizeDirection.BottomRight");
        code.Should().Contain("WM_SYSCOMMAND");
        code.Should().Contain("SC_SIZE");
        code.Should().Contain("ReleaseCapture();");
        code.Should().Contain("SendMessageW(");
        code.Should().Contain("_parentHwnd");
    }

    [Fact]
    public void VideoPlayerWindow_RightDockInspectorExposesNiratanPanelTabs()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var code = ReadVideoPlayerWindowCode();

        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoInspectorMiningHistoryTabButton\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoInspectorSubtitleListTabButton\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoInspectorChaptersTabButton\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoInspectorVideoTabButton\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoInspectorAudioTabButton\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoInspectorSubtitlesTabButton\"");
        xaml.Should().Contain("Unchecked=\"InspectorTabButton_Unchecked\"");
        xaml.Should().Contain("Text=\"挖卡历史\"");
        xaml.Should().Contain("Text=\"字幕列表\"");
        xaml.Should().Contain("Text=\"章节\"");
        xaml.Should().Contain("Click=\"LookupCurrentSubtitleButton_Click\"");
        xaml.Should().Contain("暂无挖卡历史。保存当前字幕后会显示在这里。");
        xaml.Should().NotContain("挖卡历史记录源接入后会显示在这里。");
        xaml.Should().Contain("x:Name=\"InspectorMiningHistoryContent\"");
        xaml.Should().Contain("x:Name=\"InspectorSubtitleListContent\"");
        xaml.Should().Contain("x:Name=\"InspectorChaptersContent\"");

        code.Should().Contain("VideoInspectorTab.MiningHistory");
        code.Should().Contain("VideoInspectorTab.SubtitleList");
        code.Should().Contain("VideoInspectorTab.Chapters");
        code.Should().Contain("VideoInspectorTab.Video");
        code.Should().Contain("VideoInspectorTab.Audio");
        code.Should().Contain("VideoInspectorTab.Subtitles");
        code.Should().Contain("LookupCurrentSubtitleButton_Click");
        code.Should().Contain("InspectorTabButton_Unchecked");
    }

    [Fact]
    public void VideoPlayerWindow_OverlayPreservesVideoWheelVolumeControl()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var code = ReadVideoPlayerWindowCode();

        xaml.Should().Contain("PointerWheelChanged=\"BottomChromePopupRoot_PointerWheelChanged\"");
        code.Should().Contain("BottomChromePopupRoot_PointerWheelChanged");
        code.Should().Contain("HandleVideoSurfacePointerWheelChangedAsync");
        code.Should().Contain("e.GetCurrentPoint(BottomChromePopupRoot)");
        code.Should().Contain("TransformToVisual(VideoSurface)");
        code.Should().Contain("VideoSurfaceVolumeScroll.TryGetAdjustment");
    }

    [Fact]
    public void VideoPlayerWindow_TranscriptSidebarUsesNiratanFullHeightCardStack()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var transcriptXaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoTranscriptListControl.xaml"));
        var code = ReadVideoPlayerWindowMainCode();
        var transcriptCode = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.Transcript.cs"));
        var playbackCode = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.Playback.cs"));
        var subtitleOverlayCode = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.SubtitleOverlay.cs"));
        var inspectorCode = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.Inspector.cs"));
        var tracksCode = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.Tracks.cs"));
        var viewModelCode = File.ReadAllText(Path.Combine(ProjectRoot, "ViewModels", "Pages", "VideoPlayerViewModel.cs"));
        var transcriptLoadCoordinatorCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Video", "VideoSubtitleTranscriptLoadCoordinator.cs"));
        var appCode = File.ReadAllText(Path.Combine(ProjectRoot, "App.xaml.cs"));

        XDocument.Parse(xaml);
        XDocument.Parse(transcriptXaml);

        xaml.Should().Contain("xmlns:video=\"using:Hoshi.Views.Video\"");
        xaml.Should().Contain("<video:VideoTranscriptListControl x:Name=\"InspectorSubtitleListContent\"");
        xaml.Should().NotContain("x:Name=\"TranscriptListView\"");

        transcriptXaml.Should().Contain("x:Key=\"VideoStudyListCardStyle\"");

        var transcriptStart = transcriptXaml.IndexOf("x:Name=\"TranscriptRoot\"", StringComparison.Ordinal);
        transcriptStart.Should().BeGreaterThan(0);

        var transcriptBlock = transcriptXaml.Substring(
            transcriptStart,
            Math.Min(5000, transcriptXaml.Length - transcriptStart));
        transcriptBlock.Should().StartWith("x:Name=\"TranscriptRoot\"");
        transcriptBlock.Should().Contain("<RowDefinition Height=\"Auto\" />");
        transcriptBlock.Should().Contain("<RowDefinition Height=\"*\" />");
        transcriptBlock.Should().Contain("Grid.Row=\"1\"");
        transcriptBlock.Should().Contain("Style=\"{StaticResource VideoStudyListCardStyle}\"");
        transcriptBlock.Should().Contain("Tapped=\"TranscriptCardBody_Tapped\"");
        transcriptBlock.Should().Contain("Tapped=\"TranscriptActions_Tapped\"");
        transcriptBlock.Should().Contain("ItemsSource=\"{Binding TranscriptVisibleRows}\"");
        transcriptBlock.Should().Contain("ScrollViewer.VerticalScrollBarVisibility=\"Hidden\"");
        transcriptBlock.Should().NotContain("MaxHeight=\"520\"");
        transcriptBlock.Should().NotContain("ItemClick=\"TranscriptListView_ItemClick\"");
        transcriptBlock.Should().NotContain("Style=\"{StaticResource VideoInspectorCardStyle}\"");

        transcriptCode.Should().Contain("OpenInspectorTab(VideoInspectorTab.SubtitleList)");
        transcriptCode.Should().Contain("ViewModel.RefreshTranscriptWindowForCurrentRow()");
        transcriptCode.Should().Contain("TranscriptVisibleRows_CollectionChanged");
        inspectorCode.Should().Contain("InspectorScrollableContent.Visibility = tab == VideoInspectorTab.SubtitleList");
        code.Should().Contain("InspectorSubtitleListContent.Initialize(ViewModel)");
        code.Should().Contain("InspectorSubtitleListContent.TranscriptSelected");
        code.Should().NotContain("private void UpdateTranscriptListVisibility()");
        code.Should().NotContain("private async Task TogglePlayPauseAsync");
        code.Should().NotContain("private void ApplySubtitleAppearance()");
        code.Should().NotContain("private void OpenInspectorTab");
        transcriptCode.Should().Contain("partial class VideoPlayerWindow");
        transcriptCode.Should().Contain("InspectorSubtitleListContent_TranscriptSelected");
        transcriptCode.Should().Contain("private void UpdateTranscriptListVisibility()");
        playbackCode.Should().Contain("partial class VideoPlayerWindow");
        playbackCode.Should().Contain("private async Task TogglePlayPauseAsync");
        playbackCode.Should().Contain("private async Task HandleVideoShortcutActionAsync");
        subtitleOverlayCode.Should().Contain("partial class VideoPlayerWindow");
        subtitleOverlayCode.Should().Contain("private void ApplySubtitleAppearance()");
        inspectorCode.Should().Contain("partial class VideoPlayerWindow");
        inspectorCode.Should().Contain("private void OpenInspectorTab");
        tracksCode.Should().Contain("partial class VideoPlayerWindow");
        tracksCode.Should().Contain("LoadEmbeddedTranscriptAsync");
        tracksCode.Should().Contain("CompleteEmbeddedTranscriptLoad");
        viewModelCode.Should().Contain("ReplaceEmbeddedSubtitleCues");
        viewModelCode.Should().Contain("BeginEmbeddedTranscriptLoad");
        tracksCode.Should().Contain("_subtitleTranscriptLoadCoordinator.LoadAsync");
        code.Should().NotContain("private async Task SelectSubtitleTrackAsync");
        code.Should().NotContain("_subtitleTranscriptLoadGeneration");
        transcriptLoadCoordinatorCode.Should().Contain("VideoSubtitleTranscriptLoadCoordinator");
        transcriptLoadCoordinatorCode.Should().Contain("CancelCore");
        appCode.Should().Contain("IVideoSubtitleTranscriptExtractor, FfmpegVideoSubtitleTranscriptExtractor");
        inspectorCode.Should().Contain("_isInspectorOpen = true");
        inspectorCode.Should().Contain("InspectorPanel.Visibility = Visibility.Visible");
    }

    [Fact]
    public void VideoSettingsPage_ExposesNiratanSettingsWithoutControlBarLayout()
    {
        var pagePath = Path.Combine(ProjectRoot, "Views", "Pages", "VideoSettingsPage.xaml");
        var settingsXaml = ReadProjectFile("Views", "Pages", "SettingsPage.xaml");
        var advancedXaml = ReadProjectFile("Views", "Pages", "AdvancedSettingsPage.xaml");
        var advancedCode = ReadProjectFile("Views", "Pages", "AdvancedSettingsPage.xaml.cs");
        var appCode = ReadProjectFile("App.xaml.cs");
        var navigationCode = ReadProjectFile("Services", "UI", "NavigationService.cs");
        var appPageCode = ReadProjectFile("Enums", "AppPage.cs");
        var enResources = ReadProjectFile("Strings", "en-US", "Resources.resw");
        var zhResources = ReadProjectFile("Strings", "zh-CN", "Resources.resw");

        File.Exists(pagePath).Should().BeTrue();
        var pageXaml = File.ReadAllText(pagePath);
        var pageCode = ReadProjectFile("Views", "Pages", "VideoSettingsPage.xaml.cs");
        XDocument.Parse(pageXaml);

        settingsXaml.Should().Contain("Tag=\"Hoshi.Views.Pages.VideoSettingsPage\"");
        settingsXaml.Should().NotContain("Content=\"Video\"\r\n                                IsEnabled=\"False\"");
        advancedXaml.Should().Contain("x:Uid=\"VideoSettingsButton\"");
        advancedXaml.Should().Contain("Click=\"VideoSettings_Click\"");
        advancedXaml.Should().NotContain("AutomationProperties.AutomationId=\"VideoSettingsButton\"\r\n                                IsEnabled=\"False\"");
        advancedCode.Should().Contain("private void VideoSettings_Click");
        advancedCode.Should().Contain("NavigateSettingsSubpage(typeof(VideoSettingsPage))");
        appCode.Should().Contain("AddTransient<VideoSettingsPageViewModel>");
        appCode.Should().Contain("AddTransient<KeyboardShortcutsSettingsPageViewModel>");
        appCode.Should().Contain("AddSingleton<IShortcutService, ShortcutService>");
        navigationCode.Should().Contain("typeof(VideoSettingsPage) => AppPage.VideoSettingsPage");
        navigationCode.Should().Contain("typeof(KeyboardShortcutsSettingsPage) => AppPage.KeyboardShortcutsSettingsPage");
        appPageCode.Should().Contain("VideoSettingsPage");
        appPageCode.Should().Contain("KeyboardShortcutsSettingsPage");

        foreach (var automationId in new[]
        {
            "VideoAutoPlayNextToggle",
            "VideoRememberPlaybackStateToggle",
            "VideoSeekIntervalNumberBox",
            "VideoMiningHistoryLimitNumberBox",
            "VideoKeyboardShortcutsButton",
            "VideoHardwareDecodingToggle",
            "VideoDeinterlacingToggle",
            "VideoHdrEnhancementToggle",
            "VideoBrightnessSlider",
            "VideoContrastSlider",
            "VideoSaturationSlider",
            "VideoGammaSlider",
            "VideoHueSlider",
            "VideoSubtitleFontFamilyComboBox",
            "VideoSubtitleFontSizeNumberBox",
            "VideoSubtitleFontWeightNumberBox",
            "VideoSubtitleShadowRadiusSlider",
            "VideoSubtitleBackgroundOpacitySlider",
            "VideoSubtitleBackgroundDisabledToggle",
            "VideoSubtitleVerticalPositionSlider",
            "VideoSubtitleColorTextBox",
            "VideoSubtitleLookupHighlightColorTextBox",
            "VideoSubtitleLookupHighlightTextColorTextBox",
            "VideoSubtitleMaskToggle",
            "VideoSubtitleMaskModeComboBox",
            "VideoSubtitleMaskBlurRadiusSlider",
            "VideoSubtitleMaskHiddenOpacitySlider",
        })
        {
            pageXaml.Should().Contain($"AutomationProperties.AutomationId=\"{automationId}\"");
        }

        pageXaml.Should().NotContain("ControlBar");
        pageXaml.Should().NotContain("Control Bar");
        pageXaml.Should().NotContain("Layout");
        pageXaml.Should().Contain("ItemsSource=\"{x:Bind ViewModel.AvailableSubtitleFonts}\"");
        pageXaml.Should().Contain("SelectedValuePath=\"SubtitleFontFamily\"");
        pageXaml.Should().Contain("SelectedValue=\"{x:Bind ViewModel.SubtitleFontFamily, Mode=TwoWay}\"");
        pageXaml.Should().NotContain("VideoSubtitleFontFamilyTextBox");
        pageCode.Should().Contain("Frame.Navigate(typeof(KeyboardShortcutsSettingsPage)");
        settingsXaml.Should().Contain("Tag=\"Hoshi.Views.Pages.KeyboardShortcutsSettingsPage\"");
        enResources.Should().Contain("VideoSettingsPageTitle.Text");
        zhResources.Should().Contain("VideoSettingsPageTitle.Text");
        zhResources.Should().Contain("KeyboardShortcutsPageTitle.Text");
        zhResources.Should().Contain("VideoKeyboardShortcutsCard.Header");
        zhResources.Should().Contain("<value>视频设置</value>");
    }

    [Fact]
    public void VideoPlayerWindow_UsesVideoSettingsDefaultsAndPlaybackLifecycle()
    {
        var windowCode = ReadVideoPlayerWindowCode();
        var viewModelCode = ReadProjectFile("ViewModels", "Pages", "VideoPlayerViewModel.cs");
        var appSettingsCode = ReadProjectFile("Models", "Settings", "AppSettings.cs");

        appSettingsCode.Should().Contain("public VideoSettings VideoSettings { get; set; } = new();");
        viewModelCode.Should().Contain("ApplySettings(settingsService.Current.VideoSettings)");
        viewModelCode.Should().Contain("public void ApplySettings(VideoSettings settings)");
        viewModelCode.Should().Contain("AutoPlayNextEpisode");
        viewModelCode.Should().Contain("RememberPlaybackState");
        viewModelCode.Should().Contain("SeekIntervalSeconds");
        viewModelCode.Should().NotContain("VideoControlBarLayout");

        windowCode.Should().Contain("App.GetService<IVideoLibraryService>");
        windowCode.Should().Contain("ViewModel.SeekIntervalSeconds");
        windowCode.Should().Contain("TryResolve(ShortcutScope.Video");
        windowCode.Should().Contain("HandleVideoShortcutActionAsync");
        windowCode.Should().Contain("VideoShortcutActions.SeekForwardId");
        windowCode.Should().NotContain("IsVideoShortcutKey");
        windowCode.Should().NotContain("case VirtualKey.Left");
        windowCode.Should().Contain("RestorePlaybackStateIfNeededAsync");
        windowCode.Should().Contain("SaveCurrentVideoProgressAsync");
        windowCode.Should().Contain("_isOpeningVideo");
        windowCode.Should().Contain("VideoProgressSaveGuard.ShouldSaveProgress");
        windowCode.Should().Contain("WaitForSeekPositionAsync");
        windowCode.Should().Contain("TryAutoPlayNextEpisodeAsync");
        windowCode.Should().Contain("SavePlaybackStateAsync");
        windowCode.Should().Contain("RememberPlaybackState");
        windowCode.Should().Contain("AutoPlayNextEpisode");
        windowCode.Should().NotContain("SeekStepSeconds");
        windowCode.Should().NotContain("VideoControlBarLayout");
    }

    [Fact]
    public void VideoPlayerWindow_PausesPlaybackUntilRestoreSeekIsApplied()
    {
        var windowCode = ReadVideoPlayerWindowMainCode();
        var methodStart = windowCode.IndexOf(
            "private async Task OpenPendingVideoAsync",
            StringComparison.Ordinal);
        var methodEnd = windowCode.IndexOf(
            "private async void LookupCurrentSubtitleButton_Click",
            methodStart,
            StringComparison.Ordinal);
        var methodCode = windowCode[methodStart..methodEnd];

        var pauseBeforeOpen = methodCode.IndexOf(
            "await _playbackEngine.SetPausedAsync(true, ct);",
            StringComparison.Ordinal);
        var openVideo = methodCode.IndexOf(
            "await _playbackEngine.OpenAsync(video.FilePath, video.SubtitlePath, restoreStartPosition, ct);",
            StringComparison.Ordinal);
        var restorePlayback = methodCode.IndexOf(
            "var restoredSubtitle = await RestorePlaybackStateIfNeededAsync(restoreState, restoreStartPosition, ct);",
            StringComparison.Ordinal);
        var resumeAfterRestore = methodCode.IndexOf(
            "await _playbackEngine.SetPausedAsync(false, ct);",
            StringComparison.Ordinal);

        methodCode.Should().Contain("var restoreState = await LoadPlaybackStateAsync(video, ct);");
        methodCode.Should().Contain("var restoreStartPosition = ViewModel.RememberPlaybackState");
        methodCode.Should().Contain("restoreState.ResolveRestorePosition(TimeSpan.Zero)");
        pauseBeforeOpen.Should().BeGreaterThanOrEqualTo(0);
        pauseBeforeOpen.Should().BeLessThan(openVideo);
        resumeAfterRestore.Should().BeGreaterThan(restorePlayback);
    }

    [Fact]
    public void VideoSubtitleFontPicker_UsesSharedFontOptions()
    {
        var settingsViewModelCode = ReadProjectFile("ViewModels", "Pages", "SettingsPageViewModel.cs");
        var videoSettingsViewModelCode = ReadProjectFile("ViewModels", "Pages", "VideoSettingsPageViewModel.cs");
        var videoPlayerViewModelCode = ReadProjectFile("ViewModels", "Pages", "VideoPlayerViewModel.cs");
        var playerXaml = ReadProjectFile("Views", "Video", "VideoPlayerWindow.xaml");
        var subtitleOverlayCode = ReadProjectFile("Views", "Video", "VideoPlayerWindow.SubtitleOverlay.cs");
        var fontCatalogPath = Path.Combine(ProjectRoot, "Models", "Settings", "JapaneseFontCatalog.cs");

        File.Exists(fontCatalogPath).Should().BeTrue("reader and video subtitle fonts should be defined once");
        var fontCatalogCode = File.ReadAllText(fontCatalogPath);
        fontCatalogCode.Should().Contain("Klee One");
        fontCatalogCode.Should().Contain("DefaultReaderCssValue");
        fontCatalogCode.Should().Contain("DefaultSubtitleFontFamily");
        settingsViewModelCode.Should().Contain("JapaneseFontCatalog.Fonts");
        videoSettingsViewModelCode.Should().Contain("JapaneseFontCatalog.Fonts");
        videoPlayerViewModelCode.Should().Contain("JapaneseFontCatalog.Fonts");
        playerXaml.Should().Contain("ItemsSource=\"{x:Bind ViewModel.AvailableSubtitleFonts}\"");
        playerXaml.Should().Contain("SelectedValuePath=\"SubtitleFontFamily\"");
        playerXaml.Should().Contain("SelectedValue=\"{x:Bind ViewModel.SubtitleFontFamily, Mode=TwoWay}\"");
        playerXaml.Should().Contain("SelectionChanged=\"SubtitleFontFamilyComboBox_SelectionChanged\"");
        subtitleOverlayCode.Should().Contain("SubtitleFontFamilyComboBox_SelectionChanged");
        subtitleOverlayCode.Should().Contain("ViewModel.SetSubtitleFontFamily(fontFamily);");
        subtitleOverlayCode.Should().Contain("ViewModel.RefreshSubtitlePanelHeight();");
        subtitleOverlayCode.Should().Contain("ApplySubtitleAppearance();");
        playerXaml.Should().NotContain("<ComboBoxItem Tag=\"Yu Gothic UI\"");
    }
}
