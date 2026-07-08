using FluentAssertions;
using Hoshi.Models;

namespace Hoshi.Tests.Models;

public class VideoPlaybackStateTests
{
    [Fact]
    public void ShouldPersistProgress_IgnoresZeroLoadingSnapshot()
    {
        VideoPlaybackState.ShouldPersistProgress(TimeSpan.Zero, TimeSpan.Zero)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void ResolveRestorePosition_SkipsNearEndProgress()
    {
        var state = new VideoPlaybackState(
            PositionSeconds: 119.2,
            DurationSeconds: 120,
            SubtitleSelection: VideoSubtitleSelection.Off());

        state.ResolveRestorePosition(TimeSpan.FromSeconds(120)).Should().BeNull();
    }

    [Fact]
    public void SubtitleSelection_RepresentsExternalEmbeddedAndOffStates()
    {
        VideoSubtitleSelection.ExternalFile(@"D:\Anime\Episode.ja.srt").Kind
            .Should()
            .Be(VideoSubtitleSelectionKind.ExternalFile);
        VideoSubtitleSelection.EmbeddedTrack(7, "Japanese").TrackId.Should().Be(7);
        VideoSubtitleSelection.Off().Kind.Should().Be(VideoSubtitleSelectionKind.Off);
    }
}
