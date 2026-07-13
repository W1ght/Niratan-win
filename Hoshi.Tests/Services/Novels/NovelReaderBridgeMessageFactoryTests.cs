using System.Text.Json;
using FluentAssertions;
using Hoshi.Models.Novel;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public class NovelReaderBridgeMessageFactoryTests
{
    [Fact]
    public void CreateSetChapterMessage_SerializesVersionTypeAndPayload()
    {
        var json = NovelReaderBridgeMessageFactory.CreateSetChapterMessage(2, 10);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("version").GetInt32().Should().Be(1);
        root.GetProperty("type").GetString().Should().Be("setChapter");

        var payload = root.GetProperty("payload");
        payload.GetProperty("index").GetInt32().Should().Be(2);
        payload.GetProperty("totalChapters").GetInt32().Should().Be(10);
    }

    [Fact]
    public void CreateSetChapterMessage_HandlesZeroIndex()
    {
        var json = NovelReaderBridgeMessageFactory.CreateSetChapterMessage(0, 1);

        using var document = JsonDocument.Parse(json);
        var payload = document.RootElement.GetProperty("payload");
        payload.GetProperty("index").GetInt32().Should().Be(0);
        payload.GetProperty("totalChapters").GetInt32().Should().Be(1);
    }

    [Fact]
    public void CreateRestoreProgressMessage_SerializesVersionTypeAndPayload()
    {
        var json = NovelReaderBridgeMessageFactory.CreateRestoreProgressMessage(0.5);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("version").GetInt32().Should().Be(1);
        root.GetProperty("type").GetString().Should().Be("restoreProgress");

        var payload = root.GetProperty("payload");
        payload.GetProperty("progress").GetDouble().Should().Be(0.5);
    }

    [Fact]
    public void CreateRestoreProgressMessage_HandlesBoundaryValues()
    {
        var zeroJson = NovelReaderBridgeMessageFactory.CreateRestoreProgressMessage(0);
        using var zeroDoc = JsonDocument.Parse(zeroJson);
        zeroDoc.RootElement.GetProperty("payload").GetProperty("progress").GetDouble()
            .Should().Be(0);

        var oneJson = NovelReaderBridgeMessageFactory.CreateRestoreProgressMessage(1);
        using var oneDoc = JsonDocument.Parse(oneJson);
        oneDoc.RootElement.GetProperty("payload").GetProperty("progress").GetDouble()
            .Should().Be(1);
    }

    [Fact]
    public void NavigationMessages_CarryOptionalGeneration()
    {
        using var chapter = JsonDocument.Parse(
            NovelReaderBridgeMessageFactory.CreateSetChapterMessage(2, 4, 0.75, 17));
        using var restore = JsonDocument.Parse(
            NovelReaderBridgeMessageFactory.CreateRestoreProgressMessage(0.5, 18));

        chapter.RootElement.GetProperty("payload")
            .GetProperty("navigationGeneration").GetInt64().Should().Be(17);
        restore.RootElement.GetProperty("payload")
            .GetProperty("navigationGeneration").GetInt64().Should().Be(18);
    }

    [Fact]
    public void CreateSetChapterMessage_UsesTypedEndTargetWithoutApproximateProgress()
    {
        using var chapter = JsonDocument.Parse(
            NovelReaderBridgeMessageFactory.CreateSetChapterMessage(
                1,
                3,
                progress: null,
                navigationGeneration: 21,
                restoreTarget: ReaderChapterRestoreTarget.End));

        var payload = chapter.RootElement.GetProperty("payload");
        payload.GetProperty("restoreTarget").GetString().Should().Be("end");
        payload.TryGetProperty("progress", out _).Should().BeFalse();
        payload.GetProperty("navigationGeneration").GetInt64().Should().Be(21);
    }

    [Fact]
    public void CreateSetChapterMessage_UsesImmutableNavigationRenderInstruction()
    {
        var render = new ReaderNavigationRenderRequest(
            22,
            new ReaderNavigationPositionSnapshot("book-1", 1, 0.4, 40, 100, 7),
            ReaderNavigationDestination.AtChapterEnd(2));

        var factory = typeof(NovelReaderBridgeMessageFactory).GetMethod(
            nameof(NovelReaderBridgeMessageFactory.CreateSetChapterMessage),
            [typeof(ReaderNavigationRenderRequest), typeof(int)]);
        factory.Should().NotBeNull();
        var json = (string)factory!.Invoke(null, [render, 4])!;
        using var chapter = JsonDocument.Parse(json);

        var payload = chapter.RootElement.GetProperty("payload");
        payload.GetProperty("index").GetInt32().Should().Be(2);
        payload.GetProperty("totalChapters").GetInt32().Should().Be(4);
        payload.GetProperty("restoreTarget").GetString().Should().Be("end");
        payload.TryGetProperty("progress", out _).Should().BeFalse();
        payload.GetProperty("navigationGeneration").GetInt64().Should().Be(22);
    }

    [Fact]
    public void CreateJumpToFragmentMessage_SerializesTypedDestination()
    {
        using var document = JsonDocument.Parse(
            NovelReaderBridgeMessageFactory.CreateJumpToFragmentMessage("section 2", 19));

        document.RootElement.GetProperty("type").GetString().Should().Be("jumpToFragment");
        var payload = document.RootElement.GetProperty("payload");
        payload.GetProperty("fragment").GetString().Should().Be("section 2");
        payload.GetProperty("navigationGeneration").GetInt64().Should().Be(19);
    }
}
