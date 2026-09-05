using FluentAssertions;
using Niratan.Models.Games;

namespace Niratan.Tests.Models.Games;

public sealed class GalGameTextLineSourceLabelTests
{
    [Fact]
    public void KirikiriTextRenderSourceHasAnExplicitLabel()
    {
        new GalGameTextLine { SourceKind = 6 }
            .SourceLabel.Should().Be("KiriKiri TextRender");
    }
}
