using System;
using Niratan.Enums;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT;

namespace Niratan.Views.Dictionary;

internal static class DictionaryPopupMaterial
{
    public static AcrylicBrush CreateInAppAcrylicThinBrush(ThemeMode themeMode = ThemeMode.System)
    {
        var brush = new AcrylicBrush
        {
            AlwaysUseFallback = false,
        };
        ApplyTheme(brush, themeMode);
        return brush;
    }

    public static DictionaryDesktopAcrylicThinBackdrop? TryApplyDesktopAcrylicThin(
        Window window,
        FrameworkElement rootElement)
    {
        window.SystemBackdrop = null;
        var controller = DictionaryDesktopAcrylicThinBackdrop.TryCreate(window, rootElement);
        if (controller is not null)
            return controller;

        window.SystemBackdrop = new DesktopAcrylicBackdrop();
        return null;
    }

    public static SolidColorBrush CreateTransparentBrush() => new(Colors.Transparent);

    public static SolidColorBrush CreateWindowFallbackBrush(ThemeMode themeMode = ThemeMode.System) =>
        new(GetWindowTintColor(themeMode));

    public static Windows.UI.Color GetOpaqueSurfaceColor(ThemeMode themeMode)
    {
        return IsThemeDark(themeMode)
            ? Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00)
            : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    }

    public static Windows.UI.Color GetOutlineColor(ThemeMode themeMode)
    {
        return IsThemeDark(themeMode)
            ? Windows.UI.Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3C)
            : Windows.UI.Color.FromArgb(0xFF, 0xD1, 0xD1, 0xD6);
    }

    public static void ApplyTheme(AcrylicBrush brush, ThemeMode themeMode)
    {
        var isDark = IsThemeDark(themeMode);
        brush.AlwaysUseFallback = false;
        brush.TintColor = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x24, 0x24, 0x24)
            : Windows.UI.Color.FromArgb(0xFF, 0xF8, 0xF8, 0xF8);
        brush.TintOpacity = isDark ? 0.12 : 0.78;
        brush.TintLuminosityOpacity = isDark ? 0.18 : 0.62;
        brush.FallbackColor = isDark
            ? Windows.UI.Color.FromArgb(0x58, 0x24, 0x24, 0x24)
            : Windows.UI.Color.FromArgb(0xDC, 0xF8, 0xF8, 0xF8);
    }

    public static bool IsThemeDark(ThemeMode themeMode) => themeMode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
    };

    private static Windows.UI.Color GetWindowTintColor(ThemeMode themeMode)
    {
        return IsThemeDark(themeMode)
            ? Windows.UI.Color.FromArgb(0x18, 0x18, 0x18, 0x18)
            : Windows.UI.Color.FromArgb(0x22, 0xF8, 0xF8, 0xF8);
    }
}

internal sealed class DictionaryDesktopAcrylicThinBackdrop : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _rootElement;
    private readonly DesktopAcrylicController _controller;
    private readonly SystemBackdropConfiguration _configuration;
    private ThemeMode _themeMode = ThemeMode.System;
    private bool _isDisposed;

    private DictionaryDesktopAcrylicThinBackdrop(
        Window window,
        FrameworkElement rootElement,
        DesktopAcrylicController controller,
        SystemBackdropConfiguration configuration)
    {
        _window = window;
        _rootElement = rootElement;
        _controller = controller;
        _configuration = configuration;
    }

    public static DictionaryDesktopAcrylicThinBackdrop? TryCreate(
        Window window,
        FrameworkElement rootElement)
    {
        if (!DesktopAcrylicController.IsSupported())
            return null;

        DispatcherQueue.GetForCurrentThread()?.EnsureSystemDispatcherQueue();

        var configuration = new SystemBackdropConfiguration
        {
            IsInputActive = true,
        };
        var controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Thin,
        };
        var backdrop = new DictionaryDesktopAcrylicThinBackdrop(
            window,
            rootElement,
            controller,
            configuration);

        backdrop.UpdateConfigurationTheme();
        window.Activated += backdrop.OnWindowActivated;
        window.Closed += backdrop.OnWindowClosed;
        rootElement.ActualThemeChanged += backdrop.OnRootActualThemeChanged;
        controller.AddSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>());
        controller.SetSystemBackdropConfiguration(configuration);
        return backdrop;
    }

    public void SetTheme(ThemeMode themeMode)
    {
        _themeMode = themeMode;
        UpdateConfigurationTheme();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        _configuration.IsInputActive =
            args.WindowActivationState != WindowActivationState.Deactivated;
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        Dispose();
    }

    private void OnRootActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_themeMode == ThemeMode.System)
            UpdateConfigurationTheme();
    }

    private void UpdateConfigurationTheme()
    {
        _configuration.Theme = _themeMode switch
        {
            ThemeMode.Dark => SystemBackdropTheme.Dark,
            ThemeMode.Light => SystemBackdropTheme.Light,
            _ => _rootElement.ActualTheme switch
            {
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                ElementTheme.Light => SystemBackdropTheme.Light,
                _ => SystemBackdropTheme.Default,
            },
        };
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _window.Activated -= OnWindowActivated;
        _window.Closed -= OnWindowClosed;
        _rootElement.ActualThemeChanged -= OnRootActualThemeChanged;
        _controller.Dispose();
    }
}
