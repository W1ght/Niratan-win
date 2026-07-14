using System.Text.Json;
using FluentAssertions;
using Niratan.Models.Novel;
using Niratan.Services.Novels;

namespace Niratan.Tests.Services.Novels;

public sealed class ReaderHighlightServiceTests
{
    [Fact]
    public async Task SaveAsync_WritesMacCompatibleHighlightsJsonAndLoadsItBack()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new ReaderHighlightService();
        var createdAt = new DateTimeOffset(2026, 6, 23, 12, 34, 56, TimeSpan.Zero);
        var highlight = new ReaderHighlight(
            Guid.Parse("2a8f87f4-9165-4f2c-8104-28f04a39f69f"),
            Character: 123,
            Offset: 45,
            Text: "漢字",
            Color: ReaderHighlightColor.Purple,
            CreatedAt: createdAt);

        await service.SaveAsync(temp.Path, [highlight], ct);

        var jsonPath = System.IO.Path.Combine(temp.Path, "highlights.json");
        var json = await File.ReadAllTextAsync(jsonPath, ct);
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement.EnumerateArray().Single();
        item.GetProperty("id").GetString().Should().Be(highlight.Id.ToString("D"));
        item.GetProperty("character").GetInt32().Should().Be(123);
        item.GetProperty("offset").GetInt32().Should().Be(45);
        item.GetProperty("text").GetString().Should().Be("漢字");
        item.GetProperty("color").GetString().Should().Be("purple");
        item.GetProperty("createdAt").ValueKind.Should().Be(JsonValueKind.Number);

        var loaded = await service.LoadAsync(temp.Path, ct);

        loaded.Should().ContainSingle().Which.Should().BeEquivalentTo(highlight);
    }

    [Fact]
    public async Task LoadAsync_ReturnsEmptyListWhenHighlightsFileIsMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new ReaderHighlightService();

        var loaded = await service.LoadAsync(temp.Path, ct);

        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_ReturnsEmptyListWhenHighlightsFileIsMalformed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temp.Path, "highlights.json"),
            "{ this is not valid json",
            ct);
        var service = new ReaderHighlightService();

        var loaded = await service.LoadAsync(temp.Path, ct);

        loaded.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_ReadsMacAbsoluteDateValues()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new ReaderHighlightService();
        var createdAt = new DateTimeOffset(2026, 6, 23, 12, 34, 56, TimeSpan.Zero);
        var macSeconds = (createdAt - ReaderHighlightService.MacAbsoluteDateReference).TotalSeconds;
        var json = $$"""
            [
              {
                "id": "2a8f87f4-9165-4f2c-8104-28f04a39f69f",
                "character": 10,
                "offset": 2,
                "text": "猫",
                "color": "yellow",
                "createdAt": {{macSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
              }
            ]
            """;
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "highlights.json"), json, ct);

        var loaded = await service.LoadAsync(temp.Path, ct);

        loaded.Should().ContainSingle().Which.CreatedAt.Should().Be(createdAt);
    }

    [Fact]
    public void CreateFromChapterSelection_AddsChapterStartToSelectionCharacterOffset()
    {
        var service = new ReaderHighlightService();
        var id = Guid.Parse("2a8f87f4-9165-4f2c-8104-28f04a39f69f");
        var createdAt = new DateTimeOffset(2026, 6, 23, 12, 34, 56, TimeSpan.Zero);

        var highlight = service.CreateFromChapterSelection(
            id,
            chapterIndex: 2,
            chapterCharacterOffset: 7,
            rawOffset: 11,
            text: "読む",
            ReaderHighlightColor.Green,
            createdAt,
            [10, 20, 30]);

        highlight.Should().Be(new ReaderHighlight(
            id,
            Character: 37,
            Offset: 11,
            Text: "読む",
            Color: ReaderHighlightColor.Green,
            CreatedAt: createdAt));
    }

    [Fact]
    public void GetChapterHighlights_ReturnsOnlyHighlightsInsideChapterRangeInReadingOrder()
    {
        var service = new ReaderHighlightService();
        var createdAt = new DateTimeOffset(2026, 6, 23, 12, 34, 56, TimeSpan.Zero);
        var highlights = new[]
        {
            HighlightAt(30, createdAt),
            HighlightAt(29, createdAt),
            HighlightAt(9, createdAt),
            HighlightAt(10, createdAt),
        };

        var chapterHighlights = service.GetChapterHighlights(highlights, [10, 20, 5], chapterIndex: 1);

        chapterHighlights.Select(h => h.Character).Should().Equal(10, 29);
    }

    [Fact]
    public void ResolveJumpTarget_MapsWholeBookCharacterToChapterProgress()
    {
        var service = new ReaderHighlightService();
        var createdAt = new DateTimeOffset(2026, 6, 23, 12, 34, 56, TimeSpan.Zero);
        var highlight = HighlightAt(125, createdAt);

        var target = service.ResolveJumpTarget(highlight, [100, 50]);

        target.Should().Be(new ReaderHighlightJumpTarget(ChapterIndex: 1, ChapterProgress: 0.5));
    }

    [Fact]
    public void ResolveJumpTarget_ClampsPastEndToLastReadableCharacter()
    {
        var service = new ReaderHighlightService();
        var createdAt = new DateTimeOffset(2026, 6, 23, 12, 34, 56, TimeSpan.Zero);
        var highlight = HighlightAt(150, createdAt);

        var target = service.ResolveJumpTarget(highlight, [100, 50]);

        target.Should().Be(new ReaderHighlightJumpTarget(ChapterIndex: 1, ChapterProgress: 49 / 50d));
    }

    private static ReaderHighlight HighlightAt(int character, DateTimeOffset createdAt) =>
        new(
            Guid.NewGuid(),
            Character: character,
            Offset: character,
            Text: character.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Color: ReaderHighlightColor.Yellow,
            CreatedAt: createdAt);

    private sealed class TempBookDirectory : IDisposable
    {
        public TempBookDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
