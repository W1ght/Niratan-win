using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Niratan.Enums;
using Niratan.Models.Common;
using Niratan.Models.DTO;
using Niratan.Models.Nyaa;
using Niratan.Models.Settings;
using Niratan.Models.Video;
using Niratan.Services.Nyaa;
using Niratan.Services.QBittorrent;
using Niratan.Services.Settings;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class NyaaSubscriptionServiceTests
{
    [Fact]
    public async Task MonoTorrent_subscription_marks_release_seen_only_after_task_completes()
    {
        var selected = Item("ep1", "[SubsPlease] Test Anime - 01 [1080p]", trusted: true);
        var newer = Item("ep2", "[SubsPlease] Test Anime - 02 [1080p]", trusted: true);
        var resources = SearchReturning(selected, newer);
        IReadOnlyList<NyaaDownloadTaskSnapshot> tasks = [];
        var manager = new Mock<INyaaDownloadManager>();
        manager.Setup(value => value.GetTasks()).Returns(() => tasks);
        manager.Setup(value => value.Enqueue(selected)).Returns("task-ep1");
        manager.Setup(value => value.Enqueue(newer)).Returns("task-ep2");
        var settings = new MutableSettingsService(new AppSettings());
        using var service = CreateService(resources, manager, settings);
        var posterPath = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Niratan",
            "Cache",
            "VideoMetadataArtwork",
            "poster.jpg"));

        var result = await service.SubscribeAsync(
            Identity() with { PosterUrl = "https://image.example/poster.jpg" },
            "Test Anime",
            "1_0",
            selected,
            1,
            new NyaaSubscriptionArtwork(
                "https://image.example/detail.jpg",
                posterPath));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1);
        manager.Verify(value => value.Enqueue(selected), Times.Once);
        resources.Verify(value => value.SearchAsync(
            It.IsAny<VideoResourceSearchRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        var persisted = settings.Current.DiscoverySettings.NyaaSubscriptions.Should().ContainSingle().Subject;
        persisted.DownloadBackend.Should().Be(DownloadBackendKind.MonoTorrent);
        persisted.PosterUrl.Should().Be("https://image.example/detail.jpg");
        persisted.PosterPath.Should().Be(posterPath);
        persisted.SeenItemIds.Should().NotContain("ep1").And.NotContain("ep2");
        persisted.ProcessedLogicalItemKeys.Should().BeEmpty();

        tasks = [CompletedTask("task-ep1", selected)];
        manager.Raise(value => value.TasksChanged += null, EventArgs.Empty);
        await WaitUntilAsync(() => settings.Current.DiscoverySettings.NyaaSubscriptions[0]
            .SeenItemIds.Contains("ep1"));

        var checkedAgain = await service.CheckOneAsync("anilist:123");

        checkedAgain.Value.Should().Be(1);
        settings.Current.DiscoverySettings.NyaaSubscriptions[0].SeenItemIds.Should().NotContain("ep2");
        tasks = [CompletedTask("task-ep1", selected), CompletedTask("task-ep2", newer)];
        manager.Raise(value => value.TasksChanged += null, EventArgs.Empty);
        await WaitUntilAsync(() => settings.Current.DiscoverySettings.NyaaSubscriptions[0]
            .SeenItemIds.Contains("ep2"));

        settings.Current.DiscoverySettings.NyaaSubscriptions[0].SeenItemIds.Should().Contain("ep2");
        settings.Current.DiscoverySettings.NyaaSubscriptions[0].ProcessedLogicalItemKeys
            .Should().Contain("S01E0001").And.Contain("S01E0002");
    }

    [Fact]
    public async Task MonoTorrent_subscription_resolves_download_manager_outside_submit_caller()
    {
        var selected = Item("ep1", "[Group] Test Anime - 01 [1080p]", trusted: false);
        var resources = SearchReturning(selected);
        var manager = ManagerWithNoTasks();
        manager.Setup(value => value.Enqueue(selected)).Returns("task-ep1");
        var settings = new MutableSettingsService(new AppSettings());
        var callerThreadId = Environment.CurrentManagedThreadId;
        var callerActive = 1;
        var resolvedInline = 0;
        var lazyManager = new Lazy<INyaaDownloadManager>(() =>
        {
            if (Environment.CurrentManagedThreadId == callerThreadId
                && Volatile.Read(ref callerActive) == 1)
            {
                Volatile.Write(ref resolvedInline, 1);
            }
            return manager.Object;
        });
        using var service = new NyaaSubscriptionService(
            resources.Object,
            Mock.Of<IVideoDiscoveryService>(),
            lazyManager,
            Mock.Of<IQbittorrentDownloadCoordinator>(),
            settings,
            NullLogger<NyaaSubscriptionService>.Instance);

        var pending = service.SubscribeAsync(
            Identity(),
            "Test Anime",
            "1_0",
            selected,
            TestContext.Current.CancellationToken);
        Volatile.Write(ref callerActive, 0);
        var result = await pending;

        result.IsSuccess.Should().BeTrue();
        Volatile.Read(ref resolvedInline).Should().Be(0);
        manager.Verify(value => value.Enqueue(selected), Times.Once);
    }

    [Fact]
    public async Task Inclusive_start_and_logical_episode_keys_prevent_duplicate_release_ids()
    {
        var episodeOneLowerSeeders = Item(
            "ep1-low",
            "[Group] Test Anime - 01 [1080p]",
            trusted: false,
            seeders: 2);
        var episodeOnePreferred = Item(
            "ep1-preferred",
            "[Group] Test Anime - 01 [1080p]",
            trusted: false,
            seeders: 20);
        var episodeTwo = Item("ep2", "[Group] Test Anime - 02 [1080p]", trusted: false);
        IReadOnlyList<NyaaTorrentItem> searchItems =
            [episodeOneLowerSeeders, episodeOnePreferred, episodeTwo];
        var resources = new Mock<IVideoResourceSearchService>();
        resources.Setup(value => value.SearchAsync(
                It.IsAny<VideoResourceSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Result<IReadOnlyList<NyaaTorrentItem>>.Success(searchItems));
        var stored = Subscription(enabled: true);
        stored.DownloadBackend = DownloadBackendKind.Qbittorrent;
        stored.Trusted = false;
        stored.SelectedCategory = "Anime";
        var settings = new MutableSettingsService(new AppSettings
        {
            DiscoverySettings = new DiscoverySettings { NyaaSubscriptions = [stored] },
        });
        var coordinator = new Mock<IQbittorrentDownloadCoordinator>();
        coordinator.Setup(value => value.AddAsync(
                It.IsAny<NyaaTorrentItem>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        using var service = CreateService(
            resources,
            ManagerWithNoTasks(),
            settings,
            coordinator);

        var firstCheck = await service.CheckOneAsync("anilist:123");

        firstCheck.Value.Should().Be(2);
        coordinator.Verify(value => value.AddAsync(
            episodeOnePreferred,
            It.IsAny<CancellationToken>()), Times.Once);
        coordinator.Verify(value => value.AddAsync(
            episodeOneLowerSeeders,
            It.IsAny<CancellationToken>()), Times.Never);
        coordinator.Verify(value => value.AddAsync(
            episodeTwo,
            It.IsAny<CancellationToken>()), Times.Once);
        settings.Current.DiscoverySettings.NyaaSubscriptions.Single()
            .ProcessedLogicalItemKeys.Should()
            .Contain("S01E0001").And.Contain("S01E0002");

        var alternateEpisodeOne = Item(
            "ep1-alternate-id",
            "[Group] Test Anime - 01 [1080p]",
            trusted: false,
            seeders: 50);
        var episodeThree = Item("ep3", "[Group] Test Anime - 03 [1080p]", trusted: false);
        searchItems = [alternateEpisodeOne, episodeThree];

        var secondCheck = await service.CheckOneAsync("anilist:123");

        secondCheck.Value.Should().Be(1);
        coordinator.Verify(value => value.AddAsync(
            alternateEpisodeOne,
            It.IsAny<CancellationToken>()), Times.Never);
        coordinator.Verify(value => value.AddAsync(
            episodeThree,
            It.IsAny<CancellationToken>()), Times.Once);
        settings.Current.DiscoverySettings.NyaaSubscriptions.Single()
            .ProcessedLogicalItemKeys.Should().Contain("S01E0003");
    }

    [Theory]
    [InlineData("[Group] Test Anime - 01-12 [1080p]", false)]
    [InlineData("[Group] Test Anime - 01 [1080p]", true)]
    public async Task Subscribe_rejects_batch_and_remake_as_rule_sources(
        string title,
        bool isRemake)
    {
        var selected = Item("selected", title, trusted: false, remake: isRemake);
        var manager = ManagerWithNoTasks();
        var settings = new MutableSettingsService(new AppSettings());
        using var service = CreateService(
            SearchReturning(selected),
            manager,
            settings);

        var result = await service.SubscribeAsync(Identity(), "Test Anime", "1_0", selected);

        result.IsSuccess.Should().BeFalse();
        settings.Current.DiscoverySettings.NyaaSubscriptions.Should().BeEmpty();
        manager.Verify(value => value.Enqueue(It.IsAny<NyaaTorrentItem>()), Times.Never);
    }

    [Fact]
    public async Task Check_excludes_batch_and_remake_results()
    {
        var batch = Item("batch", "[Group] Test Anime - 01-12 [1080p]", trusted: false);
        var remake = Item(
            "remake",
            "[Group] Test Anime - 02 [1080p]",
            trusted: false,
            remake: true);
        var original = Item("original", "[Group] Test Anime - 02 [1080p]", trusted: false);
        var settings = new MutableSettingsService(new AppSettings
        {
            DiscoverySettings = new DiscoverySettings
            {
                NyaaSubscriptions = [Subscription(enabled: true)],
            },
        });
        var manager = ManagerWithNoTasks();
        manager.Setup(value => value.Enqueue(original)).Returns("original-task");
        using var service = CreateService(
            SearchReturning(batch, remake, original),
            manager,
            settings);

        var result = await service.CheckOneAsync("anilist:123");

        result.Value.Should().Be(1);
        manager.Verify(value => value.Enqueue(original), Times.Once);
        manager.Verify(value => value.Enqueue(batch), Times.Never);
        manager.Verify(value => value.Enqueue(remake), Times.Never);
    }

    [Fact]
    public async Task Subscription_keeps_creation_backend_when_global_backend_changes()
    {
        var selected = Item("ep1", "[Group] Test Anime - 01 [1080p]", trusted: false);
        var newer = Item("ep2", "[Group] Test Anime - 02 [1080p]", trusted: false);
        var resources = SearchReturning(selected, newer);
        var manager = new Mock<INyaaDownloadManager>();
        manager.Setup(value => value.GetTasks()).Returns([]);
        var coordinator = new Mock<IQbittorrentDownloadCoordinator>();
        coordinator.Setup(value => value.AddAsync(selected, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        coordinator.Setup(value => value.AddAsync(newer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var appSettings = new AppSettings { DownloadBackend = DownloadBackendKind.Qbittorrent };
        var settings = new MutableSettingsService(appSettings);
        using var service = CreateService(resources, manager, settings, coordinator);

        var subscribed = await service.SubscribeAsync(Identity(), "Test Anime", "1_0", selected);
        appSettings.DownloadBackend = DownloadBackendKind.MonoTorrent;
        var checkedAgain = await service.CheckOneAsync("anilist:123");

        subscribed.IsSuccess.Should().BeTrue();
        checkedAgain.IsSuccess.Should().BeTrue();
        coordinator.Verify(
            value => value.AddAsync(selected, It.IsAny<CancellationToken>()),
            Times.Once);
        coordinator.Verify(
            value => value.AddAsync(newer, It.IsAny<CancellationToken>()),
            Times.Once);
        manager.Verify(value => value.Enqueue(It.IsAny<NyaaTorrentItem>()), Times.Never);
        settings.Current.DiscoverySettings.NyaaSubscriptions[0].DownloadBackend
            .Should().Be(DownloadBackendKind.Qbittorrent);
        settings.Current.DiscoverySettings.NyaaSubscriptions[0].SeenItemIds.Should().Contain("ep2");
    }

    [Fact]
    public async Task Failed_external_enqueue_records_error_without_marking_release_seen()
    {
        var selected = Item("ep1", "[Group] Test Anime - 01 [1080p]", trusted: false);
        var newer = Item("ep2", "[Group] Test Anime - 02 [1080p]", trusted: false);
        var coordinator = new Mock<IQbittorrentDownloadCoordinator>();
        coordinator.Setup(value => value.AddAsync(selected, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure("qB is offline"));
        var settings = new MutableSettingsService(new AppSettings
        {
            DownloadBackend = DownloadBackendKind.Qbittorrent,
        });
        using var service = CreateService(
            SearchReturning(selected, newer),
            ManagerWithNoTasks(),
            settings,
            coordinator);

        var result = await service.SubscribeAsync(Identity(), "Test Anime", "1_0", selected);

        result.IsSuccess.Should().BeFalse();
        settings.Current.DiscoverySettings.NyaaSubscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_subscription_is_not_polled_and_snapshots_are_clones()
    {
        var stored = Subscription(enabled: false);
        var settings = new MutableSettingsService(new AppSettings
        {
            DiscoverySettings = new DiscoverySettings { NyaaSubscriptions = [stored] },
        });
        var resources = new Mock<IVideoResourceSearchService>();
        using var service = CreateService(resources, ManagerWithNoTasks(), settings);

        var snapshot = service.GetSubscriptions().Single();
        snapshot.Title = "mutated";
        snapshot.SeenItemIds.Add("mutated");
        await service.CheckAllAsync();

        settings.Current.DiscoverySettings.NyaaSubscriptions[0].Title.Should().Be("Test Anime");
        settings.Current.DiscoverySettings.NyaaSubscriptions[0].SeenItemIds.Should().NotContain("mutated");
        resources.Verify(value => value.SearchAsync(
            It.IsAny<VideoResourceSearchRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Pausing_subscription_cancels_in_flight_search_before_any_enqueue()
    {
        var searchStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var searchCancelled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resources = new Mock<IVideoResourceSearchService>();
        resources.Setup(value => value.SearchAsync(
                It.IsAny<VideoResourceSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<VideoResourceSearchRequest, CancellationToken>(async (_, token) =>
            {
                searchStarted.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    searchCancelled.TrySetResult(true);
                    throw;
                }

                return Result<IReadOnlyList<NyaaTorrentItem>>.Success([]);
            });
        var settings = new MutableSettingsService(new AppSettings
        {
            DiscoverySettings = new DiscoverySettings
            {
                NyaaSubscriptions = [Subscription(enabled: true)],
            },
        });
        var manager = ManagerWithNoTasks();
        var coordinator = new Mock<IQbittorrentDownloadCoordinator>();
        using var service = CreateService(resources, manager, settings, coordinator);
        var testCancellation = TestContext.Current.CancellationToken;

        var check = service.CheckOneAsync("anilist:123", testCancellation);
        await searchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);
        var pause = service.SetEnabledAsync("anilist:123", false, testCancellation);

        await searchCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);
        var result = await check;
        await pause;

        result.IsCancelled.Should().BeTrue();
        settings.Current.DiscoverySettings.NyaaSubscriptions.Single().Enabled.Should().BeFalse();
        manager.Verify(value => value.Enqueue(It.IsAny<NyaaTorrentItem>()), Times.Never);
        coordinator.Verify(value => value.AddAsync(
            It.IsAny<NyaaTorrentItem>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Check_strictly_matches_title_season_trust_and_category()
    {
        var correct = Item("correct", "[Group] Air S02E03 [1080p]", trusted: false);
        var substringTitle = Item(
            "substring-title",
            "[Group] Fairy Tail S02E03 [1080p]",
            trusted: false);
        var missingSeason = Item("missing-season", "[Group] Air - 03 [1080p]", trusted: false);
        var wrongSeason = Item("wrong-season", "[Group] Air S03E03 [1080p]", trusted: false);
        var wrongTrust = Item("wrong-trust", "[Group] Air S02E03 [1080p]", trusted: true);
        var wrongCategory = Item(
            "wrong-category",
            "[Group] Air S02E03 [1080p]",
            trusted: false,
            category: "Live Action");
        var stored = Subscription(enabled: true, season: 2);
        stored.Title = "Air";
        stored.Aliases = [];
        stored.StartAfterEpisode = 3;
        stored.Trusted = false;
        stored.SelectedCategory = "Anime";
        var settings = new MutableSettingsService(new AppSettings
        {
            DiscoverySettings = new DiscoverySettings
            {
                NyaaSubscriptions = [stored],
            },
        });
        var manager = ManagerWithNoTasks();
        manager.Setup(value => value.Enqueue(correct)).Returns("correct-task");
        using var service = CreateService(
            SearchReturning(
                substringTitle,
                missingSeason,
                wrongSeason,
                wrongTrust,
                wrongCategory,
                correct),
            manager,
            settings);

        var result = await service.CheckOneAsync("anilist:123");

        result.Value.Should().Be(1);
        manager.Verify(value => value.Enqueue(correct), Times.Once);
        manager.Verify(value => value.Enqueue(substringTitle), Times.Never);
        manager.Verify(value => value.Enqueue(missingSeason), Times.Never);
        manager.Verify(value => value.Enqueue(wrongSeason), Times.Never);
        manager.Verify(value => value.Enqueue(wrongTrust), Times.Never);
        manager.Verify(value => value.Enqueue(wrongCategory), Times.Never);
    }

    [Fact]
    public async Task Accepted_movie_release_completes_one_shot_subscription()
    {
        var selected = Item("selected", "[Group] Test Movie [1080p]", trusted: false);
        var resources = SearchReturning(selected);
        var coordinator = new Mock<IQbittorrentDownloadCoordinator>();
        coordinator.Setup(value => value.AddAsync(selected, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var settings = new MutableSettingsService(new AppSettings
        {
            DownloadBackend = DownloadBackendKind.Qbittorrent,
        });
        using var service = CreateService(resources, ManagerWithNoTasks(), settings, coordinator);
        var movie = Identity() with
        {
            MediaKind = VideoMetadataMediaKind.Movie,
            Title = "Test Movie",
            SeasonNumber = null,
        };

        var result = await service.SubscribeAsync(movie, "Test Movie", "1_0", selected);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1);
        settings.Current.DiscoverySettings.NyaaSubscriptions.Single().Enabled.Should().BeFalse();
        settings.Current.DiscoverySettings.NyaaSubscriptions.Single().SeenItemIds.Should().Contain("selected");
        settings.Current.DiscoverySettings.NyaaSubscriptions.Single().ProcessedLogicalItemKeys
            .Should().Contain("movie");
    }

    [Fact]
    public async Task Check_completion_merges_into_latest_discovery_settings()
    {
        var settings = new MutableSettingsService(new AppSettings
        {
            DiscoverySettings = new DiscoverySettings
            {
                ExploreProviderOrder = ["tmdb"],
                NyaaSubscriptions = [Subscription(enabled: true)],
            },
        });
        var resources = new Mock<IVideoResourceSearchService>();
        var otherSubscription = Subscription(enabled: false);
        otherSubscription.Key = "anilist:other";
        otherSubscription.ProviderItemId = "other";
        resources.Setup(value => value.SearchAsync(
                It.IsAny<VideoResourceSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                settings.Current.DiscoverySettings.ExploreProviderOrder = ["anilist", "tmdb"];
                settings.Current.DiscoverySettings.EnabledRecommendationFeeds["anilist:trending"] = false;
                settings.Current.DiscoverySettings.NyaaSubscriptions.Add(otherSubscription);
                return Result<IReadOnlyList<NyaaTorrentItem>>.Failure("offline");
            });
        using var service = CreateService(resources, ManagerWithNoTasks(), settings);

        await service.CheckOneAsync("anilist:123");

        settings.Current.DiscoverySettings.ExploreProviderOrder.Should().Equal("anilist", "tmdb");
        settings.Current.DiscoverySettings.EnabledRecommendationFeeds["anilist:trending"].Should().BeFalse();
        settings.Current.DiscoverySettings.NyaaSubscriptions.Should().Contain(value => value.Key == "anilist:other");
    }

    [Fact]
    public async Task Removing_subscription_does_not_mutate_download_tasks_or_files()
    {
        var manager = ManagerWithNoTasks();
        var coordinator = new Mock<IQbittorrentDownloadCoordinator>();
        var settings = new MutableSettingsService(new AppSettings
        {
            DiscoverySettings = new DiscoverySettings
            {
                NyaaSubscriptions = [Subscription(enabled: true)],
            },
        });
        using var service = CreateService(
            new Mock<IVideoResourceSearchService>(),
            manager,
            settings,
            coordinator);

        await service.RemoveAsync("anilist:123");

        settings.Current.DiscoverySettings.NyaaSubscriptions.Should().BeEmpty();
        manager.Verify(value => value.Cancel(It.IsAny<string>()), Times.Never);
        manager.Verify(value => value.Remove(It.IsAny<string>()), Times.Never);
        coordinator.Verify(value => value.DeleteAsync(
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Legacy_subscription_marker_is_a_disabled_setup_row_and_can_be_upgraded()
    {
        var selected = Item("ep1", "[Group] Test Anime - 01 [1080p]", trusted: false);
        var manager = ManagerWithNoTasks();
        manager.Setup(value => value.Enqueue(selected)).Returns("selected-task");
        var settings = new MutableSettingsService(new AppSettings
        {
            DiscoverySettings = new DiscoverySettings
            {
                SubscribedVideoKeys = ["anilist:123"],
            },
        });
        using var service = CreateService(
            SearchReturning(selected),
            manager,
            settings);

        var legacyRow = service.GetSubscriptions().Should().ContainSingle().Subject;

        legacyRow.Key.Should().Be("anilist:123");
        legacyRow.Enabled.Should().BeFalse();
        legacyRow.ReleaseGroup.Should().BeEmpty();
        legacyRow.Resolution.Should().BeEmpty();
        legacyRow.CreatedAt.Should().Be(DateTimeOffset.MinValue);
        service.IsSubscribed(Identity()).Should().BeFalse(
            "legacy markers need a concrete Nyaa rule before they count as managed subscriptions");

        var upgraded = await service.SubscribeAsync(
            Identity(),
            "Test Anime",
            "1_0",
            selected);

        upgraded.IsSuccess.Should().BeTrue();
        manager.Verify(value => value.Enqueue(selected), Times.Once);
        settings.Current.DiscoverySettings.SubscribedVideoKeys.Should().BeEmpty();
        var modern = settings.Current.DiscoverySettings.NyaaSubscriptions
            .Should().ContainSingle().Subject;
        modern.Key.Should().Be("anilist:123");
        modern.Enabled.Should().BeTrue();
        modern.ReleaseGroup.Should().Be("Group");
        modern.Resolution.Should().Be("1080p");
    }

    [Fact]
    public async Task Missing_cached_cover_is_rehydrated_through_discovery_artwork_pipeline()
    {
        var cacheRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Niratan",
            "Cache",
            "VideoMetadataArtwork"));
        var missingPath = Path.Combine(cacheRoot, $"missing-{Guid.NewGuid():N}.jpg");
        var restoredPath = Path.Combine(cacheRoot, $"restored-{Guid.NewGuid():N}.jpg");
        File.Exists(missingPath).Should().BeFalse();
        var stored = Subscription(enabled: true);
        stored.PosterUrl = "https://image.example/poster.jpg";
        stored.PosterPath = missingPath;
        var settings = new MutableSettingsService(new AppSettings
        {
            DiscoverySettings = new DiscoverySettings { NyaaSubscriptions = [stored] },
        });
        var discovery = new Mock<IVideoDiscoveryService>();
        discovery.Setup(value => value.ResolveArtworkAsync(
                "https://image.example/poster.jpg",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(restoredPath);
        using var service = CreateService(
            new Mock<IVideoResourceSearchService>(),
            ManagerWithNoTasks(),
            settings,
            discovery: discovery);

        await service.RefreshArtworkAsync("anilist:123");

        discovery.Verify(value => value.ResolveArtworkAsync(
            "https://image.example/poster.jpg",
            It.IsAny<CancellationToken>()), Times.Once);
        settings.Current.DiscoverySettings.NyaaSubscriptions.Single()
            .PosterPath.Should().Be(restoredPath);
        service.GetSubscriptions().Single().PosterPath.Should().Be(restoredPath);
    }

    [Fact]
    public void Legacy_json_defaults_to_enabled_MonoTorrent_and_cover_round_trips()
    {
        const string legacyJson = """
            {
              "Key": "anilist:123",
              "Title": "Test Anime",
              "PosterUrl": "https://image.example/poster.jpg",
              "PosterPath": "C:\\cache\\poster.jpg"
            }
            """;
        var options = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() },
        };

        var restored = JsonSerializer.Deserialize<NyaaVideoSubscription>(legacyJson, options)!;
        var roundTrip = JsonSerializer.Deserialize<NyaaVideoSubscription>(
            JsonSerializer.Serialize(restored, options),
            options)!;

        restored.Enabled.Should().BeTrue();
        restored.DownloadBackend.Should().Be(DownloadBackendKind.MonoTorrent);
        roundTrip.PosterUrl.Should().Be("https://image.example/poster.jpg");
        roundTrip.PosterPath.Should().Be(@"C:\cache\poster.jpg");
    }

    private static NyaaSubscriptionService CreateService(
        Mock<IVideoResourceSearchService> resources,
        Mock<INyaaDownloadManager> manager,
        MutableSettingsService settings,
        Mock<IQbittorrentDownloadCoordinator>? coordinator = null,
        Mock<IVideoDiscoveryService>? discovery = null) =>
        new(
            resources.Object,
            discovery?.Object ?? Mock.Of<IVideoDiscoveryService>(),
            new Lazy<INyaaDownloadManager>(() => manager.Object),
            coordinator?.Object ?? Mock.Of<IQbittorrentDownloadCoordinator>(),
            settings,
            NullLogger<NyaaSubscriptionService>.Instance);

    private static Mock<IVideoResourceSearchService> SearchReturning(
        params NyaaTorrentItem[] items)
    {
        var resources = new Mock<IVideoResourceSearchService>();
        resources.Setup(value => value.SearchAsync(
                It.IsAny<VideoResourceSearchRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<NyaaTorrentItem>>.Success(items));
        return resources;
    }

    private static Mock<INyaaDownloadManager> ManagerWithNoTasks()
    {
        var manager = new Mock<INyaaDownloadManager>();
        manager.Setup(value => value.GetTasks()).Returns([]);
        return manager;
    }

    private static NyaaVideoSubscription Subscription(bool enabled, int season = 1) => new()
    {
        Key = "anilist:123",
        ProviderId = "anilist",
        ProviderItemId = "123",
        MediaKind = VideoMetadataMediaKind.Anime,
        Title = "Test Anime",
        Year = 2026,
        SeasonNumber = season,
        StartAfterEpisode = 1,
        Query = "Test Anime",
        CategoryCode = "1_0",
        ReleaseGroup = "Group",
        Resolution = "1080p",
        Enabled = enabled,
        DownloadBackend = DownloadBackendKind.MonoTorrent,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static VideoMetadataCandidate Identity() => new(
        "anilist",
        "123",
        VideoMetadataMediaKind.Anime,
        "Test Anime",
        null,
        2026,
        1,
        null,
        null,
        ["Test Anime"],
        ImmutableDictionary<string, string>.Empty.Add("anilist", "123"),
        null);

    private static NyaaTorrentItem Item(
        string id,
        string title,
        bool trusted,
        bool remake = false,
        string category = "Anime",
        int seeders = 10,
        DateTimeOffset? publishedAt = null) => new(
        id,
        title,
        new Uri($"https://nyaa.si/download/{id}.torrent"),
        new Uri($"https://nyaa.si/view/{id}"),
        category,
        1024,
        seeders,
        0,
        0,
        publishedAt ?? DateTimeOffset.UtcNow,
        trusted,
        remake);

    private static NyaaDownloadTaskSnapshot CompletedTask(string taskId, NyaaTorrentItem item) => new(
        taskId,
        item,
        NyaaDownloadTaskState.Completed,
        100,
        0,
        0,
        "Completed",
        null,
        null,
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 50 && !condition(); attempt++)
            await Task.Delay(10);
        condition().Should().BeTrue();
    }

    private sealed class MutableSettingsService(AppSettings current) : ISettingsService
    {
        public AppSettings Current { get; private set; } = current;
        public event EventHandler<SettingsChangedEventArgs>? SettingChanged;

        public void Set<T>(Expression<Func<AppSettings, T>> selector, T value)
        {
            if (value is not DiscoverySettings discovery)
                throw new InvalidOperationException("Unexpected settings mutation.");
            Current.DiscoverySettings = discovery;
            SettingChanged?.Invoke(this, new SettingsChangedEventArgs
            {
                PropertyName = nameof(AppSettings.DiscoverySettings),
                NewValue = discovery,
            });
        }

        public void ReplaceCurrent(AppSettings settings) => Current = settings;
        public Task SaveAsync() => Task.CompletedTask;
        public Task LoadAsync() => Task.CompletedTask;
        public void Reset() => Current = new AppSettings();
    }
}
