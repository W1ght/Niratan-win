using System.Xml.Linq;
using FluentAssertions;

namespace Niratan.Tests.Views.Pages;

public sealed class ReaderChromeContractTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));

    private static string ReadProjectFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([ProjectRoot, .. parts]));

    [Theory]
    [InlineData("NavigationPage.xaml")]
    [InlineData("InitializationErrorPage.xaml")]
    public void ShellTitleBar_IsEmptyAndThirtyTwoPixelsHigh(string fileName)
    {
        var xaml = ReadProjectFile("Views", "Pages", fileName);
        var document = XDocument.Parse(xaml);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var titleBar = document.Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(x + "Name") == "AppTitleBar");

        titleBar.Attribute("Height")?.Value.Should().Be("32");
        titleBar.Elements().Should().BeEmpty();
        xaml.Should().NotContain("GlobalSearchBox");
        xaml.Should().NotContain("Square44x44Logo");
        xaml.Should().NotContain("Text=\"Niratan\"");
    }

    [Fact]
    public void ReaderTopChrome_IsInitiallyHiddenFullWidthAcrylicOverlay()
    {
        var xaml = ReadProjectFile("Views", "Pages", "NovelReaderPage.xaml");
        var document = XDocument.Parse(xaml);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var chrome = document.Descendants(presentation + "Border")
            .Single(element => (string?)element.Attribute(x + "Name") == "ReaderTopChrome");

        chrome.Attribute("Visibility")?.Value.Should().Be("Collapsed");
        chrome.Attribute("HorizontalAlignment")?.Value.Should().Be("Stretch");
        chrome.Attribute("Background")?.Value.Should().Contain("Acrylic");
        chrome.Attribute("Tapped")?.Value.Should().Be("ReaderTopChrome_Tapped");
    }
}
