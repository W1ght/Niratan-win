using System.Xml.Linq;
using FluentAssertions;

namespace Hoshi.Tests.Views.Pages;

public sealed class LookupPageShellContractTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "Hoshi"));

    private static string ReadProjectFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([ProjectRoot, .. parts]));

    [Fact]
    public void NavigationPage_DoesNotExposeManualLookupWindowCommandInTitleBar()
    {
        var navigationXaml = ReadProjectFile("Views", "Pages", "NavigationPage.xaml");
        var navigationCode = ReadProjectFile("Views", "Pages", "NavigationPage.xaml.cs");

        XDocument.Parse(navigationXaml);

        navigationXaml.Should().NotContain("AutomationProperties.AutomationId=\"GlobalLookupButton\"");
        navigationXaml.Should().NotContain("x:Uid=\"GlobalLookupButton\"");
        navigationXaml.Should().NotContain("Click=\"OpenGlobalLookup_Click\"");
        navigationCode.Should().NotContain("OpenGlobalLookup_Click");
        navigationCode.Should().NotContain("IGlobalLookupWindowService");

        var zhResources = ReadProjectFile("Strings", "zh-CN", "Resources.resw");
        var enResources = ReadProjectFile("Strings", "en-US", "Resources.resw");
        zhResources.Should().NotContain("GlobalLookupButton.AutomationProperties.Name");
        enResources.Should().NotContain("GlobalLookupButton.AutomationProperties.Name");
    }

    [Fact]
    public void NovelLookupPage_IsNamedLookupWindowInLocalizedShell()
    {
        var lookupPageXaml = ReadProjectFile("Views", "Pages", "NovelLookupPage.xaml");
        var navigationXaml = ReadProjectFile("Views", "Pages", "NavigationPage.xaml");
        var zhResources = ReadProjectFile("Strings", "zh-CN", "Resources.resw");
        var enResources = ReadProjectFile("Strings", "en-US", "Resources.resw");

        XDocument.Parse(lookupPageXaml);
        XDocument.Parse(navigationXaml);

        navigationXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelLookupNavItem\"");
        lookupPageXaml.Should().Contain("x:Uid=\"NovelLookupPageTitle\"");
        zhResources.Should().Contain("<data name=\"NovelLookupNavItem.Content\" xml:space=\"preserve\"><value>查词</value></data>");
        zhResources.Should().Contain("<data name=\"NovelLookupPageTitle.Text\" xml:space=\"preserve\"><value>查词</value></data>");
        enResources.Should().Contain("<data name=\"NovelLookupNavItem.Content\" xml:space=\"preserve\"><value>Lookup Window</value></data>");
        enResources.Should().Contain("<data name=\"NovelLookupPageTitle.Text\" xml:space=\"preserve\"><value>Lookup Window</value></data>");
    }
}
