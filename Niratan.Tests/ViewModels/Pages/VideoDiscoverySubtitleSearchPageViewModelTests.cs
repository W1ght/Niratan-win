using System.Collections.Immutable;
using FluentAssertions;
using Moq;
using Niratan.Models.Common;
using Niratan.Models.Video;
using Niratan.Services.UI;
using Niratan.Services.Video;
using Niratan.ViewModels.Pages;

namespace Niratan.Tests.ViewModels.Pages;

public sealed class VideoDiscoverySubtitleSearchPageViewModelTests
{
    [Fact]
    public async Task Save_next_to_video_fails_when_the_picked_video_no_longer_exists()
    {
        var directory = Directory.CreateTempSubdirectory("niratan-jimaku-stale-video-");
        try
        {
            var videoPath = Path.Combine(directory.FullName, "episode.mkv");
            await File.WriteAllBytesAsync(
                videoPath,
                [0],
                TestContext.Current.CancellationToken);
            var (viewModel, subtitles, dialogs) = CreateViewModel();
            using (viewModel)
            {
                dialogs.Setup(service => service.OpenFilePickerAsync(
                        ".mkv", ".mp4", ".m4v", ".webm", ".avi", ".mov", ".wmv"))
                    .ReturnsAsync(videoPath);
                await InitializeAndSelectAsync(viewModel);
                viewModel.SelectedDestination = viewModel.Destinations.Single(option =>
                    option.Value == VideoDiscoverySubtitleDestination.ExistingVideo);
                await viewModel.PickTargetCommand.ExecuteAsync(null);
                File.Delete(videoPath);

                await viewModel.SaveSelectedCommand.ExecuteAsync(null);

                viewModel.ErrorMessage.Should().NotBeNullOrWhiteSpace();
                File.Exists(videoPath).Should().BeFalse();
                subtitles.Verify(service => service.DownloadAsync(
                    It.IsAny<JimakuSubtitleItem>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()), Times.Never);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Save_to_directory_fails_when_the_picked_directory_no_longer_exists()
    {
        var directory = Directory.CreateTempSubdirectory("niratan-jimaku-stale-directory-");
        var directoryPath = directory.FullName;
        try
        {
            var (viewModel, subtitles, dialogs) = CreateViewModel();
            using (viewModel)
            {
                dialogs.Setup(service => service.OpenFolderPickerAsync()).ReturnsAsync(directoryPath);
                await InitializeAndSelectAsync(viewModel);
                viewModel.SelectedDestination = viewModel.Destinations.Single(option =>
                    option.Value == VideoDiscoverySubtitleDestination.Directory);
                await viewModel.PickTargetCommand.ExecuteAsync(null);
                directory.Delete(recursive: true);

                await viewModel.SaveSelectedCommand.ExecuteAsync(null);

                viewModel.ErrorMessage.Should().NotBeNullOrWhiteSpace();
                Directory.Exists(directoryPath).Should().BeFalse();
                subtitles.Verify(service => service.DownloadAsync(
                    It.IsAny<JimakuSubtitleItem>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()), Times.Never);
            }
        }
        finally
        {
            if (Directory.Exists(directoryPath))
                Directory.Delete(directoryPath, recursive: true);
        }
    }

    private static (VideoDiscoverySubtitleSearchPageViewModel ViewModel,
        Mock<IJimakuSubtitleService> Subtitles,
        Mock<IDialogService> Dialogs) CreateViewModel()
    {
        var subtitles = new Mock<IJimakuSubtitleService>();
        subtitles.Setup(service => service.SearchAsync(
                It.IsAny<VideoSubtitleSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<JimakuSubtitleItem>>.Success([CreateSubtitle()]));
        var dialogs = new Mock<IDialogService>();
        return (new VideoDiscoverySubtitleSearchPageViewModel(subtitles.Object, dialogs.Object),
            subtitles,
            dialogs);
    }

    private static async Task InitializeAndSelectAsync(
        VideoDiscoverySubtitleSearchPageViewModel viewModel)
    {
        await viewModel.InitializeAsync(new VideoDiscoveryNavigationTarget(
            CreateIdentity(),
            new VideoDiscoveryArtwork(null, null, null)));
        viewModel.SelectedResult = viewModel.Results.Single();
    }

    private static VideoMetadataCandidate CreateIdentity() => new(
        "anilist",
        "123",
        VideoMetadataMediaKind.Anime,
        "Test Anime",
        "テストアニメ",
        2026,
        1,
        2,
        2,
        ["Test Anime"],
        ImmutableDictionary<string, string>.Empty.Add("anilist", "123"),
        null);

    private static JimakuSubtitleItem CreateSubtitle() => new(
        42,
        "Test Anime",
        "Test Anime - 02.ja.srt",
        new Uri("https://cdn.jimaku.cc/test.srt"),
        32,
        "ja",
        2);
}
