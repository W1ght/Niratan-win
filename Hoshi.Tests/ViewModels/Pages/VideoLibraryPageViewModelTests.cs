using FluentAssertions;
using Moq;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Services.UI;
using Hoshi.Services.Video;
using Hoshi.ViewModels.Components;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Tests.ViewModels.Pages;

public class VideoLibraryPageViewModelTests
{
    [Fact]
    public async Task InitializeAsync_LoadsVideos()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem
                {
                    Id = "video-1",
                    Title = "Episode 1",
                    FilePath = "D:\\Anime\\episode1.mkv",
                },
            ],
        };
        var sut = CreateSut(videoService: service);

        await sut.InitializeAsync();

        sut.Videos.Should().ContainSingle();
        sut.Videos[0].Video.Title.Should().Be("Episode 1");
    }

    [Fact]
    public async Task ImportCommand_PicksVideoAndImports()
    {
        var dialog = new Mock<IDialogService>();
        dialog
            .Setup(d => d.OpenFilePickerAsync(".mkv", ".mp4", ".webm", ".avi", ".mov"))
            .ReturnsAsync("D:\\Anime\\episode1.mkv");
        var service = new RecordingVideoLibraryService();
        var notification = new Mock<INotificationService>();
        var sut = CreateSut(
            videoService: service,
            dialogService: dialog.Object,
            notificationService: notification.Object);

        await sut.ImportVideoCommand.ExecuteAsync(null);

        service.ImportedPaths.Should().Equal("D:\\Anime\\episode1.mkv");
        notification.Verify(n => n.ShowSuccess("Video imported.", "Video imported"), Times.Once);
    }

    [Fact]
    public async Task OpenVideoCommand_UsesDedicatedPlayerService()
    {
        var service = new RecordingVideoLibraryService();
        var player = new RecordingVideoPlayerWindowService();
        var video = new VideoItem
        {
            Id = "video-1",
            Title = "Episode 1",
            FilePath = "D:\\Anime\\episode1.mkv",
        };
        var sut = CreateSut(videoService: service, playerService: player);

        await sut.OpenVideoCommand.ExecuteAsync(new VideoItemViewModel(video));

        player.OpenedVideos.Should().ContainSingle().Which.Id.Should().Be("video-1");
        service.MarkedOpenedIds.Should().Equal("video-1");
    }

    private static VideoLibraryPageViewModel CreateSut(
        IVideoLibraryService? videoService = null,
        IDialogService? dialogService = null,
        INotificationService? notificationService = null,
        IVideoPlayerWindowService? playerService = null)
    {
        return new VideoLibraryPageViewModel(
            videoService ?? new RecordingVideoLibraryService(),
            dialogService ?? Mock.Of<IDialogService>(),
            notificationService ?? Mock.Of<INotificationService>(),
            playerService ?? new RecordingVideoPlayerWindowService());
    }

    private sealed class RecordingVideoLibraryService : IVideoLibraryService
    {
        public IReadOnlyList<VideoItem> Videos { get; init; } = [];
        public List<string> ImportedPaths { get; } = [];
        public List<string> MarkedOpenedIds { get; } = [];

        public Task<Result<IReadOnlyList<VideoItem>>> GetVideosAsync(
            string? queryText = null,
            CancellationToken ct = default) =>
            Task.FromResult(Result<IReadOnlyList<VideoItem>>.Success(Videos));

        public Task<Result<VideoItem>> ImportVideoAsync(string filePath, CancellationToken ct = default)
        {
            ImportedPaths.Add(filePath);
            return Task.FromResult(Result<VideoItem>.Success(new VideoItem
            {
                Id = Path.GetFileNameWithoutExtension(filePath),
                Title = Path.GetFileNameWithoutExtension(filePath),
                FilePath = filePath,
            }));
        }

        public Task<Result<VideoItem?>> GetVideoAsync(string videoId, CancellationToken ct = default) =>
            Task.FromResult(Result<VideoItem?>.Success(Videos.FirstOrDefault(video => video.Id == videoId)));

        public Task<Result> MarkOpenedAsync(string videoId, CancellationToken ct = default)
        {
            MarkedOpenedIds.Add(videoId);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> DeleteVideoAsync(string videoId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> SaveProgressAsync(
            string videoId,
            double positionSeconds,
            double durationSeconds,
            CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class RecordingVideoPlayerWindowService : IVideoPlayerWindowService
    {
        public List<VideoItem> OpenedVideos { get; } = [];

        public Task OpenAsync(VideoItem video, CancellationToken ct = default)
        {
            OpenedVideos.Add(video);
            return Task.CompletedTask;
        }
    }
}
