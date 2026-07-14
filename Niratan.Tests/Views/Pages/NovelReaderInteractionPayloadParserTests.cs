using System.Text.Json;
using FluentAssertions;
using Niratan.Views.Pages;

namespace Niratan.Tests.Views.Pages;

public sealed class NovelReaderInteractionPayloadParserTests
{
    [Fact]
    public void ReaderBlankClick_ValidPayloadParsesCoordinatesAndViewport()
    {
        using var document = JsonDocument.Parse(
            """{"payload":{"x":12.5,"y":64,"viewportWidth":800,"viewportHeight":600}}""");

        NovelReaderInteractionPayloadParser.TryParseReaderBlankClick(
                document.RootElement,
                out var payload)
            .Should().BeTrue();

        payload.Should().Be(new NovelReaderBlankClickPayload(12.5, 64, 800, 600));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"payload\":null}")]
    [InlineData("{\"payload\":{\"x\":1,\"y\":2,\"viewportWidth\":800}}")]
    [InlineData("{\"payload\":{\"x\":\"1\",\"y\":2,\"viewportWidth\":800,\"viewportHeight\":600}}")]
    [InlineData("{\"payload\":{\"x\":-1,\"y\":2,\"viewportWidth\":800,\"viewportHeight\":600}}")]
    [InlineData("{\"payload\":{\"x\":1,\"y\":-2,\"viewportWidth\":800,\"viewportHeight\":600}}")]
    [InlineData("{\"payload\":{\"x\":801,\"y\":2,\"viewportWidth\":800,\"viewportHeight\":600}}")]
    [InlineData("{\"payload\":{\"x\":1,\"y\":601,\"viewportWidth\":800,\"viewportHeight\":600}}")]
    [InlineData("{\"payload\":{\"x\":1,\"y\":2,\"viewportWidth\":0,\"viewportHeight\":600}}")]
    [InlineData("{\"payload\":{\"x\":1,\"y\":2,\"viewportWidth\":800,\"viewportHeight\":0}}")]
    [InlineData("{\"payload\":{\"x\":1e999,\"y\":2,\"viewportWidth\":800,\"viewportHeight\":600}}")]
    public void ReaderBlankClick_InvalidPayloadIsRejected(string json)
    {
        using var document = JsonDocument.Parse(json);

        NovelReaderInteractionPayloadParser.TryParseReaderBlankClick(
                document.RootElement,
                out _)
            .Should().BeFalse();
    }
}
