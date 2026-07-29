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
    [InlineData(1.999)]
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

    [Theory]
    [InlineData(-20_000, -10_000)]
    [InlineData(-250, -250)]
    [InlineData(250, 250)]
    [InlineData(20_000, 10_000)]
    public void FromVideoItem_ClampsPersistedSubtitleDelay(int stored, int expected)
    {
        var state = VideoPlaybackState.FromVideoItem(new VideoItem
        {
            SubtitleDelayMilliseconds = stored,
        });

        state.SubtitleDelayMilliseconds.Should().Be(expected);
    }

    [Fact]
    public void FromVideoItem_NormalizesPlaybackAndAudioDelay()
    {
        var state = VideoPlaybackState.FromVideoItem(new VideoItem
        {
            PlaybackSpeed = 20,
            AudioDelaySeconds = -50,
        });

        state.PlaybackSpeed.Should().Be(5);
        state.AudioDelaySeconds.Should().Be(-30);
    }

    [Fact]
    public void AudioSelection_PreservesStableTrackIdentity()
    {
        var track = new VideoTrackInfo(
            3,
            VideoTrackType.Audio,
            "Japanese",
            "ja",
            "aac",
            7,
            null,
            false,
            true);

        var selection = VideoAudioSelection.EmbeddedTrack(track);

        selection.Kind.Should().Be(VideoAudioSelectionKind.EmbeddedTrack);
        selection.TrackId.Should().Be(3);
        selection.FfIndex.Should().Be(7);
        selection.Title.Should().Be("Japanese");
        selection.Language.Should().Be("ja");
        selection.Codec.Should().Be("aac");
    }

    [Fact]
    public void AudioSelection_ResolvesByFfIndexThenUniqueMetadata()
    {
        var stored = new VideoAudioSelection(
            VideoAudioSelectionKind.EmbeddedTrack,
            TrackId: 99,
            FfIndex: 7,
            Title: "Japanese",
            Language: "ja",
            Codec: "aac");
        var ffIndexMatch = new VideoTrackInfo(
            3,
            VideoTrackType.Audio,
            "Other title",
            "en",
            "opus",
            7,
            null,
            false,
            false);
        var metadataMatch = new VideoTrackInfo(
            4,
            VideoTrackType.Audio,
            "Japanese",
            "ja",
            "aac",
            8,
            null,
            false,
            false);

        stored.ResolveTrack([metadataMatch, ffIndexMatch]).Should().Be(ffIndexMatch);
        (stored with { FfIndex = 77 }).ResolveTrack([metadataMatch]).Should().Be(metadataMatch);
    }

    [Fact]
    public void AudioSelection_DoesNotGuessWhenMetadataIsAmbiguous()
    {
        var stored = new VideoAudioSelection(
            VideoAudioSelectionKind.EmbeddedTrack,
            TrackId: 99,
            FfIndex: 77,
            Title: "Japanese",
            Language: "ja",
            Codec: "aac");
        var first = new VideoTrackInfo(1, VideoTrackType.Audio, "Japanese", "ja", "aac", 1, null, false, false);
        var second = new VideoTrackInfo(2, VideoTrackType.Audio, "Japanese", "ja", "aac", 2, null, false, false);

        stored.ResolveTrack([first, second]).Should().BeNull();
    }
}
