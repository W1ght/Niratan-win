using System.Text.Json;
using FluentAssertions;
using Niratan.Models.Novel;
using Niratan.Services.Novels;

namespace Niratan.Tests.Services.Novels;

public class NovelReaderBridgeMessageFactoryTests
{
    [Fact]
    public void CreateSetChapterMessage_SerializesVersionTypeAndPayload()
    {
        var json = NovelReaderBridgeMessageFactory.CreateSetChapterMessage(2, 10, 41);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("version").GetInt32().Should().Be(1);
        root.GetProperty("type").GetString().Should().Be("setChapter");

        var payload = root.GetProperty("payload");
        payload.GetProperty("index").GetInt32().Should().Be(2);
        payload.GetProperty("totalChapters").GetInt32().Should().Be(10);
        payload.GetProperty("renderAttemptId").GetInt64().Should().Be(41);
    }

    [Fact]
    public void CreateSetChapterMessage_HandlesZeroIndex()
    {
        var json = NovelReaderBridgeMessageFactory.CreateSetChapterMessage(0, 1, 42);

        using var document = JsonDocument.Parse(json);
        var payload = document.RootElement.GetProperty("payload");
        payload.GetProperty("index").GetInt32().Should().Be(0);
        payload.GetProperty("totalChapters").GetInt32().Should().Be(1);
    }

    [Fact]
    public void CreateRestoreProgressMessage_SerializesVersionTypeAndPayload()
    {
        var json = NovelReaderBridgeMessageFactory.CreateRestoreProgressMessage(0.5, 48);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        root.GetProperty("version").GetInt32().Should().Be(1);
        root.GetProperty("type").GetString().Should().Be("restoreProgress");

        var payload = root.GetProperty("payload");
        payload.GetProperty("progress").GetDouble().Should().Be(0.5);
        payload.GetProperty("renderAttemptId").GetInt64().Should().Be(48);
    }

    [Fact]
    public void CreateRestoreProgressMessage_HandlesBoundaryValues()
    {
        var zeroJson = NovelReaderBridgeMessageFactory.CreateRestoreProgressMessage(0, 49);
        using var zeroDoc = JsonDocument.Parse(zeroJson);
        zeroDoc.RootElement.GetProperty("payload").GetProperty("progress").GetDouble()
            .Should().Be(0);

        var oneJson = NovelReaderBridgeMessageFactory.CreateRestoreProgressMessage(1, 50);
        using var oneDoc = JsonDocument.Parse(oneJson);
        oneDoc.RootElement.GetProperty("payload").GetProperty("progress").GetDouble()
            .Should().Be(1);
    }

    [Fact]
    public void NavigationMessages_CarryOptionalGeneration()
    {
        using var chapter = JsonDocument.Parse(
            NovelReaderBridgeMessageFactory.CreateSetChapterMessage(2, 4, 43, 0.75, 17));
        using var restore = JsonDocument.Parse(
            NovelReaderBridgeMessageFactory.CreateRestoreProgressMessage(0.5, 46, 18));

        chapter.RootElement.GetProperty("payload")
            .GetProperty("navigationGeneration").GetInt64().Should().Be(17);
        restore.RootElement.GetProperty("payload")
            .GetProperty("navigationGeneration").GetInt64().Should().Be(18);
        restore.RootElement.GetProperty("payload")
            .GetProperty("renderAttemptId").GetInt64().Should().Be(46);
    }

    [Fact]
    public void CreateSetChapterMessage_UsesTypedEndTargetWithoutApproximateProgress()
    {
        using var chapter = JsonDocument.Parse(
            NovelReaderBridgeMessageFactory.CreateSetChapterMessage(
                1,
                3,
                renderAttemptId: 44,
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
            [typeof(ReaderNavigationRenderRequest), typeof(int), typeof(long)]);
        factory.Should().NotBeNull();
        var json = (string)factory!.Invoke(null, [render, 4, 45L])!;
        using var chapter = JsonDocument.Parse(json);

        var payload = chapter.RootElement.GetProperty("payload");
        payload.GetProperty("index").GetInt32().Should().Be(2);
        payload.GetProperty("totalChapters").GetInt32().Should().Be(4);
        payload.GetProperty("restoreTarget").GetString().Should().Be("end");
        payload.TryGetProperty("progress", out _).Should().BeFalse();
        payload.GetProperty("navigationGeneration").GetInt64().Should().Be(22);
        payload.GetProperty("renderAttemptId").GetInt64().Should().Be(45);
    }

    [Fact]
    public void CreateJumpToFragmentMessage_SerializesTypedDestination()
    {
        using var document = JsonDocument.Parse(
            NovelReaderBridgeMessageFactory.CreateJumpToFragmentMessage("section 2", 47, 19));

        document.RootElement.GetProperty("type").GetString().Should().Be("jumpToFragment");
        var payload = document.RootElement.GetProperty("payload");
        payload.GetProperty("fragment").GetString().Should().Be("section 2");
        payload.GetProperty("renderAttemptId").GetInt64().Should().Be(47);
        payload.GetProperty("navigationGeneration").GetInt64().Should().Be(19);
    }

    [Theory]
    [InlineData("forward")]
    [InlineData("backward")]
    public void CreateNavigatePageMessage_SerializesNarrowAuthorizedCommand(string direction)
    {
        using var document = JsonDocument.Parse(
            NovelReaderBridgeMessageFactory.CreateNavigatePageMessage(direction));

        document.RootElement.GetProperty("version").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("type").GetString().Should().Be("navigatePage");
        document.RootElement.GetProperty("payload").GetProperty("direction")
            .GetString().Should().Be(direction);
    }

    [Fact]
    public void CreateNavigatePageMessage_RejectsUnknownDirection()
    {
        var act = () => NovelReaderBridgeMessageFactory.CreateNavigatePageMessage("sideways");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateSetWheelNavigationMessage_SerializesTypedBooleanCommand(bool enabled)
    {
        using var document = JsonDocument.Parse(
            NovelReaderBridgeMessageFactory.CreateSetWheelNavigationMessage(enabled));

        document.RootElement.GetProperty("version").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("type").GetString()
            .Should().Be("setWheelNavigation");
        document.RootElement.GetProperty("payload").GetProperty("enabled")
            .GetBoolean().Should().Be(enabled);
    }

    [Fact]
    public void CreateSasayakiHighlightMessage_SerializesTypedPositionCommand()
    {
        using var document = JsonDocument.Parse(
            NovelReaderBridgeMessageFactory.CreateSasayakiHighlightMessage(
                generation: 7,
                startCodePoint: 12,
                length: 4,
                autoScroll: true,
                textColor: "#fff",
                backgroundColor: "#000"));

        document.RootElement.GetProperty("type").GetString()
            .Should().Be("highlightSasayakiCue");
        var payload = document.RootElement.GetProperty("payload");
        payload.GetProperty("generation").GetInt64().Should().Be(7);
        payload.GetProperty("startCodePoint").GetInt32().Should().Be(12);
        payload.GetProperty("length").GetInt32().Should().Be(4);
        payload.GetProperty("autoScroll").GetBoolean().Should().BeTrue();
        payload.GetProperty("textColor").GetString().Should().Be("#fff");
        payload.GetProperty("backgroundColor").GetString().Should().Be("#000");
    }

    [Fact]
    public void CreateSasayakiHighlightMessage_ConvertsWinUiArgbColorsToCssRgbaHex()
    {
        using var document = JsonDocument.Parse(
            NovelReaderBridgeMessageFactory.CreateSasayakiHighlightMessage(
                generation: 7,
                startCodePoint: 12,
                length: 4,
                autoScroll: true,
                textColor: "#FF112233",
                backgroundColor: "#6652C7FA"));

        var payload = document.RootElement.GetProperty("payload");
        payload.GetProperty("textColor").GetString().Should().Be("#112233FF");
        payload.GetProperty("backgroundColor").GetString().Should().Be("#52C7FA66");
    }

    [Fact]
    public void CreateClearSasayakiHighlightMessage_SerializesTypedCommand()
    {
        using var document = JsonDocument.Parse(
            NovelReaderBridgeMessageFactory.CreateClearSasayakiHighlightMessage());

        document.RootElement.GetProperty("type").GetString()
            .Should().Be("clearSasayakiHighlight");
        document.RootElement.GetProperty("payload").ValueKind
            .Should().Be(JsonValueKind.Object);
    }
}
