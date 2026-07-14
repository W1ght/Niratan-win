using FluentAssertions;
using Niratan.Models.Shortcuts;
using Windows.System;

namespace Niratan.Tests.Models.Shortcuts;

public sealed class ReaderKeyboardShortcutTests
{
    [Theory]
    [InlineData("LeftArrow", "\u2190")]
    [InlineData("RightArrow", "\u2192")]
    [InlineData("UpArrow", "\u2191")]
    [InlineData("DownArrow", "\u2193")]
    [InlineData("PageUp", "Page Up")]
    [InlineData("PageDown", "Page Down")]
    [InlineData("Space", "Space")]
    [InlineData("Escape", "Esc")]
    [InlineData("[", "[")]
    [InlineData("j", "J")]
    public void Label_UsesNiratanAlignedKeyNames(string key, string expected)
    {
        new ReaderKeyboardShortcut(key).Label.Should().Be(expected);
    }

    [Fact]
    public void KeyboardShortcutBinding_RoundTripsBracketOemVirtualKeys()
    {
        var bracketLeftKey = (VirtualKey)219;
        var bracketRightKey = (VirtualKey)221;

        KeyboardShortcutBinding.FromVirtualKey(bracketLeftKey).Should().Be(new KeyboardShortcutBinding("["));
        KeyboardShortcutBinding.FromVirtualKey(bracketRightKey).Should().Be(new KeyboardShortcutBinding("]"));

        ShortcutInputMapper.TryGetVirtualKey(new KeyboardShortcutBinding("["), out var leftKey, out _)
            .Should().BeTrue();
        ShortcutInputMapper.TryGetVirtualKey(new KeyboardShortcutBinding("]"), out var rightKey, out _)
            .Should().BeTrue();
        leftKey.Should().Be(bracketLeftKey);
        rightKey.Should().Be(bracketRightKey);
    }

    [Fact]
    public void Label_UsesStableWindowsModifierOrder()
    {
        var shortcut = new ReaderKeyboardShortcut(
            "r",
            ReaderKeyboardShortcutModifiers.Control
                | ReaderKeyboardShortcutModifiers.Shift
                | ReaderKeyboardShortcutModifiers.Alt
                | ReaderKeyboardShortcutModifiers.Windows);

        shortcut.Label.Should().Be("Ctrl+Shift+Alt+Win+R");
    }

    [Fact]
    public void ReaderShortcutActions_DefineNiratanReaderDefaults()
    {
        ReaderShortcutActions.All.Select(action => action.Id).Should().Equal(
            "reader.previousPage",
            "reader.nextPage",
            "reader.close",
            "reader.toggleFocusMode",
            "reader.toggleStatistics",
            "reader.toggleLyricsMode");

        ReaderShortcutActions.PreviousPage.DefaultShortcut.Label.Should().Be("\u2190");
        ReaderShortcutActions.NextPage.DefaultShortcut.Label.Should().Be("\u2192");
        ReaderShortcutActions.Close.DefaultShortcut.Label.Should().Be("Esc");
        ReaderShortcutActions.ToggleFocusMode.DefaultShortcut.Label.Should().Be("F");
        ReaderShortcutActions.ToggleStatistics.DefaultShortcut.Label.Should().Be("T");
        ReaderShortcutActions.ToggleLyricsMode.DefaultShortcut.Label.Should().Be("L");
    }

    [Fact]
    public void SasayakiShortcutActions_DefineNiratanDefaults()
    {
        SasayakiShortcutActions.All.Select(action => action.Id).Should().Equal(
            "sasayaki.previousCue",
            "sasayaki.playPause",
            "sasayaki.nextCue",
            "sasayaki.replayCue",
            "sasayaki.jumpCue");

        SasayakiShortcutActions.PreviousCue.DefaultShortcut.Label.Should().Be("[");
        SasayakiShortcutActions.PlayPause.DefaultShortcut.Label.Should().Be("P");
        SasayakiShortcutActions.NextCue.DefaultShortcut.Label.Should().Be("]");
        SasayakiShortcutActions.ReplayCue.DefaultShortcut.Label.Should().Be("R");
        SasayakiShortcutActions.JumpCue.DefaultShortcut.Label.Should().Be("J");
    }
}
