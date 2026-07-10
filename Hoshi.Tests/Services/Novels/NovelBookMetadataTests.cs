using System.Text.Json;
using FluentAssertions;
using Hoshi.Models.Novel;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public sealed class NovelBookMetadataTests
{
    [Fact]
    public async Task Metadata_RoundTripsNiratanFieldsAndMacAbsoluteDate()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new NiratanJsonFileStore();
            var path = Path.Combine(directory, "metadata.json");
            var value = new NovelBookMetadata(
                Id: "abc",
                Title: "星",
                Epub: "abc.epub",
                Cover: "cover.jpg",
                Folder: "abc",
                LastAccess: new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero),
                RenamedTitle: "星・改",
                ProfileId: "default-ja",
                BookLanguage: "ja");

            await store.WriteAsync(path, value, ct);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            var root = document.RootElement;
            root.GetProperty("id").GetString().Should().Be("abc");
            root.GetProperty("title").GetString().Should().Be("星");
            root.GetProperty("epub").GetString().Should().Be("abc.epub");
            root.GetProperty("cover").GetString().Should().Be("cover.jpg");
            root.GetProperty("folder").GetString().Should().Be("abc");
            root.GetProperty("lastAccess").ValueKind.Should().Be(JsonValueKind.Number);
            root.GetProperty("renamedTitle").GetString().Should().Be("星・改");
            root.GetProperty("profileId").GetString().Should().Be("default-ja");
            root.GetProperty("bookLanguage").GetString().Should().Be("ja");

            var result = await store.ReadAsync<NovelBookMetadata>(path, ct);
            result.Status.Should().Be(NovelJsonReadStatus.Success);
            result.Value.Should().BeEquivalentTo(value);
            value.DisplayTitle.Should().Be("星・改");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DisplayTitle_FallsBackToOriginalTitle()
    {
        var value = new NovelBookMetadata(
            "abc",
            "原題",
            null,
            null,
            "abc",
            DateTimeOffset.UnixEpoch);

        value.DisplayTitle.Should().Be("原題");
    }
}
