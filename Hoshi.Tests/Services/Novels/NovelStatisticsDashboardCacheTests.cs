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

    private static NovelBook Book(string id, string path) =>
        new() { Id = id, Title = id, ExtractedPath = path };

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
