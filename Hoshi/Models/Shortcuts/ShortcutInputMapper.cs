using System.Runtime.InteropServices;
using Windows.System;

namespace Hoshi.Models.Shortcuts;

public static partial class ShortcutInputMapper
{
    public static bool IsModifierKey(VirtualKey key) =>
        key is VirtualKey.Control
            or VirtualKey.LeftControl
            or VirtualKey.RightControl
            or VirtualKey.Shift
            or VirtualKey.LeftShift
            or VirtualKey.RightShift
            or VirtualKey.Menu
            or VirtualKey.LeftMenu
            or VirtualKey.RightMenu
            or VirtualKey.LeftWindows
            or VirtualKey.RightWindows;

    public static KeyboardShortcutModifiers GetCurrentModifiers()
    {
        var modifiers = KeyboardShortcutModifiers.None;

        if (IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.LeftControl) || IsKeyDown(VirtualKey.RightControl))
            modifiers |= KeyboardShortcutModifiers.Control;

        if (IsKeyDown(VirtualKey.Shift) || IsKeyDown(VirtualKey.LeftShift) || IsKeyDown(VirtualKey.RightShift))
            modifiers |= KeyboardShortcutModifiers.Shift;

        if (IsKeyDown(VirtualKey.Menu) || IsKeyDown(VirtualKey.LeftMenu) || IsKeyDown(VirtualKey.RightMenu))
            modifiers |= KeyboardShortcutModifiers.Alt;

        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows))
            modifiers |= KeyboardShortcutModifiers.Windows;

        return modifiers;
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (GetKeyState((int)key) & 0x8000) != 0;

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int virtualKey);
}
