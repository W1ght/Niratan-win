using FluentAssertions;
using Moq;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;
using Niratan.Services.Nyaa;
using Niratan.Services.UI;
using Niratan.ViewModels.Dialogs;

namespace Niratan.Tests.ViewModels.Dialogs;

public sealed class NyaaImportDialogViewModelTests
{
    [Fact]
    public async Task Download_refresh_updates_existing_collection_in_place()
    {
        var initialTask = CreateTask("task-1", progress: 0);
        IReadOnlyList<NyaaDownloadTaskSnapshot> tasks = [initialTask];
        var client = new Mock<INyaaClient>();
        var manager = new Mock<INyaaDownloadManager>();
        manager.Setup(service => service.GetTasks()).Returns(() => tasks);

        using var viewModel = new NyaaImportDialogViewModel(
            client.Object,
            new Lazy<INyaaDownloadManager>(() => manager.Object),
            Mock.Of<IFileRevealService>(),
            Mock.Of<INotificationService>());
        await viewModel.InitializeAsync();

        var downloads = viewModel.Downloads;
        tasks = [initialTask with { ProgressPercent = 42 }];
        manager.Raise(service => service.TasksChanged += null, EventArgs.Empty);

        viewModel.Downloads.Should().BeSameAs(downloads);
        viewModel.Downloads.Single().ProgressPercent.Should().Be(42);
    }

    [Fact]
    public async Task Search_results_can_be_sorted_and_filtered_without_new_network_request()
    {
        var results = new[]
        {
            CreateItem("1", "Zulu", seeders: 2, trusted: true),
            CreateItem("2", "Alpha", seeders: 10, trusted: false),
            CreateItem("3", "Beta", seeders: 0, trusted: true),
        };
        var client = new Mock<INyaaClient>();
        client
            .Setup(service => service.SearchAsync(
                It.IsAny<NyaaSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<NyaaTorrentItem>>.Success(results));
        var manager = new Mock<INyaaDownloadManager>();
        manager.Setup(service => service.GetTasks()).Returns([]);

        using var viewModel = new NyaaImportDialogViewModel(
            client.Object,
            new Lazy<INyaaDownloadManager>(() => manager.Object),
            Mock.Of<IFileRevealService>(),
            Mock.Of<INotificationService>())
        {
            SearchQuery = "example",
        };

        await viewModel.InitializeAsync();
        await viewModel.SearchCommand.ExecuteAsync(null);
        viewModel.Results.Select(row => row.Title).Should().Equal("Alpha", "Zulu", "Beta");

        viewModel.SelectedResultFilter = viewModel.ResultFilters.Single(option =>
            option.Code == "trusted");
        viewModel.Results.Select(row => row.Title).Should().Equal("Zulu", "Beta");

        viewModel.SelectedResultFilter = viewModel.ResultFilters.Single(option =>
            option.Code == "all");
        viewModel.SelectedResultSort = viewModel.ResultSortOptions.Single(option =>
            option.Code == "title");
        viewModel.Results.Select(row => row.Title).Should().Equal("Alpha", "Beta", "Zulu");
        client.Verify(service => service.SearchAsync(
            It.IsAny<NyaaSearchRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static NyaaTorrentItem CreateItem(
        string id,
        string title,
        int seeders,
        bool trusted) =>
        new(
            id,
            title,
            new Uri($"https://nyaa.si/download/{id}.torrent"),
            new Uri($"https://nyaa.si/view/{id}"),
            "Literature",
            1024,
            seeders,
            0,
            1,
            DateTimeOffset.Now,
            trusted,
            false);

    private static NyaaDownloadTaskSnapshot CreateTask(string taskId, double progress) =>
        new(
            taskId,
            CreateItem(taskId, "Test task", seeders: 1, trusted: true),
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
