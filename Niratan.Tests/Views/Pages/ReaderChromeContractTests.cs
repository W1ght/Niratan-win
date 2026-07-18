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
    public void ReaderTopChrome_IsFixedFullWidthToolbarAboveReaderContent()
    {
        var xaml = ReadProjectFile("Views", "Pages", "NovelReaderPage.xaml");
        var document = XDocument.Parse(xaml);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var chrome = document.Descendants(presentation + "Border")
            .Single(element => (string?)element.Attribute(x + "Name") == "ReaderTopChrome");

        chrome.Attribute("Visibility").Should().BeNull();
        chrome.Attribute("HorizontalAlignment")?.Value.Should().Be("Stretch");
        chrome.Attribute("Background")?.Value.Should().Contain("SolidBackground");
        chrome.Attribute("BorderThickness")?.Value.Should().Be("0,0,0,1");
        chrome.Attribute("Tapped").Should().BeNull();

        var toolbarGrid = chrome.Element(presentation + "Grid");
        toolbarGrid.Should().NotBeNull();
        toolbarGrid!.Attribute("Height")?.Value.Should().Be("32");
        toolbarGrid.Attribute("Padding")?.Value.Should().Be("4,0,0,0");

        var rootGrid = document.Root!.Element(presentation + "Grid");
        rootGrid!.Attribute("RowDefinitions")?.Value.Should().Be("Auto,*,Auto");
    }

    [Fact]
    public void ReaderGallery_HasToolbarEntryUnreadBlurAndZoomViewer()
    {
        var xaml = ReadProjectFile("Views", "Pages", "NovelReaderPage.xaml");
        var code = ReadProjectFile("Views", "Pages", "NovelReaderPage.xaml.cs");
        var document = XDocument.Parse(xaml);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        xaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderGalleryButton\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderGalleryGrid\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderGalleryBlurUnreadToggle\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"NovelReaderImageViewer\"");
        xaml.Should().Contain("ZoomMode=\"Enabled\"");
        xaml.Should().Contain("MaxZoomFactor=\"5\"");

        var viewer = document.Descendants(presentation + "Grid")
            .Single(element => (string?)element.Attribute(x + "Name") == "ReaderImageViewerOverlay");
        viewer.Ancestors(presentation + "ContentDialog")
            .Single()
            .Attribute(x + "Name")?.Value.Should().Be("ReaderGalleryPanelDialog");
        xaml.Should().Contain("x:Name=\"ReaderGalleryPanelContent\"");
        xaml.Should().Contain("MaxWidth=\"10000\"");
        xaml.Should().Contain("x:Key=\"ContentDialogMaxHeight\">10000");
        xaml.Should().Contain("HorizontalScrollBarVisibility=\"Visible\"");
        xaml.Should().Contain("VerticalScrollBarVisibility=\"Visible\"");
        xaml.Should().Contain("Width=\"{Binding ViewportWidth, ElementName=ReaderImageViewerScrollViewer}\"");
        xaml.Should().Contain("Height=\"{Binding ViewportHeight, ElementName=ReaderImageViewerScrollViewer}\"");
        code.Should().Contain("UpdateReaderGalleryPanelSize(ActualWidth, ActualHeight)");
        code.Should().NotContain("ReaderGalleryPanelDialog.Hide();");
    }
}
