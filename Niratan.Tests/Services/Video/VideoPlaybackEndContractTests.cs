using FluentAssertions;

namespace Niratan.Tests.Services.Video;

public sealed class VideoPlaybackEndContractTests
{
    [Fact]
    public void NormalMpvEof_SnapsPlayerTimelineToDuration()
    {
        var projectRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));
        var engineContract = File.ReadAllText(
            Path.Combine(projectRoot, "Services", "Video", "IVideoPlaybackEngine.cs"));
        var engine = File.ReadAllText(
            Path.Combine(projectRoot, "Services", "Video", "MpvPlaybackEngine.cs"));
        var player = File.ReadAllText(
            Path.Combine(projectRoot, "Views", "Video", "VideoPlayerWindow.xaml.cs"));

        engineContract.Should().Contain("event EventHandler? PlaybackEnded;");
        engine.Should().Contain("endFile.Reason == MpvNative.MpvEndFileReasonEof");
        engine.Should().Contain("PlaybackEnded?.Invoke(this, EventArgs.Empty);");
        player.Should().Contain("ViewModel.UpdatePosition(duration, duration);");
    }
}
