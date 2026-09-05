using System;
using System.Threading.Tasks;
using Niratan.Models.Games;
using Niratan.Models.Settings;
using Niratan.Models.DTO;
using Niratan.Services.Settings;
using Niratan.Views.Games;
using Windows.Graphics;

namespace Niratan.Services.Games;

public sealed class GalGameTextOverlayService : IDisposable
{
    private readonly ISettingsService _settings;
    private GalGameTextOverlayWindow? _window;
    private bool _visible;
    private bool _dismissedByUser;

    public GalGameTextOverlayService(ISettingsService settings)
    {
        _settings = settings;
        _settings.SettingChanged += Settings_SettingChanged;
    }

    public void Show(
        Func<GalGameTextLine, int, string?, RectInt32, Task> lookup,
        Func<GalGameThreadPreview, Task> selectThread,
        Func<Task>? refresh = null,
        Func<Task>? stop = null,
        Func<string, Task>? toolbarAction = null,
        bool force = false)
    {
        if (force)
            _dismissedByUser = false;
        if (_dismissedByUser)
            return;

        _window ??= CreateWindow();
        _window.ApplyAppearance(CurrentAppearance());
        _window.LookupRequested = lookup;
        _window.ThreadSelected = selectThread;
        _window.RefreshRequested = refresh;
        _window.StopRequested = stop;
        _window.ToolbarActionRequested = toolbarAction;
        if (_visible)
            return;

        _visible = true;
        _window.ShowOverlay();
    }

    public void ResetDismissal() => _dismissedByUser = false;

    public void UpdateSnapshot(
        System.Collections.Generic.IReadOnlyList<GalGameThreadPreview> threads,
        System.Collections.Generic.IReadOnlyList<GalGameTextLine> lines,
        string status,
        ulong? selectedThreadId = null)
    {
        _window?.UpdateSnapshot(threads, lines, status, selectedThreadId);
    }

    public void Hide()
    {
        _visible = false;
        _dismissedByUser = true;
        _window?.HideOverlay();
        if (_window is not null)
        {
            _window.LookupRequested = null;
            _window.ThreadSelected = null;
            _window.RefreshRequested = null;
            _window.StopRequested = null;
            _window.ToolbarActionRequested = null;
        }
    }

    public void Dispose()
    {
        _settings.SettingChanged -= Settings_SettingChanged;
        _visible = false;
        _dismissedByUser = true;
        _window?.Close();
        _window = null;
    }

    private GalGameTextOverlayWindow CreateWindow()
    {
        var window = new GalGameTextOverlayWindow();
        window.ApplyAppearance(CurrentAppearance());
        window.Hidden += (_, _) => _visible = false;
        window.Hidden += (_, _) => _dismissedByUser = true;
        window.Closed += (_, _) =>
        {
            _dismissedByUser = true;
            if (ReferenceEquals(_window, window))
            {
                _visible = false;
                _window = null;
            }
        };
        return window;
    }

    private GalGameOverlayAppearanceSettings CurrentAppearance() =>
        (_settings.Current.GalGameSettings?.OverlayAppearance
            ?? new GalGameOverlayAppearanceSettings()).Normalize();

    private void Settings_SettingChanged(object? sender, SettingsChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName is nameof(AppSettings.GalGameSettings)
            or nameof(ISettingsService.Current))
        {
            _window?.ApplyAppearance(CurrentAppearance());
        }
    }
}
