using System.Reflection;
using System.Collections.ObjectModel;
using FluentAssertions;
using Moq;
using Niratan.Models.Manga;
using Niratan.Services.Manga;
using Niratan.Services.UI;
using Niratan.ViewModels.Components;
using Niratan.ViewModels.Pages;

namespace Niratan.Tests.ViewModels.Pages;

public sealed class MangaLibraryPageViewModelTests
{
    [Fact]
    public async Task MangaDiscoveryHome_LoadsRecommendationFeedsConcurrently()
    {
        var allRequestsStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var startedRequests = 0;
        var discovery = new Mock<IMangaDiscoveryService>(MockBehavior.Strict);
        discovery.SetupGet(service => service.Providers)
            .Returns([new MangaDiscoveryProvider("bangumi", "Bangumi")]);
        discovery.Setup(service => service.GetFeeds(
                "bangumi",
                MangaDiscoveryFeedKind.Recommendation))
            .Returns(
            [
                new MangaDiscoveryFeed("bangumi", "rank", "Rank", MangaDiscoveryFeedKind.Recommendation),
                new MangaDiscoveryFeed("bangumi", "heat", "Heat", MangaDiscoveryFeedKind.Recommendation),
                new MangaDiscoveryFeed("bangumi", "date", "Date", MangaDiscoveryFeedKind.Recommendation),
            ]);
        discovery.Setup(service => service.GetPageAsync(
                "bangumi",
                It.IsAny<MangaDiscoveryRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, MangaDiscoveryRequest, CancellationToken>(async (_, request, ct) =>
            {
                if (Interlocked.Increment(ref startedRequests) == 3)
                    allRequestsStarted.TrySetResult();
                await allRequestsStarted.Task.WaitAsync(ct);
                return new MangaDiscoveryPage(
                    "bangumi",
                    request.FeedId,
                    1,
                    1,
                    [DiscoveryItem(request.FeedId)]);
            });
        discovery.Setup(service => service.GetPosterPathAsync(
                It.IsAny<MangaDiscoveryItem>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var viewModel = CreateViewModel(discovery: discovery.Object);

        var initializeTask = viewModel.InitializeBrowseAsync(MangaHomeSection.Discover);
        await allRequestsStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        await initializeTask;

        startedRequests.Should().Be(3);
        viewModel.MangaDiscoverSections.Should().HaveCount(3);
    }

    [Fact]
    public async Task MangaDiscoveryHome_PublishesFastFeedBeforeSlowFeedCompletes()
    {
        var releaseSlowFeed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSectionPublished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new Mock<IMangaDiscoveryService>(MockBehavior.Strict);
        discovery.SetupGet(service => service.Providers)
            .Returns([new MangaDiscoveryProvider("bangumi", "Bangumi")]);
        discovery.Setup(service => service.GetFeeds(
                "bangumi",
                MangaDiscoveryFeedKind.Recommendation))
            .Returns(
            [
                new MangaDiscoveryFeed("bangumi", "rank", "Rank", MangaDiscoveryFeedKind.Recommendation),
                new MangaDiscoveryFeed("bangumi", "heat", "Heat", MangaDiscoveryFeedKind.Recommendation),
            ]);
        discovery.Setup(service => service.GetPageAsync(
                "bangumi",
                It.IsAny<MangaDiscoveryRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, MangaDiscoveryRequest, CancellationToken>(async (_, request, ct) =>
            {
                if (request.FeedId == "heat")
                    await releaseSlowFeed.Task.WaitAsync(ct);
                return new MangaDiscoveryPage(
                    "bangumi",
                    request.FeedId,
                    1,
                    1,
                    [DiscoveryItem(request.FeedId)]);
            });
        var viewModel = CreateViewModel(discovery: discovery.Object);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.MangaDiscoverSections)
                && viewModel.MangaDiscoverSections.Count == 1)
            {
                firstSectionPublished.TrySetResult();
            }
        };

        var initializeTask = viewModel.InitializeBrowseAsync(MangaHomeSection.Discover);
        await firstSectionPublished.Task.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        viewModel.MangaDiscoverSections.Should().ContainSingle();
        viewModel.MangaDiscoverSections[0].FeedId.Should().Be("rank");
        var progressivelyPublishedSections = viewModel.MangaDiscoverSections;
        initializeTask.IsCompleted.Should().BeFalse();
        releaseSlowFeed.TrySetResult();
        await initializeTask;
        viewModel.MangaDiscoverSections.Should().BeSameAs(
            progressivelyPublishedSections);
        viewModel.MangaDiscoverSections.Should().HaveCount(2);
    }

    [Fact]
    public async Task MangaDiscoveryReturningToDiscoverReusesLoadedSections()
    {
        var requests = 0;
        var discovery = new Mock<IMangaDiscoveryService>(MockBehavior.Strict);
        discovery.SetupGet(service => service.Providers)
            .Returns([new MangaDiscoveryProvider("bangumi", "Bangumi")]);
        discovery.Setup(service => service.GetFeeds(
                "bangumi",
                MangaDiscoveryFeedKind.Recommendation))
            .Returns(
            [
                new MangaDiscoveryFeed("bangumi", "rank", "Rank", MangaDiscoveryFeedKind.Recommendation),
            ]);
        discovery.Setup(service => service.GetPageAsync(
                "bangumi",
                It.IsAny<MangaDiscoveryRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref requests);
                return new MangaDiscoveryPage(
                    "bangumi",
                    "rank",
                    1,
                    1,
                    [DiscoveryItem("rank")]);
            });
        var viewModel = CreateViewModel(discovery: discovery.Object);

        await viewModel.InitializeBrowseAsync(MangaHomeSection.Discover);
        await viewModel.SelectDiscoverCommand.ExecuteAsync(null);

        requests.Should().Be(1);
        viewModel.MangaDiscoverSections.Should().ContainSingle();
    }

    [Fact]
    public async Task MangaDiscoveryReturningWhileInitialLoadIsActiveDoesNotRestartIt()
    {
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var discovery = new Mock<IMangaDiscoveryService>(MockBehavior.Strict);
        discovery.SetupGet(service => service.Providers)
            .Returns([new MangaDiscoveryProvider("bangumi", "Bangumi")]);
        discovery.Setup(service => service.GetFeeds(
                "bangumi",
                MangaDiscoveryFeedKind.Recommendation))
            .Returns(
            [
                new MangaDiscoveryFeed(
                    "bangumi",
                    "rank",
                    "Rank",
                    MangaDiscoveryFeedKind.Recommendation),
            ]);
        discovery.Setup(service => service.GetPageAsync(
                "bangumi",
                It.IsAny<MangaDiscoveryRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, MangaDiscoveryRequest, CancellationToken>(
                async (_, request, ct) =>
                {
                    Interlocked.Increment(ref requests);
                    requestStarted.TrySetResult();
                    await releaseRequest.Task.WaitAsync(ct);
                    return new MangaDiscoveryPage(
                        "bangumi",
                        request.FeedId,
                        1,
                        1,
                        [DiscoveryItem(request.FeedId)]);
                });
        var viewModel = CreateViewModel(discovery: discovery.Object);

        var initializeTask = viewModel.InitializeBrowseAsync(
            MangaHomeSection.Discover);
        await requestStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        viewModel.SelectedSection = MangaHomeSection.Browse;
        var returnTask = viewModel.SelectBrowseSectionAsync(
            MangaHomeSection.Discover);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        requests.Should().Be(1);
        releaseRequest.TrySetResult();
        await Task.WhenAll(initializeTask, returnTask);
        viewModel.MangaDiscoverSections.Should().ContainSingle();
    }

    [Fact]
    public async Task MangaDiscoveryRefreshClearsMetadataCacheBeforeReloading()
    {
        var discovery = new Mock<IMangaDiscoveryService>(MockBehavior.Strict);
        discovery.SetupGet(service => service.Providers)
            .Returns([new MangaDiscoveryProvider("bangumi", "Bangumi")]);
        discovery.Setup(service => service.GetFeeds(
                "bangumi",
                MangaDiscoveryFeedKind.Recommendation))
            .Returns(
            [
                new MangaDiscoveryFeed(
                    "bangumi",
                    "rank",
                    "Rank",
                    MangaDiscoveryFeedKind.Recommendation),
            ]);
        discovery.Setup(service => service.GetPageAsync(
                "bangumi",
                It.IsAny<MangaDiscoveryRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MangaDiscoveryPage(
                "bangumi",
                "rank",
                1,
                1,
                [DiscoveryItem("rank")]));
        discovery.Setup(service => service.ClearCache());
        var viewModel = CreateViewModel(discovery: discovery.Object);

        await viewModel.InitializeBrowseAsync(MangaHomeSection.Discover);
        await viewModel.RefreshMangaDiscoverCommand.ExecuteAsync(null);

        discovery.Verify(service => service.ClearCache(), Times.Once);
        discovery.Verify(service => service.GetPageAsync(
            "bangumi",
            It.IsAny<MangaDiscoveryRequest>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task MangaDiscoveryCategoryReordersLoadedSectionsWithoutReloading()
    {
        var requests = 0;
        var discovery = new Mock<IMangaDiscoveryService>(MockBehavior.Strict);
        discovery.SetupGet(service => service.Providers)
            .Returns([new MangaDiscoveryProvider("bangumi", "Bangumi")]);
        discovery.Setup(service => service.GetFeeds(
                "bangumi",
                MangaDiscoveryFeedKind.Recommendation))
            .Returns(
            [
                new MangaDiscoveryFeed("bangumi", "rank", "Rank", MangaDiscoveryFeedKind.Recommendation),
                new MangaDiscoveryFeed("bangumi", "heat", "Heat", MangaDiscoveryFeedKind.Recommendation),
            ]);
        discovery.Setup(service => service.GetPageAsync(
                "bangumi",
                It.IsAny<MangaDiscoveryRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, MangaDiscoveryRequest, CancellationToken>((_, request, _) =>
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult(new MangaDiscoveryPage(
                    "bangumi",
                    request.FeedId,
                    1,
                    1,
                    [DiscoveryItem(request.FeedId)]));
            });
        var viewModel = CreateViewModel(discovery: discovery.Object);

        await viewModel.InitializeBrowseAsync(MangaHomeSection.Discover);
        viewModel.SelectedMangaDiscoveryFeed = viewModel.MangaDiscoveryFeeds
            .Single(feed => feed.Id == "heat");

        requests.Should().Be(2);
        viewModel.MangaDiscoverSections.Select(section => section.FeedId)
            .Should().Equal("heat", "rank");
    }

    [Fact]
    public void MangaDiscoveryCardFactsMatchVideoCardFormat()
    {
        var item = DiscoveryItem("facts") with { Year = 2024, Score = 8.3 };
        var card = new MangaDiscoveryCardViewModel(item, () => Task.CompletedTask);

        card.FactsText.Should().Be(
            $"2024 · ★ {8.3.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)}");
    }

    [Fact]
    public async Task MangaDiscoveryPaginationKeepsTheQueryThatCreatedPageOne()
    {
        var searches = new List<(string Query, int Page)>();
        var discovery = new Mock<IMangaDiscoveryService>(MockBehavior.Strict);
        discovery.SetupGet(service => service.Providers)
            .Returns([new MangaDiscoveryProvider("bangumi", "Bangumi")]);
        discovery.Setup(service => service.GetFeeds(
                "bangumi",
                MangaDiscoveryFeedKind.Recommendation))
            .Returns(
            [
                new MangaDiscoveryFeed(
                    "bangumi",
                    "rank",
                    "Rank",
                    MangaDiscoveryFeedKind.Recommendation),
            ]);
        discovery.Setup(service => service.GetPageAsync(
                "bangumi",
                It.IsAny<MangaDiscoveryRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MangaDiscoveryPage(
                "bangumi",
                "rank",
                1,
                1,
                [DiscoveryItem("home")]));
        discovery.Setup(service => service.SearchAsync(
                "bangumi",
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, string, int, CancellationToken>(
                (provider, query, page, _) =>
                {
                    searches.Add((query, page));
                    return Task.FromResult(new MangaDiscoveryPage(
                        provider,
                        "search",
                        page,
                        2,
                        [DiscoveryItem(page.ToString())]));
                });
        var viewModel = CreateViewModel(discovery: discovery.Object);

        await viewModel.InitializeBrowseAsync(MangaHomeSection.Discover);
        viewModel.MangaDiscoverQuery = "First title";
        await viewModel.SearchMangaDiscoverCommand.ExecuteAsync(null);
        viewModel.MangaDiscoverQuery = "Edited but not submitted";
        await viewModel.LoadMoreMangaDiscoverCommand.ExecuteAsync(null);

        searches.Should().Equal(
            ("First title", 1),
            ("First title", 2));
        viewModel.MangaDiscoverItems.Should().HaveCount(2);
    }

    [Fact]
    public async Task MangaDiscoveryNewSearchCancelsPostersFromPreviousResults()
    {
        var posterStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var posterCanceled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var discovery = new Mock<IMangaDiscoveryService>(MockBehavior.Strict);
        discovery.SetupGet(service => service.Providers)
            .Returns([new MangaDiscoveryProvider("bangumi", "Bangumi")]);
        discovery.Setup(service => service.GetFeeds(
                "bangumi",
                MangaDiscoveryFeedKind.Recommendation))
            .Returns(
            [
                new MangaDiscoveryFeed(
                    "bangumi",
                    "rank",
                    "Rank",
                    MangaDiscoveryFeedKind.Recommendation),
            ]);
        discovery.Setup(service => service.GetPageAsync(
                "bangumi",
                It.IsAny<MangaDiscoveryRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MangaDiscoveryPage(
                "bangumi",
                "rank",
                1,
                1,
                [DiscoveryItem("old")]));
        discovery.Setup(service => service.SearchAsync(
                "bangumi",
                "new title",
                1,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MangaDiscoveryPage(
                "bangumi",
                "search",
                1,
                1,
                [DiscoveryItem("new")]));
        discovery.Setup(service => service.GetPosterPathAsync(
                It.IsAny<MangaDiscoveryItem>(),
                It.IsAny<CancellationToken>()))
            .Returns<MangaDiscoveryItem, CancellationToken>(async (_, ct) =>
            {
                posterStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    posterCanceled.TrySetResult();
                    throw;
                }
            });
        var viewModel = CreateViewModel(discovery: discovery.Object);

        await viewModel.InitializeBrowseAsync(MangaHomeSection.Discover);
        var oldPosterTask = viewModel.EnsureMangaDiscoveryPosterAsync(
            viewModel.MangaDiscoverSections.Single().Items.Single());
        await posterStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        viewModel.MangaDiscoverQuery = "new title";
        await viewModel.SearchMangaDiscoverCommand.ExecuteAsync(null);

        await posterCanceled.Task.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        await oldPosterTask;
    }

    [Fact]
    public async Task MangaDiscoveryPosters_UseBoundedParallelDownloads()
    {
        var releaseDownloads = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sixDownloadsStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var activeDownloads = 0;
        var maximumActiveDownloads = 0;
        var totalDownloads = 0;
        var discovery = new Mock<IMangaDiscoveryService>(MockBehavior.Strict);
        discovery.Setup(service => service.GetPosterPathAsync(
                It.IsAny<MangaDiscoveryItem>(),
                It.IsAny<CancellationToken>()))
            .Returns<MangaDiscoveryItem, CancellationToken>(async (_, ct) =>
            {
                var active = Interlocked.Increment(ref activeDownloads);
                Interlocked.Increment(ref totalDownloads);
                UpdateMaximum(ref maximumActiveDownloads, active);
                if (active == 6)
                    sixDownloadsStarted.TrySetResult();
                try
                {
                    await releaseDownloads.Task.WaitAsync(ct);
                    return null;
                }
                finally
                {
                    Interlocked.Decrement(ref activeDownloads);
                }
            });
        var viewModel = CreateViewModel(discovery: discovery.Object);
        var cards = Enumerable.Range(1, 12)
            .Select(index => new MangaDiscoveryCardViewModel(
                DiscoveryItem(index.ToString()),
                () => Task.CompletedTask))
            .ToList();

        var loadTask = InvokePrivateTask(
            viewModel,
            "LoadMangaDiscoveryPostersAsync",
            cards,
            TestContext.Current.CancellationToken);
        await sixDownloadsStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        maximumActiveDownloads.Should().Be(6);
        totalDownloads.Should().Be(6);
        releaseDownloads.TrySetResult();
        await loadTask;
        totalDownloads.Should().Be(12);
        maximumActiveDownloads.Should().Be(6);
    }

    [Fact]
    public async Task MangaDiscoveryDetails_DoesNotWaitForPosterBeforeMatchingExtension()
    {
        var source = new MihonInstalledExtension
        {
            SourceId = "1",
            SourceName = "Example source",
            PackageName = "extension.example",
        };
        var manga = new MihonManga
        {
            Url = "/title/1",
            Title = "Example",
        };
        var mihon = new Mock<IMihonExtensionService>(MockBehavior.Strict);
        mihon.Setup(service => service.LoadConfigurationAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MihonExtensionConfiguration());
        mihon.Setup(service => service.GetInstalledSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([source]);
        mihon.Setup(service => service.BrowseAsync(
                It.IsAny<MihonExtensionConfiguration>(),
                source,
                "Example",
                1,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MihonPagedManga { MangaList = [manga] });
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
        var discovery = new Mock<IMangaDiscoveryService>(MockBehavior.Strict);
        discovery.Setup(service => service.GetPosterPathAsync(
                It.IsAny<MangaDiscoveryItem>(),
                It.IsAny<CancellationToken>()))
            .Returns<MangaDiscoveryItem, CancellationToken>((_, ct) =>
                Task.Delay(Timeout.InfiniteTimeSpan, ct)
                    .ContinueWith(
                        _ => (string?)null,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default));
        var viewModel = CreateViewModel(mihon.Object, discovery.Object);
        var item = DiscoveryItem("1") with
        {
            OriginalTitle = "Original example",
            Year = 2024,
            Overview = "Discovery overview",
            Score = 8.4,
            Aliases = ["Example alias"],
        };
        var details = new RemoteMangaDetailViewModel(
            "Bangumi",
            item.ProviderItemId,
            item.Title,
            supportsOnlineLibrary: false);
        details.ApplyDiscoveryDetails(item);
        var ct = InvokePrivate(
                viewModel,
                "BeginRemoteDetailsLoad",
                details)
            .Should()
            .BeOfType<CancellationToken>()
            .Subject;

        var loadTask = InvokePrivateTask(
            viewModel,
            "LoadMangaDiscoveryDetailsAsync",
            details,
            item,
            ct);
        await loadTask.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        viewModel.SelectedRemoteMangaDetails.Should().NotBeNull();
        viewModel.SelectedRemoteMangaDetails!.Title.Should().Be("Example");
        viewModel.SelectedRemoteMangaDetails.OriginalTitle.Should().Be("Original example");
        viewModel.SelectedRemoteMangaDetails.Metadata.Should().Contain("2024");
        viewModel.SelectedRemoteMangaDetails.Metadata.Should().Contain("8.4");
        viewModel.SelectedRemoteMangaDetails.Description.Should().Be("Discovery overview");
        viewModel.SelectedRemoteMangaDetails.SearchTitles.Should().Contain("Example alias");
        viewModel.OnNavigatedFrom();
    }

    [Fact]
    public async Task MangaDiscoveryExtensionMatchingSkipsUnrelatedFirstResultAndTriesAliases()
    {
        var source = new MihonInstalledExtension
        {
            SourceId = "1",
            SourceName = "Example source",
            PackageName = "extension.example",
        };
        var unrelated = new MihonManga
        {
            Url = "/title/unrelated",
            Title = "Unrelated result",
        };
        var aliasMatch = new MihonManga
        {
            Url = "/title/alias",
            Title = "Exact alias",
        };
        var mihon = new Mock<IMihonExtensionService>(MockBehavior.Strict);
        mihon.Setup(service => service.BrowseAsync(
                It.IsAny<MihonExtensionConfiguration>(),
                source,
                "Primary title",
                1,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MihonPagedManga { MangaList = [unrelated] });
        mihon.Setup(service => service.BrowseAsync(
                It.IsAny<MihonExtensionConfiguration>(),
                source,
                "Exact alias",
                1,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MihonPagedManga { MangaList = [aliasMatch] });
        var viewModel = CreateViewModel(mihon.Object);

        var result = await ((Task<MihonManga?>)InvokePrivate(
            viewModel,
            "FindMihonMangaByTitlesAsync",
            new MihonExtensionConfiguration(),
            source,
            new[] { "Primary title", "Exact alias" },
            TestContext.Current.CancellationToken)!)!;

        result.Should().BeSameAs(aliasMatch);
        mihon.Verify(service => service.BrowseAsync(
            It.IsAny<MihonExtensionConfiguration>(),
            source,
            "Exact alias",
            1,
            It.IsAny<CancellationToken>()), Times.Once);
    }

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

    [Fact]
    public async Task MihonDetails_CanSwitchInstalledExtensionByTitle()
    {
        var firstSource = new MihonInstalledExtension
        {
            SourceId = "1",
            SourceName = "First source",
            Lang = "ja",
            BaseUrl = "https://first.example",
            PackageName = "extension.first",
        };
        var secondSource = new MihonInstalledExtension
        {
            SourceId = "2",
            SourceName = "Second source",
            Lang = "en",
            BaseUrl = "https://second.example",
            PackageName = "extension.second",
        };
        var firstManga = new MihonManga
        {
            Url = "/first/title",
            Title = "Example title",
        };
        var secondManga = new MihonManga
        {
            Url = "/second/title",
            Title = "Example title",
        };
        var mihon = new Mock<IMihonExtensionService>(MockBehavior.Strict);
        mihon.Setup(service => service.GetMangaDetailsAsync(
                It.IsAny<MihonExtensionConfiguration>(),
                firstSource,
                firstManga,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstManga);
        mihon.Setup(service => service.GetChaptersAsync(
                It.IsAny<MihonExtensionConfiguration>(),
                firstSource,
                firstManga,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MihonChapter { Name = "First chapter", Url = "/first/chapter" }]);
        mihon.Setup(service => service.GetThumbnailPathAsync(
                firstSource,
                firstManga,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        mihon.Setup(service => service.BrowseAsync(
                It.IsAny<MihonExtensionConfiguration>(),
                secondSource,
                "Example title",
                1,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MihonPagedManga { MangaList = [secondManga] });
        mihon.Setup(service => service.GetMangaDetailsAsync(
                It.IsAny<MihonExtensionConfiguration>(),
                secondSource,
                secondManga,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(secondManga);
        mihon.Setup(service => service.GetChaptersAsync(
                It.IsAny<MihonExtensionConfiguration>(),
                secondSource,
                secondManga,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MihonChapter { Name = "Second chapter", Url = "/second/chapter" }]);
        mihon.Setup(service => service.GetThumbnailPathAsync(
                secondSource,
                secondManga,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        var viewModel = new MangaLibraryPageViewModel(
            Mock.Of<IMangaLibraryService>(),
            Mock.Of<IMangaReaderWindowService>(),
            Mock.Of<ISuwayomiService>(),
            mihon.Object,
            Mock.Of<IDialogService>(),
            Mock.Of<INotificationService>());
        viewModel.MihonInstalledSources = new ObservableCollection<MihonInstalledExtension>
        {
            firstSource,
            secondSource,
        };

        await InvokePrivateAsync(
            viewModel,
            "ShowMihonMangaDetailsAsync",
            firstSource,
            firstManga);

        var secondOption = viewModel.SelectedRemoteMangaDetails!
            .ExtensionOptions
            .Single(option => option.Source == secondSource);
        await viewModel.SelectRemoteMangaExtensionCommand
            .ExecuteAsync(secondOption);

        viewModel.SelectedRemoteMangaDetails.Title.Should().Be("Example title");
        viewModel.SelectedRemoteMangaDetails.Chapters
            .Should().ContainSingle(chapter => chapter.Title == "Second chapter");
        viewModel.SelectedRemoteMangaDetails.ExtensionOptions
            .Single(option => option.Source == secondSource)
            .IsSelected.Should().BeTrue();
        viewModel.SelectedRemoteMangaDetails.SelectedExtensionId.Should().Be(
            RemoteMangaExtensionOptionViewModel.GetKey(secondSource));
        viewModel.SelectedRemoteMangaDetails.SelectedExtension.Should().NotBeNull();
        viewModel.SelectedRemoteMangaDetails.SelectedExtension!.Name.Should().Be(
            "Second source");
        mihon.Verify(service => service.BrowseAsync(
            It.IsAny<MihonExtensionConfiguration>(),
            secondSource,
            "Example title",
            1,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static object? InvokePrivate(
        object instance,
        string methodName,
        params object[] arguments) =>
        GetPrivateMethod(instance, methodName).Invoke(instance, arguments);

    private static async Task InvokePrivateAsync(
        object instance,
        string methodName,
        params object[] arguments)
    {
        var task = InvokePrivateTask(instance, methodName, arguments);
        await task;
    }

    private static Task InvokePrivateTask(
        object instance,
        string methodName,
        params object[] arguments) =>
        GetPrivateMethod(instance, methodName)
            .Invoke(instance, arguments)
            .Should()
            .BeAssignableTo<Task>()
            .Subject;

    private static MangaLibraryPageViewModel CreateViewModel(
        IMihonExtensionService? mihon = null,
        IMangaDiscoveryService? discovery = null) =>
        new(
            Mock.Of<IMangaLibraryService>(),
            Mock.Of<IMangaReaderWindowService>(),
            Mock.Of<ISuwayomiService>(),
            mihon ?? Mock.Of<IMihonExtensionService>(),
            Mock.Of<IDialogService>(),
            Mock.Of<INotificationService>(),
            discovery);

    private static MangaDiscoveryItem DiscoveryItem(string id) =>
        new(
            "bangumi",
            id,
            "Example",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            ["Example"]);

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (candidate <= current
                || Interlocked.CompareExchange(ref maximum, candidate, current) == current)
            {
                return;
            }
        }
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
