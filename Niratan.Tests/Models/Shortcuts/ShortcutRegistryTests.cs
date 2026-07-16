using FluentAssertions;
using Niratan.Models.Shortcuts;

namespace Niratan.Tests.Models.Shortcuts;

public sealed class ShortcutRegistryTests
{
    [Fact]
    public void ApplicationRegistry_DefinesUniqueGlobalReaderSasayakiAndVideoActions()
    {
        var registry = ShortcutRegistry.Application;

        registry.Actions.Select(action => action.Id)
            .Should()
            .OnlyHaveUniqueItems();
        registry.Action(VideoShortcutActions.PlayPauseId).Should().NotBeNull();
        registry.Action(VideoShortcutActions.LookupSubtitleId)!.DefaultBinding.Label.Should().Be("F2");
        registry.Action(GlobalShortcutActions.LookupSelectedTextId)
            .Should().Be(GlobalShortcutActions.LookupSelectedText);
        registry.ActionsIn(ShortcutCategory.Global)
            .Should().ContainSingle()
            .Which.DefaultBinding.Label.Should().Be("Ctrl+Alt+D");
        registry.ActionsIn(ShortcutCategory.Video)
            .Select(action => action.Id)
            .Should()
            .Contain([
                VideoShortcutActions.SeekBackwardId,
                VideoShortcutActions.SeekForwardId,
                VideoShortcutActions.CloseOverlayOrFullscreenId,
            ]);
        registry.ActionsIn(ShortcutCategory.Reader).Should().NotBeEmpty();
        registry.ActionsIn(ShortcutCategory.Sasayaki).Should().NotBeEmpty();
    }

    [Fact]
    public void ConflictChecker_UsesScopesAndPopupShadowing()
    {
        var reader = new ShortcutAction(
            "reader.test",
            "Reader Test",
            "ShortcutActionReaderTest",
            ShortcutCategory.Reader,
            [ShortcutScope.Reader],
            new KeyboardShortcutBinding("f"));
        var video = new ShortcutAction(
            "video.test",
            "Video Test",
            "ShortcutActionVideoTest",
            ShortcutCategory.Video,
            [ShortcutScope.Video],
            new KeyboardShortcutBinding("f"));
        var popup = new ShortcutAction(
            "popup.test",
            "Popup Test",
            "ShortcutActionPopupTest",
            ShortcutCategory.DictionaryPopup,
            [ShortcutScope.Popup],
            new KeyboardShortcutBinding("f"));

        ShortcutConflictChecker.Relationship(
                reader,
                reader.DefaultBinding,
                video,
                video.DefaultBinding)
            .Should()
            .Be(ShortcutConflictKind.None);
        ShortcutConflictChecker.Relationship(
                reader,
                reader.DefaultBinding,
                popup,
                popup.DefaultBinding)
            .Should()
            .Be(ShortcutConflictKind.Shadowed);
        ShortcutConflictChecker.Relationship(
                video,
                video.DefaultBinding,
                VideoShortcutActions.ToggleFullscreen,
                VideoShortcutActions.ToggleFullscreen.DefaultBinding)
            .Should()
            .Be(ShortcutConflictKind.Conflict);
    }

    [Theory]
    [InlineData("LeftArrow", "\u2190")]
    [InlineData("RightArrow", "\u2192")]
    [InlineData("UpArrow", "\u2191")]
    [InlineData("DownArrow", "\u2193")]
    [InlineData("Add", "+")]
    [InlineData("Subtract", "-")]
    [InlineData("Escape", "Esc")]
    public void KeyboardShortcutBinding_LabelUsesUnifiedKeyNames(string key, string expected)
    {
        new KeyboardShortcutBinding(key).Label.Should().Be(expected);
    }
}
