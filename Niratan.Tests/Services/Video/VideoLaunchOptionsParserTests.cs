using FluentAssertions;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public class VideoLaunchOptionsParserTests
{
    [Fact]
    public void Parse_ReadsQuotedVideoAndSubtitlePaths()
    {
        var options = VideoLaunchOptionsParser.Parse(
            "--open-video \"C:\\Media\\日本語 video.mp4\" --subtitle \"C:\\Media\\日本語 video.srt\"");

        options.Should().NotBeNull();
        options!.VideoPath.Should().Be(@"C:\Media\日本語 video.mp4");
        options.SubtitlePath.Should().Be(@"C:\Media\日本語 video.srt");
    }

    [Fact]
    public void Parse_TreatsFirstBareArgumentAsVideoPath()
    {
        var options = VideoLaunchOptionsParser.Parse(
            "\"C:\\Media\\episode 01.mkv\" --subtitle \"C:\\Media\\episode 01.ja.srt\"");

        options.Should().NotBeNull();
        options!.VideoPath.Should().Be(@"C:\Media\episode 01.mkv");
        options.SubtitlePath.Should().Be(@"C:\Media\episode 01.ja.srt");
    }

    [Fact]
    public void Parse_ReadsEnvironmentCommandLineTokens()
    {
        var options = VideoLaunchOptionsParser.Parse([
            "--open-video",
            @"C:\Media\episode 02.mkv",
            "--subtitle",
            @"C:\Media\episode 02.srt",
        ]);

        options.Should().NotBeNull();
        options!.VideoPath.Should().Be(@"C:\Media\episode 02.mkv");
        options.SubtitlePath.Should().Be(@"C:\Media\episode 02.srt");
    }

    [Fact]
    public void Parse_JoinsUnquotedOptionValuesUntilNextKnownOption()
    {
        var options = VideoLaunchOptionsParser.Parse([
            "--open-video",
            @"D:\smb\himoto\[Kamigami]",
            "Himouto!",
            "Umaru-chan",
            "-",
            "08.mkv",
            "--subtitle",
            @"C:\Temp\niratan-video.srt",
        ]);

        options.Should().NotBeNull();
        options!.VideoPath.Should().Be(@"D:\smb\himoto\[Kamigami] Himouto! Umaru-chan - 08.mkv");
        options.SubtitlePath.Should().Be(@"C:\Temp\niratan-video.srt");
    }
}
