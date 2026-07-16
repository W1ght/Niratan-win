using FluentAssertions;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class YouTubeUrlParserTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=yrL6Qny0E5M")]
    [InlineData("https://www.youtube.com/watch?v=yrL6Qny0E5M&t=41s")]
    [InlineData("https://youtu.be/yrL6Qny0E5M")]
    [InlineData("https://youtube.com/shorts/yrL6Qny0E5M")]
    [InlineData("https://www.youtube.com/embed/yrL6Qny0E5M")]
    [InlineData("https://www.youtube-nocookie.com/embed/yrL6Qny0E5M")]
    public void TryParse_AcceptsSupportedVideoUrls(string url)
    {
        YouTubeUrlParser.TryParse(url, out var id, out var canonical).Should().BeTrue();
        id.Should().Be("yrL6Qny0E5M");
        canonical.Should().Be("https://www.youtube.com/watch?v=yrL6Qny0E5M");
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=yrL6Qny0E5M&t=41s", 41)]
    [InlineData("https://youtu.be/yrL6Qny0E5M?t=1m21s", 81)]
    [InlineData("https://www.youtube-nocookie.com/embed/yrL6Qny0E5M?start=125", 125)]
    [InlineData("https://www.youtube.com/watch?v=yrL6Qny0E5M#t=2m", 120)]
    public void TryParse_ExtractsRequestedStartPosition(string url, int expectedSeconds)
    {
        YouTubeUrlParser.TryParse(url, out var id, out var canonical, out var start).Should().BeTrue();

        id.Should().Be("yrL6Qny0E5M");
        canonical.Should().Be("https://www.youtube.com/watch?v=yrL6Qny0E5M");
        start.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData("https://youtube.com.evil.test/watch?v=yrL6Qny0E5M")]
    [InlineData("https://evil.youtube.com/watch?v=yrL6Qny0E5M")]
    [InlineData("https://www.youtube.com/playlist?list=yrL6Qny0E5M")]
    [InlineData("https://www.youtube.com/watch?v=yrL6Qny0E5M&list=PL123")]
    [InlineData("https://youtu.be/yrL6Qny0E5M/extra")]
    [InlineData("https://youtube-nocookie.com/watch?v=yrL6Qny0E5M")]
    [InlineData("https://www.youtube.com/watch?v=too-short")]
    public void TryParse_RejectsUnsupportedOrAmbiguousUrls(string url)
    {
        YouTubeUrlParser.TryParse(url, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void IsRemoteKey_RecognizesStableYouTubeIdentity()
    {
        YouTubeUrlParser.IsRemoteKey("remote://youtube/yrL6Qny0E5M").Should().BeTrue();
        YouTubeUrlParser.IsRemoteKey("C:\\video.mp4").Should().BeFalse();
    }
}
