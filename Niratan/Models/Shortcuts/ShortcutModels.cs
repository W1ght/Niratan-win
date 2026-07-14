using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Windows.System;

namespace Niratan.Models.Shortcuts;

[Flags]
public enum KeyboardShortcutModifiers
{
    None = 0,
    Control = 1 << 0,
    Shift = 1 << 1,
    Alt = 1 << 2,
    Windows = 1 << 3,
}

public enum ShortcutScope
{
    Global,
    Reader,
    Dictionary,
    Popup,
    Sasayaki,
    Video,
}

public enum ShortcutCategory
{
    Global,
    Reader,
    DictionaryPopup,
    Sasayaki,
    Video,
}

public enum ShortcutConflictKind
{
    None,
    Shadowed,
    Conflict,
}

public readonly record struct KeyboardShortcutBinding
{
    [JsonConstructor]
    public KeyboardShortcutBinding(string key, KeyboardShortcutModifiers modifiers = KeyboardShortcutModifiers.None)
    {
        Key = NormalizeKey(key);
        Modifiers = modifiers;
    }

    public string Key { get; init; } = "";
    public KeyboardShortcutModifiers Modifiers { get; init; } = KeyboardShortcutModifiers.None;
    public bool IsEmpty => string.IsNullOrWhiteSpace(Key);

    public string Label
    {
        get
        {
            if (IsEmpty)
                return "";

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

    public bool Matches(KeyboardShortcutBinding other) =>
        string.Equals(NormalizeKey(Key), NormalizeKey(other.Key), StringComparison.Ordinal)
        && Modifiers == other.Modifiers;

    public static KeyboardShortcutBinding FromReaderShortcut(ReaderKeyboardShortcut shortcut) =>
        new(
            shortcut.Key,
            (KeyboardShortcutModifiers)(int)shortcut.Modifiers);

    public static KeyboardShortcutBinding FromVirtualKey(
        VirtualKey key,
        KeyboardShortcutModifiers modifiers = KeyboardShortcutModifiers.None)
    {
        if (ShortcutInputMapper.IsModifierKey(key))
            return new KeyboardShortcutBinding("");

        return new KeyboardShortcutBinding(KeyFromVirtualKey(key), modifiers);
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
            "Add" => "+",
            "Subtract" => "-",
            _ when Key.Length == 1 => Key.ToUpperInvariant(),
            _ => Key,
        };

    private static string NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "";

        return key.Trim() switch
        {
            "Left" => "LeftArrow",
            "Right" => "RightArrow",
            "Up" => "UpArrow",
            "Down" => "DownArrow",
            "Esc" => "Escape",
            var value when value.Length == 1 => value.ToLowerInvariant(),
            var value => value,
        };
    }

    private static string KeyFromVirtualKey(VirtualKey key) =>
        key switch
        {
            VirtualKey.Left => "LeftArrow",
            VirtualKey.Right => "RightArrow",
            VirtualKey.Up => "UpArrow",
            VirtualKey.Down => "DownArrow",
            VirtualKey.PageUp => "PageUp",
            VirtualKey.PageDown => "PageDown",
            VirtualKey.Space => "Space",
            VirtualKey.Escape => "Escape",
            VirtualKey.Add => "Add",
            VirtualKey.Subtract => "Subtract",
            _ when (int)key == 219 => "[",
            _ when (int)key == 221 => "]",
            VirtualKey.Number0 => "0",
            VirtualKey.Number1 => "1",
            VirtualKey.Number2 => "2",
            VirtualKey.Number3 => "3",
            VirtualKey.Number4 => "4",
            VirtualKey.Number5 => "5",
            VirtualKey.Number6 => "6",
            VirtualKey.Number7 => "7",
            VirtualKey.Number8 => "8",
            VirtualKey.Number9 => "9",
            _ when key >= VirtualKey.A && key <= VirtualKey.Z => key.ToString().ToLowerInvariant(),
            _ => key.ToString(),
        };

    private static IReadOnlyList<(KeyboardShortcutModifiers Modifier, string Label)> ModifierLabels() =>
    [
        (KeyboardShortcutModifiers.Control, "Ctrl"),
        (KeyboardShortcutModifiers.Shift, "Shift"),
        (KeyboardShortcutModifiers.Alt, "Alt"),
        (KeyboardShortcutModifiers.Windows, "Win"),
    ];
}

public sealed record ShortcutAction(
    string Id,
    string Title,
    string TitleResourceKey,
    ShortcutCategory Category,
    IReadOnlyList<ShortcutScope> Scopes,
    KeyboardShortcutBinding DefaultBinding);

public sealed record ShortcutConflict(
    ShortcutConflictKind Kind,
    ShortcutAction Action,
    KeyboardShortcutBinding Binding);

public static class VideoShortcutActions
{
    public const string PlayPauseId = "video.playPause";
    public const string SeekBackwardId = "video.seekBackward";
    public const string SeekForwardId = "video.seekForward";
    public const string VolumeUpId = "video.volumeUp";
    public const string VolumeDownId = "video.volumeDown";
    public const string PreviousSubtitleId = "video.previousSubtitle";
    public const string NextSubtitleId = "video.nextSubtitle";
    public const string ToggleSubtitlesId = "video.toggleSubtitles";
    public const string CycleSubtitleTrackId = "video.cycleSubtitleTrack";
    public const string ToggleFullscreenId = "video.toggleFullscreen";
    public const string LookupSubtitleId = "video.lookupSubtitle";
    public const string ToggleHardwareDecodingId = "video.toggleHardwareDecoding";
    public const string IncreaseSubtitleSizeId = "video.increaseSubtitleSize";
    public const string DecreaseSubtitleSizeId = "video.decreaseSubtitleSize";
    public const string CloseOverlayOrFullscreenId = "video.closeOverlayOrFullscreen";

    public static readonly ShortcutAction PlayPause = Create(
        PlayPauseId,
        "Play/Pause",
        "Space");

    public static readonly ShortcutAction SeekBackward = Create(
        SeekBackwardId,
        "Seek Backward",
        "LeftArrow");

    public static readonly ShortcutAction SeekForward = Create(
        SeekForwardId,
        "Seek Forward",
        "RightArrow");

    public static readonly ShortcutAction VolumeUp = Create(
        VolumeUpId,
        "Volume Up",
        "UpArrow");

    public static readonly ShortcutAction VolumeDown = Create(
        VolumeDownId,
        "Volume Down",
        "DownArrow");

    public static readonly ShortcutAction PreviousSubtitle = Create(
        PreviousSubtitleId,
        "Previous Subtitle",
        "PageUp");

    public static readonly ShortcutAction NextSubtitle = Create(
        NextSubtitleId,
        "Next Subtitle",
        "PageDown");

    public static readonly ShortcutAction ToggleSubtitles = Create(
        ToggleSubtitlesId,
        "Toggle Subtitles",
        "v");

    public static readonly ShortcutAction CycleSubtitleTrack = Create(
        CycleSubtitleTrackId,
        "Cycle Subtitle Track",
        "s");

    public static readonly ShortcutAction ToggleFullscreen = Create(
        ToggleFullscreenId,
        "Toggle Full Screen",
        "f");

    public static readonly ShortcutAction LookupSubtitle = Create(
        LookupSubtitleId,
        "Lookup Current Subtitle",
        "F2");

    public static readonly ShortcutAction ToggleHardwareDecoding = Create(
        ToggleHardwareDecodingId,
        "Toggle Hardware Decoding",
        "F8");

    public static readonly ShortcutAction IncreaseSubtitleSize = Create(
        IncreaseSubtitleSizeId,
        "Increase Subtitle Size",
        "Add");

    public static readonly ShortcutAction DecreaseSubtitleSize = Create(
        DecreaseSubtitleSizeId,
        "Decrease Subtitle Size",
        "Subtract");

    public static readonly ShortcutAction CloseOverlayOrFullscreen = Create(
        CloseOverlayOrFullscreenId,
        "Close Overlay or Full Screen",
        "Escape");

    public static IReadOnlyList<ShortcutAction> All =>
    [
        PlayPause,
        SeekBackward,
        SeekForward,
        VolumeUp,
        VolumeDown,
        PreviousSubtitle,
        NextSubtitle,
        ToggleSubtitles,
        CycleSubtitleTrack,
        ToggleFullscreen,
        LookupSubtitle,
        ToggleHardwareDecoding,
        IncreaseSubtitleSize,
        DecreaseSubtitleSize,
        CloseOverlayOrFullscreen,
    ];

    private static ShortcutAction Create(string id, string title, string defaultKey) =>
        new(
            id,
            title,
            ShortcutResourceKey.ForActionId(id),
            ShortcutCategory.Video,
            [ShortcutScope.Video],
            new KeyboardShortcutBinding(defaultKey));
}

public sealed class ShortcutRegistry
{
    public static readonly ShortcutRegistry Application = new(BuildApplicationActions());

    private readonly IReadOnlyDictionary<string, ShortcutAction> _actionsById;

    public ShortcutRegistry(IEnumerable<ShortcutAction> actions)
    {
        var materialized = actions.ToList();
        var duplicateId = materialized
            .GroupBy(action => action.Id)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;

        if (duplicateId != null)
            throw new ArgumentException($"Duplicate shortcut action id: {duplicateId}", nameof(actions));

        Actions = materialized;
        _actionsById = materialized.ToDictionary(action => action.Id);
    }

    public IReadOnlyList<ShortcutAction> Actions { get; }

    public ShortcutAction? Action(string id) =>
        _actionsById.TryGetValue(id, out var action) ? action : null;

    public IReadOnlyList<ShortcutAction> ActionsIn(ShortcutCategory category) =>
        Actions.Where(action => action.Category == category).ToList();

    private static IReadOnlyList<ShortcutAction> BuildApplicationActions() =>
    [
        .. ReaderShortcutActions.All.Select(action => FromReaderAction(
            action,
            ShortcutCategory.Reader,
            ShortcutScope.Reader)),
        .. SasayakiShortcutActions.All.Select(action => FromReaderAction(
            action,
            ShortcutCategory.Sasayaki,
            ShortcutScope.Sasayaki)),
        .. VideoShortcutActions.All,
    ];

    private static ShortcutAction FromReaderAction(
        ReaderShortcutAction action,
        ShortcutCategory category,
        ShortcutScope scope) =>
        new(
            action.Id,
            action.Title,
            ShortcutResourceKey.ForActionId(action.Id),
            category,
            [scope],
            KeyboardShortcutBinding.FromReaderShortcut(action.DefaultShortcut));
}

public static class ShortcutConflictChecker
{
    public static ShortcutConflictKind Relationship(
        ShortcutAction first,
        KeyboardShortcutBinding firstBinding,
        ShortcutAction second,
        KeyboardShortcutBinding secondBinding)
    {
        if (first.Id == second.Id || firstBinding.IsEmpty || secondBinding.IsEmpty)
            return ShortcutConflictKind.None;

        if (!firstBinding.Matches(secondBinding))
            return ShortcutConflictKind.None;

        if (ScopesOverlap(first.Scopes, second.Scopes))
            return ShortcutConflictKind.Conflict;

        if (PopupShadows(first.Scopes, second.Scopes) || PopupShadows(second.Scopes, first.Scopes))
            return ShortcutConflictKind.Shadowed;

        return ShortcutConflictKind.None;
    }

    private static bool ScopesOverlap(IReadOnlyList<ShortcutScope> first, IReadOnlyList<ShortcutScope> second)
    {
        if (first.Contains(ShortcutScope.Global) || second.Contains(ShortcutScope.Global))
            return true;

        if (first.Intersect(second).Any())
            return true;

        return Intersects(first, ShortcutScope.Reader, second, ShortcutScope.Sasayaki)
            || Intersects(first, ShortcutScope.Sasayaki, second, ShortcutScope.Reader);
    }

    private static bool PopupShadows(IReadOnlyList<ShortcutScope> popup, IReadOnlyList<ShortcutScope> underlying) =>
        popup.Contains(ShortcutScope.Popup)
        && underlying.Any(scope => scope is ShortcutScope.Reader or ShortcutScope.Video or ShortcutScope.Sasayaki);

    private static bool Intersects(
        IReadOnlyList<ShortcutScope> first,
        ShortcutScope firstScope,
        IReadOnlyList<ShortcutScope> second,
        ShortcutScope secondScope) =>
        first.Contains(firstScope) && second.Contains(secondScope);
}

public sealed class ShortcutConfiguration
{
    public Dictionary<string, KeyboardShortcutBinding> Bindings { get; set; } = [];

    public KeyboardShortcutBinding GetBinding(ShortcutAction action) =>
        Bindings.TryGetValue(action.Id, out var binding) && !binding.IsEmpty
            ? binding
            : action.DefaultBinding;

    public void SetBinding(string actionId, KeyboardShortcutBinding binding)
    {
        if (binding.IsEmpty)
        {
            Bindings.Remove(actionId);
            return;
        }

        Bindings[actionId] = binding;
    }

    public void ResetBinding(string actionId) => Bindings.Remove(actionId);

    public ShortcutConfiguration Clone() =>
        new()
        {
            Bindings = Bindings.ToDictionary(item => item.Key, item => item.Value),
        };
}

public static class ShortcutResourceKey
{
    public static string ForActionId(string actionId)
    {
        var parts = actionId.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return "ShortcutAction" + string.Concat(parts.Select(ToPascal));
    }

    private static string ToPascal(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? ""
            : char.ToUpperInvariant(value[0]) + value[1..];
}
