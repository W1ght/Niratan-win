using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Niratan.Models.Shortcuts;
using Niratan.Services.Profiles;
using Niratan.Services.Settings;
using Niratan.Services.Shortcuts;
using Serilog;
using Windows.Graphics;
using Windows.System;

namespace Niratan.Services.Dictionary;

public interface IGlobalLookupHotKeyRegistrar
{
    Task RegisterAsync(
        KeyboardShortcutBinding hotKey,
        Func<CancellationToken, Task> handler,
        CancellationToken ct = default);

    void Unregister();
}

public interface ISelectedTextReader
{
    Task<SelectedTextSnapshot?> TryReadSelectedTextAsync(CancellationToken ct = default);
}

public sealed class GlobalSelectionLookupService : IGlobalSelectionLookupService
{
    private readonly ISettingsService _settingsService;
    private readonly IGlobalLookupHotKeyRegistrar _hotKeyRegistrar;
    private readonly ISelectedTextReader _selectedTextReader;
    private readonly IGlobalLookupPopupService _popupService;
    private readonly IProfileRuntimeService? _profileRuntime;
    private readonly IShortcutService? _shortcutService;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private KeyboardShortcutBinding _registeredHotKey;
    private string _statusText = "Global lookup disabled.";

    public GlobalSelectionLookupService(
        ISettingsService settingsService,
        IGlobalLookupHotKeyRegistrar hotKeyRegistrar,
        ISelectedTextReader selectedTextReader,
        IGlobalLookupPopupService popupService,
        IProfileRuntimeService? profileRuntime = null,
        IShortcutService? shortcutService = null)
    {
        _settingsService = settingsService;
        _hotKeyRegistrar = hotKeyRegistrar;
        _selectedTextReader = selectedTextReader;
        _popupService = popupService;
        _profileRuntime = profileRuntime;
        _shortcutService = shortcutService;
        if (_shortcutService is not null)
            _shortcutService.ShortcutsChanged += OnShortcutsChanged;
    }

    public string StatusText => _statusText;

    public event EventHandler? StatusChanged;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _initializeGate.WaitAsync(ct);
        try
        {
            await InitializeCoreAsync(ct);
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        var settings = _settingsService.Current.GlobalLookup;
        var hotKey = ResolveHotKeyBinding();
        _hotKeyRegistrar.Unregister();
        _registeredHotKey = default;

        if (!settings.Enabled)
        {
            SetStatus("Global lookup disabled.");
            return;
        }

        try
        {
            await _hotKeyRegistrar.RegisterAsync(hotKey, TriggerLookupAsync, ct);
            _registeredHotKey = hotKey;
            try
            {
                await _popupService.PrewarmAsync(ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[GlobalLookup] Popup prewarm failed; lookup will initialize on demand.");
            }
            SetStatus($"Global lookup hotkey registered: {hotKey.Label}.");
        }
        catch (NotSupportedException)
        {
            SetStatus("Global lookup hotkey registration is not available in this build.");
        }
        catch (Exception ex)
        {
            SetStatus($"Global lookup hotkey registration failed: {ex.Message}");
        }
    }

    private KeyboardShortcutBinding ResolveHotKeyBinding() =>
        _shortcutService?.GetBinding(GlobalShortcutActions.LookupSelectedText)
        ?? GlobalShortcutActions.LookupSelectedText.DefaultBinding;

    private async void OnShortcutsChanged(object? sender, EventArgs e)
    {
        if (!_settingsService.Current.GlobalLookup.Enabled)
            return;

        var current = ResolveHotKeyBinding();
        if (_registeredHotKey.Matches(current))
            return;

        await InitializeAsync();
    }

    public async Task TriggerLookupAsync(CancellationToken ct = default)
    {
        if (!_settingsService.Current.GlobalLookup.Enabled)
            return;

        var selection = await _selectedTextReader.TryReadSelectedTextAsync(ct);
        var query = selection?.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            Log.Information("[GlobalLookup] Hotkey ignored because selected text is empty.");
            return;
        }

        Log.Information("[GlobalLookup] Hotkey lookup requested for '{Query}'", query);
        if (_profileRuntime is not null)
            await _profileRuntime.ActivateGlobalAsync(ct);

        await _popupService.ShowAsync(
            new SelectedTextSnapshot(query, selection?.ScreenBounds),
            ct);
    }

    private void SetStatus(string statusText)
    {
        if (string.Equals(_statusText, statusText, StringComparison.Ordinal))
            return;

        _statusText = statusText;
        Log.Information("[GlobalLookup] {StatusText}", statusText);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class NoOpGlobalLookupHotKeyRegistrar : IGlobalLookupHotKeyRegistrar
{
    public Task RegisterAsync(
        KeyboardShortcutBinding hotKey,
        Func<CancellationToken, Task> handler,
        CancellationToken ct = default) =>
        throw new NotSupportedException("Global lookup hotkey registration is not available in this build.");

    public void Unregister()
    {
    }
}

public sealed class Win32GlobalLookupHotKeyRegistrar : IGlobalLookupHotKeyRegistrar
{
    private const int HotKeyId = 0x4847;
    private const int GwlpWndProc = -4;
    private const uint WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWindows = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private IntPtr _hwnd;
    private IntPtr _previousWndProc;
    private WndProcDelegate? _wndProc;
    private Func<CancellationToken, Task>? _handler;
    private bool _registered;

    public Task RegisterAsync(
        KeyboardShortcutBinding hotKey,
        Func<CancellationToken, Task> handler,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Unregister();

        if (!ShortcutInputMapper.TryGetVirtualKey(hotKey, out var virtualKey, out _))
            throw new NotSupportedException($"Global lookup hotkey '{hotKey.Label}' is not supported in this build.");

        var window = App.MainWindow
            ?? throw new InvalidOperationException("Main window is not ready for global lookup hotkey registration.");
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Main window handle is not available for global lookup hotkey registration.");

        _handler = handler;
        _wndProc = WndProc;
        var wndProcPointer = Marshal.GetFunctionPointerForDelegate(_wndProc);
        _previousWndProc = SetWindowLongPtr(_hwnd, GwlpWndProc, wndProcPointer);
        if (_previousWndProc == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to attach global lookup hotkey message hook.");

        var modifiers = ToWin32Modifiers(hotKey.Modifiers) | ModNoRepeat;
        if (!RegisterHotKey(_hwnd, HotKeyId, modifiers, (uint)virtualKey))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            RestoreWindowProc();
            throw new InvalidOperationException($"Failed to register global lookup hotkey: {error.Message}", error);
        }

        _registered = true;
        Log.Information("[GlobalLookup] Registered Win32 hotkey {HotKey} hwnd={Hwnd}", hotKey.Label, _hwnd);
        return Task.CompletedTask;
    }

    private static uint ToWin32Modifiers(KeyboardShortcutModifiers modifiers)
    {
        var result = 0u;
        if (modifiers.HasFlag(KeyboardShortcutModifiers.Control))
            result |= ModControl;
        if (modifiers.HasFlag(KeyboardShortcutModifiers.Shift))
            result |= ModShift;
        if (modifiers.HasFlag(KeyboardShortcutModifiers.Alt))
            result |= ModAlt;
        if (modifiers.HasFlag(KeyboardShortcutModifiers.Windows))
            result |= ModWindows;
        return result;
    }

    public void Unregister()
    {
        if (_registered && _hwnd != IntPtr.Zero)
            UnregisterHotKey(_hwnd, HotKeyId);

        _registered = false;
        RestoreWindowProc();
        _handler = null;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        if (msg == WmHotKey && wParam.ToUInt32() == HotKeyId)
        {
            Log.Information("[GlobalLookup] Global lookup hotkey message received");
            _ = InvokeHandlerAsync();
            return IntPtr.Zero;
        }

        return CallWindowProc(_previousWndProc, hWnd, msg, wParam, lParam);
    }

    private async Task InvokeHandlerAsync()
    {
        var handler = _handler;
        if (handler is null)
            return;

        try
        {
            await handler(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[GlobalLookup] Hotkey handler failed.");
        }
    }

    private void RestoreWindowProc()
    {
        if (_hwnd != IntPtr.Zero && _previousWndProc != IntPtr.Zero)
            SetWindowLongPtr(_hwnd, GwlpWndProc, _previousWndProc);

        _hwnd = IntPtr.Zero;
        _previousWndProc = IntPtr.Zero;
        _wndProc = null;
    }

    private delegate IntPtr WndProcDelegate(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        IntPtr lParam);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        if (IntPtr.Size == 8)
            return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);

        return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(
        IntPtr lpPrevWndFunc,
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

public sealed class NoOpSelectedTextReader : ISelectedTextReader
{
    public Task<SelectedTextSnapshot?> TryReadSelectedTextAsync(CancellationToken ct = default) =>
        Task.FromResult<SelectedTextSnapshot?>(null);
}

public sealed class CascadingSelectedTextReader : ISelectedTextReader
{
    private readonly ISelectedTextReader[] _readers;

    public CascadingSelectedTextReader(params ISelectedTextReader[] readers)
    {
        _readers = readers;
    }

    public async Task<SelectedTextSnapshot?> TryReadSelectedTextAsync(CancellationToken ct = default)
    {
        foreach (var reader in _readers)
        {
            try
            {
                var selection = await reader.TryReadSelectedTextAsync(ct);
                if (!string.IsNullOrWhiteSpace(selection?.Text))
                    return selection;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[GlobalLookup] Selected-text reader failed; trying next reader.");
            }
        }

        return null;
    }
}

public sealed class UIAutomationSelectedTextReader : ISelectedTextReader
{
    private const int MaxTextPatternElementsToProbe = 64;
    private const int MaxTextLength = 512;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(350);
    private static readonly SemaphoreSlim s_readGate = new(1, 1);
    private static readonly Condition s_textPatternAvailableCondition =
        new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);

    public async Task<SelectedTextSnapshot?> TryReadSelectedTextAsync(CancellationToken ct = default)
    {
        if (!s_readGate.Wait(0))
        {
            Log.Debug("[GlobalLookup] UI Automation selected-text read skipped because a previous read is still running.");
            return null;
        }

        var releaseGate = true;
        try
        {
            var readTask = Task.Run(ReadSelectedTextCore, CancellationToken.None);
            var completedTask = await Task.WhenAny(readTask, Task.Delay(ReadTimeout, ct));
            if (!ReferenceEquals(completedTask, readTask))
            {
                releaseGate = false;
                _ = readTask.ContinueWith(
                    task =>
                    {
                        try
                        {
                            if (task.Exception is not null)
                                Log.Debug(task.Exception, "[GlobalLookup] Timed-out UI Automation read failed later.");
                        }
                        finally
                        {
                            s_readGate.Release();
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                Log.Debug("[GlobalLookup] UI Automation selected-text read timed out.");
                return null;
            }

            return await readTask;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[GlobalLookup] UI Automation selected-text read failed.");
            return null;
        }
        finally
        {
            if (releaseGate)
                s_readGate.Release();
        }
    }

    private static SelectedTextSnapshot? ReadSelectedTextCore()
    {
        var element = AutomationElement.FocusedElement;
        var selected = ReadSelectedTextFromElementAndParents(element);
        if (!string.IsNullOrWhiteSpace(selected?.Text))
            return selected;

        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
            return null;

        var root = AutomationElement.FromHandle(foregroundWindow);
        selected = ReadSelectedTextFromElement(root);
        if (!string.IsNullOrWhiteSpace(selected?.Text))
            return selected;

        try
        {
            var matches = root.FindAll(TreeScope.Descendants, s_textPatternAvailableCondition);
            var count = Math.Min(matches.Count, MaxTextPatternElementsToProbe);
            for (var i = 0; i < count; i++)
            {
                selected = ReadSelectedTextFromElement(matches[i]);
                if (!string.IsNullOrWhiteSpace(selected?.Text))
                    return selected;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[GlobalLookup] UI Automation foreground subtree scan failed.");
        }

        return null;
    }

    private static SelectedTextSnapshot? ReadSelectedTextFromElementAndParents(AutomationElement? element)
    {
        for (var depth = 0; element is not null && depth < 6; depth++)
        {
            var selected = ReadSelectedTextFromElement(element);
            if (!string.IsNullOrWhiteSpace(selected?.Text))
                return selected;

            try
            {
                element = TreeWalker.ControlViewWalker.GetParent(element);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static SelectedTextSnapshot? ReadSelectedTextFromElement(AutomationElement? element)
    {
        try
        {
            if (element?.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) == true
                && patternObject is TextPattern textPattern)
            {
                return CreateSelectionSnapshot(textPattern.GetSelection());
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[GlobalLookup] UI Automation TextPattern selection read failed.");
        }

        return null;
    }

    private static SelectedTextSnapshot? CreateSelectionSnapshot(TextPatternRange[] ranges)
    {
        if (ranges.Length == 0)
            return null;

        var builder = new StringBuilder();
        foreach (var range in ranges)
        {
            var text = range.GetText(MaxTextLength);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (builder.Length > 0)
                builder.AppendLine();
            builder.Append(text);
        }

        if (builder.Length == 0)
            return null;

        return new SelectedTextSnapshot(
            builder.ToString(),
            TryGetSelectionBounds(ranges));
    }

    private static RectInt32? TryGetSelectionBounds(TextPatternRange[] ranges)
    {
        double? left = null;
        double? top = null;
        double right = 0;
        double bottom = 0;

        foreach (var range in ranges)
        {
            System.Windows.Rect[] rectangles;
            try
            {
                rectangles = range.GetBoundingRectangles();
            }
            catch
            {
                continue;
            }

            foreach (var rectangle in rectangles)
            {
                var x = rectangle.X;
                var y = rectangle.Y;
                var width = rectangle.Width;
                var height = rectangle.Height;
                if (!double.IsFinite(x)
                    || !double.IsFinite(y)
                    || !double.IsFinite(width)
                    || !double.IsFinite(height)
                    || width <= 0
                    || height <= 0)
                {
                    continue;
                }

                left = left is null ? x : Math.Min(left.Value, x);
                top = top is null ? y : Math.Min(top.Value, y);
                right = Math.Max(right, x + width);
                bottom = Math.Max(bottom, y + height);
            }
        }

        if (left is null || top is null)
            return null;

        var roundedLeft = (int)Math.Floor(left.Value);
        var roundedTop = (int)Math.Floor(top.Value);
        return new RectInt32(
            roundedLeft,
            roundedTop,
            Math.Max(1, (int)Math.Ceiling(right) - roundedLeft),
            Math.Max(1, (int)Math.Ceiling(bottom) - roundedTop));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}

public sealed class Win32FocusedEditSelectedTextReader : ISelectedTextReader
{
    private const int GuiThreadInfoSize = 72;
    private const int GetWindowTextMaxClassNameLength = 256;
    private const int WM_GETTEXT = 0x000D;
    private const int WM_GETTEXTLENGTH = 0x000E;
    private const int EM_GETSEL = 0x00B0;
    private const int EM_GETSELTEXT = 0x043E;
    private const uint SendMessageTimeoutFlags = 0x0001 | 0x0002; // SMTO_BLOCK | SMTO_ABORTIFHUNG
    private const uint SendMessageTimeoutMilliseconds = 120;

    public Task<SelectedTextSnapshot?> TryReadSelectedTextAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
            return Task.FromResult<SelectedTextSnapshot?>(null);

        var threadId = GetWindowThreadProcessId(foregroundWindow, out _);
        if (threadId == 0)
            return Task.FromResult<SelectedTextSnapshot?>(null);

        var info = new GUITHREADINFO { cbSize = GuiThreadInfoSize };
        if (!GetGUIThreadInfo(threadId, ref info) || info.hwndFocus == IntPtr.Zero)
            return Task.FromResult<SelectedTextSnapshot?>(null);

        var className = GetWindowClassName(info.hwndFocus);
        if (!IsSupportedTextClass(className))
            return Task.FromResult<SelectedTextSnapshot?>(null);

        if (!TryGetSelectionRange(info.hwndFocus, out var selectionStart, out var selectionEnd))
            return Task.FromResult<SelectedTextSnapshot?>(null);

        if (selectionEnd <= selectionStart)
            return Task.FromResult<SelectedTextSnapshot?>(null);

        var selectedLength = selectionEnd - selectionStart;
        if (className.Contains("RICHEDIT", StringComparison.OrdinalIgnoreCase)
            && TryReadRichEditSelectedText(info.hwndFocus, selectedLength, out var richEditSelection))
        {
            return Task.FromResult<SelectedTextSnapshot?>(
                CreateSnapshot(richEditSelection!, info));
        }

        if (!TrySendMessageTimeout(
                info.hwndFocus,
                WM_GETTEXTLENGTH,
                IntPtr.Zero,
                IntPtr.Zero,
                out var textLengthResult))
        {
            return Task.FromResult<SelectedTextSnapshot?>(null);
        }

        var textLength = textLengthResult.ToInt32();
        if (textLength <= 0)
            return Task.FromResult<SelectedTextSnapshot?>(null);

        var textBuilder = new StringBuilder(textLength + 1);
        if (SendMessageTimeout(
                info.hwndFocus,
                WM_GETTEXT,
                new IntPtr(textBuilder.Capacity),
                textBuilder,
                SendMessageTimeoutFlags,
                SendMessageTimeoutMilliseconds,
                out _) == IntPtr.Zero)
        {
            return Task.FromResult<SelectedTextSnapshot?>(null);
        }

        var text = textBuilder.ToString();
        if (selectionStart < 0
            || selectionStart >= text.Length
            || selectionEnd > text.Length)
        {
            return Task.FromResult<SelectedTextSnapshot?>(null);
        }

        return Task.FromResult<SelectedTextSnapshot?>(
            CreateSnapshot(text[selectionStart..selectionEnd], info));
    }

    private static SelectedTextSnapshot CreateSnapshot(string text, GUITHREADINFO info) =>
        new(text, TryGetCaretScreenBounds(info));

    private static RectInt32? TryGetCaretScreenBounds(GUITHREADINFO info)
    {
        var hwnd = info.hwndCaret != IntPtr.Zero ? info.hwndCaret : info.hwndFocus;
        if (hwnd == IntPtr.Zero)
            return null;

        var topLeft = new POINT(info.rcCaret.left, info.rcCaret.top);
        var bottomRight = new POINT(info.rcCaret.right, info.rcCaret.bottom);
        if (!ClientToScreen(hwnd, ref topLeft) || !ClientToScreen(hwnd, ref bottomRight))
            return null;

        return new RectInt32(
            topLeft.X,
            topLeft.Y,
            Math.Max(1, bottomRight.X - topLeft.X),
            Math.Max(1, bottomRight.Y - topLeft.Y));
    }

    private static bool TryReadRichEditSelectedText(
        IntPtr hwnd,
        int selectedLength,
        out string? selectedText)
    {
        selectedText = null;
        if (selectedLength <= 0)
            return false;

        var builder = new StringBuilder(selectedLength + 1);
        if (SendMessageTimeout(
                hwnd,
                EM_GETSELTEXT,
                IntPtr.Zero,
                builder,
                SendMessageTimeoutFlags,
                SendMessageTimeoutMilliseconds,
                out _) == IntPtr.Zero)
        {
            return false;
        }

        var text = builder.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        selectedText = text;
        return true;
    }

    private static bool TryGetSelectionRange(IntPtr hwnd, out int selectionStart, out int selectionEnd)
    {
        selectionStart = 0;
        selectionEnd = 0;

        var startPointer = Marshal.AllocHGlobal(sizeof(int));
        var endPointer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(startPointer, 0);
            Marshal.WriteInt32(endPointer, 0);

            if (!TrySendMessageTimeout(hwnd, EM_GETSEL, startPointer, endPointer, out _))
                return false;

            selectionStart = Marshal.ReadInt32(startPointer);
            selectionEnd = Marshal.ReadInt32(endPointer);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(startPointer);
            Marshal.FreeHGlobal(endPointer);
        }
    }

    private static bool TrySendMessageTimeout(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        out IntPtr result) =>
        SendMessageTimeout(
            hwnd,
            message,
            wParam,
            lParam,
            SendMessageTimeoutFlags,
            SendMessageTimeoutMilliseconds,
            out result) != IntPtr.Zero;

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var builder = new StringBuilder(GetWindowTextMaxClassNameLength);
        return GetClassName(hwnd, builder, builder.Capacity) > 0
            ? builder.ToString()
            : string.Empty;
    }

    private static bool IsSupportedTextClass(string className) =>
        className.Contains("EDIT", StringComparison.OrdinalIgnoreCase)
        || className.Contains("RICHEDIT", StringComparison.OrdinalIgnoreCase);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        StringBuilder lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }
}
