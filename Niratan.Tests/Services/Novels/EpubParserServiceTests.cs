using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Niratan.Services.Novels;

namespace Niratan.Tests.Services.Novels;

public sealed class EpubParserServiceTests
{
    [Fact]
    public async Task ParseExtracted_DoesNotRequireOriginalEpubFile()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var epubPath = Path.Combine(temp.Path, "source.epub");
        var extractedPath = Path.Combine(temp.Path, "extracted");
        await CreateMinimalEpubAsync(epubPath, ct);

        var sut = new EpubParserService(NullLogger<EpubParserService>.Instance);
        var imported = sut.Parse(epubPath, extractedPath);
        File.Delete(epubPath);

        var reopened = sut.ParseExtracted(extractedPath, "Fallback title");

        reopened.Title.Should().Be(imported.Title);
        reopened.Chapters.Should().ContainSingle();
        reopened.Chapters[0].Href.Should().EndWith(Path.Combine("OEBPS", "chapter1.xhtml"));
    }

    [Fact]
    public async Task Parse_PrefersEpub3CoverImagePropertyOverLegacyMetadataAndFirstImage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var epubPath = Path.Combine(temp.Path, "source.epub");
        var extractedPath = Path.Combine(temp.Path, "extracted");
        await CreateCoverSelectionEpubAsync(epubPath, ct);

        var sut = new EpubParserService(NullLogger<EpubParserService>.Instance);
        var book = sut.Parse(epubPath, extractedPath);

        book.CoverHref.Should().Be(Path.Combine(
            extractedPath,
            "OEBPS",
            "Images",
            "declared-cover.jpg"));
        book.Manifest["declared-cover"].Properties.Should().Be("scripted cover-image");
    }

    private static async Task CreateMinimalEpubAsync(string path, CancellationToken ct)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        await WriteEntryAsync(
            archive,
            "META-INF/container.xml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml" />
              </rootfiles>
            </container>
            """,
            ct);

        await WriteEntryAsync(
            archive,
            "OEBPS/content.opf",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <package version="3.0" xmlns="http://www.idpf.org/2007/opf" unique-identifier="book-id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="book-id">urn:uuid:test</dc:identifier>
                <dc:title>Readable after import</dc:title>
                <dc:language>ja</dc:language>
              </metadata>
              <manifest>
                <item id="chapter1" href="chapter1.xhtml" media-type="application/xhtml+xml" />
              </manifest>
              <spine>
                <itemref idref="chapter1" />
              </spine>
            </package>
            """,
            ct);

        await WriteEntryAsync(
            archive,
            "OEBPS/chapter1.xhtml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xml:lang="ja">
              <head><title>Chapter 1</title></head>
              <body><p>星を編む</p></body>
            </html>
            """,
            ct);
    }

    private static async Task CreateCoverSelectionEpubAsync(string path, CancellationToken ct)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        await WriteEntryAsync(
            archive,
            "META-INF/container.xml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml" />
              </rootfiles>
            </container>
            """,
            ct);

        await WriteEntryAsync(
            archive,
            "OEBPS/content.opf",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <package version="3.0" xmlns="http://www.idpf.org/2007/opf" unique-identifier="book-id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="book-id">urn:uuid:test</dc:identifier>
                <dc:title>Cover selection</dc:title>
                <dc:language>ja</dc:language>
                <meta name="cover" content="legacy-cover" />
              </metadata>
              <manifest>
                <item id="blank" href="Images/blank.jpg" media-type="image/jpeg" />
                <item id="legacy-cover" href="Images/legacy-cover.jpg" media-type="image/jpeg" />
                <item id="declared-cover" href="Images/declared-cover.jpg" media-type="image/jpeg" properties="scripted cover-image" />
                <item id="chapter1" href="chapter1.xhtml" media-type="application/xhtml+xml" />
              </manifest>
              <spine>
                <itemref idref="chapter1" />
              </spine>
            </package>
            """,
            ct);

        await WriteEntryAsync(archive, "OEBPS/Images/blank.jpg", "blank", ct);
        await WriteEntryAsync(archive, "OEBPS/Images/legacy-cover.jpg", "legacy", ct);
        await WriteEntryAsync(archive, "OEBPS/Images/declared-cover.jpg", "declared", ct);
        await WriteEntryAsync(
            archive,
            "OEBPS/chapter1.xhtml",
            "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body>読む</body></html>",
            ct);
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string entryName,
        string content,
        CancellationToken ct)
    {
        var entry = archive.CreateEntry(entryName);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content.AsMemory(), ct);
    }

    private sealed class TempBookDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"niratan-epub-parser-{Guid.NewGuid():N}");

        public TempBookDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
