using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Hoshi.Models.Sasayaki;
using Hoshi.Models.Settings;
using Hoshi.Services.Settings;
using Windows.UI;

namespace Hoshi.ViewModels.Pages;

public partial class SasayakiSettingsPageViewModel : ObservableObject
{
    private const int MinimumSearchWindowSize = 100;
    private const int MaximumSearchWindowSize = 10000;

    private readonly ISettingsService _settingsService;
    private bool _isInitializing = true;

    public double[] AvailablePlaybackRates { get; } =
        [0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

    [ObservableProperty]
    public partial bool EnableSasayaki { get; set; }

    [ObservableProperty]
    public partial bool ReaderShowSasayakiToggle { get; set; }

    [ObservableProperty]
    public partial bool AutoScroll { get; set; }

    [ObservableProperty]
    public partial bool AutoPauseOnLookup { get; set; }

    [ObservableProperty]
    public partial bool ShowSkipControls { get; set; }

    [ObservableProperty]
    public partial bool EnableSync { get; set; }

    [ObservableProperty]
    public partial int SearchWindowSize { get; set; } = SasayakiSettings.DefaultSearchWindow;

    public double SearchWindowSizeValue
    {
        get => SearchWindowSize;
        set => SearchWindowSize = ClampSearchWindow((int)Math.Round(value));
    }

    [ObservableProperty]
    public partial double SelectedPlaybackRate { get; set; } = 1.0;

    [ObservableProperty]
    public partial Color LightTextColor { get; set; }

    [ObservableProperty]
    public partial Color LightBackgroundColor { get; set; }

    [ObservableProperty]
    public partial Color DarkTextColor { get; set; }

    [ObservableProperty]
    public partial Color DarkBackgroundColor { get; set; }

    public SasayakiSettingsPageViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        LoadSettings();
        _isInitializing = false;
    }

    private void LoadSettings()
    {
        var settings = _settingsService.Current.SasayakiSettings;

        EnableSasayaki = settings.EnableSasayaki;
        ReaderShowSasayakiToggle = settings.ReaderShowSasayakiToggle;
        AutoScroll = settings.AutoScroll;
        AutoPauseOnLookup = settings.AutoPauseOnLookup;
        ShowSkipControls = settings.ShowSkipControls;
        EnableSync = settings.EnableSync;
        SearchWindowSize = ClampSearchWindow(settings.SearchWindowSize);
        SelectedPlaybackRate = settings.PlaybackRate;
        LightTextColor = ParseColor(settings.LightTextColor, "#FF000000");
        LightBackgroundColor = ParseColor(settings.LightBackgroundColor, "#6652C7FA");
        DarkTextColor = ParseColor(settings.DarkTextColor, "#FFFFFFFF");
        DarkBackgroundColor = ParseColor(settings.DarkBackgroundColor, "#6652C7FA");
    }

    private void SaveSettings()
    {
        if (_isInitializing) return;

        var settings = new SasayakiSettings
        {
            EnableSasayaki = EnableSasayaki,
            ReaderShowSasayakiToggle = ReaderShowSasayakiToggle,
            AutoScroll = AutoScroll,
            AutoPauseOnLookup = AutoPauseOnLookup,
            ShowSkipControls = ShowSkipControls,
            EnableSync = EnableSync,
            SearchWindowSize = ClampSearchWindow(SearchWindowSize),
            PlaybackRate = SelectedPlaybackRate,
            LightTextColor = FormatColor(LightTextColor),
            LightBackgroundColor = FormatColor(LightBackgroundColor),
            DarkTextColor = FormatColor(DarkTextColor),
            DarkBackgroundColor = FormatColor(DarkBackgroundColor),
        };

        _settingsService.Set(s => s.SasayakiSettings, settings);
        _ = _settingsService.SaveAsync();
    }

    partial void OnEnableSasayakiChanged(bool value) => SaveSettings();
    partial void OnReaderShowSasayakiToggleChanged(bool value) => SaveSettings();
    partial void OnAutoScrollChanged(bool value) => SaveSettings();
    partial void OnAutoPauseOnLookupChanged(bool value) => SaveSettings();
    partial void OnShowSkipControlsChanged(bool value) => SaveSettings();
    partial void OnEnableSyncChanged(bool value) => SaveSettings();
    partial void OnSearchWindowSizeChanged(int value)
    {
        OnPropertyChanged(nameof(SearchWindowSizeValue));
        SaveSettings();
    }
    partial void OnSelectedPlaybackRateChanged(double value) => SaveSettings();
    partial void OnLightTextColorChanged(Color value) => SaveSettings();
    partial void OnLightBackgroundColorChanged(Color value) => SaveSettings();
    partial void OnDarkTextColorChanged(Color value) => SaveSettings();
    partial void OnDarkBackgroundColorChanged(Color value) => SaveSettings();

    public void OnNavigatedFrom() => SaveSettings();

    private static int ClampSearchWindow(int value) =>
        Math.Clamp(value, MinimumSearchWindowSize, MaximumSearchWindowSize);

    private static Color ParseColor(string? value, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length == 6) text = "FF" + text;

        if (text.Length == 8
            && byte.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber, null, out var a)
            && byte.TryParse(text.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(text.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(text.Substring(6, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return Color.FromArgb(a, r, g, b);
        }

        return ParseColor(fallback, "#FF000000");
    }

    private static string FormatColor(Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
