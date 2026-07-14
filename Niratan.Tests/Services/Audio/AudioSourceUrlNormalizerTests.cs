using FluentAssertions;
using Niratan.Services.Audio;

namespace Niratan.Tests.Services.Audio;

public sealed class AudioSourceUrlNormalizerTests
{
    [Fact]
    public void Normalize_RewritesHttpLocalhostToIpv4Loopback()
    {
        var url = "http://localhost:5050/?term={term}&reading={reading}";

        var normalized = AudioSourceUrlNormalizer.Normalize(url);

        normalized.Should().Be("http://127.0.0.1:5050/?term={term}&reading={reading}");
    }

    [Fact]
    public void Normalize_RewritesMalformedHttpLocalhostWithBackslashes()
    {
        var url = "http:\\localhost:5050\\?term=%E4%BD%95%E6%99%82";

        var normalized = AudioSourceUrlNormalizer.Normalize(url);

        normalized.Should().Be("http://127.0.0.1:5050/?term=%E4%BD%95%E6%99%82");
    }

    [Theory]
    [InlineData("https://localhost:5050/?term={term}")]
    [InlineData("http://localhost.example:5050/?term={term}")]
    [InlineData("http://localhostaudio:5050/?term={term}")]
    public void Normalize_DoesNotRewriteNonHttpLocalhostAuthority(string url)
    {
        AudioSourceUrlNormalizer.Normalize(url).Should().Be(url);
    }
}
