using FluentAssertions;
using Niratan.Services.Nyaa;

namespace Niratan.Tests.Services.Nyaa;

public sealed class NyaaRssParserTests
{
    private static readonly Uri BaseUri = new("https://nyaa.si/");

    [Fact]
    public void Parse_reads_nyaa_fields_and_derives_torrent_url()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0" xmlns:nyaa="https://nyaa.si/xmlns/nyaa">
              <channel>
                <item>
                  <title>[Group] Example resource pack</title>
                  <link>https://nyaa.si/view/123456</link>
                  <guid isPermaLink="true">https://nyaa.si/view/123456</guid>
                  <pubDate>Wed, 22 Jul 2026 10:30:00 +0000</pubDate>
                  <nyaa:category>Literature - Raw</nyaa:category>
                  <nyaa:size>1.5 GiB</nyaa:size>
                  <nyaa:seeders>42</nyaa:seeders>
                  <nyaa:leechers>3</nyaa:leechers>
                  <nyaa:downloads>900</nyaa:downloads>
                  <nyaa:trusted>Yes</nyaa:trusted>
                  <nyaa:remake>No</nyaa:remake>
                </item>
              </channel>
            </rss>
            """;

        var item = new NyaaRssParser().Parse(xml, BaseUri).Should().ContainSingle().Subject;

        item.Id.Should().Be("123456");
        item.TorrentUri.Should().Be(new Uri("https://nyaa.si/download/123456.torrent"));
        item.SizeBytes.Should().Be(1610612736);
        item.Seeders.Should().Be(42);
        item.Leechers.Should().Be(3);
        item.Downloads.Should().Be(900);
        item.IsTrusted.Should().BeTrue();
        item.IsRemake.Should().BeFalse();
    }

    [Fact]
    public void Parse_rejects_cross_origin_download_links()
    {
        const string xml = """
            <rss version="2.0" xmlns:nyaa="https://nyaa.si/xmlns/nyaa">
              <channel>
                <item>
                  <title>Unsafe</title>
                  <link>https://evil.example/download/1.torrent</link>
                  <guid>https://evil.example/view/1</guid>
                </item>
              </channel>
            </rss>
            """;

        new NyaaRssParser().Parse(xml, BaseUri).Should().BeEmpty();
    }

    [Fact]
    public void Parse_rejects_non_default_port_on_nyaa_host()
    {
        const string xml = """
            <rss version="2.0" xmlns:nyaa="https://nyaa.si/xmlns/nyaa">
              <channel>
                <item>
                  <title>Unsafe port</title>
                  <link>https://nyaa.si:444/download/1.torrent</link>
                  <guid>https://nyaa.si:444/view/1</guid>
                </item>
              </channel>
            </rss>
            """;

        new NyaaRssParser().Parse(xml, BaseUri).Should().BeEmpty();
    }

    [Theory]
    [InlineData("800 B", 800)]
    [InlineData("2 KiB", 2048)]
    [InlineData("2.5 MiB", 2621440)]
    [InlineData("1 GiB", 1073741824)]
    public void ParseSize_understands_nyaa_size_units(string input, long expected)
    {
        NyaaRssParser.ParseSize(input).Should().Be(expected);
    }
}
