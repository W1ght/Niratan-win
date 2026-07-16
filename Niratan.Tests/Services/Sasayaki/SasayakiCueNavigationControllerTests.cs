using FluentAssertions;
using Niratan.Models.Sasayaki;
using Niratan.Services.Sasayaki;

namespace Niratan.Tests.Services.Sasayaki;

public sealed class SasayakiCueNavigationControllerTests
{
    [Fact]
    public void Navigation_UsesPortableMatchesAsThePlayableTimeline()
    {
        var controller = new SasayakiCueNavigationController();
        var matches = CreateMatches();
        controller.Load(new SasayakiMatchData { Matches = matches, Unmatched = 1 });

        controller.UpdatePosition(3.5);

        controller.CurrentCueIndex.Should().Be(1);
        controller.CurrentMatch.Should().BeSameAs(matches[1]);
        controller.GetMatchedCueIndexBefore(3.5).Should().Be(0);
        controller.GetMatchedCueIndexAfter(1.5).Should().Be(1);
    }

    [Fact]
    public void GetCueIndex_ResolvesAnEquivalentDeserializedMatch()
    {
        var controller = new SasayakiCueNavigationController();
        controller.Load(new SasayakiMatchData { Matches = CreateMatches() });
        var equivalent = new SasayakiMatch { Id = "2", StartTime = 3 };

        controller.GetCueIndex(equivalent).Should().Be(1);
    }

    private static List<SasayakiMatch> CreateMatches() =>
    [
        new SasayakiMatch
        {
            Id = "0",
            StartTime = 1,
            EndTime = 2,
            Text = "最初",
        },
        new SasayakiMatch
        {
            Id = "2",
            StartTime = 3,
            EndTime = 4,
            Text = "次",
        },
    ];
}
