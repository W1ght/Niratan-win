using System.Reflection;
using FluentAssertions;
using Moq;
using Niratan.Models.Manga;
using Niratan.Services.Manga;
using Niratan.Services.UI;
using Niratan.ViewModels.Pages;

namespace Niratan.Tests.ViewModels.Pages;

public sealed class MangaLibraryPageViewModelTests
{
    [Fact]
    public async Task MihonDetails_AddAndRemoveLibraryPersistsConfiguration()
    {
        var source = new MihonInstalledExtension
        {
            SourceId = "42",
            SourceName = "Example Source",
            Lang = "ja",
            BaseUrl = "https://manga.example",
            PackageName = "eu.kanade.tachiyomi.extension.ja.example",
        };
        var manga = new MihonManga
        {
            Url = "/title/1",
            Title = "Example",
            Author = "Author",
            Genres = ["Drama"],
        };
        var configuration = new MihonExtensionConfiguration();
        var savedConfigurations = new List<MihonExtensionConfiguration>();
        var mihon = new Mock<IMihonExtensionService>(MockBehavior.Strict);
        mihon.Setup(service => service.GetMangaDetailsAsync(
                It.IsAny<MihonExtensionConfiguration>(),
                source,
                manga,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(manga);
        mihon.Setup(service => service.GetChaptersAsync(
                It.IsAny<MihonExtensionConfiguration>(),
                source,
                manga,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        mihon.Setup(service => service.GetThumbnailPathAsync(
                source,
                manga,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        mihon.Setup(service => service.SaveConfigurationAsync(
                It.IsAny<MihonExtensionConfiguration>(),
                It.IsAny<CancellationToken>()))
            .Callback<MihonExtensionConfiguration, CancellationToken>(
                (saved, _) => savedConfigurations.Add(saved))
            .Returns(Task.CompletedTask);
        var viewModel = new MangaLibraryPageViewModel(
            Mock.Of<IMangaLibraryService>(),
            Mock.Of<IMangaReaderWindowService>(),
            Mock.Of<ISuwayomiService>(),
            mihon.Object,
            Mock.Of<IDialogService>(),
            Mock.Of<INotificationService>());
        InvokePrivate(
            viewModel,
            "ApplyMihonConfiguration",
            configuration);

        await InvokePrivateAsync(
            viewModel,
            "ShowMihonMangaDetailsAsync",
            source,
            manga);

        viewModel.SelectedRemoteMangaDetails.Should().NotBeNull();
        viewModel.SelectedRemoteMangaDetails!.SupportsOnlineLibrary
            .Should().BeTrue();
        viewModel.SelectedRemoteMangaDetails.IsInOnlineLibrary
            .Should().BeFalse();

        await viewModel.ToggleRemoteMangaLibraryCommand.ExecuteAsync(null);

        savedConfigurations.Should().ContainSingle();
        savedConfigurations[0].Library.Should().ContainSingle();
        viewModel.SelectedRemoteMangaDetails.IsInOnlineLibrary
            .Should().BeTrue();

        await viewModel.ToggleRemoteMangaLibraryCommand.ExecuteAsync(null);

        savedConfigurations.Should().HaveCount(2);
        savedConfigurations[1].Library.Should().BeEmpty();
        viewModel.SelectedRemoteMangaDetails.IsInOnlineLibrary
            .Should().BeFalse();
    }

    private static void InvokePrivate(
        object instance,
        string methodName,
        params object[] arguments) =>
        GetPrivateMethod(instance, methodName).Invoke(instance, arguments);

    private static async Task InvokePrivateAsync(
        object instance,
        string methodName,
        params object[] arguments)
    {
        var task = GetPrivateMethod(instance, methodName)
            .Invoke(instance, arguments)
            .Should()
            .BeAssignableTo<Task>()
            .Subject;
        await task;
    }

    private static MethodInfo GetPrivateMethod(
        object instance,
        string methodName) =>
        instance.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(
            instance.GetType().FullName,
            methodName);
}
