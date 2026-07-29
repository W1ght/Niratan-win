using System.Text;
using FluentAssertions;
using Niratan.Services.Manga;

namespace Niratan.Tests.Services.Manga;

public sealed class MangaMokuroParserTests
{
    [Fact]
    public void GetRegions_MapsCharactersAndPreservesWholeBlockSentence()
    {
        var data = Encoding.UTF8.GetBytes(
            """
            {
              "pages": [{
                "img_path": "001.jpg",
                "img_width": 1000,
                "img_height": 2000,
                "blocks": [{
                  "box": [100, 200, 300, 600],
                  "vertical": true,
                  "lines": ["日本", "語"],
                  "lines_coords": [
                    [[200, 200], [300, 200], [300, 600], [200, 600]],
                    [[100, 200], [200, 200], [200, 600], [100, 600]]
                  ]
                }]
              }]
            }
            """);

        var regions = MangaMokuroParser.GetRegions(data, "001.jpg", 0);

        regions.Should().HaveCount(3);
        regions.Should().OnlyContain(region => region.Sentence == "日本語");
        regions.Select(region => region.Utf16Offset).Should().Equal(0, 1, 2);
        regions.Should().OnlyContain(region => region.IsVertical);
        regions[0].X.Should().BeApproximately(0.2, 0.0001);
        regions[0].Y.Should().BeApproximately(0.1, 0.0001);
    }

    [Fact]
    public void GetRegions_MatchesPageByFileNameBeforeIndex()
    {
        var data = Encoding.UTF8.GetBytes(
            """
            {
              "pages": [
                { "img_path": "001.jpg", "img_width": 100, "img_height": 100, "blocks": [] },
                {
                  "img_path": "nested/002.jpg",
                  "img_width": 100,
                  "img_height": 100,
                  "blocks": [{ "box": [0, 0, 100, 100], "vertical": false, "lines": ["二"] }]
                }
              ]
            }
            """);

        var regions = MangaMokuroParser.GetRegions(data, "other/002.jpg", 0);

        regions.Should().ContainSingle();
        regions[0].Sentence.Should().Be("二");
    }
}
