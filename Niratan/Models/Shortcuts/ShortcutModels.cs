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
            _ when (int)key == 188 => ",",
            _ when (int)key == 190 => ".",
            _ when (int)key == 191 => "/",
            _ when (int)key == 219 => "[",
            _ when (int)key == 220 => "\\",
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

public static class GlobalShortcutActions
{
    public const string OpenId = "global.open";
    public const string LookupSelectedTextId = "global.lookupSelectedText";

    public static readonly ShortcutAction Open = new(
        OpenId,
        "Open",
        ShortcutResourceKey.ForActionId(OpenId),
        ShortcutCategory.Global,
        [ShortcutScope.Global],
        new KeyboardShortcutBinding("o", KeyboardShortcutModifiers.Control));

    public static readonly ShortcutAction LookupSelectedText = new(
        LookupSelectedTextId,
        "Lookup Selected Text",
        ShortcutResourceKey.ForActionId(LookupSelectedTextId),
        ShortcutCategory.Global,
        [ShortcutScope.Global],
        new KeyboardShortcutBinding(
            "d",
            KeyboardShortcutModifiers.Control | KeyboardShortcutModifiers.Alt));

    public static IReadOnlyList<ShortcutAction> All => [Open, LookupSelectedText];
}

public static class DictionaryShortcutActions
{
    public const string PreviousEntryId = "dictionary.previousEntry";
    public const string NextEntryId = "dictionary.nextEntry";

    public static readonly ShortcutAction PreviousEntry = Create(
        PreviousEntryId,
        "Previous Entry",
        "PageUp",
        KeyboardShortcutModifiers.Alt);

    public static readonly ShortcutAction NextEntry = Create(
        NextEntryId,
        "Next Entry",
        "PageDown",
        KeyboardShortcutModifiers.Alt);

    public static IReadOnlyList<ShortcutAction> All => [PreviousEntry, NextEntry];

    private static ShortcutAction Create(
        string id,
        string title,
        string key,
        KeyboardShortcutModifiers modifiers) =>
        new(
            id,
            title,
            ShortcutResourceKey.ForActionId(id),
            ShortcutCategory.DictionaryPopup,
            [ShortcutScope.Dictionary],
            new KeyboardShortcutBinding(key, modifiers));
}

public static class PopupShortcutActions
{
    public const string DismissId = "popup.dismiss";

    public static readonly ShortcutAction Dismiss = new(
        DismissId,
        "Close Popup",
        ShortcutResourceKey.ForActionId(DismissId),
        ShortcutCategory.DictionaryPopup,
        [ShortcutScope.Popup],
        new KeyboardShortcutBinding("Escape"));

    public static IReadOnlyList<ShortcutAction> All => [Dismiss];
}

public static class VideoShortcutActions
{
    public const string PlayPauseId = "video.playPause";
    public const string SeekBackwardId = "video.seekBackward";
    public const string SeekForwardId = "video.seekForward";
    public const string PreviousEpisodeId = "video.previousEpisode";
    public const string NextEpisodeId = "video.nextEpisode";
    public const string DecreaseSpeedId = "video.decreaseSpeed";
    public const string IncreaseSpeedId = "video.increaseSpeed";
    public const string ResetSpeedId = "video.resetSpeed";
    public const string ToggleMuteId = "video.toggleMute";
    public const string VolumeDownId = "video.volumeDown";
    public const string VolumeUpId = "video.volumeUp";
    public const string PreviousSubtitleCueId = "video.previousSubtitleCue";
    public const string MineCurrentSubtitleId = "video.mineCurrentSubtitle";
    public const string NextSubtitleCueId = "video.nextSubtitleCue";
    public const string ToggleSubtitlesVisibleId = "video.toggleSubtitlesVisible";
    public const string ToggleSubtitleGapFastForwardId = "video.toggleSubtitleGapFastForward";
    public const string CycleSubtitleTrackId = "video.cycleSubtitleTrack";
    public const string SubtitleEarlierId = "video.subtitleEarlier";
    public const string SubtitleLaterId = "video.subtitleLater";
    public const string ResetSubtitleTimingId = "video.resetSubtitleTiming";
    public const string AlignPreviousSubtitleToCurrentTimeId = "video.alignPreviousSubtitleToCurrentTime";
    public const string AlignNextSubtitleToCurrentTimeId = "video.alignNextSubtitleToCurrentTime";
    public const string AudioEarlierId = "video.audioEarlier";
    public const string AudioLaterId = "video.audioLater";
    public const string ToggleFileLoopId = "video.toggleFileLoop";
    public const string SetABLoopStartId = "video.setABLoopStart";
    public const string SetABLoopEndId = "video.setABLoopEnd";
    public const string ToggleTranscriptId = "video.toggleTranscript";
    public const string RotateClockwiseId = "video.rotateClockwise";
    public const string ToggleFullScreenId = "video.toggleFullScreen";
    public const string ExitFocusModeId = "video.exitFocusMode";
    public const string LookupSubtitleId = "video.lookupSubtitle";
    public const string ToggleHardwareDecodingId = "video.toggleHardwareDecoding";
    public const string IncreaseSubtitleSizeId = "video.increaseSubtitleSize";
    public const string DecreaseSubtitleSizeId = "video.decreaseSubtitleSize";

    // Source-compatible aliases for the pre-alignment Windows names.
    public const string PreviousSubtitleId = PreviousSubtitleCueId;
    public const string NextSubtitleId = NextSubtitleCueId;
    public const string ToggleSubtitlesId = ToggleSubtitlesVisibleId;
    public const string ToggleFullscreenId = ToggleFullScreenId;
    public const string CloseOverlayOrFullscreenId = ExitFocusModeId;

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

    public static readonly ShortcutAction PreviousEpisode = Create(
        PreviousEpisodeId,
        "Previous Episode",
        "LeftArrow",
        KeyboardShortcutModifiers.Control | KeyboardShortcutModifiers.Shift);

    public static readonly ShortcutAction NextEpisode = Create(
        NextEpisodeId,
        "Next Episode",
        "RightArrow",
        KeyboardShortcutModifiers.Control | KeyboardShortcutModifiers.Shift);

    public static readonly ShortcutAction DecreaseSpeed = Create(
        DecreaseSpeedId,
        "Decrease Playback Speed",
        "[");

    public static readonly ShortcutAction IncreaseSpeed = Create(
        IncreaseSpeedId,
        "Increase Playback Speed",
        "]");

    public static readonly ShortcutAction ResetSpeed = Create(
        ResetSpeedId,
        "Reset Playback Speed",
        "\\");

    public static readonly ShortcutAction ToggleMute = Create(
        ToggleMuteId,
        "Mute / Unmute",
        "m");

    public static readonly ShortcutAction VolumeUp = Create(
        VolumeUpId,
        "Volume Up",
        "UpArrow");

    public static readonly ShortcutAction VolumeDown = Create(
        VolumeDownId,
        "Volume Down",
        "DownArrow");

    public static readonly ShortcutAction PreviousSubtitleCue = Create(
        PreviousSubtitleCueId,
        "Previous Subtitle",
        "LeftArrow",
        KeyboardShortcutModifiers.Alt);

    public static readonly ShortcutAction MineCurrentSubtitle = Create(
        MineCurrentSubtitleId,
        "Mine Current Subtitle",
        "z",
        KeyboardShortcutModifiers.Control | KeyboardShortcutModifiers.Shift);

    public static readonly ShortcutAction NextSubtitleCue = Create(
        NextSubtitleCueId,
        "Next Subtitle",
        "RightArrow",
        KeyboardShortcutModifiers.Alt);

    public static readonly ShortcutAction ToggleSubtitlesVisible = Create(
        ToggleSubtitlesVisibleId,
        "Show / Hide Subtitles",
        "v");

    public static readonly ShortcutAction ToggleSubtitleGapFastForward = Create(
        ToggleSubtitleGapFastForwardId,
        "Fast-forward Subtitle Gaps",
        "f",
        KeyboardShortcutModifiers.Shift);

    public static readonly ShortcutAction CycleSubtitleTrack = Create(
        CycleSubtitleTrackId,
        "Cycle Subtitle Track",
        "s");

    public static readonly ShortcutAction SubtitleEarlier = Create(
        SubtitleEarlierId,
        "Subtitle Earlier",
        ",",
        KeyboardShortcutModifiers.Alt);

    public static readonly ShortcutAction SubtitleLater = Create(
        SubtitleLaterId,
        "Subtitle Later",
        ".",
        KeyboardShortcutModifiers.Alt);

    public static readonly ShortcutAction ResetSubtitleTiming = Create(
        ResetSubtitleTimingId,
        "Reset Subtitle Timing",
        "/",
        KeyboardShortcutModifiers.Alt);

    public static readonly ShortcutAction AlignPreviousSubtitleToCurrentTime = Create(
        AlignPreviousSubtitleToCurrentTimeId,
        "Align Previous Subtitle to Current Time",
        "LeftArrow",
        KeyboardShortcutModifiers.Shift);

    public static readonly ShortcutAction AlignNextSubtitleToCurrentTime = Create(
        AlignNextSubtitleToCurrentTimeId,
        "Align Next Subtitle to Current Time",
        "RightArrow",
        KeyboardShortcutModifiers.Shift);

    public static readonly ShortcutAction AudioEarlier = Create(
        AudioEarlierId,
        "Audio Earlier",
        ",",
        KeyboardShortcutModifiers.Alt | KeyboardShortcutModifiers.Shift);

    public static readonly ShortcutAction AudioLater = Create(
        AudioLaterId,
        "Audio Later",
        ".",
        KeyboardShortcutModifiers.Alt | KeyboardShortcutModifiers.Shift);

    public static readonly ShortcutAction ToggleFileLoop = Create(
        ToggleFileLoopId,
        "Toggle File Loop",
        "l");

    public static readonly ShortcutAction SetABLoopStart = Create(
        SetABLoopStartId,
        "Set A-B Loop Start",
        "a",
        KeyboardShortcutModifiers.Alt);

    public static readonly ShortcutAction SetABLoopEnd = Create(
        SetABLoopEndId,
        "Set A-B Loop End",
        "b",
        KeyboardShortcutModifiers.Alt);

    public static readonly ShortcutAction ToggleTranscript = Create(
        ToggleTranscriptId,
        "Toggle Transcript",
        "t");

    public static readonly ShortcutAction RotateClockwise = Create(
        RotateClockwiseId,
        "Rotate Clockwise",
        "r");

    public static readonly ShortcutAction ToggleFullScreen = Create(
        ToggleFullScreenId,
        "Toggle Full Screen",
        "f");

    public static readonly ShortcutAction ExitFocusMode = Create(
        ExitFocusModeId,
        "Exit Full Screen or Focus Mode",
        "Escape");

    public static readonly ShortcutAction PreviousSubtitle = PreviousSubtitleCue;
    public static readonly ShortcutAction NextSubtitle = NextSubtitleCue;
    public static readonly ShortcutAction ToggleSubtitles = ToggleSubtitlesVisible;
    public static readonly ShortcutAction ToggleFullscreen = ToggleFullScreen;
    public static readonly ShortcutAction CloseOverlayOrFullscreen = ExitFocusMode;

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

    public static IReadOnlyList<ShortcutAction> All =>
    [
        PlayPause,
        SeekBackward,
        SeekForward,
        PreviousEpisode,
        NextEpisode,
        DecreaseSpeed,
        IncreaseSpeed,
        ResetSpeed,
        ToggleMute,
        VolumeDown,
        VolumeUp,
        MineCurrentSubtitle,
        PreviousSubtitleCue,
        NextSubtitleCue,
        ToggleSubtitlesVisible,
        ToggleSubtitleGapFastForward,
        CycleSubtitleTrack,
        SubtitleEarlier,
        SubtitleLater,
        ResetSubtitleTiming,
        AlignPreviousSubtitleToCurrentTime,
        AlignNextSubtitleToCurrentTime,
        AudioEarlier,
        AudioLater,
        ToggleFileLoop,
        SetABLoopStart,
        SetABLoopEnd,
        ToggleTranscript,
        RotateClockwise,
        ToggleFullScreen,
        ExitFocusMode,
        LookupSubtitle,
        ToggleHardwareDecoding,
        IncreaseSubtitleSize,
        DecreaseSubtitleSize,
    ];

    private static ShortcutAction Create(
        string id,
        string title,
        string defaultKey,
        KeyboardShortcutModifiers modifiers = KeyboardShortcutModifiers.None) =>
        new(
            id,
            title,
            ShortcutResourceKey.ForActionId(id),
            ShortcutCategory.Video,
            [ShortcutScope.Video],
            new KeyboardShortcutBinding(defaultKey, modifiers));
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
        .. GlobalShortcutActions.All,
        .. ReaderShortcutActions.All.Select(action => FromReaderAction(
            action,
            ShortcutCategory.Reader,
            ShortcutScope.Reader)),
        .. SasayakiShortcutActions.All.Select(action => FromReaderAction(
            action,
            ShortcutCategory.Sasayaki,
            ShortcutScope.Sasayaki)),
        .. DictionaryShortcutActions.All,
        .. PopupShortcutActions.All,
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

    public int DictionaryEntryJumpCount { get; set; } = 1;

    public KeyboardShortcutBinding GetBinding(ShortcutAction action)
    {
        if (Bindings.TryGetValue(action.Id, out var binding) && !binding.IsEmpty)
            return binding;

        var legacyId = LegacyActionId(action.Id);
        return legacyId is not null
            && Bindings.TryGetValue(legacyId, out binding)
            && !binding.IsEmpty
                ? binding
                : action.DefaultBinding;
    }

    public bool HasCustomBinding(string actionId)
    {
        if (Bindings.ContainsKey(actionId))
            return true;

        var legacyId = LegacyActionId(actionId);
        return legacyId is not null && Bindings.ContainsKey(legacyId);
    }

    public void SetBinding(string actionId, KeyboardShortcutBinding binding)
    {
        if (binding.IsEmpty)
        {
            Bindings.Remove(actionId);
            return;
        }

        Bindings[actionId] = binding;
    }

    public void ResetBinding(string actionId)
    {
        Bindings.Remove(actionId);
        var legacyId = LegacyActionId(actionId);
        if (legacyId is not null)
            Bindings.Remove(legacyId);
    }

    public ShortcutConfiguration Clone() =>
        new()
        {
            Bindings = Bindings.ToDictionary(item => item.Key, item => item.Value),
            DictionaryEntryJumpCount = DictionaryEntryJumpCount,
        };

    private static string? LegacyActionId(string actionId) =>
        actionId switch
        {
            VideoShortcutActions.PreviousSubtitleCueId => "video.previousSubtitle",
            VideoShortcutActions.NextSubtitleCueId => "video.nextSubtitle",
            VideoShortcutActions.ToggleSubtitlesVisibleId => "video.toggleSubtitles",
            VideoShortcutActions.ToggleFullScreenId => "video.toggleFullscreen",
            VideoShortcutActions.ExitFocusModeId => "video.closeOverlayOrFullscreen",
            _ => null,
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
