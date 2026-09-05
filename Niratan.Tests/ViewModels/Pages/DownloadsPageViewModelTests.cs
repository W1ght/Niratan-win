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
using Niratan.Tests.TestUtils;
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
    public async Task BuiltIn_task_refresh_updates_existing_collection_in_place()
    {
        var initialTask = CreateTask("task-1", progress: 0);
        IReadOnlyList<NyaaDownloadTaskSnapshot> tasks = [initialTask];
        var manager = new Mock<INyaaDownloadManager>();
        manager.Setup(value => value.GetTasks()).Returns(() => tasks);
        var settings = new AppSettings { DownloadBackend = DownloadBackendKind.MonoTorrent };
        using var sut = CreateSut(manager, CreateSettingsService(settings));

        await sut.InitializeAsync();
        var collection = sut.BuiltInTasks;
        var row = sut.BuiltInTasks.Single();
        tasks = [initialTask with { ProgressPercent = 42 }];
        manager.Raise(value => value.TasksChanged += null, EventArgs.Empty);

        sut.BuiltInTasks.Should().BeSameAs(collection);
        sut.BuiltInTasks.Single().Should().BeSameAs(row);
        sut.BuiltInTasks.Single().ProgressPercent.Should().Be(42);
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

    [Fact]
    public async Task Saving_settings_persists_normalized_MonoTorrent_configuration()
    {
        var manager = new Mock<INyaaDownloadManager>();
        manager.Setup(value => value.GetTasks()).Returns([]);
        var appSettings = new AppSettings();
        var settingsService = CreateSettingsService(appSettings);
        using var sut = CreateSut(manager, settingsService);
        await sut.InitializeAsync();
        sut.MonoTorrentAdditionalTrackersText = "udp://tracker.example:6969/announce\nhttps://tracker.example/announce";
        sut.MonoTorrentListenPort = 51413;
        sut.MonoTorrentDhtEnabled = false;
        sut.MonoTorrentPeerExchangeEnabled = false;
        sut.MonoTorrentMaximumConnections = 300;
        sut.MonoTorrentMaximumConnectionsPerTorrent = 90;
        sut.MonoTorrentDownloadRateLimitKiB = 4096;
        sut.MonoTorrentUploadRateLimitKiB = 512;

        await sut.SaveSettingsCommand.ExecuteAsync(null);

        appSettings.MonoTorrentSettings.AdditionalTrackers.Should().Equal(
            "udp://tracker.example:6969/announce",
            "https://tracker.example/announce");
        appSettings.MonoTorrentSettings.ListenPort.Should().Be(51413);
        appSettings.MonoTorrentSettings.EnableDht.Should().BeFalse();
        appSettings.MonoTorrentSettings.EnablePeerExchange.Should().BeFalse();
        appSettings.MonoTorrentSettings.MaximumConnections.Should().Be(300);
        appSettings.MonoTorrentSettings.MaximumConnectionsPerTorrent.Should().Be(90);
        appSettings.MonoTorrentSettings.DownloadRateLimitKiB.Should().Be(4096);
        appSettings.MonoTorrentSettings.UploadRateLimitKiB.Should().Be(512);
    }

    [Fact]
    public async Task Saving_settings_rejects_invalid_MonoTorrent_tracker_without_mutating_settings()
    {
        var manager = new Mock<INyaaDownloadManager>();
        manager.Setup(value => value.GetTasks()).Returns([]);
        var appSettings = new AppSettings();
        var settingsService = CreateSettingsService(appSettings);
        using var sut = CreateSut(manager, settingsService);
        await sut.InitializeAsync();
        sut.MonoTorrentAdditionalTrackersText = "file:///C:/not-a-tracker";

        await sut.SaveSettingsCommand.ExecuteAsync(null);

        sut.ErrorMessage.Should().Contain("file:///C:/not-a-tracker");
        appSettings.MonoTorrentSettings.AdditionalTrackers.Should().BeEmpty();
        settingsService.Verify(value => value.SaveAsync(), Times.Never);
    }

    [Fact]
    public async Task Saving_settings_persists_a_writable_custom_MonoTorrent_download_root()
    {
        using var temporaryDirectory = new TempDirectory();
        var customRoot = Path.Combine(temporaryDirectory.Path, "downloads");
        var manager = new Mock<INyaaDownloadManager>();
        manager.Setup(value => value.GetTasks()).Returns([]);
        var appSettings = new AppSettings();
        var settingsService = CreateSettingsService(appSettings);
        using var sut = CreateSut(manager, settingsService);
        await sut.InitializeAsync();
        sut.MonoTorrentDownloadRootPath = customRoot;

        await sut.SaveSettingsCommand.ExecuteAsync(null);

        appSettings.MonoTorrentSettings.DownloadRootPath.Should().Be(customRoot);
        Directory.Exists(customRoot).Should().BeTrue();
        settingsService.Verify(value => value.SaveAsync(), Times.Once);
    }

    [Fact]
    public async Task Saving_settings_rejects_a_relative_MonoTorrent_download_root()
    {
        var manager = new Mock<INyaaDownloadManager>();
        manager.Setup(value => value.GetTasks()).Returns([]);
        var appSettings = new AppSettings();
        var settingsService = CreateSettingsService(appSettings);
        using var sut = CreateSut(manager, settingsService);
        await sut.InitializeAsync();
        sut.MonoTorrentDownloadRootPath = "relative-downloads";

        await sut.SaveSettingsCommand.ExecuteAsync(null);

        sut.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        appSettings.MonoTorrentSettings.DownloadRootPath.Should().BeEmpty();
        settingsService.Verify(value => value.SaveAsync(), Times.Never);
    }

    [Fact]
    public async Task Browse_and_reset_MonoTorrent_download_root_update_only_the_draft()
    {
        using var temporaryDirectory = new TempDirectory();
        var manager = new Mock<INyaaDownloadManager>();
        manager.Setup(value => value.GetTasks()).Returns([]);
        var appSettings = new AppSettings();
        var dialogs = new Mock<IDialogService>();
        dialogs.Setup(value => value.OpenFolderPickerAsync())
            .ReturnsAsync(temporaryDirectory.Path);
        using var sut = CreateSut(
            manager,
            CreateSettingsService(appSettings),
            dialogService: dialogs);
        await sut.InitializeAsync();

        await sut.BrowseMonoTorrentDownloadRootCommand.ExecuteAsync(null);

        sut.MonoTorrentDownloadRootPath.Should().Be(temporaryDirectory.Path);
        appSettings.MonoTorrentSettings.DownloadRootPath.Should().BeEmpty();

        sut.ResetMonoTorrentDownloadRootCommand.Execute(null);
        sut.MonoTorrentDownloadRootPath.Should().Be(MonoTorrentDownloadRootPolicy.DefaultPath);
        sut.MonoTorrentDownloadRootIsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task Removing_subscription_requires_confirmation_and_keeps_download_tasks_out_of_scope()
    {
        var manager = new Mock<INyaaDownloadManager>();
        manager.Setup(value => value.GetTasks()).Returns([]);
        IReadOnlyList<NyaaVideoSubscription> snapshots =
        [
            new()
            {
                Key = "anilist:123",
                ProviderId = "anilist",
                ProviderItemId = "123",
                Title = "Test Anime",
                Query = "Test Anime",
                ReleaseGroup = "Group",
                Resolution = "1080p",
            },
        ];
        var subscriptions = new Mock<INyaaSubscriptionService>();
        subscriptions.Setup(value => value.GetSubscriptions()).Returns(() => snapshots);
        subscriptions.Setup(value => value.RemoveAsync(
                "anilist:123",
                It.IsAny<CancellationToken>()))
            .Callback(() => snapshots = [])
            .Returns(Task.CompletedTask);
        var dialogs = new Mock<IDialogService>();
        dialogs.Setup(value => value.ConfirmAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ReturnsAsync(true);
        using var sut = CreateSut(
            manager,
            CreateSettingsService(new AppSettings()),
            subscriptionService: subscriptions,
            dialogService: dialogs);

        await sut.InitializeAsync();
        sut.SelectSubscriptionsCommand.Execute(null);
        await sut.RemoveSubscriptionCommand.ExecuteAsync(sut.Subscriptions.Single());

        dialogs.Verify(value => value.ConfirmAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Once);
        subscriptions.Verify(value => value.RemoveAsync(
            "anilist:123",
            It.IsAny<CancellationToken>()), Times.Once);
        manager.Verify(value => value.Remove(It.IsAny<string>()), Times.Never);
        sut.Subscriptions.Should().BeEmpty();
    }

    private static DownloadsPageViewModel CreateSut(
        Mock<INyaaDownloadManager> manager,
        Mock<ISettingsService> settingsService,
        Mock<IQbittorrentDownloadCoordinator>? coordinator = null,
        Mock<INyaaSubscriptionService>? subscriptionService = null,
        Mock<IDialogService>? dialogService = null)
    {
        coordinator ??= new Mock<IQbittorrentDownloadCoordinator>();
        coordinator.Setup(value => value.RefreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<QbittorrentTorrent>>.Success([]));
        if (subscriptionService is null)
        {
            subscriptionService = new Mock<INyaaSubscriptionService>();
            subscriptionService.Setup(value => value.GetSubscriptions()).Returns([]);
        }
        return new DownloadsPageViewModel(
            Mock.Of<INyaaClient>(),
            new Lazy<INyaaDownloadManager>(() => manager.Object),
            coordinator.Object,
            Mock.Of<IQbittorrentClient>(),
            Mock.Of<IQbittorrentCredentialStore>(),
            settingsService.Object,
            dialogService?.Object ?? Mock.Of<IDialogService>(),
            Mock.Of<IFileRevealService>(),
            Mock.Of<IVideoDownloadImportService>(),
            subscriptionService.Object);
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
        service.Setup(value => value.Set(
                It.IsAny<Expression<Func<AppSettings, MonoTorrentSettings>>>(),
                It.IsAny<MonoTorrentSettings>()))
            .Callback<Expression<Func<AppSettings, MonoTorrentSettings>>, MonoTorrentSettings>(
                (_, monoTorrent) => settings.MonoTorrentSettings = monoTorrent);
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

    private static NyaaDownloadTaskSnapshot CreateTask(string taskId, double progress) =>
        new(
            taskId,
            CreateItem(taskId),
            NyaaDownloadTaskState.Downloading,
            progress,
            0,
            0,
            "Downloading",
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
}
