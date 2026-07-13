using System.Text.Json;
using FluentAssertions;
using Hoshi.Models.Novel;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public sealed class NovelBookSidecarServiceTests
{
    [Fact]
    public async Task SaveBookmarkAsync_WritesMacCompatibleBookmarkJsonAndLoadsItBack()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new NovelBookSidecarService();
        var lastModified = new DateTimeOffset(2026, 6, 24, 1, 2, 3, TimeSpan.Zero);
        var bookmark = new NovelBookmark(
            ChapterIndex: 2,
            Progress: 0.375,
            CharacterCount: 1234,
            LastModified: lastModified);

        await service.SaveBookmarkAsync(temp.Path, bookmark, ct);

        var json = await File.ReadAllTextAsync(Path.Combine(temp.Path, "bookmark.json"), ct);
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("chapterIndex").GetInt32().Should().Be(2);
        document.RootElement.GetProperty("progress").GetDouble().Should().Be(0.375);
        document.RootElement.GetProperty("characterCount").GetInt32().Should().Be(1234);
        document.RootElement.GetProperty("lastModified").ValueKind.Should().Be(JsonValueKind.Number);

        var loaded = await service.LoadBookmarkAsync(temp.Path, ct);

        loaded.Should().BeEquivalentTo(bookmark);
    }

    [Fact]
    public async Task SaveBookmarkAsync_NormalizesLastModifiedToRemoteMillisecondPrecision()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new NovelBookSidecarService();
        var millisecond = DateTimeOffset.FromUnixTimeMilliseconds(1000);
        var bookmark = new NovelBookmark(
            ChapterIndex: 0,
            Progress: 0.25,
            CharacterCount: 25,
            LastModified: millisecond.AddTicks(TimeSpan.TicksPerMillisecond - 1));

        await service.SaveBookmarkAsync(temp.Path, bookmark, ct);

        var loaded = await service.LoadBookmarkAsync(temp.Path, ct);
        loaded!.LastModified!.Value.ToUnixTimeMilliseconds()
            .Should().Be(millisecond.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task SaveBookInfoAsync_WritesMacCompatibleChapterInfoFromSpineOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new NovelBookSidecarService();
        var chapters = new[]
        {
            new EpubChapter { Href = Path.Combine(temp.Path, "OEBPS", "chapter1.xhtml"), SpineIndex = 0 },
            new EpubChapter { Href = Path.Combine(temp.Path, "OEBPS", "chapter2.xhtml"), SpineIndex = 1 },
        };

        var bookInfo = service.CreateBookInfo(
            chapters,
            chapterCharacterCounts: [100, 50],
            containerDirectory: Path.Combine(temp.Path, "OEBPS"));
        await service.SaveBookInfoAsync(temp.Path, bookInfo, ct);

        var json = await File.ReadAllTextAsync(Path.Combine(temp.Path, "bookinfo.json"), ct);
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("characterCount").GetInt32().Should().Be(150);
        var chapterInfo = document.RootElement.GetProperty("chapterInfo");
        var first = chapterInfo.GetProperty("chapter1.xhtml");
        first.GetProperty("spineIndex").GetInt32().Should().Be(0);
        first.GetProperty("currentTotal").GetInt32().Should().Be(0);
        first.GetProperty("chapterCount").GetInt32().Should().Be(100);
        var second = chapterInfo.GetProperty("chapter2.xhtml");
        second.GetProperty("spineIndex").GetInt32().Should().Be(1);
        second.GetProperty("currentTotal").GetInt32().Should().Be(100);
        second.GetProperty("chapterCount").GetInt32().Should().Be(50);

        var loaded = await service.LoadBookInfoAsync(temp.Path, ct);

        loaded.Should().BeEquivalentTo(bookInfo);
    }

    [Fact]
    public async Task LoadBookmarkAsync_ReturnsNullWhenFileIsMissingOrMalformed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new NovelBookSidecarService();

        (await service.LoadBookmarkAsync(temp.Path, ct)).Should().BeNull();

        await File.WriteAllTextAsync(Path.Combine(temp.Path, "bookmark.json"), "{ nope", ct);
        (await service.LoadBookmarkAsync(temp.Path, ct)).Should().BeNull();
    }

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
