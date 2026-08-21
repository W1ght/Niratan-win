using FluentAssertions;

namespace Niratan.Tests.Views.Pages;

public sealed class NovelReaderSasayakiAssetTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));

    [Fact]
    public void AudioSubtitleImport_LoadsAndAppliesPlaybackWithoutResettingPosition()
    {
        var code = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs"));
        var start = code.IndexOf(
            "private async Task LoadSasayakiAsync",
            StringComparison.Ordinal);
        var end = code.IndexOf(
            "private async Task LoadSasayakiSidecarAsync",
            start,
            StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        end.Should().BeGreaterThan(start);
        var method = code[start..end];

        method.Should().Contain("LoadPlaybackAsync(bookRootPath)");
        method.Should().Contain("ApplySasayakiPlayback(playback)");
        method.Should().NotContain("SaveSasayakiPlaybackAsync(0)");
    }

    [Fact]
    public void Player_DefersPlaybackRestoreUntilMediaOpened()
    {
        var code = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Sasayaki", "SasayakiPlayer.cs"));

        code.Should().Contain("SasayakiSeekLandingState _seekLanding");
        code.Should().Contain("player.MediaOpened += OnMediaOpened");
        code.Should().Contain("ApplyPendingSeek(sender)");
        code.Should().Contain("_seekLanding.TryAcceptPosition(position)");
    }

    [Fact]
    public void LookupPopupDismissal_ResumesOnlyPlaybackPausedByLookup()
    {
        var code = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs"));

        code.Should().Contain("ResumeSasayakiAfterLookup();");
        code.Should().Contain("_sasayakiLookupPlayback.TryPauseForLookup(");
        code.Should().Contain("_sasayakiLookupPlayback.TryResumeAfterDismiss(");
        code.Should().Contain("_sasayakiLookupPlayback.CancelAutoResume();");
    }

    [Fact]
    public void AudiobookPlayback_OnlyExplicitPausePausesReaderStatistics()
    {
        var code = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs"));

        var toggleStart = code.IndexOf(
            "private async Task ToggleSasayakiPlaybackAsync",
            StringComparison.Ordinal);
        var toggleEnd = code.IndexOf(
            "private async void SasayakiPrevCue_Click",
            toggleStart,
            StringComparison.Ordinal);
        toggleStart.Should().BeGreaterThanOrEqualTo(0);
        toggleEnd.Should().BeGreaterThan(toggleStart);
        var toggleMethod = code[toggleStart..toggleEnd];
        toggleMethod.Should().Contain("await _sasayakiPlaybackToggleSemaphore.WaitAsync();");
        toggleMethod.Should().Contain(
            "if (_sasayakiVM.IsPlaying || player.IsPlaying)");
        toggleMethod.Should().Contain("await ViewModel.PauseStatisticsAsync();");
        toggleMethod.Should().Contain(
            "await ViewModel.StartStatisticsForAudiobookPlaybackAsync();");

        var playerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Sasayaki", "SasayakiPlayer.cs"));
        var pauseStart = playerCode.IndexOf(
            "public void Pause()",
            StringComparison.Ordinal);
        var pauseEnd = playerCode.IndexOf(
            "public void Resume()",
            pauseStart,
            StringComparison.Ordinal);
        pauseStart.Should().BeGreaterThanOrEqualTo(0);
        pauseEnd.Should().BeGreaterThan(pauseStart);
        playerCode[pauseStart..pauseEnd].Should().NotContain("!IsPlaying");

        var lookupStart = code.IndexOf(
            "private void PauseSasayakiForLookup",
            StringComparison.Ordinal);
        var lookupEnd = code.IndexOf(
            "private void ResumeSasayakiAfterLookup",
            lookupStart,
            StringComparison.Ordinal);
        lookupStart.Should().BeGreaterThanOrEqualTo(0);
        lookupEnd.Should().BeGreaterThan(lookupStart);
        code[lookupStart..lookupEnd].Should().NotContain("PauseStatisticsAsync");
    }

    [Fact]
    public void DirectMiningAudio_IsAwaitedBeforeReturningAnkiTag()
    {
        var code = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs"));

        code.Should().Contain("var storedFilename = await GenerateDirectSasayakiMiningAudioAsync(");
        code.Should().Contain("return AnkiDirectMediaStore.GenerateAsync(");
        code.Should().Contain("AudioClipTag: AnkiMediaMarkup.ForFieldPlaceholder(storedFilename)");
        code.Should().NotContain("_ = GenerateDirectSasayakiMiningAudioAsync(");
        code.Should().NotContain("private static void ReplaceFile(");
    }

    [Fact]
    public void LyricsLookup_UsesAdaptiveHitBoundsAndInvalidatesPendingRequestOnMiss()
    {
        var controlCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Controls", "ReaderLyricsModeControl.xaml.cs"));
        var pageCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs"));
        var rendererCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Sasayaki", "ReaderLyricsCanvasRenderer.cs"));

        controlCode.Should().Contain("e.Handled = true;");
        controlCode.Should().Contain("DismissLookupRequested?.Invoke(this, EventArgs.Empty);");
        controlCode.Should().Contain("canvasOffset.X");
        controlCode.Should().Contain("hit.Bounds.Width");
        controlCode.Should().Contain("hit.Bounds.Height");
        pageCode.Should().Contain("Interlocked.Increment(ref _lookupRequestVersion);");
        pageCode.Should().Contain("_popupOverlay?.Dismiss();");
        rendererCode.Should().Contain(
            "drawingSession.DrawText(glyph.Text, glyph.Bounds, glyph.Color, glyph.Format);");
    }
}
