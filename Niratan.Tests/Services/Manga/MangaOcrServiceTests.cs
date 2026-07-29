using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Niratan.Models.Manga;
using Niratan.Services.Manga;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Manga;

public sealed class MangaOcrServiceTests
{
    [Fact]
    public async Task GetCachedRegionsAsync_CurrentManifest_RestoresCompletedPage()
    {
        using var temp = new TempDirectory();
        var key = new MangaOcrCacheKey("book", 0, "page-0", null);
        var itemDirectory = Path.Combine(
            temp.Path,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ItemId)))
                .ToLowerInvariant());
        Directory.CreateDirectory(itemDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(itemDirectory, "manifest.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 4,
                engineSignature = "google-lens-v3-ja-niratan-layout",
                itemId = key.ItemId,
                modifiedAt = (DateTimeOffset?)null,
                pageIdentities = new[] { key.PageIdentity },
            }),
            TestContext.Current.CancellationToken);
        var expected = new[]
        {
            new MangaTextRegion(
                "cached",
                0,
                "block",
                "line",
                "日本",
                0,
                true,
                0.1,
                0.2,
                0.1,
                0.2),
        };
        await File.WriteAllTextAsync(
            Path.Combine(itemDirectory, "000000.json"),
            JsonSerializer.Serialize(expected),
            TestContext.Current.CancellationToken);

        using var service = new MangaOcrService(
            temp.Path,
            NullLogger<MangaOcrService>.Instance);

        var cached = await service.GetCachedRegionsAsync(
            key,
            [key.PageIdentity],
            TestContext.Current.CancellationToken);

        cached.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task GetCachedRegionsAsync_OldLayoutManifest_InvalidatesCachedPage()
    {
        using var temp = new TempDirectory();
        var key = new MangaOcrCacheKey("book", 0, "page-0", null);
        var itemDirectory = Path.Combine(
            temp.Path,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ItemId)))
                .ToLowerInvariant());
        Directory.CreateDirectory(itemDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(itemDirectory, "manifest.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                engineSignature = "google-lens-v1-ja",
                itemId = key.ItemId,
                modifiedAt = (DateTimeOffset?)null,
                pageIdentities = new[] { key.PageIdentity },
            }),
            TestContext.Current.CancellationToken);
        var pagePath = Path.Combine(itemDirectory, "000000.json");
        await File.WriteAllTextAsync(
            pagePath,
            JsonSerializer.Serialize(new[]
            {
                new MangaTextRegion(
                    "old",
                    0,
                    "block",
                    "line",
                    "日本",
                    0,
                    true,
                    0.1,
                    0.8,
                    0.1,
                    0.1),
            }),
            TestContext.Current.CancellationToken);
        using var service = new MangaOcrService(
            temp.Path,
            NullLogger<MangaOcrService>.Instance);

        var cached = await service.GetCachedRegionsAsync(
            key,
            [key.PageIdentity],
            TestContext.Current.CancellationToken);

        cached.Should().BeNull();
        File.Exists(pagePath).Should().BeFalse();
        var manifest = await File.ReadAllTextAsync(
            Path.Combine(itemDirectory, "manifest.json"),
            TestContext.Current.CancellationToken);
        manifest.Should().Contain("\"schemaVersion\": 4");
        manifest.Should().Contain(
            "\"engineSignature\": \"google-lens-v3-ja-niratan-layout\"");
    }

    [Fact]
    public async Task GetCachedRegionsAsync_SplitAdjacentColumns_MergesWithoutReupload()
    {
        using var temp = new TempDirectory();
        var key = new MangaOcrCacheKey("book", 0, "page-0", null);
        var itemDirectory = Path.Combine(
            temp.Path,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ItemId)))
                .ToLowerInvariant());
        Directory.CreateDirectory(itemDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(itemDirectory, "manifest.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 4,
                engineSignature = "google-lens-v3-ja-niratan-layout",
                itemId = key.ItemId,
                modifiedAt = (DateTimeOffset?)null,
                pageIdentities = new[] { key.PageIdentity },
            }),
            TestContext.Current.CancellationToken);
        var cachedRegions = new[]
        {
            new MangaTextRegion(
                "right-0", 0, "right", "right-line", "あんたの", 0, true,
                0.83, 0.33, 0.02, 0.07),
            new MangaTextRegion(
                "middle-0", 0, "middle", "middle-line", "落とし物", 0, true,
                0.80, 0.33, 0.02, 0.07),
            new MangaTextRegion(
                "left-0", 0, "left", "left-line", "じゃないの?", 0, true,
                0.77, 0.33, 0.02, 0.10),
        };
        await File.WriteAllTextAsync(
            Path.Combine(itemDirectory, "000000.json"),
            JsonSerializer.Serialize(cachedRegions),
            TestContext.Current.CancellationToken);
        using var service = new MangaOcrService(
            temp.Path,
            NullLogger<MangaOcrService>.Instance);

        var cached = await service.GetCachedRegionsAsync(
            key,
            [key.PageIdentity],
            TestContext.Current.CancellationToken);

        cached.Should().NotBeNull();
        cached!.Select(region => region.BlockId).Distinct().Should().ContainSingle();
        cached.Select(region => region.Sentence).Distinct().Should()
            .Equal("あんたの落とし物じゃないの?");
        cached.Select(region => region.Utf16Offset).Should().Equal(0, 4, 8);
    }
}
