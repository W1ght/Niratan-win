using FluentAssertions;
using Hoshi.Services.Sasayaki;

namespace Hoshi.Tests.Services.Sasayaki;

public sealed class SasayakiParserTests
{
    [Fact]
    public void Parse_SplitsCrLfSubtitleBlocks()
    {
        var parser = new SasayakiParser();
        var srt = """
            1
            00:00:01,000 --> 00:00:02,000
            最初の行

            2
            00:00:03,500 --> 00:00:04,750
            次の行
            """.Replace("\n", "\r\n");

        var cues = parser.Parse(srt);

        cues.Should().HaveCount(2);
        cues[0].Text.Should().Be("最初の行");
        cues[1].StartTime.Should().Be(3.5);
    }
}
