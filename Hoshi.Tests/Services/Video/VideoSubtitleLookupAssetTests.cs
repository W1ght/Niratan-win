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

    [Fact]
    public void VideoSubtitleLookup_UsesWebViewDomRangeHitTesting()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var code = ReadVideoPlayerWindowCode();
        var script = File.ReadAllText(Path.Combine(ProjectRoot, "Web", "VideoSubtitle", "subtitle-overlay.js"));

        xaml.Should().Contain("x:Name=\"SubtitleWebView\"");
        xaml.Should().NotContain("x:Name=\"SubtitleTextBox\"");
        xaml.Should().NotContain("x:Name=\"SubtitleTextRun\"");
        code.Should().Contain("WebMessageReceived += OnSubtitleWebMessageReceived");
        code.Should().Contain("UpdateSubtitleWebViewAsync");
        code.Should().NotContain("VideoSubtitleHitTestResolver.ResolveCharacterIndex");
        script.Should().Contain("document.caretPositionFromPoint");
        script.Should().Contain("getClientRects()");
        script.Should().Contain("getCharacterAtPoint");
        script.Should().Contain("window.chrome?.webview?.postMessage");
    }

    [Fact]
    public void VideoSubtitleLookup_UsesFloatingDictionaryPopup()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var code = ReadVideoPlayerWindowCode();

        xaml.Should().Contain("x:Name=\"VideoDictionaryPanelChrome\"");
        xaml.Should().Contain("HorizontalAlignment=\"Stretch\"");
        xaml.Should().Contain("VerticalAlignment=\"Stretch\"");
        xaml.Should().Contain("BorderThickness=\"0\"");
        xaml.Should().Contain("Visibility=\"Collapsed\"");
        xaml.Should().Contain("x:Name=\"PopupOverlayCanvas\"");
        xaml.Should().Contain("SizeChanged=\"PopupOverlayCanvas_SizeChanged\"");
        code.Should().NotContain("_popupOverlay.EmbedRoot(PopupOverlayCanvas)");
        code.Should().NotContain("IGlobalLookupPopupService");
        code.Should().NotContain("GlobalLookupPopupWindow");
        code.Should().NotContain("_globalLookupPopupService");
        code.Should().Contain("SubtitleWebView.TransformToVisual(PopupOverlayCanvas)");
        code.Should().Contain("VideoDictionaryPanelChrome.Visibility = Visibility.Visible");
        code.Should().Contain("EnsureVideoDictionaryOverlaySurfaceVisible(overlay);");
        code.Should().Contain("EnsureVideoDictionaryOverlaySurfaceVisible(EnsurePopupOverlay());");
        code.Should().Contain("await overlay.ShowLookupAsync(");
        code.Should().Contain("VideoDictionaryPanelChrome.Visibility = Visibility.Collapsed");
        code.Should().Contain("_popupOverlay?.UpdateRootSize(e.NewSize.Width, e.NewSize.Height)");
        code.Should().Contain("TryDismissLookupPopupFromOutsidePointer");
        code.Should().Contain("IsDescendantOf(source, VideoDictionaryPanelChrome)");
    }

    [Fact]
    public void VideoSubtitleAppearance_AppliesInspectorValuesToTransparentOverlay()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var code = ReadVideoPlayerWindowCode();
        var script = File.ReadAllText(Path.Combine(ProjectRoot, "Web", "VideoSubtitle", "subtitle-overlay.js"));
        var css = File.ReadAllText(Path.Combine(ProjectRoot, "Web", "VideoSubtitle", "subtitle-overlay.css"));

        xaml.Should().Contain("x:Name=\"SubtitleVisibleText\"");
        xaml.Should().NotContain("x:Name=\"SubtitleBlurTextLayer\"");
        xaml.Should().NotContain("x:Name=\"SubtitleNativeBlurTextLayer\"");
        xaml.Should().Contain("x:Name=\"SubtitleMaskBlurImage\"");
        xaml.Should().Contain("Opacity=\"0\"");
        code.Should().Contain("fontSize = ViewModel.SubtitleFontSize");
        code.Should().Contain("shadowRadius = ViewModel.SubtitleShadowRadius");
        code.Should().Contain("blurRadius = ViewModel.CalculateSubtitleMaskBlurRadius");
        code.Should().Contain("UpdateSubtitleNativeTextAppearance");
        code.Should().Contain("ApplySubtitleTextBlockStyle(textBlock, text, fontSize, fontWeight, fontFamily, lineHeight, shadowForeground)");
        code.Should().Contain("ApplySubtitleTextBlockStyle(SubtitleVisibleText, text, fontSize, fontWeight, fontFamily, lineHeight, foreground)");
        code.Should().Contain("SubtitleNativeTextLayer.Visibility = isBlurred");
        code.Should().Contain("SubtitleMaskBlurImage.Visibility = isBlurred");
        code.Should().Contain("UpdateSubtitleMaskBlurImageAsync");
        code.Should().NotContain("SubtitleNativeBlurTextLayer");
        code.Should().Contain("SubtitleWebView.Opacity = 0");
        code.Should().NotContain("SubtitleWebView.Opacity = isBlurred");
        code.Should().NotContain("VideoSubtitleBlurLayout");
        code.Should().NotContain("VideoSubtitleMaskBlurLayout");
        code.Should().Contain("VideoSubtitleMaskBitmapRenderer.RenderPngAsync");
        File.Exists(Path.Combine(ProjectRoot, "Services", "Video", "VideoSubtitleMaskBitmapRenderer.cs"))
            .Should()
            .BeTrue();
        code.Should().Contain("subtitleColor = ViewModel.SubtitleColorHex");
        script.Should().Contain("--subtitle-filter-blur");
        css.Should().Contain("filter: blur(var(--subtitle-filter-blur, 0px))");
    }

    [Fact]
    public void VideoSubtitleAppearance_DoesNotRenderSubtitleBackgroundLayer()
    {
        var code = ReadVideoPlayerWindowCode();
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var script = File.ReadAllText(Path.Combine(ProjectRoot, "Web", "VideoSubtitle", "subtitle-overlay.js"));
        var css = File.ReadAllText(Path.Combine(ProjectRoot, "Web", "VideoSubtitle", "subtitle-overlay.css"));

        xaml.Should().NotContain("SubtitleBackgroundOpacitySlider");
        xaml.Should().NotContain("SubtitleBackgroundDisabledToggle");
        xaml.Should().NotContain("VideoInspectorBackgroundOpacityText");
        xaml.Should().NotContain("VideoInspectorNoBackgroundToggle");
        code.Should().NotContain("SubtitleBackgroundOpacitySlider");
        code.Should().NotContain("SubtitleBackgroundDisabledToggle");
        xaml.Should().Contain("Opacity=\"0\"");
        script.Should().NotContain("has-background");
        script.Should().NotContain("--subtitle-background-opacity");
        css.Should().NotContain("has-background");
        css.Should().NotContain("--subtitle-background-opacity");
        css.Should().Contain("background: transparent !important");
        code.Split("SubtitleWebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0)")
            .Should()
            .HaveCountGreaterThanOrEqualTo(3);
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
        code.Should().Contain("SubtitlePanelBorder.AddHandler(UIElement.PointerPressedEvent");
        code.Should().Contain("SubtitleWebView.AddHandler(UIElement.PointerPressedEvent");
        code.Should().Contain("SubtitleWebView.GotFocus += SubtitleWebView_GotFocus");
        code.Should().Contain("SubtitleWebView_PointerPressed");
        code.Should().Contain("SubtitlePanelBorder_PointerPressed");
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
        xaml.Should().Contain("挖卡历史记录源接入后会显示在这里。");
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
        playbackCode.Should().Contain("private async Task HandleVideoShortcutKeyAsync");
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
}
