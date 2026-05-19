using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public class NovelEpubImportServiceTests
{
    private static NovelEpubImportService CreateSut()
    {
        var parser = new EpubParserService(NullLogger<EpubParserService>.Instance);
        return new NovelEpubImportService(
            parser,
            NullLogger<NovelEpubImportService>.Instance
        );
    }

    [Fact]
    public async Task ImportAsync_ReturnsFailure_WhenFileIsNotEpub()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "not epub", ct);
        var sut = CreateSut();

        var result = await sut.ImportAsync(path, ct);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain(".epub");
    }

    [Fact]
    public async Task ImportAsync_ReturnsFailure_WhenEpubIsMissingContainerXml()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.epub");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var mimetype = archive.CreateEntry("mimetype");
            await using var mimeStream = mimetype.Open();
            await using var writer = new StreamWriter(mimeStream);
            await writer.WriteAsync("application/epub+zip");
        }

        var sut = CreateSut();

        var result = await sut.ImportAsync(path, ct);

        result.IsSuccess.Should().BeFalse();
        result.ErrorTitle.Should().Be("EPUB import failed");
    }
}
