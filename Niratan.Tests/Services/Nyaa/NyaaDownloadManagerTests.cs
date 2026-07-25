using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;
using Niratan.Services.Nyaa;

namespace Niratan.Tests.Services.Nyaa;

public sealed class NyaaDownloadManagerTests
{
    [Fact]
    public async Task Enqueue_downloads_imports_and_keeps_completed_task_in_manager()
    {
        var item = CreateItem("123", "Example");
        var downloadService = new Mock<ITorrentDownloadService>();
        downloadService
            .Setup(service => service.DownloadAsync(
                It.IsAny<string>(),
                item,
                It.IsAny<IProgress<TorrentDownloadProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TorrentDownloadResult>.Success(
                new TorrentDownloadResult(@"C:\downloads\example", [])));
        var importService = new Mock<IResourcePackageImportService>();
        importService
            .Setup(service => service.ImportAsync(
                @"C:\downloads\example",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ResourcePackageImportResult>.Success(
                new ResourcePackageImportResult(1, 1, 0, [])));

        using var manager = new NyaaDownloadManager(
            downloadService.Object,
            importService.Object,
            new WeakReferenceMessenger(),
            NullLogger<NyaaDownloadManager>.Instance);

        var taskId = manager.Enqueue(item);
        await WaitUntilAsync(
            () => manager.GetTasks().Single().State == NyaaDownloadTaskState.Completed);

        var task = manager.GetTasks().Should().ContainSingle().Subject;
        task.TaskId.Should().Be(taskId);
        task.ProgressPercent.Should().Be(100);
        task.ImportResult.Should().NotBeNull();
        task.ImportResult!.MatchedNovelCount.Should().Be(1);
    }

    [Fact]
    public async Task Failed_task_can_be_retried()
    {
        var item = CreateItem("456", "Retry");
        var attempts = 0;
        var downloadService = new Mock<ITorrentDownloadService>();
        downloadService
            .Setup(service => service.DownloadAsync(
                It.IsAny<string>(),
                item,
                It.IsAny<IProgress<TorrentDownloadProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++attempts == 1
                ? Result<TorrentDownloadResult>.Failure("first failure")
                : Result<TorrentDownloadResult>.Success(
                    new TorrentDownloadResult(@"C:\downloads\retry", [])));
        var importService = new Mock<IResourcePackageImportService>();
        importService
            .Setup(service => service.ImportAsync(
                @"C:\downloads\retry",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ResourcePackageImportResult>.Success(
                new ResourcePackageImportResult(0, 0, 1, [])));

        using var manager = new NyaaDownloadManager(
            downloadService.Object,
            importService.Object,
            new WeakReferenceMessenger(),
            NullLogger<NyaaDownloadManager>.Instance);

        var taskId = manager.Enqueue(item);
        await WaitUntilAsync(
            () => manager.GetTasks().Single().State == NyaaDownloadTaskState.Failed);
        manager.Retry(taskId);
        await WaitUntilAsync(
            () => manager.GetTasks().Single().State == NyaaDownloadTaskState.Completed);

        attempts.Should().Be(2);
        manager.GetTasks().Single().ImportResult!.ImportedVideoCount.Should().Be(1);
    }

    private static NyaaTorrentItem CreateItem(string id, string title) =>
        new(
            id,
            title,
            new Uri($"https://nyaa.si/download/{id}.torrent"),
            new Uri($"https://nyaa.si/view/{id}"),
            "Literature",
            1024,
            1,
            0,
            1,
            DateTimeOffset.Now,
            false,
            false);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
