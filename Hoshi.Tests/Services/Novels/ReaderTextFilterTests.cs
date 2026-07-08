using FluentAssertions;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public sealed class ReaderTextFilterTests
{
    [Fact]
    public void CountReadableCharacters_FiltersChapterTextLikeAndroidBookInfo()
    {
        const string html = """
            <html>
            <head><title>Hidden Title</title></head>
            <body>
              <ruby>漢<rt>かん</rt></ruby>字<script>bad</script> A&nbsp;!𠮷
            </body>
            </html>
            """;

        ReaderTextFilter.CountReadableCharacters(html).Should().Be(4);
    }
}
