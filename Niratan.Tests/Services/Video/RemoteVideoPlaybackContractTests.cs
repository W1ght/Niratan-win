using System;
using System.Collections.Generic;
using FluentAssertions;
using Niratan.Models;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class RemoteVideoPlaybackContractTests
{
    [Fact]
    public void PreferredSubtitle_RestoresSavedLanguageThenJapaneseThenEnglish()
    {
        var source = CreateSource([
            Subtitle("en", "English"),
            Subtitle("ja", "Japanese"),
            Subtitle("fr", "French"),
        ]);

        source.PreferredSubtitle(["fr"])!.Language.Should().Be("fr");
        source.PreferredSubtitle()!.Language.Should().Be("ja");
    }

    [Fact]
    public void FilterPublisherSubtitles_RemovesAutomaticCaptions()
    {
        var options = new[]
        {
            Subtitle("ja", "Japanese"),
            new RemoteVideoSubtitleOption("ja-auto", "ja", "Japanese (auto)", "https://example.test/auto", true),
        };

        YoutubeExplodeRemoteVideoResolver.FilterPublisherSubtitles(options)
            .Should().ContainSingle()
            .Which.IsAutomatic.Should().BeFalse();
    }

    [Fact]
    public void BuildLoadRequestCommandArgs_CarriesStartAndExternalAudio()
    {
        var request = new VideoPlaybackRequest(
            "https://example.test/video",
            "https://example.test/audio",
            null,
            new Dictionary<string, string> { ["Referer"] = "https://www.youtube.com/" },
            TimeSpan.FromSeconds(12.5));

        var args = MpvPlaybackEngine.BuildLoadRequestCommandArgs(request);

        args.Should().HaveCount(5);
        args[0].Should().Be("loadfile");
        args[4].Should().Contain("start=12.5").And.Contain("pause=yes").And.Contain("audio-file=");
    }

    [Fact]
    public void ResolveExpiry_UsesEarliestSignedExpiryMinusFiveMinutes()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);
        var streams = new[]
        {
            StreamWithUrl("https://example.test/a?expire=1900007200"),
            StreamWithUrl("https://example.test/b?expire=1900003600"),
        };

        YoutubeExplodeRemoteVideoResolver.ResolveExpiry(streams, now)
            .Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_900_003_600).AddMinutes(-5));
        YoutubeExplodeRemoteVideoResolver.ResolveExpiry([StreamWithUrl("https://example.test/no-expiry")], now)
            .Should().Be(now.AddHours(4));
    }

    private static RemoteVideoSubtitleOption Subtitle(string language, string name) =>
        new(language, language, name, $"https://example.test/{language}", false);

    private static RemoteVideoStream StreamWithUrl(string url) =>
        new(url, "1", 720, true, true, "mp4", "h264", "aac", 1000, new Dictionary<string, string>());

    private static ResolvedRemoteVideoSource CreateSource(IReadOnlyList<RemoteVideoSubtitleOption> subtitles)
    {
        var stream = new RemoteVideoStream(
            "https://example.test/video",
            "1",
            720,
            true,
            true,
            "mp4",
            "h264",
            "aac",
            1000,
            new Dictionary<string, string>());
        return new ResolvedRemoteVideoSource(
            new RemoteVideoIdentity("youtube", "yrL6Qny0E5M", "original", "canonical", "Title", null, null),
            stream,
            null,
            null,
            stream,
            subtitles,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(4),
            [new RemoteVideoQualityOption("1", 720, stream, null)]);
    }
}
