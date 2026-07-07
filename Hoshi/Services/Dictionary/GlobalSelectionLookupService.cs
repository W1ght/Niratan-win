using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Hoshi.Models.Settings;
using Hoshi.Services.Settings;
using Serilog;
using Windows.System;

namespace Hoshi.Services.Dictionary;

public interface IGlobalLookupHotKeyRegistrar
{
    Task RegisterAsync(
        string hotKey,
        Func<CancellationToken, Task> handler,
        CancellationToken ct = default);

    void Unregister();
}

public interface ISelectedTextReader
{
    Task<string?> TryReadSelectedTextAsync(CancellationToken ct = default);
}

public sealed class GlobalSelectionLookupService : IGlobalSelectionLookupService
{
    private readonly ISettingsService _settingsService;
    private readonly IGlobalLookupHotKeyRegistrar _hotKeyRegistrar;
    private readonly ISelectedTextReader _selectedTextReader;
    private readonly IGlobalLookupPopupService _popupService;
    private string _statusText = "Global lookup disabled.";

    public GlobalSelectionLookupService(
        ISettingsService settingsService,
        IGlobalLookupHotKeyRegistrar hotKeyRegistrar,
        ISelectedTextReader selectedTextReader,
        IGlobalLookupPopupService popupService)
    {
        _settingsService = settingsService;
        _hotKeyRegistrar = hotKeyRegistrar;
        _selectedTextReader = selectedTextReader;
        _popupService = popupService;
    }

    public string StatusText => _statusText;

    public event EventHandler? StatusChanged;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var settings = _settingsService.Current.GlobalLookup;
        _hotKeyRegistrar.Unregister();

        if (!settings.Enabled)
        {
            SetStatus("Global lookup disabled.");
            return;
        }

        try
        {
            await _hotKeyRegistrar.RegisterAsync(settings.HotKey, TriggerLookupAsync, ct);
            SetStatus($"Global lookup hotkey registered: {settings.HotKey}.");
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

    public async Task TriggerLookupAsync(CancellationToken ct = default)
    {
        if (!_settingsService.Current.GlobalLookup.Enabled)
            return;

        var selectedText = await _selectedTextReader.TryReadSelectedTextAsync(ct);
        var query = string.IsNullOrWhiteSpace(selectedText)
            ? null
            : selectedText.Trim();
        if (query is null)
        {
            Log.Information("[GlobalLookup] Hotkey ignored because selected text is empty.");
            return;
        }

        Log.Information("[GlobalLookup] Hotkey lookup requested for '{Query}'", query);
        await _popupService.ShowAsync(query, ct);
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
        string hotKey,
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
    private const uint ModNoRepeat = 0x4000;

    private IntPtr _hwnd;
    private IntPtr _previousWndProc;
    private WndProcDelegate? _wndProc;
    private Func<CancellationToken, Task>? _handler;
    private bool _registered;

    public Task RegisterAsync(
        string hotKey,
        Func<CancellationToken, Task> handler,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Unregister();

        if (!string.Equals(hotKey, GlobalLookupSettings.DefaultHotKey, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Global lookup hotkey '{hotKey}' is not supported in this build.");

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

        if (!RegisterHotKey(_hwnd, HotKeyId, ModControl | ModAlt | ModNoRepeat, (uint)VirtualKey.D))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            RestoreWindowProc();
            throw new InvalidOperationException($"Failed to register global lookup hotkey: {error.Message}", error);
        }

        _registered = true;
        Log.Information("[GlobalLookup] Registered Win32 hotkey {HotKey} hwnd={Hwnd}", hotKey, _hwnd);
        return Task.CompletedTask;
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
    public Task<string?> TryReadSelectedTextAsync(CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}

public sealed class CascadingSelectedTextReader : ISelectedTextReader
{
    private readonly ISelectedTextReader[] _readers;

    public CascadingSelectedTextReader(params ISelectedTextReader[] readers)
    {
        _readers = readers;
    }

    public async Task<string?> TryReadSelectedTextAsync(CancellationToken ct = default)
    {
        foreach (var reader in _readers)
        {
            try
            {
                var text = await reader.TryReadSelectedTextAsync(ct);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
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

    public async Task<string?> TryReadSelectedTextAsync(CancellationToken ct = default)
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

    private static string? ReadSelectedTextCore()
    {
        var element = AutomationElement.FocusedElement;
        var selected = ReadSelectedTextFromElementAndParents(element);
        if (!string.IsNullOrWhiteSpace(selected))
            return selected;

        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
            return null;

        var root = AutomationElement.FromHandle(foregroundWindow);
        selected = ReadSelectedTextFromElement(root);
        if (!string.IsNullOrWhiteSpace(selected))
            return selected;

        try
        {
            var matches = root.FindAll(TreeScope.Descendants, s_textPatternAvailableCondition);
            var count = Math.Min(matches.Count, MaxTextPatternElementsToProbe);
            for (var i = 0; i < count; i++)
            {
                selected = ReadSelectedTextFromElement(matches[i]);
                if (!string.IsNullOrWhiteSpace(selected))
                    return selected;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[GlobalLookup] UI Automation foreground subtree scan failed.");
        }

        return null;
    }

    private static string? ReadSelectedTextFromElementAndParents(AutomationElement? element)
    {
        for (var depth = 0; element is not null && depth < 6; depth++)
        {
            var selected = ReadSelectedTextFromElement(element);
            if (!string.IsNullOrWhiteSpace(selected))
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

    private static string? ReadSelectedTextFromElement(AutomationElement? element)
    {
        try
        {
            if (element?.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) == true
                && patternObject is TextPattern textPattern)
            {
                return JoinSelectedText(textPattern.GetSelection());
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[GlobalLookup] UI Automation TextPattern selection read failed.");
        }

        return null;
    }

    private static string? JoinSelectedText(TextPatternRange[] ranges)
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

        return builder.Length > 0 ? builder.ToString() : null;
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

    public Task<string?> TryReadSelectedTextAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
            return Task.FromResult<string?>(null);

        var threadId = GetWindowThreadProcessId(foregroundWindow, out _);
        if (threadId == 0)
            return Task.FromResult<string?>(null);

        var info = new GUITHREADINFO { cbSize = GuiThreadInfoSize };
        if (!GetGUIThreadInfo(threadId, ref info) || info.hwndFocus == IntPtr.Zero)
            return Task.FromResult<string?>(null);

        var className = GetWindowClassName(info.hwndFocus);
        if (!IsSupportedTextClass(className))
            return Task.FromResult<string?>(null);

        if (!TryGetSelectionRange(info.hwndFocus, out var selectionStart, out var selectionEnd))
            return Task.FromResult<string?>(null);

        if (selectionEnd <= selectionStart)
            return Task.FromResult<string?>(null);

        var selectedLength = selectionEnd - selectionStart;
        if (className.Contains("RICHEDIT", StringComparison.OrdinalIgnoreCase)
            && TryReadRichEditSelectedText(info.hwndFocus, selectedLength, out var richEditSelection))
        {
            return Task.FromResult<string?>(richEditSelection);
        }

        if (!TrySendMessageTimeout(
                info.hwndFocus,
                WM_GETTEXTLENGTH,
                IntPtr.Zero,
                IntPtr.Zero,
                out var textLengthResult))
        {
            return Task.FromResult<string?>(null);
        }

        var textLength = textLengthResult.ToInt32();
        if (textLength <= 0)
            return Task.FromResult<string?>(null);

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
            return Task.FromResult<string?>(null);
        }

        var text = textBuilder.ToString();
        if (selectionStart < 0
            || selectionStart >= text.Length
            || selectionEnd > text.Length)
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult<string?>(text[selectionStart..selectionEnd]);
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
}
