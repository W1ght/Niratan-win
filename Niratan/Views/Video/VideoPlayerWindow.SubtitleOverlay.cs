using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Niratan.Helpers;
using Niratan.Models.Settings;
using Niratan.Models.Shortcuts;
using Niratan.Services.Settings;
using Niratan.Services.Video;
using Niratan.Views.Controls;
using Niratan.Views.Dictionary;
using Serilog;

namespace Niratan.Views.Video;

public sealed partial class VideoPlayerWindow
{
    private async Task InitializeSubtitleWebViewAsync()
    {
        if (_isSubtitleWebViewInitialized)
            return;

        _isSubtitleWebViewInitialized = true;
        SubtitleWebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        var environment = await WebView2EnvironmentHelper.GetOrCreateAsync();
        await SubtitleWebView.EnsureCoreWebView2Async(environment);
        SubtitleWebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);

        var coreWebView = SubtitleWebView.CoreWebView2;
        if (coreWebView == null)
            return;

        coreWebView.Settings.AreDefaultContextMenusEnabled = false;
        coreWebView.Settings.AreDevToolsEnabled = false;
        coreWebView.WebMessageReceived += OnSubtitleWebMessageReceived;
        coreWebView.NavigateToString(BuildSubtitleOverlayHtml());
    }

    private static string BuildSubtitleOverlayHtml()
    {
        var assetRoot = Path.Combine(AppContext.BaseDirectory, "Web", "VideoSubtitle");
        var html = File.ReadAllText(Path.Combine(assetRoot, "subtitle-overlay.html"));
        var css = File.ReadAllText(Path.Combine(assetRoot, "subtitle-overlay.css"));
        var script = File.ReadAllText(Path.Combine(assetRoot, "subtitle-overlay.js"));
        return html
            .Replace("__NIRATAN_VIDEO_SUBTITLE_CSS__", css, StringComparison.Ordinal)
            .Replace("__NIRATAN_VIDEO_SUBTITLE_JS__", script, StringComparison.Ordinal);
    }

    private async Task UpdateSubtitleWebViewAsync()
    {
        if (!_isSubtitleWebViewReady || SubtitleWebView.CoreWebView2 == null)
            return;

        var displaySettings = App.GetService<global::Niratan.Services.Settings.ISettingsService>()
            .Current.DictionaryDisplaySettings;
        var textOpacity = ViewModel.CalculateSubtitleMaskOpacity(
            _isSubtitlePointerOver,
            _isLookupPopupVisible,
            _isPaused);
        var blurRadius = ViewModel.CalculateSubtitleMaskBlurRadius(
            _isSubtitlePointerOver,
            _isLookupPopupVisible,
            _isPaused);
        var state = new
        {
            text = ViewModel.CurrentSubtitleText,
            fontFamily = ViewModel.SubtitleFontFamily,
            fontSize = ViewModel.SubtitleFontSize,
            fontWeight = ViewModel.SubtitleFontWeight,
            shadowRadius = ViewModel.SubtitleShadowRadius,
            textOpacity,
            blurRadius,
            subtitleColor = ViewModel.SubtitleColorHex,
            lookupHighlightColor = ViewModel.SubtitleLookupHighlightColorHex,
            lookupHighlightTextColor = ViewModel.SubtitleLookupHighlightTextColorHex,
            scanLength = Math.Clamp(displaySettings.ScanLength, 1, 64),
            scanNonJapaneseText = displaySettings.ScanNonJapaneseText,
        };

        try
        {
            var json = JsonSerializer.Serialize(state);
            await SubtitleWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.niratanVideoSubtitle?.setState({json});");
        }
        catch
        {
            // WebView2 can reject script execution while navigating; the next ready/state update will recover.
        }
    }

    private async Task HighlightSubtitleCanvasSelectionAsync(int selectionStart, string matchedText)
    {
        if (string.IsNullOrWhiteSpace(matchedText))
        {
            return;
        }

        _subtitleSelectionStart = Math.Clamp(
            selectionStart,
            0,
            Math.Max(0, (ViewModel.CurrentSubtitleText ?? "").Length - 1));
        _subtitleSelectionLength = matchedText.Length;
        SubtitleCanvas.Invalidate();

        if (!_isSubtitleWebViewReady || SubtitleWebView.CoreWebView2 == null)
            return;

        var highlightCount = matchedText.EnumerateRunes().Count();
        if (highlightCount <= 0)
            return;

        try
        {
            await SubtitleWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.niratanVideoSubtitle?.highlightSelection({highlightCount});");
        }
        catch
        {
        }
    }

    private async Task ClearSubtitleSelectionAsync()
    {
        ClearSubtitleCanvasSelection();
        if (!_isSubtitleWebViewReady || SubtitleWebView.CoreWebView2 == null)
            return;

        try
        {
            await SubtitleWebView.CoreWebView2.ExecuteScriptAsync(
                "window.niratanVideoSubtitle?.clearSelection();");
        }
        catch
        {
        }
    }

    private void ClearSubtitleCanvasSelection()
    {
        _subtitleSelectionStart = -1;
        _subtitleSelectionLength = 0;
        SubtitleCanvas.Invalidate();
    }

    private void SubtitleFontSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel == null)
            return;

        SetSubtitleFontSize(e.NewValue);
    }

    private void SubtitleFontWeightNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (ViewModel == null || _isUpdatingSubtitleAppearance || double.IsNaN(args.NewValue))
            return;

        ViewModel.SubtitleFontWeight = (int)Math.Clamp(Math.Round(args.NewValue / 100) * 100, 100, 900);
        ApplySubtitleAppearance();
    }

    private void SubtitleFontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel == null || _isUpdatingSubtitleAppearance)
            return;

        var fontFamily = ResolveSelectedSubtitleFontFamily();
        if (fontFamily == null)
            return;

        ViewModel.SetSubtitleFontFamily(fontFamily);
        ViewModel.RefreshSubtitlePanelHeight();
        SubtitleFontFamilyComboBox.SelectedValue = ViewModel.SubtitleFontFamily;
        ApplySubtitleAppearance();
        ViewModel.StatusText = $"Subtitle font {ViewModel.SubtitleFontFamilyText}";
    }

    private void SubtitleShadowRadiusSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel == null || _isUpdatingSubtitleAppearance)
            return;

        ViewModel.SetSubtitleShadowRadius(e.NewValue);
        ViewModel.RefreshSubtitlePanelHeight();
        ApplySubtitleAppearance();
    }

    private void SubtitleVerticalPositionSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel == null || _isUpdatingSubtitleAppearance)
            return;

        ViewModel.SubtitleVerticalPosition = VideoSubtitlePositionPolicy.Normalize(e.NewValue);
        ApplySubtitleAppearance();
    }

    private void SubtitleColorPicker_ColorChanged(CompactColorPicker sender, ColorChangedEventArgs args)
    {
        if (ViewModel == null || _isUpdatingSubtitleAppearance)
            return;

        var colorHex = FormatColorHex(args.NewColor);
        switch (sender.Name)
        {
            case nameof(SubtitleColorPicker):
                ViewModel.SetSubtitleColor(colorHex);
                break;
            case nameof(SubtitleLookupHighlightColorPicker):
                ViewModel.SetSubtitleLookupHighlightColor(colorHex);
                break;
            case nameof(SubtitleLookupHighlightTextColorPicker):
                ViewModel.SetSubtitleLookupHighlightTextColor(colorHex);
                break;
        }

        ApplySubtitleAppearance();
    }

    private void RestoreSubtitleDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
            return;

        _isUpdatingSubtitleAppearance = true;
        ViewModel.ResetSubtitleAppearance();
        SubtitleFontFamilyComboBox.SelectedValue = ViewModel.SubtitleFontFamily;
        SubtitleFontSizeSlider.Value = ViewModel.SubtitleFontSize;
        SubtitleFontWeightNumberBox.Value = ViewModel.SubtitleFontWeight;
        SubtitleShadowRadiusSlider.Value = ViewModel.SubtitleShadowRadius;
        SubtitleVerticalPositionSlider.Value = ViewModel.SubtitleVerticalPosition;
        SubtitleMaskBlurRadiusSlider.Value = ViewModel.SubtitleMaskBlurRadius;
        SubtitleMaskHiddenOpacitySlider.Value = ViewModel.SubtitleMaskHiddenOpacity;
        UpdateSubtitleMaskControls();
        SubtitleColorPicker.Color = ParseColorHex(ViewModel.SubtitleColorHex, Colors.White);
        SubtitleLookupHighlightColorPicker.Color = ParseColorHex(
            ViewModel.SubtitleLookupHighlightColorHex,
            Windows.UI.Color.FromArgb(0x3E, 0xB5, 0xC1, 0xCB));
        SubtitleLookupHighlightTextColorPicker.Color = ParseColorHex(
            ViewModel.SubtitleLookupHighlightTextColorHex,
            Colors.White);
        _isUpdatingSubtitleAppearance = false;
        ViewModel.RefreshSubtitlePanelHeight();
        ApplySubtitleAppearance();
        ViewModel.StatusText = "Subtitle appearance restored";
    }

    private void SubtitleMaskToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || _isUpdatingSubtitleAppearance)
            return;

        ViewModel.SubtitleMaskEnabled = SubtitleMaskToggle.IsOn;
        UpdateSubtitleMaskControls();
        ApplySubtitleAppearance();
    }

    private void SubtitleMaskModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null || _isUpdatingSubtitleAppearance)
            return;

        ViewModel.SetSubtitleMaskMode(ReferenceEquals(sender, SubtitleMaskTransparentModeButton)
            ? "Transparent"
            : "Blur");
        UpdateSubtitleMaskControls();
        ApplySubtitleAppearance();
    }

    private void SubtitleMaskBlurRadiusSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel == null || _isUpdatingSubtitleAppearance)
            return;

        ViewModel.SetSubtitleMaskBlurRadius(e.NewValue);
        ViewModel.RefreshSubtitlePanelHeight();
        ApplySubtitleAppearance();
    }

    private void SubtitleMaskHiddenOpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel == null || _isUpdatingSubtitleAppearance)
            return;

        ViewModel.SubtitleMaskHiddenOpacity = Math.Clamp(e.NewValue, 0, 1);
        ApplySubtitleAppearance();
    }

    private void SetSubtitleFontSize(double value)
    {
        var fontSize = Math.Clamp(value, 12, 72);
        ViewModel.SubtitleFontSize = fontSize;
        ViewModel.RefreshSubtitlePanelHeight();
        SubtitleFontSizeSlider.Value = fontSize;
        ApplySubtitleAppearance();
        ViewModel.StatusText = $"Subtitle size {fontSize:0}";
    }

    private async void OnSubtitleWebMessageReceived(
        CoreWebView2 sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var versionElement)
                || versionElement.GetInt32() != 1
                || !root.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            var type = typeElement.GetString();
            var payload = root.TryGetProperty("payload", out var payloadElement)
                ? payloadElement
                : default;

            switch (type)
            {
                case "ready":
                    _isSubtitleWebViewReady = true;
                    await UpdateSubtitleWebViewAsync();
                    _ = PrewarmVideoDictionaryPopupAsync();
                    break;
                case "hoverChanged":
                    _isSubtitlePointerOver = payload.ValueKind == JsonValueKind.Object
                        && payload.TryGetProperty("isHovering", out var hoveringElement)
                        && hoveringElement.GetBoolean();
                    ApplySubtitleAppearance();
                    break;
                case "lookupEmpty":
                    var emptyPolicy = VideoSubtitleLookupEmptyPolicy.FromWebPayload(payload);
                    _lastSubtitleHoverCharacterIndex = -1;
                    if (!emptyPolicy.DismissOnEmpty)
                        break;

                    ClearSubtitleLookupFromPointer();
                    RestoreVideoKeyboardFocusAfterSubtitleInteraction();
                    break;
                case "lookupRequest":
                    await HandleSubtitleWebLookupRequestAsync(payload);
                    RestoreVideoKeyboardFocusAfterSubtitleInteraction();
                    break;
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = ex.Message;
        }
    }

    private async Task HandleSubtitleWebLookupRequestAsync(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return;

        var query = payload.TryGetProperty("text", out var textElement)
            ? textElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(query))
            return;

        var offset = payload.TryGetProperty("offset", out var offsetElement)
            && offsetElement.TryGetInt32(out var parsedOffset)
                ? parsedOffset
                : 0;
        var x = GetJsonDouble(payload, "x", 0);
        var y = GetJsonDouble(payload, "y", 0);
        var width = Math.Max(1, GetJsonDouble(payload, "width", 1));
        var height = Math.Max(1, GetJsonDouble(payload, "height", ViewModel.SubtitleFontSize));
        EnsureVideoDictionaryOverlaySurfaceVisible(EnsurePopupOverlay());
        var anchor = SubtitleCanvas.TransformToVisual(PopupOverlayCanvas)
            .TransformPoint(new Windows.Foundation.Point(x, y));

        await StartSubtitleLookupAsync(
            query,
            offset,
            anchor,
            width,
            height);
    }

    private async Task PrewarmVideoDictionaryPopupAsync()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var overlay = EnsurePopupOverlay();
            await overlay.PrewarmAsync(
                RootGrid.XamlRoot,
                App.GetService<ISettingsService>().Current.Theme);
            Log.Information(
                "[VideoLookup] Popup prewarm completed in {Ms}ms",
                sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "[VideoLookup] Popup prewarm failed in {Ms}ms",
                sw.ElapsedMilliseconds);
        }
    }

    private static double GetJsonDouble(JsonElement payload, string propertyName, double fallback)
    {
        return payload.TryGetProperty(propertyName, out var element)
            && element.TryGetDouble(out var value)
            && double.IsFinite(value)
                ? value
                : fallback;
    }

    private void PopupOverlay_Dismissed(object? sender, EventArgs e)
    {
        _subtitleLookupCoordinator.CancelCurrent();
        _isLookupPopupVisible = false;
        VideoDictionaryPanelChrome.Visibility = Visibility.Collapsed;
        ApplySubtitleAppearance();
        _ = ClearSubtitleSelectionAsync();
    }

    private void ResolvePopupShowCancellation(
        DictionaryPopupOverlay? overlay,
        string? commitIdentity)
    {
        if (commitIdentity is null)
            return;

        var result = overlay?.CancelShow(commitIdentity)
            ?? DictionaryPopupShowCancellationResult.NoOwnership;
        if (result == DictionaryPopupShowCancellationResult.CommitAccepted)
            _subtitleLookupCoordinator.MarkPopupCommitAccepted(commitIdentity);
        else
            _subtitleLookupCoordinator.CancelPopupCommit(commitIdentity);

        CollapseVideoDictionarySurfaceIfUnowned();
    }

    private void PopupOverlay_RootContentAborted(
        object? sender,
        DictionaryPopupContentCommittedEventArgs e)
    {
        _subtitleLookupCoordinator.CancelPopupCommit(e.TraceId);
        CollapseVideoDictionarySurfaceIfUnowned();
    }

    private void PopupOverlay_RootShowDropped(
        object? sender,
        DictionaryPopupShowDroppedEventArgs e)
    {
        _subtitleLookupCoordinator.CancelPopupCommit(e.TraceId);
        CollapseVideoDictionarySurfaceIfUnowned();
    }

    private void CollapseVideoDictionarySurfaceIfUnowned()
    {
        if (!_isLookupPopupVisible
            && !_subtitleLookupCoordinator.HasPopupCommitCandidates)
        {
            VideoDictionaryPanelChrome.Visibility = Visibility.Collapsed;
        }
    }

    private void PopupOverlay_RootContentCommitted(
        object? sender,
        DictionaryPopupContentCommittedEventArgs e)
    {
        if (!_subtitleLookupCoordinator.TryTakePopupCommit(
                e.TraceId,
                out var commit))
        {
            return;
        }

        _ = HighlightSubtitleCanvasSelectionAsync(
            commit.SelectionStart,
            commit.MatchedText);
        _isLookupPopupVisible = true;
        VideoDictionaryPanelChrome.Visibility = Visibility.Visible;
        ViewModel.StatusText = "Lookup opened";
        ApplySubtitleAppearance();
    }

    private void PopupOverlayCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _popupOverlay?.UpdateRootSize(e.NewSize.Width, e.NewSize.Height);
    }

    private void SubtitlePanelBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isSubtitlePointerOver = true;
        ApplySubtitleAppearance();
    }

    private void SubtitlePanelBorder_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        CancelSubtitleHoverLookup();
        _isSubtitlePointerOver = false;
        _lastSubtitlePointerPoint = null;
        _lastSubtitleHoverCharacterIndex = -1;
        ApplySubtitleAppearance();
    }

    private void SubtitlePanelBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SubtitleCanvas.Invalidate();
        ApplySubtitleAppearance();
    }

    private void UpdateSubtitleVideoViewport(VideoViewportGeometry? geometry)
    {
        if (_subtitleVideoViewport == geometry)
            return;

        _subtitleVideoViewport = geometry;
        ApplySubtitleAppearance();
    }

    private void ApplySubtitleAppearance()
    {
        if (ViewModel == null)
            return;

        var surfaceWidth = Math.Max(BottomChromePopupRoot.ActualWidth, 0);
        var surfaceHeight = Math.Max(BottomChromePopupRoot.ActualHeight, 0);
        var viewportTop = 0.0;
        var viewportHeight = surfaceHeight;
        var viewportLeft = 0.0;
        var viewportRight = 0.0;

        if (_subtitleVideoViewport is { IsValid: true } geometry
            && surfaceWidth > 0
            && surfaceHeight > 0)
        {
            var horizontalScale = surfaceWidth / geometry.OsdWidth;
            var verticalScale = surfaceHeight / geometry.OsdHeight;
            viewportTop = geometry.TopMargin * verticalScale;
            viewportHeight = Math.Max(
                surfaceHeight - ((geometry.TopMargin + geometry.BottomMargin) * verticalScale),
                0);
            viewportLeft = geometry.LeftMargin * horizontalScale;
            viewportRight = geometry.RightMargin * horizontalScale;
        }

        var viewportWidth = Math.Max(surfaceWidth - viewportLeft - viewportRight, 0);
        var horizontalInset = Math.Min(16, viewportWidth / 4);
        SubtitlePanelBorder.Margin = new Thickness(
            viewportLeft + horizontalInset,
            0,
            viewportRight + horizontalInset,
            0);
        if (_subtitleVisibleBounds is { Width: > 0, Height: > 0 } visibleBounds)
        {
            SubtitlePanelTransform.Y = viewportTop + VideoSubtitlePositionPolicy.ContainerOriginY(
                viewportHeight,
                visibleBounds.Y,
                visibleBounds.Height,
                ViewModel.SubtitleVerticalPosition);
        }
        else
        {
            var subtitleHeight = SubtitlePanelBorder.ActualHeight > 0
                ? SubtitlePanelBorder.ActualHeight
                : ViewModel.SubtitlePanelHeight;
            SubtitlePanelTransform.Y = viewportTop + VideoSubtitlePositionPolicy.OriginY(
                viewportHeight,
                subtitleHeight,
                ViewModel.SubtitleVerticalPosition);
        }
        var backgroundOpacity = ViewModel.SubtitleBackgroundDisabled
            ? 0
            : Math.Clamp(ViewModel.SubtitleBackgroundOpacity, 0, 1);
        SubtitlePanelBorder.Background = backgroundOpacity <= 0
            ? new SolidColorBrush(Colors.Transparent)
            : new SolidColorBrush(Windows.UI.Color.FromArgb(
                (byte)Math.Round(backgroundOpacity * 255),
                0,
                0,
                0));
        SubtitlePanelBorder.BorderThickness = new Thickness(0);
        UpdateSubtitleCanvasAppearance();
        _ = UpdateSubtitleWebViewAsync();
    }

    private void UpdateSubtitleCanvasAppearance()
    {
        var textOpacity = ViewModel.CalculateSubtitleMaskOpacity(
            _isSubtitlePointerOver,
            _isLookupPopupVisible,
            _isPaused);
        SubtitleCanvas.Opacity = textOpacity;
        SubtitleCanvas.Invalidate();
    }

    private void SubtitleCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (ViewModel == null)
            return;

        var visibleBounds = VideoSubtitleCanvasRenderer.Draw(
            args.DrawingSession,
            new Windows.Foundation.Size(sender.ActualWidth, sender.ActualHeight),
            CreateSubtitleCanvasRenderOptions());
        if (visibleBounds.Width <= 0
            || visibleBounds.Height <= 0
            || AreClose(_subtitleVisibleBounds, visibleBounds))
        {
            return;
        }

        _subtitleVisibleBounds = visibleBounds;
        DispatcherQueue.TryEnqueue(ApplySubtitleAppearance);
    }

    private static bool AreClose(
        Windows.Foundation.Rect? current,
        Windows.Foundation.Rect candidate)
    {
        if (current is not { } value)
            return false;

        const double tolerance = 0.25;
        return Math.Abs(value.X - candidate.X) <= tolerance
            && Math.Abs(value.Y - candidate.Y) <= tolerance
            && Math.Abs(value.Width - candidate.Width) <= tolerance
            && Math.Abs(value.Height - candidate.Height) <= tolerance;
    }

    private VideoSubtitleCanvasRenderOptions CreateSubtitleCanvasRenderOptions()
    {
        var fontFamily = string.IsNullOrWhiteSpace(ViewModel.SubtitleFontFamily)
            || string.Equals(ViewModel.SubtitleFontFamily, "System Default", StringComparison.OrdinalIgnoreCase)
                ? "Segoe UI, Yu Gothic UI, Meiryo"
                : ViewModel.SubtitleFontFamily;
        return new VideoSubtitleCanvasRenderOptions(
            ViewModel.CurrentSubtitleText ?? "",
            fontFamily,
            ViewModel.SubtitleFontSize,
            ViewModel.SubtitleFontWeight,
            ParseColorHex(ViewModel.SubtitleColorHex, Colors.White),
            ViewModel.SubtitleShadowRadius,
            ViewModel.CalculateSubtitleMaskBlurRadius(
                _isSubtitlePointerOver,
                _isLookupPopupVisible,
                _isPaused),
            _subtitleSelectionStart,
            _subtitleSelectionLength,
            ParseColorHex(
                ViewModel.SubtitleLookupHighlightColorHex,
                Windows.UI.Color.FromArgb(0x3E, 0xB5, 0xC1, 0xCB)),
            ParseColorHex(ViewModel.SubtitleLookupHighlightTextColorHex, Colors.White));
    }

    private async void SubtitleCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        _lastSubtitleHoverCharacterIndex = -1;
        var point = e.GetCurrentPoint(SubtitleCanvas).Position;
        _lastSubtitlePointerPoint = point;
        await LookupSubtitleAtCanvasPointAsync(point, isHoverLookup: false);
        RestoreVideoKeyboardFocusAfterSubtitleInteraction();
    }

    private void SubtitleCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(SubtitleCanvas).Position;
        _lastSubtitlePointerPoint = point;
        var isShiftPressed = ShortcutInputMapper.GetCurrentModifiers()
            .HasFlag(KeyboardShortcutModifiers.Shift);
        if (!isShiftPressed)
        {
            CancelSubtitleHoverLookup();
            _lastSubtitleHoverCharacterIndex = -1;
            return;
        }

        ScheduleSubtitleHoverLookup(point);
    }

    private void ScheduleSubtitleHoverLookup(Windows.Foundation.Point point)
    {
        CancelSubtitleHoverLookup();
        _subtitleHoverLookupCts = new CancellationTokenSource();
        var token = _subtitleHoverLookupCts.Token;
        _ = RunSubtitleHoverLookupAsync(point, token);
    }

    private async Task RunSubtitleHoverLookupAsync(
        Windows.Foundation.Point point,
        CancellationToken ct)
    {
        try
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            await LookupSubtitleAtCanvasPointAsync(point, isHoverLookup: true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelSubtitleHoverLookup()
    {
        _subtitleHoverLookupCts?.Cancel();
        _subtitleHoverLookupCts?.Dispose();
        _subtitleHoverLookupCts = null;
    }

    private async Task LookupSubtitleAtCanvasPointAsync(
        Windows.Foundation.Point point,
        bool isHoverLookup)
    {
        if (ViewModel == null
            || !_isSubtitleWebViewReady
            || SubtitleWebView.CoreWebView2 == null)
        {
            return;
        }

        var policy = VideoSubtitleLookupEmptyPolicy.FromCanvasLookup(isHoverLookup);
        var size = new Windows.Foundation.Size(
            SubtitleCanvas.ActualWidth,
            SubtitleCanvas.ActualHeight);
        if (!VideoSubtitleCanvasRenderer.TryHitTestCharacter(
                CanvasDevice.GetSharedDevice(),
                size,
                CreateSubtitleCanvasRenderOptions(),
                point,
                out var hit))
        {
            _lastSubtitleHoverCharacterIndex = -1;
            if (policy.DismissOnEmpty)
                ClearSubtitleLookupFromPointer();
            return;
        }

        if (isHoverLookup && hit.CharacterIndex == _lastSubtitleHoverCharacterIndex)
            return;

        _lastSubtitleHoverCharacterIndex = hit.CharacterIndex;
        var requestJson = JsonSerializer.Serialize(new
        {
            offset = hit.CharacterIndex,
            x = hit.Bounds.X,
            y = hit.Bounds.Y,
            width = hit.Bounds.Width,
            height = hit.Bounds.Height,
            dismissOnEmpty = policy.DismissOnEmpty,
            isHover = policy.IsHover,
        });
        await SubtitleWebView.CoreWebView2.ExecuteScriptAsync(
            $"window.niratanVideoSubtitle?.lookupAtOffset({requestJson});");
    }

    private void ClearSubtitleLookupFromPointer()
    {
        _lastSubtitleHoverCharacterIndex = -1;
        _subtitleLookupCoordinator.CancelCurrent();
        _popupOverlay?.Dismiss();
        VideoDictionaryPanelChrome.Visibility = Visibility.Collapsed;
        _isLookupPopupVisible = false;
        _ = ClearSubtitleSelectionAsync();
        ApplySubtitleAppearance();
    }

    private void UpdateSubtitleAppearanceControls()
    {
        if (ViewModel == null)
            return;

        _isUpdatingSubtitleAppearance = true;
        SubtitleFontFamilyComboBox.SelectedValue = ViewModel.SubtitleFontFamily;
        SubtitleFontSizeSlider.Value = ViewModel.SubtitleFontSize;
        SubtitleFontWeightNumberBox.Value = ViewModel.SubtitleFontWeight;
        SubtitleShadowRadiusSlider.Value = ViewModel.SubtitleShadowRadius;
        SubtitleVerticalPositionSlider.Value = ViewModel.SubtitleVerticalPosition;
        SubtitleMaskToggle.IsOn = ViewModel.SubtitleMaskEnabled;
        SubtitleMaskBlurRadiusSlider.Value = ViewModel.SubtitleMaskBlurRadius;
        SubtitleMaskHiddenOpacitySlider.Value = ViewModel.SubtitleMaskHiddenOpacity;
        UpdateSubtitleMaskControls();
        SubtitleColorPicker.Color = ParseColorHex(ViewModel.SubtitleColorHex, Colors.White);
        SubtitleLookupHighlightColorPicker.Color = ParseColorHex(
            ViewModel.SubtitleLookupHighlightColorHex,
            Windows.UI.Color.FromArgb(0x3E, 0xB5, 0xC1, 0xCB));
        SubtitleLookupHighlightTextColorPicker.Color = ParseColorHex(
            ViewModel.SubtitleLookupHighlightTextColorHex,
            Colors.White);
        _isUpdatingSubtitleAppearance = false;
    }

    private string? ResolveSelectedSubtitleFontFamily()
    {
        if (SubtitleFontFamilyComboBox.SelectedValue is string selectedValue)
            return selectedValue;

        return (SubtitleFontFamilyComboBox.SelectedItem as global::Niratan.Models.Settings.JapaneseFontOption)
            ?.SubtitleFontFamily;
    }

    private void UpdateSubtitleMaskControls()
    {
        if (ViewModel == null)
            return;

        var isBlur = string.Equals(ViewModel.SubtitleMaskMode, "Blur", StringComparison.Ordinal);
        SubtitleMaskBlurModeButton.IsChecked = isBlur;
        SubtitleMaskTransparentModeButton.IsChecked = !isBlur;
        SubtitleMaskBlurModeButton.IsEnabled = ViewModel.SubtitleMaskEnabled;
        SubtitleMaskTransparentModeButton.IsEnabled = ViewModel.SubtitleMaskEnabled;
        SubtitleMaskBlurPanel.Visibility = isBlur ? Visibility.Visible : Visibility.Collapsed;
        SubtitleMaskHiddenOpacityPanel.Visibility = isBlur ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Windows.UI.Color ParseColorHex(string value, Windows.UI.Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var hex = value.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];

        if (hex.Length == 6)
            hex = "FF" + hex;

        if (hex.Length != 8)
            return fallback;

        try
        {
            return Windows.UI.Color.FromArgb(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16),
                Convert.ToByte(hex[6..8], 16));
        }
        catch
        {
            return fallback;
        }
    }

    private static string FormatColorHex(Windows.UI.Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private void UpdateSubtitleControlAvailability()
    {
        if (ViewModel == null)
            return;

        var hasPrimarySubtitle = !string.IsNullOrWhiteSpace(ViewModel.PrimarySubtitleName);
        ClearSubtitleButton.IsEnabled = hasPrimarySubtitle;
        ClearSubtitleButton.Visibility = hasPrimarySubtitle
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
