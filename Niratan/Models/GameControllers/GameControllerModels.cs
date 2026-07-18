using System;
using System.Collections.Generic;
using System.Linq;
using Niratan.Models.Shortcuts;

namespace Niratan.Models.GameControllers;

public enum GameControllerAction
{
    PreviousPage,
    NextPage,
    PreviousSasayakiCue,
    PlayPauseSasayaki,
    NextSasayakiCue,
    ReplaySasayakiCue,
    JumpSasayakiCue,
    ToggleStatistics,
}

public enum GameControllerFamily
{
    Xbox,
    PlayStation,
    Nintendo,
    Generic,
}

public sealed record GameControllerBinding(string Input)
{
    public static readonly GameControllerBinding ButtonA = new("buttonA");
    public static readonly GameControllerBinding ButtonB = new("buttonB");
    public static readonly GameControllerBinding ButtonX = new("buttonX");
    public static readonly GameControllerBinding ButtonY = new("buttonY");
    public static readonly GameControllerBinding DpadLeft = new("dpadLeft");
    public static readonly GameControllerBinding DpadRight = new("dpadRight");
    public static readonly GameControllerBinding LeftShoulder = new("leftShoulder");
    public static readonly GameControllerBinding RightShoulder = new("rightShoulder");
}

public sealed record GameControllerActionDefinition(
    GameControllerAction Action,
    string TitleResourceKey,
    string FallbackTitle,
    string ShortcutActionId);

public static class GameControllerActions
{
    public static IReadOnlyList<GameControllerActionDefinition> All { get; } =
    [
        new(
            GameControllerAction.PreviousPage,
            "ShortcutActionReaderPreviousPage",
            "Previous Page",
            ReaderShortcutActions.PreviousPage.Id),
        new(
            GameControllerAction.NextPage,
            "ShortcutActionReaderNextPage",
            "Next Page",
            ReaderShortcutActions.NextPage.Id),
        new(
            GameControllerAction.PreviousSasayakiCue,
            "ShortcutActionSasayakiPreviousCue",
            "Previous Cue",
            SasayakiShortcutActions.PreviousCue.Id),
        new(
            GameControllerAction.PlayPauseSasayaki,
            "ShortcutActionSasayakiPlayPause",
            "Play/Pause",
            SasayakiShortcutActions.PlayPause.Id),
        new(
            GameControllerAction.NextSasayakiCue,
            "ShortcutActionSasayakiNextCue",
            "Next Cue",
            SasayakiShortcutActions.NextCue.Id),
        new(
            GameControllerAction.ReplaySasayakiCue,
            "ShortcutActionSasayakiReplayCue",
            "Replay Cue",
            SasayakiShortcutActions.ReplayCue.Id),
        new(
            GameControllerAction.JumpSasayakiCue,
            "ShortcutActionSasayakiJumpCue",
            "Jump Cue",
            SasayakiShortcutActions.JumpCue.Id),
        new(
            GameControllerAction.ToggleStatistics,
            "ShortcutActionReaderToggleStatistics",
            "Toggle Reading Timer",
            ReaderShortcutActions.ToggleStatistics.Id),
    ];

    public static string ShortcutActionId(GameControllerAction action) =>
        Definition(action).ShortcutActionId;

    public static GameControllerActionDefinition Definition(GameControllerAction action) =>
        All.FirstOrDefault(item => item.Action == action)
        ?? throw new ArgumentOutOfRangeException(nameof(action), action, null);
}

public sealed class GameControllerConfiguration
{
    public GameControllerBinding ReaderPreviousPageControllerBinding { get; set; } =
        GameControllerBinding.DpadLeft;
    public GameControllerBinding ReaderNextPageControllerBinding { get; set; } =
        GameControllerBinding.DpadRight;
    public GameControllerBinding SasayakiPreviousCueControllerBinding { get; set; } =
        GameControllerBinding.LeftShoulder;
    public GameControllerBinding SasayakiPlayPauseControllerBinding { get; set; } =
        GameControllerBinding.ButtonA;
    public GameControllerBinding SasayakiNextCueControllerBinding { get; set; } =
        GameControllerBinding.RightShoulder;
    public GameControllerBinding SasayakiReplayCueControllerBinding { get; set; } =
        GameControllerBinding.ButtonX;
    public GameControllerBinding SasayakiJumpCueControllerBinding { get; set; } =
        GameControllerBinding.ButtonB;
    public GameControllerBinding StatisticsToggleControllerBinding { get; set; } =
        GameControllerBinding.ButtonY;

    public GameControllerBinding BindingFor(GameControllerAction action) =>
        action switch
        {
            GameControllerAction.PreviousPage => ReaderPreviousPageControllerBinding
                ?? GameControllerBinding.DpadLeft,
            GameControllerAction.NextPage => ReaderNextPageControllerBinding
                ?? GameControllerBinding.DpadRight,
            GameControllerAction.PreviousSasayakiCue => SasayakiPreviousCueControllerBinding
                ?? GameControllerBinding.LeftShoulder,
            GameControllerAction.PlayPauseSasayaki => SasayakiPlayPauseControllerBinding
                ?? GameControllerBinding.ButtonA,
            GameControllerAction.NextSasayakiCue => SasayakiNextCueControllerBinding
                ?? GameControllerBinding.RightShoulder,
            GameControllerAction.ReplaySasayakiCue => SasayakiReplayCueControllerBinding
                ?? GameControllerBinding.ButtonX,
            GameControllerAction.JumpSasayakiCue => SasayakiJumpCueControllerBinding
                ?? GameControllerBinding.ButtonB,
            GameControllerAction.ToggleStatistics => StatisticsToggleControllerBinding
                ?? GameControllerBinding.ButtonY,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };

    public void SetBinding(GameControllerAction action, GameControllerBinding binding)
    {
        switch (action)
        {
            case GameControllerAction.PreviousPage:
                ReaderPreviousPageControllerBinding = binding;
                break;
            case GameControllerAction.NextPage:
                ReaderNextPageControllerBinding = binding;
                break;
            case GameControllerAction.PreviousSasayakiCue:
                SasayakiPreviousCueControllerBinding = binding;
                break;
            case GameControllerAction.PlayPauseSasayaki:
                SasayakiPlayPauseControllerBinding = binding;
                break;
            case GameControllerAction.NextSasayakiCue:
                SasayakiNextCueControllerBinding = binding;
                break;
            case GameControllerAction.ReplaySasayakiCue:
                SasayakiReplayCueControllerBinding = binding;
                break;
            case GameControllerAction.JumpSasayakiCue:
                SasayakiJumpCueControllerBinding = binding;
                break;
            case GameControllerAction.ToggleStatistics:
                StatisticsToggleControllerBinding = binding;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }

    public static GameControllerConfiguration Defaults() => new();

    public GameControllerConfiguration Clone() =>
        new()
        {
            ReaderPreviousPageControllerBinding = BindingFor(GameControllerAction.PreviousPage),
            ReaderNextPageControllerBinding = BindingFor(GameControllerAction.NextPage),
            SasayakiPreviousCueControllerBinding = BindingFor(GameControllerAction.PreviousSasayakiCue),
            SasayakiPlayPauseControllerBinding = BindingFor(GameControllerAction.PlayPauseSasayaki),
            SasayakiNextCueControllerBinding = BindingFor(GameControllerAction.NextSasayakiCue),
            SasayakiReplayCueControllerBinding = BindingFor(GameControllerAction.ReplaySasayakiCue),
            SasayakiJumpCueControllerBinding = BindingFor(GameControllerAction.JumpSasayakiCue),
            StatisticsToggleControllerBinding = BindingFor(GameControllerAction.ToggleStatistics),
        };
}

public sealed class GameControllerActionEventArgs(GameControllerAction action) : EventArgs
{
    public GameControllerAction Action { get; } = action;
}
