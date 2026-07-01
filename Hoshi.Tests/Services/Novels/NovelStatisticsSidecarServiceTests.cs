using System.Text.Json;
using FluentAssertions;
using Hoshi.Models.Novel;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public sealed class NovelStatisticsSidecarServiceTests
{
    [Fact]
    public async Task SaveAsync_WritesMacCompatibleStatisticsJsonAndLoadsItBack()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new NovelStatisticsSidecarService();
        var statistic = new NovelReadingStatistic(
            Title: "Book One",
            DateKey: "2026-06-24",
            CharactersRead: 1234,
            ReadingTime: 456.7,
            MinReadingSpeed: 100,
            AltMinReadingSpeed: 120,
            LastReadingSpeed: 150,
            MaxReadingSpeed: 200,
            LastStatisticModified: 1780000000000);

        await service.SaveAsync(temp.Path, [statistic], ct);

        var json = await File.ReadAllTextAsync(Path.Combine(temp.Path, "statistics.json"), ct);
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement.EnumerateArray().Should().ContainSingle().Subject;
        item.GetProperty("title").GetString().Should().Be("Book One");
        item.GetProperty("dateKey").GetString().Should().Be("2026-06-24");
        item.GetProperty("charactersRead").GetInt32().Should().Be(1234);
        item.GetProperty("readingTime").GetDouble().Should().Be(456.7);
        item.GetProperty("minReadingSpeed").GetInt32().Should().Be(100);
        item.GetProperty("altMinReadingSpeed").GetInt32().Should().Be(120);
        item.GetProperty("lastReadingSpeed").GetInt32().Should().Be(150);
        item.GetProperty("maxReadingSpeed").GetInt32().Should().Be(200);
        item.GetProperty("lastStatisticModified").GetInt64().Should().Be(1780000000000);

        var loaded = await service.LoadAsync(temp.Path, ct);

        loaded.Should().ContainSingle().Which.Should().Be(statistic);
    }

    [Fact]
    public async Task LoadAsync_DeduplicatesDateKeysByLatestModifiedTimestamp()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new NovelStatisticsSidecarService();
        var older = Statistic(dateKey: "2026-06-24", charactersRead: 10, lastModified: 1);
        var newer = Statistic(dateKey: "2026-06-24", charactersRead: 20, lastModified: 2);
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "statistics.json"),
            JsonSerializer.Serialize(new[] { older, newer }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ct);

        var loaded = await service.LoadAsync(temp.Path, ct);

        loaded.Should().ContainSingle().Which.Should().Be(newer);
    }

    [Fact]
    public async Task LoadAsync_ReturnsEmptyListWhenStatisticsFileIsMissingOrMalformed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new NovelStatisticsSidecarService();

        (await service.LoadAsync(temp.Path, ct)).Should().BeEmpty();

        await File.WriteAllTextAsync(Path.Combine(temp.Path, "statistics.json"), "{ nope", ct);
        (await service.LoadAsync(temp.Path, ct)).Should().BeEmpty();
    }

    private static NovelReadingStatistic Statistic(
        string dateKey,
        int charactersRead,
        long lastModified) =>
        new(
            Title: "Book One",
            DateKey: dateKey,
            CharactersRead: charactersRead,
            ReadingTime: 1,
            MinReadingSpeed: 0,
            AltMinReadingSpeed: 0,
            LastReadingSpeed: 0,
            MaxReadingSpeed: 0,
            LastStatisticModified: lastModified);

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
