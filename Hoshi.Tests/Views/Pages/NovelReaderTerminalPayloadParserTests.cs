using System.Text.Json;
using FluentAssertions;
using Hoshi.Views.Pages;

namespace Hoshi.Tests.Views.Pages;

public sealed class NovelReaderTerminalPayloadParserTests
{
    [Theory]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":null,\"renderAttemptId\":11}}", 2, null, 11L)]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":7,\"renderAttemptId\":12}}", 2, 7L, 12L)]
    public void ChapterReady_ValidShapeParsesIntegerIdentity(
        string json,
        int expectedChapter,
        long? expectedGeneration,
        long expectedRenderAttemptId)
    {
        using var document = JsonDocument.Parse(json);

        NovelReaderTerminalPayloadParser.TryParseChapterReady(
                document.RootElement,
                out var payload)
            .Should().BeTrue();

        payload.Should().Be(new NovelReaderChapterReadyPayload(
            expectedChapter,
            expectedGeneration,
            expectedRenderAttemptId));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"payload\":null}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":null}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":-1,\"navigationGeneration\":null,\"renderAttemptId\":11}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":1.5,\"navigationGeneration\":null,\"renderAttemptId\":11}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":-1,\"renderAttemptId\":11}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":0,\"renderAttemptId\":11}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":1.5,\"renderAttemptId\":11}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":\"7\",\"renderAttemptId\":11}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":7,\"renderAttemptId\":0}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":7,\"renderAttemptId\":-1}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":7,\"renderAttemptId\":1.5}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":7,\"renderAttemptId\":\"11\"}}")]
    public void ChapterReady_MalformedShapeIsRejectedWithoutThrowing(string json)
    {
        using var document = JsonDocument.Parse(json);

        var action = () => NovelReaderTerminalPayloadParser.TryParseChapterReady(
            document.RootElement,
            out _);

        action.Should().NotThrow();
        action().Should().BeFalse();
    }

    [Fact]
    public void RestoreCompleted_ValidShapeParsesBoundedFiniteProgress()
    {
        using var document = JsonDocument.Parse(
            "{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":7,\"renderAttemptId\":13,\"progress\":0.625}}");

        NovelReaderTerminalPayloadParser.TryParseRestoreCompleted(
                document.RootElement,
                out var payload)
            .Should().BeTrue();

        payload.Should().Be(new NovelReaderRestoreCompletedPayload(2, 7, 13, 0.625));
    }

    [Fact]
    public void RestoreCompleted_OrdinaryRenderAcceptsExplicitNullGeneration()
    {
        using var document = JsonDocument.Parse(
            "{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":null,\"renderAttemptId\":14,\"progress\":0.625}}");

        NovelReaderTerminalPayloadParser.TryParseRestoreCompleted(
                document.RootElement,
                out var payload)
            .Should().BeTrue();

        ((long?)payload.NavigationGeneration).Should().BeNull();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"payload\":null}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":7,\"renderAttemptId\":13}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":7,\"progress\":0.5}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":-1,\"navigationGeneration\":7,\"renderAttemptId\":13,\"progress\":0.5}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":0,\"renderAttemptId\":13,\"progress\":0.5}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":7.5,\"renderAttemptId\":13,\"progress\":0.5}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":7,\"renderAttemptId\":0,\"progress\":0.5}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":7,\"renderAttemptId\":13.5,\"progress\":0.5}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":7,\"renderAttemptId\":13,\"progress\":-0.01}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":7,\"renderAttemptId\":13,\"progress\":1.01}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":7,\"renderAttemptId\":13,\"progress\":1e999}}")]
    [InlineData("{\"payload\":{\"chapterIndex\":2,\"navigationGeneration\":7,\"renderAttemptId\":13,\"progress\":\"0.5\"}}")]
    public void RestoreCompleted_MalformedOrUnboundedShapeIsRejectedWithoutThrowing(string json)
    {
        using var document = JsonDocument.Parse(json);

        var action = () => NovelReaderTerminalPayloadParser.TryParseRestoreCompleted(
            document.RootElement,
            out _);

        action.Should().NotThrow();
        action().Should().BeFalse();
    }
}
