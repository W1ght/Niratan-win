using FluentAssertions;
using Hoshi.Models;
using Hoshi.Services.Video;

namespace Hoshi.Tests.Services.Video;

public class FfmpegVideoSubtitleTranscriptExtractorTests
{
    [Fact]
    public async Task ExtractAsync_WithExternalSubtitleFile_ParsesWithoutFfmpeg()
    {
        var subtitlePath = Path.Combine(
            Path.GetTempPath(),
            $"hoshi-video-external-track-{Guid.NewGuid():N}.srt");
        var sut = new FfmpegVideoSubtitleTranscriptExtractor(
            new SubtitleParserService(),
            () => null);
        var track = new VideoTrackInfo(
            3,
            VideoTrackType.Subtitle,
            "Japanese",
            "jpn",
            "subrip",
            0,
            subtitlePath,
            false,
            true);

        try
        {
            await File.WriteAllTextAsync(
                subtitlePath,
                """
                1
                00:00:01,000 --> 00:00:02,000
                外部字幕
                """,
                TestContext.Current.CancellationToken);

            var cues = await sut.ExtractAsync(
                @"D:\Video\episode.mkv",
                track,
                TestContext.Current.CancellationToken);

            cues.Should().ContainSingle();
            cues[0].Text.Should().Be("外部字幕");
            cues[0].Start.Should().Be(TimeSpan.FromSeconds(1));
        }
        finally
        {
            if (File.Exists(subtitlePath))
                File.Delete(subtitlePath);
        }
    }

    [Fact]
    public async Task ExtractAsync_WithImageSubtitleTrack_ReturnsEmpty()
    {
        var sut = new FfmpegVideoSubtitleTranscriptExtractor(
            new SubtitleParserService(),
            () => "ffmpeg.exe");
        var track = new VideoTrackInfo(
            3,
            VideoTrackType.Subtitle,
            "PGS",
            "jpn",
            "hdmv_pgs_subtitle",
            2,
            null,
            true,
            true);

        var cues = await sut.ExtractAsync(
            @"D:\Video\episode.mkv",
            track,
            TestContext.Current.CancellationToken);

        cues.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_WithEmbeddedTrackAndNoFfmpeg_ReturnsEmpty()
    {
        var sut = new FfmpegVideoSubtitleTranscriptExtractor(
            new SubtitleParserService(),
            () => null);
        var track = new VideoTrackInfo(
            3,
            VideoTrackType.Subtitle,
            "Japanese",
            "jpn",
            "subrip",
            2,
            null,
            false,
            true);

        var cues = await sut.ExtractAsync(
            @"D:\Video\episode.mkv",
            track,
            TestContext.Current.CancellationToken);

        cues.Should().BeEmpty();
    }

    [Fact]
    public void Source_IncludesExplicitAndBundledFfmpegDiscovery()
    {
        var source = File.ReadAllText(GetSourceFilePath());

        source.Should().Contain("HOSHI_FFMPEG_PATH");
        source.Should().Contain("AppContext.BaseDirectory");
        source.Should().Contain("\"app\", \"bin\", \"ffmpeg.exe\"");
        source.Should().Contain("\"bin\", \"ffmpeg.exe\"");
        source.Should().Contain("ffmpeg");
        source.Should().Contain("win-x64");
    }

    private static string GetSourceFilePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "Hoshi",
                "Services",
                "Video",
                "FfmpegVideoSubtitleTranscriptExtractor.cs");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate FfmpegVideoSubtitleTranscriptExtractor.cs");
    }
}
