using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Niratan.Models.Dictionary;
using Niratan.ViewModels.Pages;
using Xunit;

namespace Niratan.Tests.ViewModels.Pages;

public class DictionarySettingsPageStabilityContractTests
{
    private static string ReadDictionarySettingsXaml([CallerFilePath] string sourcePath = "")
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourcePath)!, "..", "..", ".."));
        return File.ReadAllText(Path.Combine(
            repositoryRoot, "Niratan", "Views", "Pages", "DictionarySettingsPage.xaml"));
    }

    [Fact]
    public void TrySetEnabled_IgnoresBindingEventsThatMatchTheCurrentRowState()
    {
        var row = new DictionarySettingsItemViewModel(new InstalledDictionary(
            "test-dictionary",
            IsEnabled: true));
        var enabledPropertyChanges = 0;
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(row.IsEnabled))
                enabledPropertyChanges++;
        };

        row.TrySetEnabled(true).Should().BeFalse();
        enabledPropertyChanges.Should().Be(0);

        row.TrySetEnabled(false).Should().BeTrue();
        row.IsEnabled.Should().BeFalse();
        enabledPropertyChanges.Should().Be(1);
    }

    [Fact]
    public void InstalledDictionaryList_UsesThePageScrollViewerWithoutAFixedMaximumHeight()
    {
        var xaml = ReadDictionarySettingsXaml();

        xaml.Should().Contain("x:Name=\"DictionaryList\"");
        xaml.Should().NotContain("MaxHeight=\"620\"");
    }

    [Fact]
    public void DictionaryManagement_ExposesNiratanConfigurationEntrypoints()
    {
        var xaml = ReadDictionarySettingsXaml();

        xaml.Should().Contain("DownloadRecommendedDictionariesButton");
        xaml.Should().Contain("UpdateDictionariesButton");
        xaml.Should().Contain("DictionaryTabDefaultToggle");
        xaml.Should().Contain("DictionaryCustomCssButton");
        xaml.Should().Contain("ConfigureCollapsedDictionariesButton");
        xaml.Should().Contain("DictionaryTwoColumnLayoutToggle");
    }
}
