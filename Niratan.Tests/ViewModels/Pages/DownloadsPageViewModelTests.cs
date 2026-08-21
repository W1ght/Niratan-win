using FluentAssertions;
using Moq;
using Niratan.Enums;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;
using Niratan.Models.QBittorrent;
using Niratan.Models.Settings;
using System.Linq.Expressions;
using Niratan.Services.Nyaa;
using Niratan.Services.QBittorrent;
using Niratan.Services.Settings;
using Niratan.Services.UI;
using Niratan.Services.Video;
using Niratan.ViewModels.Components;
using Niratan.ViewModels.Pages;

namespace Niratan.Tests.ViewModels.Pages;

public sealed class DownloadsPageViewModelTests
{
    [Fact]
    public async Task Selected_MonoTorrent_backend_enqueues_the_builtin_task_manager()
    {
        var item = CreateItem("mono");
        var row = new NyaaTorrentItemViewModel(item);
        var manager = new Mock<INyaaDownloadManager>();
        manager.Setup(value => value.GetTasks()).Returns([]);
        var settings = new AppSettings();
        var settingsService = CreateSettingsService(settings);
        using var sut = CreateSut(manager, settingsService);

        await sut.InitializeAsync();
        sut.SelectedBackendOption = sut.BackendOptions.Single(
            value => value.Kind == DownloadBackendKind.MonoTorrent);
        await sut.AddToBackendCommand.ExecuteAsync(row);

        manager.Verify(value => value.Enqueue(item), Times.Once);
        row.Status.Should().Contain("MonoTorrent");
    }

    [Fact]
    public async Task Selected_qBittorrent_backend_sends_the_result_to_the_external_coordinator()
    {
        var item = CreateItem("qb");
        var row = new NyaaTorrentItemViewModel(item);
        var manager = new Mock<INyaaDownloadManager>();
        manager.Setup(value => value.GetTasks()).Returns([]);
        var coordinator = new Mock<IQbittorrentDownloadCoordinator>();
        coordinator.Setup(value => value.RefreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<QbittorrentTorrent>>.Success([]));
        coordinator.Setup(value => value.AddAsync(item, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var settingsService = CreateSettingsService(new AppSettings());
        using var sut = CreateSut(manager, settingsService, coordinator);

        await sut.InitializeAsync();
        sut.SelectedBackendOption = sut.BackendOptions.Single(
            value => value.Kind == DownloadBackendKind.Qbittorrent);
        await sut.AddToBackendCommand.ExecuteAsync(row);

        coordinator.Verify(
            value => value.AddAsync(item, It.IsAny<CancellationToken>()),
            Times.Once);
        row.Status.Should().Contain("qBittorrent");
    }

    [Fact]
    public async Task Saving_settings_persists_the_selected_backend()
    {
        var manager = new Mock<INyaaDownloadManager>();
        manager.Setup(value => value.GetTasks()).Returns([]);
        var settingsService = CreateSettingsService(new AppSettings());
        using var sut = CreateSut(manager, settingsService);
        await sut.InitializeAsync();
        sut.SelectedBackendOption = sut.BackendOptions.Single(
            value => value.Kind == DownloadBackendKind.Qbittorrent);

        await sut.SaveSettingsCommand.ExecuteAsync(null);

        settingsService.Object.Current.DownloadBackend.Should().Be(DownloadBackendKind.Qbittorrent);
    }

    private static DownloadsPageViewModel CreateSut(
        Mock<INyaaDownloadManager> manager,
        Mock<ISettingsService> settingsService,
        Mock<IQbittorrentDownloadCoordinator>? coordinator = null)
    {
        coordinator ??= new Mock<IQbittorrentDownloadCoordinator>();
        coordinator.Setup(value => value.RefreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<QbittorrentTorrent>>.Success([]));
        return new DownloadsPageViewModel(
            Mock.Of<INyaaClient>(),
            new Lazy<INyaaDownloadManager>(() => manager.Object),
            coordinator.Object,
            Mock.Of<IQbittorrentClient>(),
            Mock.Of<IQbittorrentCredentialStore>(),
            settingsService.Object,
            Mock.Of<IDialogService>(),
            Mock.Of<IFileRevealService>(),
            Mock.Of<IVideoDownloadImportService>());
    }

    private static Mock<ISettingsService> CreateSettingsService(AppSettings settings)
    {
        var service = new Mock<ISettingsService>();
        service.SetupGet(value => value.Current).Returns(settings);
        service.Setup(value => value.SaveAsync()).Returns(Task.CompletedTask);
        service.Setup(value => value.Set(
                It.IsAny<Expression<Func<AppSettings, DownloadBackendKind>>>(),
                It.IsAny<DownloadBackendKind>()))
            .Callback<Expression<Func<AppSettings, DownloadBackendKind>>, DownloadBackendKind>(
                (_, backend) => settings.DownloadBackend = backend);
        return service;
    }

    private static NyaaTorrentItem CreateItem(string id) =>
        new(
            id,
            $"[Test] {id}",
            new Uri($"https://nyaa.si/download/{id}.torrent"),
            new Uri($"https://nyaa.si/view/{id}"),
            "Anime",
            1024,
            1,
            0,
            1,
            DateTimeOffset.UtcNow,
            true,
            false);
}
