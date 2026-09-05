using FluentAssertions;
using System.Xml.Linq;

namespace Niratan.Tests.Views.Pages;

public sealed class VideoCardStyleAssetTests
{
    private const string StandardTitleStyle = "{StaticResource VideoCardTitleTextBlockStyle}";
    private const string SeasonTitleStyle = "{StaticResource VideoSeasonCardTitleTextBlockStyle}";
    private const string MetadataStyle = "{StaticResource VideoCardMetadataTextBlockStyle}";
    private const string AccentMetadataStyle = "{StaticResource VideoCardAccentMetadataTextBlockStyle}";

    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "Niratan"));

    [Fact]
    public void App_MergesTheSharedVideoCardResourceDictionary()
    {
        var document = LoadProjectXaml("App.xaml");

        document.Descendants()
            .Where(element => element.Name.LocalName == "ResourceDictionary")
            .Should()
            .ContainSingle(element =>
                (string?)element.Attribute("Source") == "Styles/VideoCardStyles.xaml");
    }

    [Fact]
    public void SharedVideoCardStyles_FixEveryCardAndTitleHeight()
    {
        var document = LoadProjectXaml("Styles", "VideoCardStyles.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        AssertDoubleResource(document, x, "VideoLandscapeCardHeight", "320");
        AssertDoubleResource(document, x, "VideoPortraitCardHeight", "360");
        AssertDoubleResource(document, x, "VideoPortraitCardSlotHeight", "378");
        AssertDoubleResource(document, x, "VideoSeasonCardHeight", "308");
        AssertDoubleResource(document, x, "VideoDiscoveryCardHeight", "350");
        AssertDoubleResource(document, x, "VideoCardTitleHeight", "48");
        AssertDoubleResource(document, x, "VideoCardMetadataHeight", "24");
        AssertDoubleResource(document, x, "VideoSeasonCardTitleHeight", "72");

        AssertStyleSetter(
            GetStyle(document, x, "VideoLandscapeCardButtonStyle"),
            "Height",
            "{StaticResource VideoLandscapeCardHeight}");
        AssertStyleSetter(
            GetStyle(document, x, "VideoPortraitCardButtonStyle"),
            "Height",
            "{StaticResource VideoPortraitCardHeight}");
        AssertStyleSetter(
            GetStyle(document, x, "VideoPortraitCardButtonStyle"),
            "Margin",
            "0,0,14,18");
        AssertStyleSetter(
            GetStyle(document, x, "VideoPortraitCardBorderStyle"),
            "Height",
            "{StaticResource VideoPortraitCardHeight}");
        AssertStyleSetter(
            GetStyle(document, x, "VideoSeasonCardButtonStyle"),
            "Height",
            "{StaticResource VideoSeasonCardHeight}");
        AssertStyleSetter(
            GetStyle(document, x, "VideoDiscoveryCardButtonStyle"),
            "Height",
            "{StaticResource VideoDiscoveryCardHeight}");

        var standardTitle = GetStyle(document, x, "VideoCardTitleTextBlockStyle");
        AssertStyleSetter(standardTitle, "Height", "{StaticResource VideoCardTitleHeight}");
        AssertStyleSetter(standardTitle, "MaxLines", "2");
        AssertStyleSetter(standardTitle, "TextWrapping", "Wrap");

        var metadata = GetStyle(document, x, "VideoCardMetadataTextBlockStyle");
        AssertStyleSetter(metadata, "Height", "{StaticResource VideoCardMetadataHeight}");
        AssertStyleSetter(metadata, "MaxLines", "1");
        AssertStyleSetter(metadata, "TextTrimming", "CharacterEllipsis");

        var seasonTitle = GetStyle(document, x, "VideoSeasonCardTitleTextBlockStyle");
        seasonTitle.Attribute("BasedOn")?.Value.Should().Be(StandardTitleStyle);
        AssertStyleSetter(seasonTitle, "Height", "{StaticResource VideoSeasonCardTitleHeight}");
        AssertStyleSetter(seasonTitle, "MaxLines", "3");

        AssertStyleSetter(
            GetStyle(document, x, "VideoEpisodeArtworkImageStyle"),
            "Stretch",
            "Uniform");
    }

    [Fact]
    public void SeasonCardStyle_UsesTheSameHoverBehaviorAsOtherVideoCards()
    {
        var document = LoadProjectXaml("Styles", "VideoCardStyles.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var baseStyle = GetStyle(document, x, "VideoCardButtonBaseStyle");
        var seasonStyle = GetStyle(document, x, "VideoSeasonCardButtonStyle");

        seasonStyle.Attribute("TargetType")?.Value.Should().Be("Button");
        seasonStyle.Attribute("BasedOn")?.Value
            .Should().Be("{StaticResource VideoCardButtonBaseStyle}");
        seasonStyle.Descendants()
            .Should().NotContain(element => element.Name.LocalName == "ControlTemplate");
        AssertStyleSetter(baseStyle, "Background", "Transparent");
        AssertStyleSetter(baseStyle, "BorderThickness", "0");
    }

    [Fact]
    public void VideoLibraryPage_UsesSharedStylesForEveryLandscapeAndPortraitCard()
    {
        var document = LoadProjectXaml("Views", "Pages", "VideoLibraryPage.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var landscapeTemplate = GetDataTemplateByKey(document, x, "VideoPosterItemTemplate");
        var landscapeButton = landscapeTemplate.Descendants()
            .Single(element => element.Name.LocalName == "Button");
        landscapeButton.Attribute("Style")?.Value
            .Should().Be("{StaticResource VideoLandscapeCardButtonStyle}");
        AssertBoundTextStyle(landscapeButton, "Video.Title", StandardTitleStyle);
        AssertBoundTextStyle(landscapeButton, "FolderName", MetadataStyle);
        AssertBoundTextStyle(landscapeButton, "WatchStatusText", MetadataStyle);

        var seriesTemplate = GetDataTemplateByType(document, x, "vmc:VideoSeriesViewModel");
        var seriesButton = seriesTemplate.Descendants()
            .Single(element => element.Name.LocalName == "Button");
        seriesButton.Attribute("Style")?.Value
            .Should().Be("{StaticResource VideoPortraitCardButtonStyle}");
        AssertBoundTextStyle(seriesButton, "Title", StandardTitleStyle);
        AssertBoundTextStyle(seriesButton, "FactsText", MetadataStyle);

        var seriesShelf = document.Descendants()
            .Single(element =>
                element.Name.LocalName == "ScrollViewer"
                && (string?)element.Attribute("AutomationProperties.AutomationId")
                    == "VideoLibrarySeriesShelf");
        var seriesPanel = seriesShelf.Descendants()
            .Single(element => element.Name.LocalName == "ItemsWrapGrid");
        seriesPanel.Attribute("ItemHeight")?.Value
            .Should().Be("{StaticResource VideoPortraitCardSlotHeight}");

        var seasonTemplate = GetDataTemplateByType(document, x, "vmc:VideoSeasonViewModel");
        var seasonButton = seasonTemplate.Descendants()
            .Single(element => element.Name.LocalName == "Button");
        seasonButton.Attribute("Style")?.Value
            .Should().Be("{StaticResource VideoSeasonCardButtonStyle}");
        seasonButton.Attribute("IsChecked").Should().BeNull();
        AssertBoundTextStyle(seasonButton, "Title", SeasonTitleStyle);
        AssertBoundTextStyle(seasonButton, "EpisodeCountText", MetadataStyle);

        var relatedTemplate = GetDataTemplateByType(document, x, "vmc:VideoRelatedItemViewModel");
        var relatedCard = relatedTemplate.Descendants()
            .Single(element => element.Name.LocalName == "Border");
        relatedCard.Attribute("Style")?.Value
            .Should().Be("{StaticResource VideoPortraitCardBorderStyle}");
        AssertBoundTextStyle(relatedCard, "Title", StandardTitleStyle);
        AssertBoundTextStyle(relatedCard, "OriginalTitle", MetadataStyle);
    }

    [Fact]
    public void VideoLibraryPage_EpisodeArtworkUsesTheSharedUncroppedImageStyle()
    {
        var document = LoadProjectXaml("Views", "Pages", "VideoLibraryPage.xaml");

        var episodeRepeater = document.Descendants()
            .Single(element =>
                element.Name.LocalName == "ItemsRepeater"
                && (string?)element.Attribute("AutomationProperties.AutomationId")
                    == "VideoLibraryEpisodeSlots");
        var artwork = episodeRepeater.Descendants()
            .Single(element =>
                element.Name.LocalName == "Image"
                && ((string?)element.Attribute("Source"))?.Contains(
                    "ArtworkImage",
                    StringComparison.Ordinal) == true);

        artwork.Attribute("Style")?.Value
            .Should().Be("{StaticResource VideoEpisodeArtworkImageStyle}");
        artwork.Attribute("Stretch").Should().BeNull(
            "the shared style must remain the single owner of the uncropped fit mode");
    }

    [Fact]
    public void DiscoverPage_RecommendationAndExploreCardsUseSharedStyles()
    {
        var document = LoadProjectXaml("Views", "Pages", "DiscoverPage.xaml");

        AssertDiscoveryCardStyles(document, "DiscoverRecommendationCardButton");
        AssertDiscoveryCardStyles(document, "DiscoverCardButton");
    }

    private static void AssertDiscoveryCardStyles(XDocument document, string automationId)
    {
        var button = document.Descendants()
            .Single(element =>
                element.Name.LocalName == "Button"
                && (string?)element.Attribute("AutomationProperties.AutomationId") == automationId);

        button.Attribute("Style")?.Value
            .Should().Be("{StaticResource VideoDiscoveryCardButtonStyle}");
        AssertBoundTextStyle(button, "Title", StandardTitleStyle);
        AssertBoundTextStyle(button, "FactsText", MetadataStyle);
        AssertBoundTextStyle(button, "SourceText", AccentMetadataStyle);
    }

    private static void AssertBoundTextStyle(
        XElement root,
        string bindingProperty,
        string expectedStyle)
    {
        var text = root.Descendants()
            .Single(element =>
                element.Name.LocalName == "TextBlock"
                && ((string?)element.Attribute("Text"))?.Contains(
                    $"{{x:Bind {bindingProperty}",
                    StringComparison.Ordinal) == true);

        text.Attribute("Style")?.Value.Should().Be(expectedStyle);
    }

    private static void AssertDoubleResource(
        XDocument document,
        XNamespace x,
        string key,
        string expectedValue)
    {
        var resource = document.Descendants()
            .Single(element =>
                element.Name.LocalName == "Double"
                && (string?)element.Attribute(x + "Key") == key);

        resource.Value.Should().Be(expectedValue);
    }

    private static XElement GetStyle(XDocument document, XNamespace x, string key) =>
        document.Descendants()
            .Single(element =>
                element.Name.LocalName == "Style"
                && (string?)element.Attribute(x + "Key") == key);

    private static void AssertStyleSetter(
        XElement style,
        string property,
        string expectedValue)
    {
        var setter = style.Elements()
            .Single(element =>
                element.Name.LocalName == "Setter"
                && (string?)element.Attribute("Property") == property);

        setter.Attribute("Value")?.Value.Should().Be(expectedValue);
    }

    private static XElement GetDataTemplateByKey(
        XDocument document,
        XNamespace x,
        string key) =>
        document.Descendants()
            .Single(element =>
                element.Name.LocalName == "DataTemplate"
                && (string?)element.Attribute(x + "Key") == key);

    private static XElement GetDataTemplateByType(
        XDocument document,
        XNamespace x,
        string dataType) =>
        document.Descendants()
            .Single(element =>
                element.Name.LocalName == "DataTemplate"
                && (string?)element.Attribute(x + "DataType") == dataType);

    private static XDocument LoadProjectXaml(params string[] pathSegments)
    {
        var path = pathSegments.Aggregate(ProjectRoot, Path.Combine);
        return XDocument.Load(path);
    }
}
