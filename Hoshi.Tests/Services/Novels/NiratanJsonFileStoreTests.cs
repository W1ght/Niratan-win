using FluentAssertions;
using Hoshi.Models.Novel;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public sealed class NiratanJsonFileStoreTests
{
    [Fact]
    public async Task ReadAsync_DistinguishesMissingFromInvalidJson()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var store = new NiratanJsonFileStore();
        var path = Path.Combine(temp.Path, "metadata.json");

        var missing = await store.ReadAsync<NovelBookMetadata>(path, ct);

        missing.Status.Should().Be(NovelJsonReadStatus.Missing);
        missing.Value.Should().BeNull();

        await File.WriteAllTextAsync(path, "{broken", ct);
        var invalid = await store.ReadAsync<NovelBookMetadata>(path, ct);

        invalid.Status.Should().Be(NovelJsonReadStatus.Invalid);
        invalid.Value.Should().BeNull();
        (await File.ReadAllTextAsync(path, ct)).Should().Be("{broken");
    }

    [Fact]
    public async Task WriteAsync_AtomicallyReplacesTargetAndRemovesTemporaryFile()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var store = new NiratanJsonFileStore();
        var path = Path.Combine(temp.Path, "metadata.json");
        await File.WriteAllTextAsync(path, "old", ct);
        var metadata = CreateMetadata("星");

        await store.WriteAsync(path, metadata, ct);

        (await store.ReadAsync<NovelBookMetadata>(path, ct)).Value
            .Should().BeEquivalentTo(metadata);
        Directory.EnumerateFiles(temp.Path, "*.tmp").Should().BeEmpty();
    }

    private static NovelBookMetadata CreateMetadata(string title) =>
        new(
            Id: "abc",
            Title: title,
            Epub: "abc.epub",
            Cover: "cover.jpg",
            Folder: "abc",
            LastAccess: new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero),
            RenamedTitle: "星・改",
            ProfileId: "default-ja",
            BookLanguage: "ja");

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
