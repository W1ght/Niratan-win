using System.Xml.Linq;
using FluentAssertions;

namespace Niratan.Tests.Views.Pages;

public sealed class KeyboardShortcutsSettingsPageContractTests
{
    private static readonly string ProjectRoot = FindProjectRoot();

    private static string ReadProjectFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([ProjectRoot, .. parts]));

    private static string FindProjectRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                var projectRoot = Path.Combine(directory.FullName, "Niratan");
                if (File.Exists(Path.Combine(projectRoot, "Niratan.csproj")))
                    return projectRoot;

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Niratan project root.");
    }

    [Fact]
    public void Page_GroupsShortcutRowsIntoNiratanCategoryCards()
    {
        var xaml = ReadProjectFile("Views", "Pages", "KeyboardShortcutsSettingsPage.xaml");
        var viewModel = ReadProjectFile(
            "ViewModels",
            "Pages",
            "KeyboardShortcutsSettingsPageViewModel.cs");

        XDocument.Parse(xaml);

        xaml.Should().Contain("ItemsSource=\"{x:Bind ViewModel.Sections, Mode=OneWay}\"");
        xaml.Should().Contain("x:DataType=\"vm:ShortcutSectionViewModel\"");
        xaml.Should().Contain("ItemsSource=\"{x:Bind Rows, Mode=OneWay}\"");
        xaml.Should().Contain("CardBackgroundFillColorDefaultBrush");
        xaml.Should().Contain("Text=\"{x:Bind DefaultShortcutText, Mode=OneWay}\"");
        xaml.Should().Contain("Content=\"{x:Bind ShortcutButtonLabel, Mode=OneWay}\"");
        xaml.Should().Contain("IsEnabled=\"{x:Bind CanReset, Mode=OneWay}\"");
        xaml.Should().Contain("AutomationProperties.AutomationId=\"DictionaryEntryJumpCountNumberBox\"");
        xaml.Should().Contain("Maximum=\"10\"");
        var code = ReadProjectFile("Views", "Pages", "KeyboardShortcutsSettingsPage.xaml.cs");
        xaml.Should().Contain("Command=\"{x:Bind RecordCommand}\"");
        xaml.Should().Contain("Command=\"{x:Bind ResetCommand}\"");
        xaml.Should().Contain("x:Name=\"ShortcutCaptureLayer\"");
        xaml.Should().Contain("ViewModel.CancelRecordingCommand");
        code.Should().Contain("ShortcutCaptureLayer.AddHandler(");
        code.Should().Contain("new KeyEventHandler(ShortcutCaptureLayer_KeyDown)");
        code.Should().Contain("ShortcutCaptureLayer.Focus(FocusState.Programmatic)");
        xaml.Should().NotContain("ItemsSource=\"{x:Bind ViewModel.Rows, Mode=OneWay}\"");
        xaml.Should().NotContain("Text=\"{x:Bind CategoryTitle, Mode=OneWay}\"");

        viewModel.Should().Contain("ShortcutCategory.Global");
        viewModel.Should().Contain("ShortcutCategory.Reader");
        viewModel.Should().Contain("ShortcutCategory.DictionaryPopup");
        viewModel.Should().Contain("ShortcutCategory.Sasayaki");
        viewModel.Should().Contain("ShortcutCategory.Video");
        viewModel.Should().Contain("SetDictionaryEntryJumpCount");
        viewModel.Should().Contain("if (sectionRows.Count > 0)");
    }

    [Fact]
    public void Page_LocalizesNiratanShortcutRowLabels()
    {
        var english = ReadProjectFile("Strings", "en-US", "Resources.resw");
        var chinese = ReadProjectFile("Strings", "zh-CN", "Resources.resw");

        XDocument.Parse(english);
        XDocument.Parse(chinese);

        english.Should().Contain("name=\"KeyboardShortcutsDefaultText\"");
        english.Should().Contain("name=\"KeyboardShortcutsPressKeysText\"");
        english.Should().Contain("<value>Dictionary / Popup</value>");
        chinese.Should().Contain("name=\"KeyboardShortcutsDefaultText\"");
        chinese.Should().Contain("name=\"KeyboardShortcutsPressKeysText\"");
        chinese.Should().Contain("<value>词典 / 弹窗</value>");
    }
}
