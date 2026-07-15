using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Serilog;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Niratan.Enums;
using Niratan.Messages;
using Niratan.Messages.Shortcuts;
using Niratan.Models.DTO;
using Niratan.Models.Settings;
using Niratan.Services.Settings;
using Niratan.Views.Pages;

namespace Niratan;

public sealed partial class MainWindow : Window, IRecipient<SetFullscreenMessage>
{
    private const string WindowTitle = "Niratan";
    private const string WindowIconPath = "Assets/AppIcon.ico";

    private const int MinWidth = 654;
    private const int MinHeight = 500;

    private readonly IMessenger _messenger;
    private readonly ISettingsService _settingsService;

    private readonly double _scaleFactor;
    private readonly OverlappedPresenter _presenter;

    private readonly Frame _rootFrame = new() { Content = new SplashPage() };
    private WindowState _lastNormalWindowState = new();
    private bool _wasMinimized;

    public MainWindow()
    {
        InitializeComponent();
        _messenger = App.GetService<IMessenger>();
        _settingsService = App.GetService<ISettingsService>();

        Title = WindowTitle;
        AppWindow.SetIcon(WindowIconPath);
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        Content = _rootFrame;
        SetTheme(_settingsService.Current.Theme);

        _presenter = OverlappedPresenter.Create();
        _presenter.PreferredMinimumWidth = MinWidth;
        _presenter.PreferredMinimumHeight = MinHeight;
        AppWindow.SetPresenter(_presenter);

        _scaleFactor = GetScaleFactor();
        var restoredBounds = GetSafeRestoredBounds(_settingsService.Current.MainWindowState);
        AppWindow.MoveAndResize(restoredBounds);
        _lastNormalWindowState = CreateWindowState(restoredBounds, isMaximized: false);

        if (_settingsService.Current.MainWindowState.IsMaximized)
            _presenter.Maximize();

        _settingsService.SettingChanged += Settings_Changed;
        Content.KeyDown += OnKeyDown;
        Closed += MainWindow_Closed;
        AppWindow.Changed += MainWindow_AppWindowChanged;

        _messenger.RegisterAll(this);
    }

    public void Receive(SetFullscreenMessage message) => SetFullscreenState(message.IsFullscreen);

    public void SetMicaBackdrop() => SystemBackdrop = new MicaBackdrop();

    public void NavigateToShell() =>
        _rootFrame.Navigate(typeof(ShellPage), null, new DrillInNavigationTransitionInfo());

    public void NavigateToError(Exception ex) =>
        _rootFrame.Navigate(
            typeof(InitializationErrorPage),
            ex,
            new DrillInNavigationTransitionInfo()
        );

    private void SetFullscreenState(bool isFullscreen)
    {
        if (isFullscreen)
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        else
            AppWindow.SetPresenter(_presenter);
    }

    private void SetTheme(ThemeMode themeMode)
    {
        var newTheme = themeMode switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        _rootFrame.RequestedTheme = newTheme;

        if (newTheme == ElementTheme.Default)
            newTheme =
                Application.Current.RequestedTheme == ApplicationTheme.Dark
                    ? ElementTheme.Dark
                    : ElementTheme.Light;

        Color buttonHoverBackgroundColor =
            newTheme == ElementTheme.Dark ? Color.FromArgb(255, 61, 61, 61) : Colors.LightGray;

        Color foregroundColor = newTheme == ElementTheme.Dark ? Colors.White : Colors.Black;

        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonHoverBackgroundColor = buttonHoverBackgroundColor;
        titleBar.ForegroundColor = foregroundColor;
        titleBar.ButtonForegroundColor = foregroundColor;
        titleBar.ButtonHoverForegroundColor = foregroundColor;
        titleBar.ButtonPressedForegroundColor = foregroundColor;
        titleBar.ButtonInactiveForegroundColor = foregroundColor;
        titleBar.InactiveForegroundColor = foregroundColor;
    }

    private void Settings_Changed(object? sender, SettingsChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.Theme))
            SetTheme(e.NewValue is ThemeMode newTheme ? newTheme : ThemeMode.System);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.F11)
        {
            _messenger.Send(new FullscreenShortcutMessage());
            e.Handled = true;
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        args.Handled = true;

        await SendAppLifecycleCheckpointAsync(AppLifecycleCheckpointReason.Closing);

        UpdateLastNormalWindowState();
        var lastWindowState = new WindowState
        {
            IsMaximized = _presenter.State == OverlappedPresenterState.Maximized,
            Width = _lastNormalWindowState.Width,
            Height = _lastNormalWindowState.Height,
            Left = _lastNormalWindowState.Left,
            Top = _lastNormalWindowState.Top,
        };

        _settingsService.Set(s => s.MainWindowState, lastWindowState);
        await _settingsService.SaveAsync();

        _settingsService.SettingChanged -= Settings_Changed;
        Closed -= MainWindow_Closed;
        AppWindow.Changed -= MainWindow_AppWindowChanged;

        Log.Information("Niratan closing — session ended");
        await Log.CloseAndFlushAsync();
        Close();
    }

    private async void MainWindow_AppWindowChanged(
        AppWindow sender,
        AppWindowChangedEventArgs args)
    {
        var isMinimized = _presenter.State == OverlappedPresenterState.Minimized;
        if (isMinimized && !_wasMinimized)
            await SendAppLifecycleCheckpointAsync(AppLifecycleCheckpointReason.Background);
        if (!isMinimized)
            UpdateLastNormalWindowState();
        _wasMinimized = isMinimized;
    }

    private RectInt32 GetSafeRestoredBounds(WindowState state)
    {
        var requestedBounds = new RectInt32(
            (int)(state.Left * _scaleFactor),
            (int)(state.Top * _scaleFactor),
            (int)(state.Width * _scaleFactor),
            (int)(state.Height * _scaleFactor));

        if (HasUsableWindowBounds(requestedBounds))
            return requestedBounds;

        var defaults = new WindowState();
        var primaryDisplay = DisplayArea.GetFromRect(
            new RectInt32(0, 0, 1, 1),
            DisplayAreaFallback.Primary);
        var workArea = primaryDisplay.WorkArea;
        var width = Math.Min((int)(defaults.Width * _scaleFactor), workArea.Width);
        var height = Math.Min((int)(defaults.Height * _scaleFactor), workArea.Height);

        return new RectInt32(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2),
            width,
            height);
    }

    private bool HasUsableWindowBounds(RectInt32 bounds)
    {
        if (bounds.Width < MinWidth * _scaleFactor || bounds.Height < MinHeight * _scaleFactor)
            return false;

        var display = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.None);
        if (display is null)
            return false;

        var workArea = display.WorkArea;
        var visibleWidth = Math.Min(bounds.X + bounds.Width, workArea.X + workArea.Width)
            - Math.Max(bounds.X, workArea.X);
        var visibleHeight = Math.Min(bounds.Y + bounds.Height, workArea.Y + workArea.Height)
            - Math.Max(bounds.Y, workArea.Y);
        return visibleWidth >= 64 && visibleHeight >= 32;
    }

    private void UpdateLastNormalWindowState()
    {
        if (_presenter.State != OverlappedPresenterState.Restored)
            return;

        var bounds = new RectInt32(
            AppWindow.Position.X,
            AppWindow.Position.Y,
            AppWindow.Size.Width,
            AppWindow.Size.Height);
        if (HasUsableWindowBounds(bounds))
            _lastNormalWindowState = CreateWindowState(bounds, isMaximized: false);
    }

    private WindowState CreateWindowState(RectInt32 bounds, bool isMaximized) =>
        new()
        {
            IsMaximized = isMaximized,
            Width = bounds.Width / _scaleFactor,
            Height = bounds.Height / _scaleFactor,
            Left = bounds.X / _scaleFactor,
            Top = bounds.Y / _scaleFactor,
        };

    private async Task<bool> SendAppLifecycleCheckpointAsync(
        AppLifecycleCheckpointReason reason)
    {
        var message = new AppBackgroundingMessage(reason);
        _ = _messenger.Send(message);
        if (!message.HasReceivedResponse)
            return false;

        try
        {
            return await message.Response;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Reader] Lifecycle checkpoint failed: {Reason}", reason);
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);

    // DPI scaling factor is used to convert logical coordinates to physical pixels
    // for MoveAndResize, which operates in physical pixels on WinUI3.
    private double GetScaleFactor()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return GetDpiForWindow(hwnd) / 96.0;
    }
}
