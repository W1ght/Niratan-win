using FluentAssertions;
using Hoshi.Models;
using Hoshi.Models.Novel;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public sealed class NovelBookStorageServiceTests
{
    [Fact]
    public async Task LoadSnapshotAsync_ScansMetadataAndProjectsBookmarkAndBookInfo()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "book-a")).FullName;
        var json = new NiratanJsonFileStore();
        var lastAccess = DateTimeOffset.Parse("2026-07-11T00:00:00Z");
        await json.WriteAsync(
            Path.Combine(root, "metadata.json"),
            new NovelBookMetadata(
                "a",
                "原題",
                "a.epub",
                "cover.jpg",
                "book-a",
                lastAccess,
                "表示名",
                "default-ja",
                "ja"),
            ct);
        var sidecars = new NovelBookSidecarService(json);
        await sidecars.SaveBookmarkAsync(
            root,
            new NovelBookmark(2, 0.25, 1234, lastAccess.AddHours(1)),
            ct);
        await sidecars.SaveBookInfoAsync(root, new NovelBookInfo(9000, []), ct);

        var service = new NovelBookStorageService(temp.Path, json, sidecars);
        var snapshot = await service.LoadSnapshotAsync(ct: ct);

        snapshot.CorruptMetadataPaths.Should().BeEmpty();
        var book = snapshot.Books.Should().ContainSingle().Subject;
        book.Id.Should().Be("a");
        book.Title.Should().Be("表示名");
        book.OriginalTitle.Should().Be("原題");
        book.RenamedTitle.Should().Be("表示名");
        book.Folder.Should().Be("book-a");
        book.FilePath.Should().Be(Path.Combine(root, "a.epub"));
        book.CoverPath.Should().Be(Path.Combine(root, "cover.jpg"));
        book.ExtractedPath.Should().Be(root);
        book.LastOpenedAt.Should().Be(lastAccess.UtcDateTime);
        book.CurrentChapterIndex.Should().Be(2);
        book.Progress.Should().Be(0.25);
        book.CurrentCharacterCount.Should().Be(1234);
        book.TotalCharacterCount.Should().Be(9000);
        book.ProfileId.Should().Be("default-ja");
        book.Language.Should().Be("ja");
    }

    [Fact]
    public async Task LoadSnapshotAsync_ReportsInvalidMetadataAndSkipsMissingMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var invalidRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "invalid")).FullName;
        Directory.CreateDirectory(Path.Combine(temp.Path, "missing"));
        var invalidPath = Path.Combine(invalidRoot, "metadata.json");
        await File.WriteAllTextAsync(invalidPath, "{broken", ct);
        var service = CreateService(temp.Path);

        var snapshot = await service.LoadSnapshotAsync(ct: ct);

        snapshot.Books.Should().BeEmpty();
        snapshot.CorruptMetadataPaths.Should().Equal(invalidPath);
        (await File.ReadAllTextAsync(invalidPath, ct)).Should().Be("{broken");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task LoadSnapshotAsync_ReportsMetadataMissingRequiredValues(string jsonText)
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "invalid")).FullName;
        var metadataPath = Path.Combine(root, "metadata.json");
        await File.WriteAllTextAsync(metadataPath, jsonText, ct);
        var service = CreateService(temp.Path);

        var snapshot = await service.LoadSnapshotAsync(ct: ct);

        snapshot.Books.Should().BeEmpty();
        snapshot.CorruptMetadataPaths.Should().Equal(metadataPath);
        (await File.ReadAllTextAsync(metadataPath, ct)).Should().Be(jsonText);
    }

    [Fact]
    public async Task LoadSnapshotAsync_RejectsMetadataPathsThatEscapeBookRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "book-a")).FullName;
        var json = new NiratanJsonFileStore();
        await json.WriteAsync(
            Path.Combine(root, "metadata.json"),
            new NovelBookMetadata(
                "a",
                "星",
                "../outside.epub",
                "../outside.jpg",
                "book-a",
                DateTimeOffset.UnixEpoch),
            ct);
        var service = new NovelBookStorageService(
            temp.Path,
            json,
            new NovelBookSidecarService(json));

        var book = (await service.LoadSnapshotAsync(ct: ct)).Books.Should().ContainSingle().Subject;

        book.FilePath.Should().BeEmpty();
        book.CoverPath.Should().BeNull();
    }

    [Fact]
    public async Task LoadSnapshotAsync_FiltersByOriginalAndDisplayTitle()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var json = new NiratanJsonFileStore();
        await WriteMetadataAsync(temp.Path, json, "a", "銀河鉄道の夜", "夜の列車", ct);
        await WriteMetadataAsync(temp.Path, json, "b", "雪国", null, ct);
        var service = new NovelBookStorageService(
            temp.Path,
            json,
            new NovelBookSidecarService(json));

        (await service.LoadSnapshotAsync("銀河", ct)).Books.Select(book => book.Id)
            .Should().Equal("a");
        (await service.LoadSnapshotAsync("列車", ct)).Books.Select(book => book.Id)
            .Should().Equal("a");
    }

    [Fact]
    public async Task BookOrder_RoundTripsAndInvalidJsonIsReportedAsStorageError()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var service = CreateService(temp.Path);

        (await service.LoadBookOrderAsync(ct)).Should().BeEmpty();
        await service.SaveBookOrderAsync(["b", "a"], ct);
        (await service.LoadBookOrderAsync(ct)).Should().Equal("b", "a");

        var path = Path.Combine(temp.Path, "book_order.json");
        await File.WriteAllTextAsync(path, "{broken", ct);
        var action = () => service.LoadBookOrderAsync(ct);

        await action.Should().ThrowAsync<InvalidDataException>();
        (await File.ReadAllTextAsync(path, ct)).Should().Be("{broken");
    }

    [Fact]
    public async Task SaveMetadataAsync_WritesMetadataForDomainBook()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var service = CreateService(temp.Path);
        var book = new NovelBook
        {
            Id = "a",
            Title = "表示名",
            OriginalTitle = "原題",
            RenamedTitle = "表示名",
            Folder = "a",
            FilePath = Path.Combine(temp.Path, "a", "a.epub"),
            CoverPath = Path.Combine(temp.Path, "a", "cover.jpg"),
            LastOpenedAt = new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc),
            ProfileId = "default-ja",
            Language = "ja",
        };

        await service.SaveMetadataAsync(book, ct);

        var stored = await new NiratanJsonFileStore().ReadAsync<NovelBookMetadata>(
            Path.Combine(temp.Path, "a", "metadata.json"),
            ct);
        stored.Status.Should().Be(NovelJsonReadStatus.Success);
        stored.Value.Should().BeEquivalentTo(new NovelBookMetadata(
            "a",
            "原題",
            "a.epub",
            "cover.jpg",
            "a",
            new DateTimeOffset(book.LastOpenedAt.Value),
            "表示名",
            "default-ja",
            "ja"));
    }

    [Fact]
    public async Task DeleteAsync_DeletesOnlyTheRequestedControlledBookDirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var bookRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "book-a")).FullName;
        var siblingRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "book-b")).FullName;
        var service = CreateService(temp.Path);

        await service.DeleteAsync("book-a", ct);

        Directory.Exists(bookRoot).Should().BeFalse();
        Directory.Exists(siblingRoot).Should().BeTrue();
        var traversal = () => service.DeleteAsync("..", ct);
        await traversal.Should().ThrowAsync<ArgumentException>();
    }

    private static NovelBookStorageService CreateService(string root)
    {
        var json = new NiratanJsonFileStore();
        return new NovelBookStorageService(root, json, new NovelBookSidecarService(json));
    }

    private static Task WriteMetadataAsync(
        string booksRoot,
        INiratanJsonFileStore json,
        string id,
        string title,
        string? renamedTitle,
        CancellationToken ct)
    {
        var root = Directory.CreateDirectory(Path.Combine(booksRoot, id)).FullName;
        return json.WriteAsync(
            Path.Combine(root, "metadata.json"),
            new NovelBookMetadata(
                id,
                title,
                $"{id}.epub",
                null,
                id,
                DateTimeOffset.UnixEpoch,
                renamedTitle),
            ct);
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
