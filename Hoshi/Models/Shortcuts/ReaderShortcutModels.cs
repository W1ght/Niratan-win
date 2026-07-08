using System;
using System.Collections.Generic;
using System.Linq;

namespace Hoshi.Models.Shortcuts;

[Flags]
public enum ReaderKeyboardShortcutModifiers
{
    None = 0,
    Control = 1 << 0,
    Shift = 1 << 1,
    Alt = 1 << 2,
    Windows = 1 << 3,
}

public readonly record struct ReaderKeyboardShortcut(
    string Key,
    ReaderKeyboardShortcutModifiers Modifiers = ReaderKeyboardShortcutModifiers.None)
{
    public string Label
    {
        get
        {
            var modifiers = Modifiers;
            var prefix = ModifierLabels()
                .Where(item => modifiers.HasFlag(item.Modifier))
                .Select(item => item.Label);
            var joinedPrefix = string.Join("+", prefix);
            return string.IsNullOrEmpty(joinedPrefix)
                ? KeyLabel
                : $"{joinedPrefix}+{KeyLabel}";
        }
    }

    private string KeyLabel =>
        Key switch
        {
            "LeftArrow" => "\u2190",
            "RightArrow" => "\u2192",
            "UpArrow" => "\u2191",
            "DownArrow" => "\u2193",
            "PageUp" => "Page Up",
            "PageDown" => "Page Down",
            "Space" => "Space",
            "Escape" => "Esc",
            _ when Key.Length == 1 => Key.ToUpperInvariant(),
            _ => Key,
        };

    private static IReadOnlyList<(ReaderKeyboardShortcutModifiers Modifier, string Label)> ModifierLabels() =>
    [
        (ReaderKeyboardShortcutModifiers.Control, "Ctrl"),
        (ReaderKeyboardShortcutModifiers.Shift, "Shift"),
        (ReaderKeyboardShortcutModifiers.Alt, "Alt"),
        (ReaderKeyboardShortcutModifiers.Windows, "Win"),
    ];
}

public sealed record ReaderShortcutAction(
    string Id,
    string Title,
    ReaderKeyboardShortcut DefaultShortcut);

public static class ReaderShortcutActions
{
    public static readonly ReaderShortcutAction PreviousPage = new(
        "reader.previousPage",
        "Previous Page",
        new ReaderKeyboardShortcut("LeftArrow"));

    public static readonly ReaderShortcutAction NextPage = new(
        "reader.nextPage",
        "Next Page",
        new ReaderKeyboardShortcut("RightArrow"));

    public static readonly ReaderShortcutAction Close = new(
        "reader.close",
        "Close Reader",
        new ReaderKeyboardShortcut("Escape"));

    public static readonly ReaderShortcutAction ToggleFocusMode = new(
        "reader.toggleFocusMode",
        "Toggle Focus Mode",
        new ReaderKeyboardShortcut("f"));

    public static readonly ReaderShortcutAction ToggleStatistics = new(
        "reader.toggleStatistics",
        "Toggle Reading Timer",
        new ReaderKeyboardShortcut("t"));

    public static readonly ReaderShortcutAction ToggleLyricsMode = new(
        "reader.toggleLyricsMode",
        "Lyrics Mode",
        new ReaderKeyboardShortcut("l"));

    public static IReadOnlyList<ReaderShortcutAction> All =>
    [
        PreviousPage,
        NextPage,
        Close,
        ToggleFocusMode,
        ToggleStatistics,
        ToggleLyricsMode,
    ];
}

public static class SasayakiShortcutActions
{
    public static readonly ReaderShortcutAction PreviousCue = new(
        "sasayaki.previousCue",
        "Previous Cue",
        new ReaderKeyboardShortcut("["));

    public static readonly ReaderShortcutAction PlayPause = new(
        "sasayaki.playPause",
        "Play/Pause",
        new ReaderKeyboardShortcut("p"));

    public static readonly ReaderShortcutAction NextCue = new(
        "sasayaki.nextCue",
        "Next Cue",
        new ReaderKeyboardShortcut("]"));

    public static readonly ReaderShortcutAction ReplayCue = new(
        "sasayaki.replayCue",
        "Replay Cue",
        new ReaderKeyboardShortcut("r"));

    public static readonly ReaderShortcutAction JumpCue = new(
        "sasayaki.jumpCue",
        "Jump Cue",
        new ReaderKeyboardShortcut("j"));

    public static IReadOnlyList<ReaderShortcutAction> All =>
    [
        PreviousCue,
        PlayPause,
        NextCue,
        ReplayCue,
        JumpCue,
    ];
}
