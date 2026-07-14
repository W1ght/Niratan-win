using FluentAssertions;

namespace Hoshi.Tests.Views.Pages;

public sealed class NovelReaderSasayakiAssetTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Hoshi"));

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

        code.Should().Contain("_pendingSeekSeconds");
        code.Should().Contain("player.MediaOpened += OnMediaOpened");
        code.Should().Contain("ApplyPendingSeek(sender)");
    }
}
