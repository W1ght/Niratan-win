using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Niratan.Models;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class YouTubeStreamSelectorTests
{
    [Fact]
    public void Select_CapsAt1080_DeduplicatesHeight_AndPrefersNativeCodecs()
    {
        var streams = new[]
        {
            Stream("audio-webm", null, false, true, "webm", null, "opus", 192),
            Stream("audio-m4a", null, false, true, "m4a", null, "mp4a.40.2", 128),
            Stream("1080-vp9", 1080, true, false, "webm", "vp9", null, 5000),
            Stream("1080-h264", 1080, true, false, "mp4", "avc1.640028", null, 3500),
            Stream("2160-h264", 2160, true, false, "mp4", "avc1", null, 9000),
            Stream("720-h264", 720, true, false, "mp4", "h264", null, 2500),
            Stream("muxed", 720, true, true, "mp4", "h264", "aac", 2200),
        };

        var selected = YouTubeStreamSelector.Select(streams);

        selected.QualityOptions.Select(option => option.Height).Should().Equal(1080, 720);
        selected.Playback.FormatId.Should().Be("1080-h264");
        selected.ExternalAudio!.FormatId.Should().Be("audio-m4a");
        selected.MuxedFallback!.FormatId.Should().Be("muxed");
        selected.Mining.FormatId.Should().Be("audio-m4a");
    }

    [Fact]
    public void Select_UsesMuxedWhenSeparateStreamsAreUnavailable()
    {
        var muxed = Stream("muxed", 720, true, true, "mp4", "h264", "aac", 2200);

        var selected = YouTubeStreamSelector.Select([muxed]);

        selected.Playback.Should().BeSameAs(muxed);
        selected.ExternalAudio.Should().BeNull();
        selected.Mining.Should().BeSameAs(muxed);
    }

    private static RemoteVideoStream Stream(
        string id,
        int? height,
        bool video,
        bool audio,
        string container,
        string? videoCodec,
        string? audioCodec,
        long bitrate) =>
        new(
            $"https://example.test/{id}?expire=2000000000",
            id,
            height,
            video,
            audio,
            container,
            videoCodec,
            audioCodec,
            bitrate,
            new Dictionary<string, string>());
}
