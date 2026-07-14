using FluentAssertions;
using Niratan.Models;

namespace Niratan.Tests.Models;

public class VideoPlaybackStateTests
{
    [Fact]
    public void ShouldPersistProgress_IgnoresZeroLoadingSnapshot()
    {
        VideoPlaybackState.ShouldPersistProgress(TimeSpan.Zero, TimeSpan.Zero)
            .Should()
            .BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ShouldPersistProgress_IgnoresNearStartSnapshots(double positionSeconds)
    {
        VideoPlaybackState.ShouldPersistProgress(
                TimeSpan.FromSeconds(positionSeconds),
                TimeSpan.FromSeconds(2406))
            .Should()
            .BeFalse();
    }

    [Fact]
    public void ShouldPersistProgress_AllowsMeaningfulPlaybackPosition()
    {
        VideoPlaybackState.ShouldPersistProgress(
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(2406))
            .Should()
            .BeTrue();
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
