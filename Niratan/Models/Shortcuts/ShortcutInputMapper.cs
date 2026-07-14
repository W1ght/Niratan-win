using System.Runtime.InteropServices;
using Windows.System;

namespace Niratan.Models.Shortcuts;

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

    public static bool TryGetVirtualKey(
        KeyboardShortcutBinding binding,
        out VirtualKey key,
        out VirtualKeyModifiers modifiers)
    {
        key = VirtualKey.None;
        modifiers = ToVirtualKeyModifiers(binding.Modifiers);

        if (binding.IsEmpty)
            return false;

        key = binding.Key switch
        {
            "LeftArrow" => VirtualKey.Left,
            "RightArrow" => VirtualKey.Right,
            "UpArrow" => VirtualKey.Up,
            "DownArrow" => VirtualKey.Down,
            "PageUp" => VirtualKey.PageUp,
            "PageDown" => VirtualKey.PageDown,
            "Space" => VirtualKey.Space,
            "Escape" => VirtualKey.Escape,
            "Add" => VirtualKey.Add,
            "Subtract" => VirtualKey.Subtract,
            "[" => (VirtualKey)219,
            "]" => (VirtualKey)221,
            _ => VirtualKey.None,
        };

        if (key != VirtualKey.None)
            return true;

        if (binding.Key.Length == 1)
        {
            var character = binding.Key[0];
            if (character is >= 'a' and <= 'z')
            {
                key = (VirtualKey)((int)VirtualKey.A + character - 'a');
                return true;
            }

            if (character is >= '0' and <= '9')
            {
                key = (VirtualKey)((int)VirtualKey.Number0 + character - '0');
                return true;
            }

            return false;
        }

        if (binding.Key.Length is >= 2 and <= 3
            && binding.Key[0] == 'F'
            && int.TryParse(binding.Key[1..], out var functionKey)
            && functionKey is >= 1 and <= 24)
        {
            key = (VirtualKey)((int)VirtualKey.F1 + functionKey - 1);
            return true;
        }

        return false;
    }

    private static VirtualKeyModifiers ToVirtualKeyModifiers(KeyboardShortcutModifiers modifiers)
    {
        var result = VirtualKeyModifiers.None;
        if (modifiers.HasFlag(KeyboardShortcutModifiers.Control))
            result |= VirtualKeyModifiers.Control;
        if (modifiers.HasFlag(KeyboardShortcutModifiers.Shift))
            result |= VirtualKeyModifiers.Shift;
        if (modifiers.HasFlag(KeyboardShortcutModifiers.Alt))
            result |= VirtualKeyModifiers.Menu;
        if (modifiers.HasFlag(KeyboardShortcutModifiers.Windows))
            result |= VirtualKeyModifiers.Windows;

        return result;
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (GetKeyState((int)key) & 0x8000) != 0;

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int virtualKey);
}
