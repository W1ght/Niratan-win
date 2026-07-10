using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;
using Hoshi.Helpers;
using Hoshi.Services.Video;

namespace Hoshi.Views.Video;

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
            .Replace("__HOSHI_VIDEO_SUBTITLE_CSS__", css, StringComparison.Ordinal)
            .Replace("__HOSHI_VIDEO_SUBTITLE_JS__", script, StringComparison.Ordinal);
    }

    private async Task UpdateSubtitleWebViewAsync()
    {
        if (!_isSubtitleWebViewReady || SubtitleWebView.CoreWebView2 == null)
            return;

        var displaySettings = App.GetService<global::Hoshi.Services.Settings.ISettingsService>()
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
                $"window.hoshiVideoSubtitle?.setState({json});");
        }
        catch
        {
            // WebView2 can reject script execution while navigating; the next ready/state update will recover.
        }
    }

    private async Task HighlightSubtitleWebSelectionAsync(string matchedText)
    {
        if (!_isSubtitleWebViewReady
            || SubtitleWebView.CoreWebView2 == null
            || string.IsNullOrWhiteSpace(matchedText))
        {
            return;
        }

        var highlightCount = matchedText.EnumerateRunes().Count();
        if (highlightCount <= 0)
            return;

        try
        {
            await SubtitleWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.hoshiVideoSubtitle?.highlightSelection({highlightCount});");
        }
        catch
        {
        }
    }

    private async Task ClearSubtitleWebSelectionAsync()
    {
        if (!_isSubtitleWebViewReady || SubtitleWebView.CoreWebView2 == null)
            return;

        try
        {
            await SubtitleWebView.CoreWebView2.ExecuteScriptAsync(
                "window.hoshiVideoSubtitle?.clearSelection();");
        }
        catch
        {
        }
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

        ViewModel.SubtitleVerticalPosition = Math.Clamp(e.NewValue, -200, 200);
        ApplySubtitleAppearance();
    }

    private void SubtitleColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
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
                    break;
                case "hoverChanged":
                    _isSubtitlePointerOver = payload.ValueKind == JsonValueKind.Object
                        && payload.TryGetProperty("isHovering", out var hoveringElement)
                        && hoveringElement.GetBoolean();
                    ApplySubtitleAppearance();
                    break;
                case "lookupEmpty":
                    await ClearSubtitleWebSelectionAsync();
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
        if (payload.ValueKind != JsonValueKind.Object || _isSubtitlePointerLookupRunning)
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
        var anchor = SubtitleWebView.TransformToVisual(PopupOverlayCanvas)
            .TransformPoint(new Windows.Foundation.Point(x, y));

        _isSubtitlePointerLookupRunning = true;
        try
        {
            await LookupCurrentSubtitleAsync(
                query,
                offset,
                anchor,
                width,
                height);
        }
        finally
        {
            _isSubtitlePointerLookupRunning = false;
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
        _isLookupPopupVisible = false;
        VideoDictionaryPanelChrome.Visibility = Visibility.Collapsed;
        ApplySubtitleAppearance();
        _ = ClearSubtitleWebSelectionAsync();
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
        _isSubtitlePointerOver = false;
        ApplySubtitleAppearance();
    }

    private void SubtitlePanelBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SubtitleMaskBlurImage.Visibility == Visibility.Visible)
            UpdateSubtitleNativeTextAppearance();
    }

    private void ApplySubtitleAppearance()
    {
        if (ViewModel == null)
            return;

        SubtitlePanelTransform.Y = -ViewModel.SubtitleVerticalPosition;
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
        UpdateSubtitleNativeTextAppearance();
        _ = UpdateSubtitleWebViewAsync();
    }

    private void UpdateSubtitleNativeTextAppearance()
    {
        if (ViewModel == null)
            return;

        var textOpacity = ViewModel.CalculateSubtitleMaskOpacity(
            _isSubtitlePointerOver,
            _isLookupPopupVisible,
            _isPaused);
        var blurRadius = ViewModel.CalculateSubtitleMaskBlurRadius(
            _isSubtitlePointerOver,
            _isLookupPopupVisible,
            _isPaused);
        var text = ViewModel.CurrentSubtitleText ?? "";
        var fontSize = ViewModel.SubtitleFontSize;
        fontSize = Math.Clamp(fontSize, 12, 160);
        var shadowRadius = ViewModel.SubtitleShadowRadius;
        var subtitleColor = ViewModel.SubtitleColorHex;
        var fontWeight = new Windows.UI.Text.FontWeight
        {
            Weight = (ushort)Math.Clamp(ViewModel.SubtitleFontWeight, 100, 900),
        };
        var fontFamilyName = string.IsNullOrWhiteSpace(ViewModel.SubtitleFontFamily)
            ? "Segoe UI, Yu Gothic UI, Meiryo"
            : ViewModel.SubtitleFontFamily;
        var fontFamily = new FontFamily(fontFamilyName);
        var foregroundColor = ParseColorHex(subtitleColor, Colors.White);
        var foreground = new SolidColorBrush(foregroundColor);
        var shadowForeground = new SolidColorBrush(Colors.Black);
        var lineHeight = fontSize * 1.25;
        var isBlurred = blurRadius > 0.01 && text.Length > 0;

        foreach (var textBlock in GetSubtitleShadowTextBlocks())
        {
            ApplySubtitleTextBlockStyle(textBlock, text, fontSize, fontWeight, fontFamily, lineHeight, shadowForeground);
        }

        ApplySubtitleTextBlockStyle(SubtitleVisibleText, text, fontSize, fontWeight, fontFamily, lineHeight, foreground);

        SubtitleNativeTextLayer.Visibility = isBlurred ? Visibility.Collapsed : Visibility.Visible;
        SubtitleMaskBlurImage.Visibility = isBlurred ? Visibility.Visible : Visibility.Collapsed;
        SubtitleNativeTextLayer.Opacity = textOpacity;
        SubtitleWebView.Opacity = 0;
        SubtitleVisibleText.Opacity = 1;
        SubtitleVisibleText.RenderTransform = null;

        var offsets = VideoSubtitleShadowLayout.CreateOffsets(
            shadowRadius,
            1);
        var shadowBlocks = GetSubtitleShadowTextBlocks();
        for (var index = 0; index < shadowBlocks.Length; index++)
        {
            var offset = offsets[index];
            var textBlock = shadowBlocks[index];
            textBlock.Opacity = offset.Opacity;
            textBlock.RenderTransform = new TranslateTransform
            {
                X = offset.X,
                Y = offset.Y,
            };
        }

        var renderGeneration = ++_subtitleMaskBlurRenderGeneration;
        if (isBlurred)
        {
            _ = UpdateSubtitleMaskBlurImageAsync(
                renderGeneration,
                text,
                fontFamilyName,
                fontSize,
                fontWeight.Weight,
                foregroundColor,
                shadowRadius,
                blurRadius);
        }
        else
        {
            SubtitleMaskBlurImage.Source = null;
        }
    }

    private async Task UpdateSubtitleMaskBlurImageAsync(
        int renderGeneration,
        string text,
        string fontFamily,
        double fontSize,
        int fontWeight,
        Windows.UI.Color foreground,
        double shadowRadius,
        double blurRadius)
    {
        try
        {
            var width = Math.Max(1, SubtitlePanelBorder.ActualWidth);
            var height = Math.Max(1, SubtitlePanelBorder.ActualHeight);
            if (width <= 1 || height <= 1)
            {
                width = Math.Max(1, RootGrid.ActualWidth);
                height = Math.Max(1, ViewModel.SubtitlePanelHeight);
            }

            var pngBytes = await VideoSubtitleMaskBitmapRenderer.RenderPngAsync(
                text,
                width,
                height,
                fontFamily,
                fontSize,
                fontWeight,
                foreground,
                shadowRadius,
                blurRadius);

            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(pngBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            if (renderGeneration != _subtitleMaskBlurRenderGeneration)
                return;

            SubtitleMaskBlurImage.Source = bitmap;
        }
        catch
        {
            if (renderGeneration == _subtitleMaskBlurRenderGeneration)
                SubtitleMaskBlurImage.Source = null;
        }
    }

    private static void ApplySubtitleTextBlockStyle(
        TextBlock textBlock,
        string text,
        double fontSize,
        Windows.UI.Text.FontWeight fontWeight,
        FontFamily fontFamily,
        double lineHeight,
        Brush foreground)
    {
        textBlock.Text = text;
        textBlock.FontSize = fontSize;
        textBlock.FontWeight = fontWeight;
        textBlock.FontFamily = fontFamily;
        textBlock.LineHeight = lineHeight;
        textBlock.Foreground = foreground;
    }

    private TextBlock[] GetSubtitleShadowTextBlocks() =>
    [
        SubtitleShadowText0,
        SubtitleShadowText1,
        SubtitleShadowText2,
        SubtitleShadowText3,
        SubtitleShadowText4,
        SubtitleShadowText5,
        SubtitleShadowText6,
        SubtitleShadowText7,
    ];

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

        return (SubtitleFontFamilyComboBox.SelectedItem as global::Hoshi.Models.Settings.JapaneseFontOption)
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
