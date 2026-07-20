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
            .Select(action => action.Id)
            .Should().Equal(
                GlobalShortcutActions.OpenId,
                GlobalShortcutActions.LookupSelectedTextId);
        GlobalShortcutActions.Open.DefaultBinding.Label.Should().Be("Ctrl+O");
        GlobalShortcutActions.LookupSelectedText.DefaultBinding.Label.Should().Be("Ctrl+Alt+D");
        registry.ActionsIn(ShortcutCategory.DictionaryPopup)
            .Select(action => action.Id)
            .Should().Equal(
                DictionaryShortcutActions.PreviousEntryId,
                DictionaryShortcutActions.NextEntryId,
                PopupShortcutActions.DismissId);
        registry.ActionsIn(ShortcutCategory.Video)
            .Select(action => action.Id)
            .Should()
            .Contain([
                VideoShortcutActions.SeekBackwardId,
                VideoShortcutActions.SeekForwardId,
                VideoShortcutActions.PreviousEpisodeId,
                VideoShortcutActions.NextEpisodeId,
                VideoShortcutActions.ToggleSubtitleGapFastForwardId,
                VideoShortcutActions.SetABLoopStartId,
                VideoShortcutActions.SetABLoopEndId,
                VideoShortcutActions.ExitFocusModeId,
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

    [Theory]
    [InlineData("\\", 220)]
    [InlineData(",", 188)]
    [InlineData(".", 190)]
    [InlineData("/", 191)]
    public void ShortcutInputMapper_MapsVideoPunctuationKeys(string key, int expectedVirtualKey)
    {
        ShortcutInputMapper.TryGetVirtualKey(
                new KeyboardShortcutBinding(key),
                out var virtualKey,
                out _)
            .Should().BeTrue();
        ((int)virtualKey).Should().Be(expectedVirtualKey);
        KeyboardShortcutBinding.FromVirtualKey(virtualKey).Key.Should().Be(key);
    }

    [Fact]
    public void VideoRegistry_ContainsNiratanAdvancedPlaybackActions()
    {
        VideoShortcutActions.All.Select(action => action.Id).Should().Contain([
            VideoShortcutActions.PreviousEpisodeId,
            VideoShortcutActions.NextEpisodeId,
            VideoShortcutActions.DecreaseSpeedId,
            VideoShortcutActions.IncreaseSpeedId,
            VideoShortcutActions.ResetSpeedId,
            VideoShortcutActions.ToggleMuteId,
            VideoShortcutActions.MineCurrentSubtitleId,
            VideoShortcutActions.ToggleSubtitleGapFastForwardId,
            VideoShortcutActions.SubtitleEarlierId,
            VideoShortcutActions.SubtitleLaterId,
            VideoShortcutActions.ResetSubtitleTimingId,
            VideoShortcutActions.AlignPreviousSubtitleToCurrentTimeId,
            VideoShortcutActions.AlignNextSubtitleToCurrentTimeId,
            VideoShortcutActions.AudioEarlierId,
            VideoShortcutActions.AudioLaterId,
            VideoShortcutActions.ToggleFileLoopId,
            VideoShortcutActions.SetABLoopStartId,
            VideoShortcutActions.SetABLoopEndId,
            VideoShortcutActions.ToggleTranscriptId,
            VideoShortcutActions.RotateClockwiseId,
            VideoShortcutActions.ToggleFullScreenId,
            VideoShortcutActions.ExitFocusModeId,
        ]);

        VideoShortcutActions.PreviousEpisode.DefaultBinding.Label.Should().Be("Ctrl+Shift+\u2190");
        VideoShortcutActions.PreviousSubtitleCue.DefaultBinding.Label.Should().Be("Alt+\u2190");
        VideoShortcutActions.AudioEarlier.DefaultBinding.Label.Should().Be("Shift+Alt+,");
    }

    [Fact]
    public void ShortcutConfiguration_ReadsAndResetsLegacyWindowsActionIds()
    {
        var configuration = new ShortcutConfiguration
        {
            Bindings =
            {
                ["video.toggleFullscreen"] = new KeyboardShortcutBinding("q"),
            },
        };

        configuration.GetBinding(VideoShortcutActions.ToggleFullScreen).Label.Should().Be("Q");
        configuration.ResetBinding(VideoShortcutActions.ToggleFullScreenId);
        configuration.Bindings.Should().NotContainKey("video.toggleFullscreen");
    }
}
