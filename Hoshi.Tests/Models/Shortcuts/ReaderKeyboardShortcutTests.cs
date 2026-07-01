using FluentAssertions;
using Hoshi.Models.Shortcuts;

namespace Hoshi.Tests.Models.Shortcuts;

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
    public void Label_UsesMacAlignedKeyNames(string key, string expected)
    {
        new ReaderKeyboardShortcut(key).Label.Should().Be(expected);
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
    public void ReaderShortcutActions_DefineReaderDefaults()
    {
        ReaderShortcutActions.All.Select(action => action.Id).Should().Equal(
            "reader.previousPage",
            "reader.nextPage",
            "reader.close",
            "reader.toggleFocusMode",
            "reader.toggleStatistics");

        ReaderShortcutActions.PreviousPage.DefaultShortcut.Label.Should().Be("\u2190");
        ReaderShortcutActions.NextPage.DefaultShortcut.Label.Should().Be("\u2192");
        ReaderShortcutActions.Close.DefaultShortcut.Label.Should().Be("Esc");
        ReaderShortcutActions.ToggleFocusMode.DefaultShortcut.Label.Should().Be("F");
        ReaderShortcutActions.ToggleStatistics.DefaultShortcut.Label.Should().Be("T");
    }

    [Fact]
    public void SasayakiShortcutActions_DefineMacAlignedDefaults()
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
