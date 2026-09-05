using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Niratan.Services.Dictionary;

public interface IClipboardSnapshot : IDisposable
{
    void Restore();
}

public interface IClipboardSelectionPlatform
{
    IClipboardSnapshot CaptureClipboard();

    uint GetClipboardSequenceNumber();

    void ClearClipboard();

    void SendCleanCopyShortcut();

    string? TryReadUnicodeText();
}

/// <summary>
/// Last-resort selected-text reader for hosts which do not expose a usable UI Automation
/// TextPattern (for example Qt/Chromium surfaces). It performs one bounded copy operation
/// and restores the previous OLE clipboard object; it never monitors the clipboard.
/// </summary>
public sealed class ClipboardCopySelectedTextReader : ISelectedTextReader
{
    private const int PollAttempts = 24;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly SemaphoreSlim s_captureGate = new(1, 1);
    private readonly IClipboardSelectionPlatform _platform;

    public ClipboardCopySelectedTextReader(IClipboardSelectionPlatform platform)
    {
        _platform = platform;
    }

    public async Task<SelectedTextSnapshot?> TryReadSelectedTextAsync(
        CancellationToken ct = default)
    {
        await s_captureGate.WaitAsync(ct);
        try
        {
            return await CaptureCoreAsync(ct);
        }
        finally
        {
            s_captureGate.Release();
        }
    }

    private async Task<SelectedTextSnapshot?> CaptureCoreAsync(CancellationToken ct)
    {
        IClipboardSnapshot snapshot;
        try
        {
            snapshot = _platform.CaptureClipboard();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[GlobalLookup] Could not snapshot the clipboard for copy fallback.");
            return null;
        }

        string? selectedText = null;
        try
        {
            _platform.ClearClipboard();
            var clearedSequence = _platform.GetClipboardSequenceNumber();
            _platform.SendCleanCopyShortcut();

            for (var attempt = 0; attempt < PollAttempts; attempt++)
            {
                await Task.Delay(PollInterval, ct);
                if (_platform.GetClipboardSequenceNumber() == clearedSequence)
                    continue;

                selectedText = _platform.TryReadUnicodeText();
                if (!string.IsNullOrWhiteSpace(selectedText))
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[GlobalLookup] Clipboard copy fallback failed.");
        }
        finally
        {
            try
            {
                snapshot.Restore();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[GlobalLookup] Failed to restore the clipboard after selection capture.");
            }
            finally
            {
                try
                {
                    snapshot.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[GlobalLookup] Failed to release the clipboard snapshot.");
                }
            }
        }

        Log.Debug(
            "[GlobalLookup] Clipboard copy fallback completed success={Success} length={Length}.",
            !string.IsNullOrWhiteSpace(selectedText),
            selectedText?.Length ?? 0);
        return string.IsNullOrWhiteSpace(selectedText)
            ? null
            : new SelectedTextSnapshot(selectedText, ScreenBounds: null);
    }
}

public sealed class Win32ClipboardSelectionPlatform : IClipboardSelectionPlatform
{
    private const uint CfUnicodeText = 13;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkC = 0x43;
    private const ushort VkLeftWindows = 0x5B;
    private const ushort VkRightWindows = 0x5C;
    private const int RpcChangedMode = unchecked((int)0x80010106);

    public IClipboardSnapshot CaptureClipboard()
    {
        var initializeResult = OleInitialize(IntPtr.Zero);
        if (initializeResult < 0 && initializeResult != RpcChangedMode)
            Marshal.ThrowExceptionForHR(initializeResult);

        var shouldUninitialize = initializeResult >= 0;
        try
        {
            var result = OleGetClipboard(out var dataObject);
            if (result < 0)
                Marshal.ThrowExceptionForHR(result);

            return new OleClipboardSnapshot(dataObject, shouldUninitialize);
        }
        catch
        {
            if (shouldUninitialize)
                OleUninitialize();
            throw;
        }
    }

    public uint GetClipboardSequenceNumber() =>
        NativeGetClipboardSequenceNumber();

    public void ClearClipboard()
    {
        if (!OpenClipboard(IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open the clipboard.");

        try
        {
            if (!EmptyClipboard())
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to clear the clipboard.");
        }
        finally
        {
            CloseClipboard();
        }
    }

    public void SendCleanCopyShortcut()
    {
        var inputs = new[]
        {
            KeyboardInput(VkShift, KeyEventKeyUp),
            KeyboardInput(VkMenu, KeyEventKeyUp),
            KeyboardInput(VkLeftWindows, KeyEventKeyUp),
            KeyboardInput(VkRightWindows, KeyEventKeyUp),
            KeyboardInput(VkControl, KeyEventKeyUp),
            KeyboardInput(VkControl, 0),
            KeyboardInput(VkC, 0),
            KeyboardInput(VkC, KeyEventKeyUp),
            KeyboardInput(VkControl, KeyEventKeyUp),
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to send the copy shortcut.");
    }

    public string? TryReadUnicodeText()
    {
        if (!IsClipboardFormatAvailable(CfUnicodeText) || !OpenClipboard(IntPtr.Zero))
            return null;

        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero)
                return null;

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                return null;

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static INPUT KeyboardInput(ushort virtualKey, uint flags) =>
        new()
        {
            Type = InputKeyboard,
            Data = new INPUTUNION
            {
                Keyboard = new KEYBDINPUT
                {
                    VirtualKey = virtualKey,
                    Flags = flags,
                },
            },
        };

    private sealed class OleClipboardSnapshot : IClipboardSnapshot
    {
        private IDataObject? _dataObject;
        private readonly bool _shouldUninitialize;
        private bool _restored;
        private bool _disposed;

        public OleClipboardSnapshot(IDataObject? dataObject, bool shouldUninitialize)
        {
            _dataObject = dataObject;
            _shouldUninitialize = shouldUninitialize;
        }

        public void Restore()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_restored)
                return;

            if (_dataObject is null)
            {
                if (!OpenClipboard(IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open the clipboard for restoration.");

                try
                {
                    if (!EmptyClipboard())
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to restore an empty clipboard.");
                }
                finally
                {
                    CloseClipboard();
                }
            }
            else
            {
                var result = OleSetClipboard(_dataObject);
                if (result < 0)
                    Marshal.ThrowExceptionForHR(result);

                // Materialize the restored formats before releasing our short-lived
                // IDataObject reference so the clipboard survives this apartment or
                // Niratan exiting later.
                result = OleFlushClipboard();
                if (result < 0)
                    Marshal.ThrowExceptionForHR(result);
            }

            _restored = true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_dataObject is not null && Marshal.IsComObject(_dataObject))
                Marshal.ReleaseComObject(_dataObject);
            _dataObject = null;

            if (_shouldUninitialize)
                OleUninitialize();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUTUNION Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)]
        public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    [DllImport("ole32.dll")]
    private static extern int OleGetClipboard([MarshalAs(UnmanagedType.Interface)] out IDataObject? dataObject);

    [DllImport("ole32.dll")]
    private static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)] IDataObject dataObject);

    [DllImport("ole32.dll")]
    private static extern int OleFlushClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", EntryPoint = "GetClipboardSequenceNumber")]
    private static extern uint NativeGetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, INPUT[] inputs, int inputSize);
}
