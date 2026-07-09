using FluentAssertions;
using Moq;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Models.Video;
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
        notification.Verify(
            n => n.ShowSuccess(
                It.Is<string>(message => !string.IsNullOrWhiteSpace(message)),
                It.Is<string>(title => !string.IsNullOrWhiteSpace(title))),
            Times.Once);
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

    [Fact]
    public async Task OpenVideoCommand_PassesVisibleVideosAsEpisodePlaylist()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem { Id = "episode-1", Title = "Episode 1", FilePath = @"D:\Anime\episode1.mkv" },
                new VideoItem { Id = "episode-2", Title = "Episode 2", FilePath = @"D:\Anime\episode2.mkv" },
                new VideoItem { Id = "episode-3", Title = "Episode 3", FilePath = @"D:\Anime\episode3.mkv" },
            ],
        };
        var player = new RecordingVideoPlayerWindowService();
        var sut = CreateSut(videoService: service, playerService: player);

        await sut.InitializeAsync();
        await sut.OpenVideoCommand.ExecuteAsync(sut.Videos[1]);

        var visibleOrder = sut.Videos.Select(video => video.Video.Id);
        player.OpenedVideos.Should().ContainSingle().Which.Id.Should().Be("episode-2");
        player.OpenedPlaylists.Should().ContainSingle()
            .Which.Select(video => video.Id)
            .Should().Equal(visibleOrder);
    }

    [Fact]
    public async Task SearchText_FiltersVisibleVideosByTitleFolderCollectionAndTags()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem
                {
                    Id = "episode-1",
                    Title = "Episode 1",
                    FilePath = @"D:\Anime\Show\Episode 1.mkv",
                    SourceFolderPath = @"D:\Anime\Show",
                    CollectionName = "Show",
                    Tags = "anime, japanese",
                },
                new VideoItem
                {
                    Id = "movie-1",
                    Title = "Movie",
                    FilePath = @"D:\Movies\Movie.mkv",
                    SourceFolderPath = @"D:\Movies",
                    CollectionName = "Movies",
                },
            ],
        };
        var sut = CreateSut(videoService: service);

        await sut.InitializeAsync();
        sut.SearchText = "japanese";

        sut.Videos.Should().ContainSingle()
            .Which.Video.Id.Should().Be("episode-1");
    }

    [Fact]
    public async Task SelectedSortOption_ProgressSortsHighestProgressFirst()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem { Id = "low", Title = "Low", FilePath = @"D:\Videos\low.mkv", LastPositionSeconds = 10, DurationSeconds = 100 },
                new VideoItem { Id = "high", Title = "High", FilePath = @"D:\Videos\high.mkv", LastPositionSeconds = 80, DurationSeconds = 100 },
                new VideoItem { Id = "none", Title = "None", FilePath = @"D:\Videos\none.mkv" },
            ],
        };
        var sut = CreateSut(videoService: service);

        await sut.InitializeAsync();
        sut.SelectedSortOption = VideoLibrarySortOption.Progress;

        sut.Videos.Select(video => video.Video.Id).Should().Equal("high", "low", "none");
    }

    [Fact]
    public async Task SelectedLayoutMode_TogglesListAndPosterFlags()
    {
        var sut = CreateSut();
        await sut.InitializeAsync();

        sut.SelectedLayoutMode = VideoLibraryLayoutMode.Posters;

        sut.IsPosterLayout.Should().BeTrue();
        sut.IsListLayout.Should().BeFalse();
    }

    [Fact]
    public async Task ContinueWatchingView_IgnoresNearStartProgress()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem { Id = "start", Title = "Start", FilePath = @"D:\Videos\start.mkv", LastPositionSeconds = 2, DurationSeconds = 2406 },
                new VideoItem { Id = "continue", Title = "Continue", FilePath = @"D:\Videos\continue.mkv", LastPositionSeconds = 10, DurationSeconds = 100 },
            ],
        };
        var sut = CreateSut(videoService: service);

        await sut.InitializeAsync();
        sut.SelectLibraryViewCommand.Execute(nameof(VideoLibraryView.ContinueWatching));

        sut.Videos.Select(video => video.Video.Id).Should().Equal("continue");
    }

    [Fact]
    public async Task SmartCollectionPreview_UsesAllRules()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem { Id = "episode", Title = "Umaru 01", FilePath = @"D:\Anime\Umaru 01.mkv", Tags = "anime" },
                new VideoItem { Id = "movie", Title = "Movie", FilePath = @"D:\Movies\Movie.mkv" },
            ],
        };
        var sut = CreateSut(videoService: service);

        await sut.InitializeAsync();
        sut.SmartCollectionNameDraft = "Umaru";
        sut.SelectedSmartRuleField = VideoSmartRuleField.FileName;
        sut.SmartRuleValueDraft = "umaru";

        sut.SmartCollectionPreviewRows.Select(row => row.Video.Id).Should().Equal("episode");
    }

    [Fact]
    public async Task CreateSmartCollectionCommand_CreatesCollectionAndReloadsFilters()
    {
        var service = new RecordingVideoLibraryService();
        var sut = CreateSut(videoService: service);
        await sut.InitializeAsync();
        sut.SmartCollectionNameDraft = "Anime";
        sut.SelectedSmartRuleField = VideoSmartRuleField.Tag;
        sut.SmartRuleValueDraft = "anime";

        await sut.CreateSmartCollectionCommand.ExecuteAsync(null);

        service.CreatedSmartCollections.Should().ContainSingle()
            .Which.Name.Should().Be("Anime");
    }

    [Fact]
    public async Task CreateSmartCollectionCommand_UsesIsTrueRuleForBoundSubtitle()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem
                {
                    Id = "subbed",
                    Title = "Subbed",
                    FilePath = @"D:\Anime\subbed.mkv",
                    SubtitlePath = @"D:\Anime\subbed.ja.srt",
                },
                new VideoItem
                {
                    Id = "raw",
                    Title = "Raw",
                    FilePath = @"D:\Anime\raw.mkv",
                },
            ],
        };
        var sut = CreateSut(videoService: service);

        await sut.InitializeAsync();
        sut.SmartCollectionNameDraft = "Subbed";
        sut.SelectedSmartRuleField = VideoSmartRuleField.HasBoundSubtitle;

        sut.SmartCollectionPreviewRows.Select(row => row.Video.Id).Should().Equal("subbed");

        await sut.CreateSmartCollectionCommand.ExecuteAsync(null);

        var rule = service.CreatedSmartCollections.Should().ContainSingle()
            .Which.SmartRules.Should().ContainSingle().Subject;
        rule.Field.Should().Be(VideoSmartRuleField.HasBoundSubtitle);
        rule.Match.Should().Be(VideoSmartRuleMatch.IsTrue);
    }

    [Fact]
    public async Task ToggleFavoriteCommand_UpdatesFavoriteAndReloads()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem
                {
                    Id = "episode-1",
                    Title = "Episode 1",
                    FilePath = @"D:\Anime\episode1.mkv",
                },
            ],
        };
        var sut = CreateSut(videoService: service);

        await sut.InitializeAsync();
        await sut.ToggleFavoriteCommand.ExecuteAsync(sut.Videos[0]);

        service.FavoriteUpdates.Should().Equal(("episode-1", true));
        service.LoadCount.Should().Be(2);
    }

    [Fact]
    public async Task AddToNewCollectionCommand_PromptsForNameAndCreatesManualCollection()
    {
        var dialog = new Mock<IDialogService>();
        dialog
            .Setup(service => service.PromptTextAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ReturnsAsync("Watch Later");
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem
                {
                    Id = "episode-1",
                    Title = "Episode 1",
                    FilePath = @"D:\Anime\episode1.mkv",
                },
            ],
        };
        var sut = CreateSut(videoService: service, dialogService: dialog.Object);

        await sut.InitializeAsync();
        await sut.AddToNewCollectionCommand.ExecuteAsync(sut.Videos[0]);

        service.CreatedManualCollections.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(("Watch Later", new[] { "episode-1" }));
        service.LoadCount.Should().Be(2);
    }

    [Fact]
    public async Task RevealFileCommand_UsesFileRevealService()
    {
        var reveal = new RecordingFileRevealService();
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem
                {
                    Id = "episode-1",
                    Title = "Episode 1",
                    FilePath = @"D:\Anime\episode1.mkv",
                },
            ],
        };
        var sut = CreateSut(videoService: service, fileRevealService: reveal);

        await sut.InitializeAsync();
        await sut.RevealFileCommand.ExecuteAsync(sut.Videos[0]);

        reveal.RevealedPaths.Should().Equal(@"D:\Anime\episode1.mkv");
    }

    [Fact]
    public async Task OpenVideoFromBeginningCommand_OpensTransientZeroProgressVideo()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem
                {
                    Id = "episode-1",
                    Title = "Episode 1",
                    FilePath = @"D:\Anime\episode1.mkv",
                    LastPositionSeconds = 120,
                    DurationSeconds = 240,
                },
            ],
        };
        var player = new RecordingVideoPlayerWindowService();
        var sut = CreateSut(videoService: service, playerService: player);

        await sut.InitializeAsync();
        await sut.OpenVideoFromBeginningCommand.ExecuteAsync(sut.Videos[0]);

        player.OpenedVideos.Should().ContainSingle()
            .Which.LastPositionSeconds.Should().Be(0);
        service.MarkedOpenedIds.Should().Equal("episode-1");
        service.ClearedProgressIds.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateMissingThumbnails_IgnoresPosterArtworkForReloadDecision()
    {
        var posterPath = Path.GetTempFileName();
        try
        {
            var service = new RecordingVideoLibraryService
            {
                Videos =
                [
                    new VideoItem
                    {
                        Id = "poster-backed",
                        Title = "Poster Backed",
                        FilePath = @"D:\Anime\poster-backed.mkv",
                        PosterPath = posterPath,
                    },
                ],
            };
            var thumbnail = new RecordingVideoThumbnailService
            {
                EnsureThumbnail = video => Task.FromResult<string?>(video.PosterPath),
            };
            var sut = CreateSut(videoService: service, thumbnailService: thumbnail);

            await sut.InitializeAsync();
            await thumbnail.WaitForCallsAsync(1, TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);

            service.LoadCount.Should().Be(1);
        }
        finally
        {
            File.Delete(posterPath);
        }
    }

    [Fact]
    public void AvailableSmartRuleFields_ExposesSelectableSmartRuleFields()
    {
        var sut = CreateSut();

        sut.AvailableSmartRuleFields.Select(field => field.Value).Should().Equal(
            VideoSmartRuleField.FileName,
            VideoSmartRuleField.ParentFolder,
            VideoSmartRuleField.Path,
            VideoSmartRuleField.Tag,
            VideoSmartRuleField.HasBoundSubtitle,
            VideoSmartRuleField.PlaybackState);
        sut.AvailableSmartRuleFields.Should().OnlyContain(field => !string.IsNullOrWhiteSpace(field.DisplayName));
    }

    [Fact]
    public async Task ScanFolderCommand_PicksFolderAndScans()
    {
        var dialog = new Mock<IDialogService>();
        dialog
            .Setup(service => service.OpenFolderPickerAsync())
            .ReturnsAsync(@"D:\Anime");
        var service = new RecordingVideoLibraryService();
        var notification = new Mock<INotificationService>();
        var sut = CreateSut(
            videoService: service,
            dialogService: dialog.Object,
            notificationService: notification.Object);

        await sut.ScanFolderCommand.ExecuteAsync(null);

        service.ScannedFolders.Should().Equal(@"D:\Anime");
        notification.Verify(
            service => service.ShowSuccess(
                It.Is<string>(message => message.Contains('0')),
                It.Is<string>(title => !string.IsNullOrWhiteSpace(title))),
            Times.Once);
    }

    [Fact]
    public async Task MarkWatchedCommand_MarksVideoAndReloads()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem { Id = "episode-1", Title = "Episode 1", FilePath = @"D:\Anime\episode1.mkv" },
            ],
        };
        var sut = CreateSut(videoService: service);

        await sut.InitializeAsync();
        await sut.MarkWatchedCommand.ExecuteAsync(sut.Videos[0]);

        service.MarkedWatchedIds.Should().Equal("episode-1");
        service.LoadCount.Should().Be(2);
    }

    [Fact]
    public async Task ClearProgressCommand_ClearsVideoProgressAndReloads()
    {
        var service = new RecordingVideoLibraryService
        {
            Videos =
            [
                new VideoItem
                {
                    Id = "episode-1",
                    Title = "Episode 1",
                    FilePath = @"D:\Anime\episode1.mkv",
                    LastPositionSeconds = 20,
                    DurationSeconds = 100,
                },
            ],
        };
        var sut = CreateSut(videoService: service);

        await sut.InitializeAsync();
        await sut.ClearProgressCommand.ExecuteAsync(sut.Videos[0]);

        service.ClearedProgressIds.Should().Equal("episode-1");
        service.LoadCount.Should().Be(2);
    }

    [Fact]
    public async Task PlayerLibraryChanged_ReloadsVisibleVideos()
    {
        var service = new RecordingVideoLibraryService();
        service.VideoResponses.Enqueue(
        [
            new VideoItem
            {
                Id = "episode-1",
                Title = "Episode 1",
                FilePath = @"D:\Anime\episode1.mkv",
                LastPositionSeconds = 2,
                DurationSeconds = 100,
            },
        ]);
        service.VideoResponses.Enqueue(
        [
            new VideoItem
            {
                Id = "episode-1",
                Title = "Episode 1",
                FilePath = @"D:\Anime\episode1.mkv",
                LastPositionSeconds = 76,
                DurationSeconds = 100,
            },
        ]);
        var player = new RecordingVideoPlayerWindowService();
        var sut = CreateSut(videoService: service, playerService: player);

        await sut.InitializeAsync();
        player.RaiseLibraryChanged();

        service.LoadCount.Should().Be(2);
        sut.Videos.Should().ContainSingle()
            .Which.Video.LastPositionSeconds.Should().Be(76);
    }

    private static VideoLibraryPageViewModel CreateSut(
        IVideoLibraryService? videoService = null,
        IDialogService? dialogService = null,
        INotificationService? notificationService = null,
        IVideoPlayerWindowService? playerService = null,
        IVideoThumbnailService? thumbnailService = null,
        IFileRevealService? fileRevealService = null)
    {
        return new VideoLibraryPageViewModel(
            videoService ?? new RecordingVideoLibraryService(),
            dialogService ?? Mock.Of<IDialogService>(),
            notificationService ?? Mock.Of<INotificationService>(),
            playerService ?? new RecordingVideoPlayerWindowService(),
            thumbnailService ?? new RecordingVideoThumbnailService(),
            fileRevealService ?? new RecordingFileRevealService());
    }

    private sealed class RecordingVideoLibraryService : IVideoLibraryService
    {
        public IReadOnlyList<VideoItem> Videos { get; init; } = [];
        public IReadOnlyList<VideoCollection> Collections { get; init; } = [];
        public Queue<IReadOnlyList<VideoItem>> VideoResponses { get; } = [];
        public List<string> ImportedPaths { get; } = [];
        public List<string> ScannedFolders { get; } = [];
        public List<string> MarkedOpenedIds { get; } = [];
        public List<string> MarkedWatchedIds { get; } = [];
        public List<string> ClearedProgressIds { get; } = [];
        public List<(string VideoId, bool IsFavorite)> FavoriteUpdates { get; } = [];
        public List<VideoCollection> CreatedSmartCollections { get; } = [];
        public List<(string Name, IReadOnlyList<string> VideoIds)> CreatedManualCollections { get; } = [];
        public int LoadCount { get; private set; }

        public Task<Result<IReadOnlyList<VideoItem>>> GetVideosAsync(
            string? queryText = null,
            CancellationToken ct = default)
        {
            LoadCount++;
            var videos = VideoResponses.Count > 0 ? VideoResponses.Dequeue() : Videos;
            return Task.FromResult(Result<IReadOnlyList<VideoItem>>.Success(videos));
        }

        public Task<Result<IReadOnlyList<VideoCollection>>> GetCollectionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Result<IReadOnlyList<VideoCollection>>.Success(Collections));

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

        public Task<Result<VideoFolderScanResult>> ScanFolderAsync(
            string folderPath,
            CancellationToken ct = default)
        {
            ScannedFolders.Add(folderPath);
            return Task.FromResult(Result<VideoFolderScanResult>.Success(
                new VideoFolderScanResult(0, [])));
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

        public Task<Result<VideoCollection>> CreateSmartCollectionAsync(
            string name,
            IReadOnlyList<VideoSmartRule> rules,
            CancellationToken ct = default)
        {
            var collection = new VideoCollection
            {
                Name = name,
                Kind = VideoCollectionKind.Smart,
                SmartRules = rules,
            };
            CreatedSmartCollections.Add(collection);
            return Task.FromResult(Result<VideoCollection>.Success(collection));
        }

        public Task<Result<VideoCollection>> CreateManualCollectionAsync(
            string name,
            IReadOnlyList<string> videoIds,
            CancellationToken ct = default)
        {
            CreatedManualCollections.Add((name, videoIds));
            return Task.FromResult(Result<VideoCollection>.Success(new VideoCollection
            {
                Name = name,
                Kind = VideoCollectionKind.Manual,
                ItemIds = videoIds.ToList(),
            }));
        }

        public Task<Result> DeleteCollectionAsync(string collectionId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> SetFavoriteAsync(
            string videoId,
            bool isFavorite,
            CancellationToken ct = default)
        {
            FavoriteUpdates.Add((videoId, isFavorite));
            return Task.FromResult(Result.Success());
        }

        public Task<Result> SaveProgressAsync(
            string videoId,
            double positionSeconds,
            double durationSeconds,
            CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> SavePlaybackStateAsync(
            string videoId,
            VideoPlaybackState state,
            CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> MarkWatchedAsync(
            string videoId,
            CancellationToken ct = default)
        {
            MarkedWatchedIds.Add(videoId);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ClearProgressAsync(
            string videoId,
            CancellationToken ct = default)
        {
            ClearedProgressIds.Add(videoId);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> SetVideoProfileAsync(
            string videoId,
            string? profileId,
            CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class RecordingVideoThumbnailService : IVideoThumbnailService
    {
        private readonly TaskCompletionSource<int> _calls = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public Func<VideoItem, Task<string?>>? EnsureThumbnail { get; init; }

        public Task<string?> EnsureThumbnailAsync(
            VideoItem video,
            bool generateIfMissing = true,
            CancellationToken ct = default)
        {
            var count = Interlocked.Increment(ref _callCount);
            _calls.TrySetResult(count);
            return EnsureThumbnail?.Invoke(video) ?? Task.FromResult(video.ThumbnailPath);
        }

        public async Task WaitForCallsAsync(int expectedCount, CancellationToken ct)
        {
            while (Volatile.Read(ref _callCount) < expectedCount)
            {
                await _calls.Task.WaitAsync(ct);
            }
        }

        public void Suspend()
        {
        }

        public void Resume()
        {
        }
    }

    private sealed class RecordingVideoPlayerWindowService : IVideoPlayerWindowService
    {
        public event EventHandler? LibraryChanged;

        public List<VideoItem> OpenedVideos { get; } = [];
        public List<IReadOnlyList<VideoItem>> OpenedPlaylists { get; } = [];

        public void RaiseLibraryChanged() => LibraryChanged?.Invoke(this, EventArgs.Empty);

        public Task OpenAsync(VideoItem video, CancellationToken ct = default)
        {
            OpenedVideos.Add(video);
            OpenedPlaylists.Add([video]);
            return Task.CompletedTask;
        }

        public Task OpenAsync(VideoItem video, IReadOnlyList<VideoItem> playlist, CancellationToken ct = default)
        {
            OpenedVideos.Add(video);
            OpenedPlaylists.Add(playlist);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingFileRevealService : IFileRevealService
    {
        public List<string> RevealedPaths { get; } = [];

        public Task<Result> RevealInFileExplorerAsync(string filePath, CancellationToken ct = default)
        {
            RevealedPaths.Add(filePath);
            return Task.FromResult(Result.Success());
        }
    }
}
