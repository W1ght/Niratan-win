using FluentAssertions;

namespace Niratan.Tests.Services.Video;

public sealed class VideoPlayerChromeContractTests
{
    [Fact]
    public void YouTubeLinkEntry_ExistsOnlyInVideoLibrary()
    {
        var projectRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));
        var playerXaml = File.ReadAllText(
            Path.Combine(projectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var playerCode = File.ReadAllText(
            Path.Combine(projectRoot, "Views", "Video", "VideoPlayerWindow.xaml.cs"));
        var libraryXaml = File.ReadAllText(
            Path.Combine(projectRoot, "Views", "Pages", "VideoLibraryPage.xaml"));

        playerXaml.Should().NotContain("OpenYouTubeLinkButton");
        playerCode.Should().NotContain("OpenYouTubeLinkButton_Click");
        libraryXaml.Should().Contain("AddYouTubeLinkButton");
    }

    [Fact]
    public void Anime4KSelection_AppliesDirectlyAndOnlyShowsDownloadWhenRequired()
    {
        var projectRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));
        var playerXaml = File.ReadAllText(
            Path.Combine(projectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var playbackCode = File.ReadAllText(
            Path.Combine(projectRoot, "Views", "Video", "VideoPlayerWindow.Playback.cs"));

        playerXaml.Should().Contain("SelectionChanged=\"Anime4KPresetComboBox_SelectionChanged\"");
        playerXaml.Should().Contain("VideoInspectorAnime4KDownloadButton");
        playbackCode.Should().Contain("ViewModel.IsVideoShaderDownloadRequired");
        playbackCode.Should().Contain("UpdateAnime4KDownloadControls");
        playerXaml.Should().NotContain("VideoInspectorAnime4KApplyButton");
        playerXaml.Should().NotContain("Download and apply");
    }

    [Fact]
    public void SubtitleCanvas_DoesNotCoverBottomChromeControls()
    {
        var projectRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));
        var playerXaml = File.ReadAllText(
            Path.Combine(projectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));

        var subtitlePanelIndex = playerXaml.IndexOf(
            "x:Name=\"SubtitlePanelBorder\"",
            StringComparison.Ordinal);
        var bottomChromeIndex = playerXaml.IndexOf(
            "x:Name=\"BottomChrome\"",
            StringComparison.Ordinal);

        subtitlePanelIndex.Should().BeGreaterThan(0);
        bottomChromeIndex.Should().BeGreaterThan(subtitlePanelIndex);
        playerXaml.Substring(subtitlePanelIndex, 160).Should().Contain("Canvas.ZIndex=\"20\"");
        playerXaml.Substring(bottomChromeIndex, 160).Should().Contain("Canvas.ZIndex=\"30\"");
    }

    [Fact]
    public void VideoStartup_UnpausesBeforeSidebarMetadataRefresh()
    {
        var projectRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));
        var playerCode = File.ReadAllText(
            Path.Combine(projectRoot, "Views", "Video", "VideoPlayerWindow.xaml.cs"));

        var unpauseIndex = playerCode.IndexOf(
            "await _playbackEngine.SetPausedAsync(false, ct);",
            StringComparison.Ordinal);
        var refreshTracksIndex = playerCode.IndexOf(
            "await RefreshMediaTracksAsync(!hasInteractiveSubtitle, ct);",
            StringComparison.Ordinal);

        unpauseIndex.Should().BeGreaterThan(0);
        refreshTracksIndex.Should().BeGreaterThan(unpauseIndex);
        playerCode.Should().Contain("var loadVideoTask = ViewModel.LoadVideoAsync(video, ct);");
        playerCode.Should().Contain("await loadVideoTask;");
    }

    [Fact]
    public void YouTubePublisherSubtitles_UseUnpackagedSafeTemporaryStorage()
    {
        var projectRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));
        var playerCode = File.ReadAllText(
            Path.Combine(projectRoot, "Views", "Video", "VideoPlayerWindow.xaml.cs"));

        playerCode.Should().Contain("AppDataHelper.GetTemporaryDataPath()");
        playerCode.Should().NotContain("ApplicationData.Current.TemporaryFolder");
    }

    [Fact]
    public void Inspector_UsesApplicationThemeAndThemeAwareColors()
    {
        var projectRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));
        var playerXaml = File.ReadAllText(
            Path.Combine(projectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var transcriptXaml = File.ReadAllText(
            Path.Combine(projectRoot, "Views", "Video", "VideoTranscriptListControl.xaml"));
        var playerCode = File.ReadAllText(
            Path.Combine(projectRoot, "Views", "Video", "VideoPlayerWindow.xaml.cs"));

        playerXaml.Should().NotContain("RequestedTheme=\"Light\"");
        playerXaml.Should().Contain("{ThemeResource SolidBackgroundFillColorBaseBrush}");
        playerXaml.Should().Contain("{ThemeResource TextFillColorPrimaryBrush}");
        playerXaml.Should().Contain("{ThemeResource CardBackgroundFillColorDefaultBrush}");
        transcriptXaml.Should().Contain("{ThemeResource CardBackgroundFillColorDefaultBrush}");
        transcriptXaml.Should().Contain("{ThemeResource TextFillColorPrimaryBrush}");
        playerCode.Should().Contain("ApplyInspectorTheme(_settingsService.Current.Theme)");
        playerCode.Should().Contain("_settingsService.SettingChanged += SettingsService_SettingChanged");
        playerCode.Should().Contain("_settingsService.SettingChanged -= SettingsService_SettingChanged");
    }
}
