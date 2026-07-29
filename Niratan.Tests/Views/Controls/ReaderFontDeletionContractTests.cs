using FluentAssertions;
using System.Runtime.CompilerServices;

namespace Niratan.Tests.Views.Controls;

public sealed class ReaderFontDeletionContractTests
{
    private static readonly string ProjectRoot = FindProjectRoot();

    private static string FindProjectRoot([CallerFilePath] string sourcePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourcePath)
            ?? throw new DirectoryNotFoundException("Could not locate the test source directory.");
        return Path.GetFullPath(Path.Combine(
            sourceDirectory,
            "..",
            "..",
            "..",
            "Niratan"));
    }

    [Fact]
    public void ReaderFontDeletion_UsesInlineConfirmationInsteadOfNestedContentDialog()
    {
        var appearanceXaml = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "Views",
            "Controls",
            "ReaderAppearanceSettingsContent.xaml"));
        var settingsViewModel = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "ViewModels",
            "Pages",
            "SettingsPageViewModel.cs"));

        appearanceXaml.Should().Contain("x:Name=\"ReaderDeleteFontConfirmationFlyout\"");
        appearanceXaml.Should().Contain(
            "Command=\"{x:Bind ViewModel.DeleteSelectedReaderFontCommand}\"");
        settingsViewModel.Should().Contain("ReaderFontDeleteConfirmationMessage");
        settingsViewModel.Should().NotContain(
            "ResourceStringHelper.GetString(\"ReaderFontDeleteDialogTitle\", \"Delete Reader Font\"),");
    }
}
