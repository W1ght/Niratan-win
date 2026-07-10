using FluentAssertions;
using Hoshi.Models;
using Hoshi.Models.Novel;
using Hoshi.Services.Novels;
using Moq;

namespace Hoshi.Tests.Services.Novels;

public sealed class NovelShelfServiceTests
{
    [Fact]
    public async Task CreateRenameAndReorderShelves_PersistNiratanShape()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await ShelfFixture.CreateAsync(["a", "b"], ct);

        (await fixture.Service.CreateAsync("收藏", ct)).IsSuccess.Should().BeTrue();
        (await fixture.Service.CreateAsync("稍后", ct)).IsSuccess.Should().BeTrue();
        var duplicate = await fixture.Service.CreateAsync(" 收藏 ", ct);
        duplicate.IsSuccess.Should().BeFalse();
        (await fixture.Service.RenameAsync("稍后", "待读", ct)).IsSuccess.Should().BeTrue();
        var reordered = await fixture.Service.ReorderShelvesAsync(["待读", "收藏"], ct);

        reordered.IsSuccess.Should().BeTrue(reordered.Error);
        reordered.Value!.Shelves.Select(shelf => shelf.Name).Should().Equal("待读", "收藏");
        var saved = await fixture.Json.ReadAsync<List<NovelShelf>>(fixture.ShelvesPath, ct);
        saved.Status.Should().Be(NovelJsonReadStatus.Success);
        saved.Value!.Select(shelf => shelf.Name).Should().Equal("待读", "收藏");
    }

    [Fact]
    public async Task DeleteShelf_MakesBooksUnshelvedWithoutDeletingThem()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await ShelfFixture.CreateAsync(["a", "b"], ct);
        await fixture.Service.CreateAsync("收藏", ct);
        await fixture.Service.MoveBooksAsync(["a", "b"], "收藏", ct);

        var result = await fixture.Service.DeleteAsync("收藏", ct);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Shelves.Should().BeEmpty();
        result.Value.UnshelvedBookOrder.Should().ContainInOrder("a", "b");
        (await fixture.Storage.LoadAsync("a", ct)).Should().NotBeNull();
        (await fixture.Storage.LoadAsync("b", ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task MoveBooksAsync_RemovesBooksFromEveryOtherSectionBeforeAppending()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await ShelfFixture.CreateAsync(["a", "b", "c"], ct);
        await fixture.Service.CreateAsync("一", ct);
        await fixture.Service.CreateAsync("二", ct);
        await fixture.Service.MoveBooksAsync(["a", "b"], "一", ct);

        var moved = await fixture.Service.MoveBooksAsync(["b", "c"], "二", ct);

        moved.Value!.Shelves.Single(shelf => shelf.Name == "一").BookIds.Should().Equal("a");
        moved.Value.Shelves.Single(shelf => shelf.Name == "二").BookIds.Should().Equal("b", "c");
        moved.Value.UnshelvedBookOrder.Should().BeEmpty();

        var unshelved = await fixture.Service.MoveBooksAsync(["b"], null, ct);
        unshelved.Value!.Shelves.Single(shelf => shelf.Name == "二").BookIds.Should().Equal("c");
        unshelved.Value.UnshelvedBookOrder.Should().Equal("b");
    }

    [Fact]
    public async Task ReorderBookAsync_ReordersOnlyWithinRequestedSection()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await ShelfFixture.CreateAsync(["a", "b", "c", "d"], ct);
        await fixture.Service.CreateAsync("收藏", ct);
        await fixture.Service.MoveBooksAsync(["a", "b"], "收藏", ct);

        var shelfResult = await fixture.Service.ReorderBookAsync("b", "a", "收藏", ct);
        var unshelvedResult = await fixture.Service.ReorderBookAsync("d", "c", null, ct);

        shelfResult.Value!.Shelves.Single().BookIds.Should().Equal("b", "a");
        unshelvedResult.Value!.UnshelvedBookOrder.Should().Equal("d", "c");
    }

    [Fact]
    public async Task LoadAsync_CleansUnknownIdsAndAppendsNewImportsToBookOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await ShelfFixture.CreateAsync(["a", "b", "c"], ct);
        await fixture.Json.WriteAsync(
            fixture.ShelvesPath,
            new[] { new NovelShelf("收藏", new[] { "a", "missing" }) },
            ct);
        await fixture.Storage.SaveBookOrderAsync(["missing", "b"], ct);

        var result = await fixture.Service.LoadAsync(ct);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Shelves.Single().BookIds.Should().Equal("a");
        result.Value.UnshelvedBookOrder.Should().Equal("b", "c");
        (await fixture.Storage.LoadBookOrderAsync(ct)).Should().Equal("b", "a", "c");
    }

    [Fact]
    public async Task RemoveBookAsync_RemovesShelfAndGlobalOrderReferences()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await ShelfFixture.CreateAsync(["a", "b"], ct);
        await fixture.Service.CreateAsync("收藏", ct);
        await fixture.Service.MoveBooksAsync(["a"], "收藏", ct);

        var result = await fixture.Service.RemoveBookAsync("a", ct);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value!.Shelves.Single().BookIds.Should().BeEmpty();
        (await fixture.Storage.LoadBookOrderAsync(ct)).Should().Equal("b");
    }

    [Fact]
    public async Task Mutation_InvalidShelvesJsonReturnsFailureWithoutOverwritingSource()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await ShelfFixture.CreateAsync(["a"], ct);
        await File.WriteAllTextAsync(fixture.ShelvesPath, "{broken", ct);

        var result = await fixture.Service.CreateAsync("收藏", ct);

        result.IsSuccess.Should().BeFalse();
        (await File.ReadAllTextAsync(fixture.ShelvesPath, ct)).Should().Be("{broken");
    }

    [Fact]
    public async Task LoadAsync_CorruptBookMetadataDoesNotCleanShelfReferences()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await ShelfFixture.CreateAsync(["a"], ct);
        await fixture.Json.WriteAsync(
            fixture.ShelvesPath,
            new[] { new NovelShelf("收藏", new[] { "a" }) },
            ct);
        var originalShelves = await File.ReadAllTextAsync(fixture.ShelvesPath, ct);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Storage.ResolveRootPath("a"), "metadata.json"),
            "{broken",
            ct);

        var result = await fixture.Service.LoadAsync(ct);

        result.IsSuccess.Should().BeFalse();
        (await File.ReadAllTextAsync(fixture.ShelvesPath, ct)).Should().Be(originalShelves);
    }

    [Fact]
    public async Task Mutation_ReadOnlyRecoveryStateDoesNotWriteShelfFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var json = new NiratanJsonFileStore();
        var sidecars = new NovelBookSidecarService(json);
        var storage = new NovelBookStorageService(temp.Path, json, sidecars);
        var state = new Mock<INovelStorageAccessState>();
        state.SetupGet(value => value.IsReadOnly).Returns(true);
        state.SetupGet(value => value.ErrorMessage).Returns("repair first");
        var service = new NovelShelfService(temp.Path, storage, json, state.Object);

        var result = await service.CreateAsync("收藏", ct);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("repair first");
        File.Exists(Path.Combine(temp.Path, "shelves.json")).Should().BeFalse();
    }

    private sealed class ShelfFixture : IDisposable
    {
        private readonly TempDirectory _temp;

        private ShelfFixture(
            TempDirectory temp,
            NiratanJsonFileStore json,
            NovelBookStorageService storage,
            NovelShelfService service)
        {
            _temp = temp;
            Json = json;
            Storage = storage;
            Service = service;
        }

        public NiratanJsonFileStore Json { get; }
        public NovelBookStorageService Storage { get; }
        public NovelShelfService Service { get; }
        public string ShelvesPath => Path.Combine(_temp.Path, "shelves.json");

        public static async Task<ShelfFixture> CreateAsync(
            IReadOnlyList<string> bookIds,
            CancellationToken ct)
        {
            var temp = new TempDirectory();
            var json = new NiratanJsonFileStore();
            var sidecars = new NovelBookSidecarService(json);
            var storage = new NovelBookStorageService(temp.Path, json, sidecars);
            foreach (var id in bookIds)
            {
                await storage.SaveMetadataAsync(new NovelBook
                {
                    Id = id,
                    Title = id,
                    OriginalTitle = id,
                    Folder = id,
                    ImportedAt = DateTime.UtcNow,
                }, ct);
            }
            await storage.SaveBookOrderAsync(bookIds, ct);
            var accessState = new Mock<INovelStorageAccessState>();
            accessState.SetupGet(value => value.IsReadOnly).Returns(false);
            var service = new NovelShelfService(temp.Path, storage, json, accessState.Object);
            return new ShelfFixture(temp, json, storage, service);
        }

        public void Dispose() => _temp.Dispose();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                Guid.NewGuid().ToString("N"));
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
