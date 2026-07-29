using FluentAssertions;
using Niratan.Models.Manga;
using Niratan.ViewModels.Components;

namespace Niratan.Tests.ViewModels.Components;

public sealed class MihonRepositorySourceItemViewModelTests
{
    [Theory]
    [InlineData("mangadex")]
    [InlineData("EN")]
    [InlineData("eu.kanade")]
    [InlineData("mangadex.org")]
    public void Matches_SearchesVisibleAndDiagnosticMetadata(string query)
    {
        var item = CreateItem(new MihonExtensionSource
        {
            Id = "2499283573021220255",
            Name = "MangaDex",
            Lang = "en",
            BaseUrl = "https://mangadex.org",
            PackageName = "eu.kanade.tachiyomi.extension.all.mangadex",
            PackageDisplayName = "Tachiyomi: MangaDex",
            Version = "1.4.209",
            PackageSourceCount = 1,
        });

        item.Matches(query).Should().BeTrue();
        item.Matches("not-a-real-source").Should().BeFalse();
    }

    [Fact]
    public void InstallCommand_IsEnabledForMultiSourceApk()
    {
        var item = CreateItem(new MihonExtensionSource
        {
            Id = "1",
            Name = "Multi source",
            PackageName = "example.multi",
            PackageSourceCount = 2,
        });

        item.InstallCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void LanguageLabel_HidesSuwayomiLocalSourceSentinel()
    {
        MangaBrowseSourceItemViewModel
            .GetLanguageLabel("LOCALSOURCELANG")
            .Should()
            .NotBe("LOCALSOURCELANG");
    }

    private static MihonRepositorySourceItemViewModel CreateItem(
        MihonExtensionSource source) =>
        new(source, _ => Task.CompletedTask);
}
