using FluentAssertions;
using Niratan.Services.Novels;

namespace Niratan.Tests.Services.Novels;

public sealed class NovelExportFileNameTests
{
    [Theory]
    [InlineData("星の本", "星の本")]
    [InlineData("星?空.epub", "星_空")]
    [InlineData("星.epub.epub", "星")]
    [InlineData(".epub", "book")]
    [InlineData("   ", "book")]
    public void CreateBaseName_ReturnsSafeName(string title, string expected)
    {
        NovelExportFileName.CreateBaseName(title).Should().Be(expected);
    }
}
