using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Niratan.Services.Novels;
using Niratan.Services.Sync;

namespace Niratan.Tests.Services.Sync;

public sealed class TtuBookDataConverterTests
{
    [Fact]
    public async Task ConvertToEpubAsync_CreatesParseableEpubFromTtuStaticData()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var bookDataPath = Path.Combine(temp.Path, "bookdata_1_6_20_2000_1000.zip");
        await CreateTtuBookDataAsync(bookDataPath, ct);
        var outputDirectory = Path.Combine(temp.Path, "output");
        var converter = new TtuBookDataConverter();

        var epubPath = await converter.ConvertToEpubAsync(bookDataPath, outputDirectory, ct);

        File.Exists(epubPath).Should().BeTrue();
        Path.GetExtension(epubPath).Should().Be(".epub");
        var parser = new EpubParserService(NullLogger<EpubParserService>.Instance);
        var parsed = parser.Parse(epubPath, Path.Combine(temp.Path, "parsed"));
        parsed.Title.Should().Be("星を読む");
        parsed.Chapters.Should().ContainSingle();
        parsed.Chapters[0].Href.Should().EndWith("chapter-0001.xhtml");
    }

    private static async Task CreateTtuBookDataAsync(
        string path,
        CancellationToken ct)
    {
        var staticData = new
        {
            title = "星を読む",
            styleSheet = "body { writing-mode: horizontal-tb; }",
            elementHtml = "<div id=\"ttu-chapter-1\"><p>星を読む。</p></div>",
            sections = new[]
            {
                new
                {
                    reference = "ttu-chapter-1",
                    charactersWeight = 20,
                    label = "第一章",
                    startCharacter = 0,
                    characters = 20,
                    parentChapter = (string?)null,
                },
            },
        };

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("staticdata.json");
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, staticData, cancellationToken: ct);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
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
