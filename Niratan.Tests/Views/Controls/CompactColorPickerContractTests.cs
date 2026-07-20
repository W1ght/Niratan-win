using FluentAssertions;

namespace Niratan.Tests.Views.Controls;

public sealed class CompactColorPickerContractTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));

    [Fact]
    public void DefaultFlyoutPlacement_UsesSupportedExplicitValue()
    {
        var source = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Controls", "CompactColorPicker.xaml.cs"));

        source.Should().Contain("new PropertyMetadata(FlyoutPlacementMode.Bottom)");
        source.Should().NotContain("new PropertyMetadata(FlyoutPlacementMode.Auto)");
    }

    [Fact]
    public void ReaderCustomColors_UseCompactPickersAndLocalizedLabels()
    {
        var appearance = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "Views",
            "Controls",
            "ReaderAppearanceSettingsContent.xaml"));
        var english = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "Strings",
            "en-US",
            "Resources.resw"));
        var chinese = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "Strings",
            "zh-CN",
            "Resources.resw"));

        appearance.Should().Contain("xmlns:controls=\"using:Niratan.Views.Controls\"");
        appearance.Should().Contain("<controls:CompactColorPicker AutomationProperties.AutomationId=\"ReaderCustomBackgroundColorPicker\"");
        appearance.Should().Contain("AutomationProperties.AutomationId=\"ReaderCustomTextColorPicker\"");
        appearance.Should().Contain("AutomationProperties.AutomationId=\"ReaderCustomInfoColorPicker\"");
        appearance.Should().NotContain("<ColorPicker IsAlphaEnabled=\"False\"");

        foreach (var resources in new[] { english, chinese })
        {
            resources.Should().Contain("ReaderCustomColorsCard.Header");
            resources.Should().Contain("ReaderCustomBackgroundColorCard.Header");
            resources.Should().Contain("ReaderCustomTextColorCard.Header");
            resources.Should().Contain("ReaderCustomInfoColorCard.Header");
            resources.Should().Contain("ReaderImportFontButton.Content");
            resources.Should().Contain("ReaderDeleteFontButton.Content");
            resources.Should().Contain("ReaderTwoColumnHorizontalPagesCard.Header");
            resources.Should().Contain("ReaderParagraphSpacingCard.Header");
        }

        chinese.Should().Contain("<value>背景颜色</value>");
        chinese.Should().Contain("<value>正文颜色</value>");
        chinese.Should().Contain("<value>信息文字颜色</value>");
        chinese.Should().Contain("<value>横排双页模式</value>");
    }
}
