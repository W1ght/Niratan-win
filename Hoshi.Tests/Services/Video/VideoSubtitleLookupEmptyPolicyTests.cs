using FluentAssertions;
using Hoshi.Services.Video;
using System.Text.Json;

namespace Hoshi.Tests.Services.Video;

public class VideoSubtitleLookupEmptyPolicyTests
{
    [Fact]
    public void CanvasHoverMiss_PreservesCommittedLookup()
    {
        var policy = VideoSubtitleLookupEmptyPolicy.FromCanvasLookup(isHoverLookup: true);

        policy.DismissOnEmpty.Should().BeFalse();
        policy.IsHover.Should().BeTrue();
    }

    [Fact]
    public void CanvasClickMiss_DismissesCommittedLookup()
    {
        var policy = VideoSubtitleLookupEmptyPolicy.FromCanvasLookup(isHoverLookup: false);

        policy.DismissOnEmpty.Should().BeTrue();
        policy.IsHover.Should().BeFalse();
    }

    [Fact]
    public void WebHoverEmpty_PreservesWhileExplicitClickEmptyDismisses()
    {
        using var hoverDocument = JsonDocument.Parse(
            """{"dismissOnEmpty":false,"isHover":true}""");
        using var clickDocument = JsonDocument.Parse(
            """{"dismissOnEmpty":true,"isHover":false}""");

        var hover = VideoSubtitleLookupEmptyPolicy.FromWebPayload(
            hoverDocument.RootElement);
        var click = VideoSubtitleLookupEmptyPolicy.FromWebPayload(
            clickDocument.RootElement);

        hover.DismissOnEmpty.Should().BeFalse();
        hover.IsHover.Should().BeTrue();
        click.DismissOnEmpty.Should().BeTrue();
        click.IsHover.Should().BeFalse();
    }

    [Fact]
    public void LegacySourceLessEmpty_FailsClosedToPreserve()
    {
        var policy = VideoSubtitleLookupEmptyPolicy.FromWebPayload(default);

        policy.DismissOnEmpty.Should().BeFalse();
    }
}
