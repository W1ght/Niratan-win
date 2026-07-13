using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Hoshi.Messages;
using Hoshi.Models;
using Hoshi.Models.Novel;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public sealed class NovelStatisticsDashboardCacheTests
{
    [Fact]
    public async Task Cache_IsOrderStableAndInvalidatesWithoutTouchingBookFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var bookRoot = Path.Combine(temp.Path, "book");
        Directory.CreateDirectory(bookRoot);
        var statisticsPath = Path.Combine(bookRoot, "statistics.json");
        await File.WriteAllTextAsync(statisticsPath, "[]", ct);
        var books = new[] { Book("b", bookRoot), Book("a", bookRoot) };
        var today = new DateOnly(2026, 7, 11);
        var firstKey = NovelStatisticsDashboardCache.CreateKey(books, today);
        NovelStatisticsDashboardCache.CreateKey(books.Reverse().ToList(), today)
            .Should().Be(firstKey);
        var messenger = new WeakReferenceMessenger();
        var cachePath = Path.Combine(temp.Path, NovelStatisticsDashboardCache.FileName);
        var cache = new NovelStatisticsDashboardCache(
            new NiratanJsonFileStore(), messenger, cachePath);
        var snapshot = new NovelStatisticsDashboardSnapshot(today, today, [], [], []);

        await cache.StoreAsync(firstKey, snapshot, ct);
        (await cache.TryLoadAsync(firstKey, ct)).Should().Be(snapshot);
        messenger.Send(new NovelLibraryChangedMessage());

        File.Exists(cachePath).Should().BeFalse();
        File.Exists(statisticsPath).Should().BeTrue();
    }

    [Fact]
    public async Task StoredSnapshot_ReloadsFromDiskInNewCacheInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var cachePath = Path.Combine(temp.Path, NovelStatisticsDashboardCache.FileName);
        var today = new DateOnly(2026, 7, 11);
        var contribution = new NovelStatisticsBookContribution(
            "book-1",
            "かがみの孤城",
            "cover.jpg",
            153371,
            44387.1125397682,
            true);
        var snapshot = new NovelStatisticsDashboardSnapshot(
            today,
            today,
            [new NovelStatisticsDayAggregate(today, 153371, 44387.1125397682, [contribution])],
            [new NovelStatisticsBookRecord("book-1", "かがみの孤城", "cover.jpg", 248250)],
            []);
        var first = new NovelStatisticsDashboardCache(
            new NiratanJsonFileStore(),
            new WeakReferenceMessenger(),
            cachePath);
        await first.StoreAsync("key", snapshot, ct);

        var reloaded = new NovelStatisticsDashboardCache(
            new NiratanJsonFileStore(),
            new WeakReferenceMessenger(),
            cachePath);

        var loaded = await reloaded.TryLoadAsync("key", ct);

        loaded.Should().BeEquivalentTo(snapshot);
        loaded!.Days.Should().ContainSingle()
            .Which.BookContributions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(contribution);
    }

    [Fact]
    public async Task CorruptCache_DeletesOnlyDerivedCache()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var cachePath = Path.Combine(temp.Path, NovelStatisticsDashboardCache.FileName);
        await File.WriteAllTextAsync(cachePath, "{ broken", ct);
        var cache = new NovelStatisticsDashboardCache(
            new NiratanJsonFileStore(), new WeakReferenceMessenger(), cachePath);

        (await cache.TryLoadAsync("key", ct)).Should().BeNull();
        File.Exists(cachePath).Should().BeFalse();
    }

    [Fact]
    public async Task CacheHit_ReturnsImmediatelyThenPublishesFreshSidecars()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var today = new DateOnly(2026, 7, 11);
        var book = Book("book-1", temp.Path);
        var statistics = new NovelStatisticsSidecarService();
        await statistics.SaveAsync(
            temp.Path,
            [new NovelReadingStatistic("Book", "2026-07-11", 900, 600, 0, 0, 0, 0, 1)],
            ct);
        var cachePath = Path.Combine(temp.Path, NovelStatisticsDashboardCache.FileName);
        var cache = new NovelStatisticsDashboardCache(
            new NiratanJsonFileStore(), new WeakReferenceMessenger(), cachePath);
        var cacheKey = NovelStatisticsDashboardCache.CreateKey([book], today);
        var stale = new NovelStatisticsDashboardSnapshot(today, today, [], [], []);
        await cache.StoreAsync(cacheKey, stale, ct);
        var refreshed = new TaskCompletionSource<NovelStatisticsDashboardSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new NovelStatisticsDashboardService(
            statistics,
            new NovelBookSidecarService(),
            new FixedTimeProvider(),
            cache);
        service.SnapshotRefreshed += (_, snapshot) => refreshed.TrySetResult(snapshot);

        var immediate = await service.LoadSnapshotAsync([book], ct);
        var fresh = await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        immediate.Should().Be(stale);
        fresh.Days.Should().ContainSingle().Which.Characters.Should().Be(900);
        (await cache.TryLoadAsync(cacheKey, ct))!.Days.Should().ContainSingle()
            .Which.Characters.Should().Be(900);
    }

    private static NovelBook Book(string id, string path) =>
        new() { Id = id, Title = id, ExtractedPath = path };

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
