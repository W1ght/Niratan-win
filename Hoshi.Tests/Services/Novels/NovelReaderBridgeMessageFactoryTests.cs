using System.Text.Json;
using FluentAssertions;
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
}
