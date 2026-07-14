using System.Xml.Linq;
using FluentAssertions;
using Niratan.Services.Novels;

namespace Niratan.Tests.Services.Novels;

public sealed class EpubActiveContentSanitizerTests
{
    private static readonly string FixturePath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "Niratan.Tests", "Fixtures", "Novels", "malicious-chapter.xhtml"));

    [Fact]
    public void Sanitize_RemovesExecutableElementsHandlersAndScriptSchemesWhilePreservingText()
    {
        var malicious = File.ReadAllText(FixturePath);

        var sanitized = EpubActiveContentSanitizer.Sanitize(malicious);
        var document = XDocument.Parse(sanitized);
        var elements = document.Descendants().ToArray();

        elements.Select(element => element.Name.LocalName.ToLowerInvariant())
            .Should().NotContain(["script", "iframe", "frame", "object", "embed", "applet", "base"]);
        elements.Where(element =>
                element.Name.LocalName.Equals("meta", StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    element.Attribute("http-equiv") is { } attribute ? attribute.Value : null,
                    "refresh",
                    StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty();
        elements.SelectMany(element => element.Attributes())
            .Should().NotContain(attribute =>
                attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                || attribute.Name.LocalName.Equals("base", StringComparison.OrdinalIgnoreCase)
                || EpubActiveContentSanitizer.IsDangerousUrl(attribute.Value));
        sanitized.Should().Contain("安全な本文");
        sanitized.Should().Contain("safe svg text");
    }

    [Fact]
    public void Sanitize_RemovesDangerousNamespacedUrlsAcrossSvgMathMlAndXmlBase()
    {
        var malicious = File.ReadAllText(FixturePath);

        var sanitized = EpubActiveContentSanitizer.Sanitize(malicious);
        var document = XDocument.Parse(sanitized);
        var attributes = document.Descendants().Attributes().ToArray();

        attributes.Should().NotContain(attribute =>
            attribute.Name.LocalName.Equals("base", StringComparison.OrdinalIgnoreCase));
        attributes.Where(attribute =>
                attribute.Name.LocalName is "href" or "src")
            .Should().NotContain(attribute =>
                EpubActiveContentSanitizer.IsDangerousUrl(attribute.Value));
        sanitized.Should().Contain("SVG alias");
        sanitized.Should().Contain("Math alias");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("java\r\nscript:alert(1)")]
    [InlineData("  JAVASCRIPT:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("data:application/javascript,alert(1)")]
    [InlineData("cover.png 1x, java\nscript:alert(1) 2x")]
    public void IsDangerousUrl_RejectsObfuscatedActiveSchemes(string value)
    {
        EpubActiveContentSanitizer.IsDangerousUrl(value).Should().BeTrue();
    }

    [Fact]
    public void ReaderHostPolicy_UsesNoScriptCspAndRejectsExternalFramesAndForgedSources()
    {
        ReaderWebContentPolicy.ChapterResponseHeaders.Should().Contain("script-src 'none'");
        ReaderWebContentPolicy.ChapterResponseHeaders.Should().Contain("object-src 'none'");
        ReaderWebContentPolicy.ChapterResponseHeaders.Should().Contain("frame-src 'none'");
        ReaderWebContentPolicy.IsAllowedTopLevelNavigation(
            "https://niratan-novel-book.local/Text/chapter.xhtml").Should().BeTrue();
        ReaderWebContentPolicy.IsAllowedTopLevelNavigation(
            "https://attacker.invalid/chapter.xhtml").Should().BeFalse();
        ReaderWebContentPolicy.IsAllowedFrameNavigation(
            "https://niratan-novel-book.local/Text/frame.xhtml").Should().BeFalse();
        ReaderWebContentPolicy.IsTrustedWebMessageSource(
            "https://niratan-novel-book.local/Text/chapter.xhtml",
            "https://niratan-novel-book.local/Text/chapter.xhtml").Should().BeTrue();
        ReaderWebContentPolicy.IsTrustedWebMessageSource(
            "https://niratan-novel-book.local/Text/forged.xhtml",
            "https://niratan-novel-book.local/Text/chapter.xhtml").Should().BeFalse();
    }

    [Theory]
    [InlineData("application/xhtml+xml")]
    [InlineData("application/xhtml+xml; charset=utf-8")]
    [InlineData("text/html")]
    public void ReaderHostPolicy_RecognizesHtmlByManifestMediaType(string mediaType)
    {
        ReaderWebContentPolicy.IsHtmlMediaType(mediaType).Should().BeTrue();
    }

    [Fact]
    public void ReaderPageAndBridge_ApplySanitizerPolicyAndKeepAuthorizedNavigationInsideClosure()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(
            FixturePath,
            "..", "..", "..", ".."));
        var page = File.ReadAllText(Path.Combine(
            projectRoot,
            "Niratan", "Views", "Pages", "NovelReaderPage.xaml.cs"));
        var bridge = File.ReadAllText(Path.Combine(
            projectRoot,
            "Niratan", "Web", "NovelReader", "reader-bridge.js"));

        page.Should().Contain("EpubActiveContentSanitizer.Sanitize(html)");
        page.Should().Contain("ReaderWebContentPolicy.IsHtmlMediaType(mediaType)");
        page.Should().Contain("CreateBlockedWebResourceResponse(sender)");
        page.Should().Contain("ReaderWebContentPolicy.ChapterResponseHeaders");
        page.Should().Contain("ReaderWebContentPolicy.IsTrustedWebMessageSource(");
        page.Should().Contain("args.Source");
        page.Should().Contain("OnReaderNavigationStarting");
        page.Should().Contain("OnReaderFrameNavigationStarting");
        page.Should().Contain("OnReaderNewWindowRequested");
        page.Should().Contain("CoreWebView2HostResourceAccessKind.DenyCors");
        page.Should().Contain("NovelReaderBridgeMessageFactory.CreateNavigatePageMessage(");
        bridge.Should().Contain("case \"navigatePage\"");
        bridge.Should().Contain("await handleNavigate(authorizedDirection)");
        bridge.Should().NotContain("window.niratanReaderNavigateAuthorized");
    }
}
