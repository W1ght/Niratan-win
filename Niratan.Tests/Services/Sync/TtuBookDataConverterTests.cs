using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Niratan.Models;
using Niratan.Models.Novel;
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

    [Fact]
    public async Task ConvertFromEpubAsync_CreatesTtuBookDataThatRoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var sourceEpub = Path.Combine(temp.Path, "source.epub");
        await CreateEpubAsync(sourceEpub, ct);
        var bookRoot = Path.Combine(temp.Path, "book");
        Directory.CreateDirectory(bookRoot);
        await File.WriteAllTextAsync(
            Path.Combine(bookRoot, NovelBookSidecarService.BookInfoFileName),
            JsonSerializer.Serialize(new NovelBookInfo(
                5,
                new Dictionary<string, NovelBookInfoChapter>
                {
                    ["chapter.xhtml"] = new(0, 0, 5),
                })),
            ct);
        var book = new NovelBook
        {
            Id = "book-1",
            Title = "表示名",
            OriginalTitle = "往復テスト",
            FilePath = sourceEpub,
            ExtractedPath = bookRoot,
            ImportedAt = DateTime.UtcNow,
        };
        var converter = new TtuBookDataConverter();
        var output = Path.Combine(temp.Path, "output");

        var bookData = await converter.ConvertFromEpubAsync(book, output, ct);

        (await converter.ReadTitleAsync(bookData, ct)).Should().Be("往復テスト");
        using (var archive = ZipFile.OpenRead(bookData))
        {
            archive.GetEntry("staticdata.json").Should().NotBeNull();
            archive.GetEntry("blobs/image.png").Should().NotBeNull();
        }
        var roundTripEpub = await converter.ConvertToEpubAsync(
            bookData,
            Path.Combine(temp.Path, "roundtrip"),
            ct);
        var parsed = new EpubParserService(NullLogger<EpubParserService>.Instance)
            .Parse(roundTripEpub, Path.Combine(temp.Path, "parsed-roundtrip"));
        parsed.Title.Should().Be("往復テスト");
        parsed.Chapters.Should().ContainSingle();
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

    private static async Task CreateEpubAsync(string path, CancellationToken ct)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        await WriteEntryAsync(archive, "mimetype", "application/epub+zip", ct);
        await WriteEntryAsync(
            archive,
            "META-INF/container.xml",
            "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"item/book.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>",
            ct);
        await WriteEntryAsync(
            archive,
            "item/book.opf",
            "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>往復テスト</dc:title></metadata><manifest><item id=\"chapter\" href=\"chapter.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"style\" href=\"style.css\" media-type=\"text/css\"/><item id=\"image\" href=\"image.png\" media-type=\"image/png\"/></manifest><spine><itemref idref=\"chapter\"/></spine></package>",
            ct);
        await WriteEntryAsync(
            archive,
            "item/chapter.xhtml",
            "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>星を読む。</p><img src=\"image.png\"/></body></html>",
            ct);
        await WriteEntryAsync(archive, "item/style.css", "body { color: black; }", ct);
        var image = archive.CreateEntry("item/image.png");
        await using var imageStream = image.Open();
        await imageStream.WriteAsync(new byte[] { 1, 2, 3 }, ct);
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string name,
        string content,
        CancellationToken ct)
    {
        var entry = archive.CreateEntry(name);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content.AsMemory(), ct);
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
