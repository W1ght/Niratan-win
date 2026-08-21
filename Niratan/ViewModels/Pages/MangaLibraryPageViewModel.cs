using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Helpers;
using Niratan.Models.Manga;
using Niratan.Services.Manga;
using Niratan.Services.UI;
using Niratan.ViewModels.Components;

namespace Niratan.ViewModels.Pages;

public enum MangaHomeSection
{
    Library,
    Discover,
    Browse,
    Sources,
    Settings,
}

public enum MangaRemoteSourceKind
{
    Suwayomi,
    Mihon,
}

public partial class MangaLibraryPageViewModel : ObservableObject
{
    private readonly IMangaLibraryService _library;
    private readonly IMangaReaderWindowService _readerWindow;
    private readonly ISuwayomiService _suwayomi;
    private readonly IMihonExtensionService _mihon;
    private readonly IMangaDiscoveryService? _mangaDiscovery;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notifications;
    private CancellationTokenSource _pageCts = new();
    private bool _isSubscribedToReader;
    private bool _onlineInitialized;
    private bool _suwayomiSettingsInitialized;
    private string? _suwayomiCredentialId;
    private string _credentialServerUrl = string.Empty;
    private SuwayomiAuthMode _credentialAuthMode;
    private string _credentialUsername = string.Empty;
    private SuwayomiServerConfiguration? _onlineConfiguration;
    private bool _mihonSettingsInitialized;
    private MihonExtensionConfiguration? _mihonConfiguration;
    private readonly List<MihonRepositorySourceItemViewModel>
        _allMihonRepositorySourceItems = [];
    private int _nextBrowsePage = 1;
    private bool _browseHasNextPage;
    private string? _activeBrowseQuery;
    private string? _activeBrowseSourceIdentity;
    private CancellationTokenSource? _remoteDetailsCts;
    private SuwayomiManga? _selectedSuwayomiManga;
    private IReadOnlyList<SuwayomiChapter> _selectedSuwayomiChapters = [];
    private MihonInstalledExtension? _selectedDetailMihonSource;
    private MihonManga? _selectedMihonManga;
    private IReadOnlyList<MihonChapter> _selectedMihonChapters = [];
    private int _mangaDiscoveryPage;
    private bool _mangaDiscoveryHasMore;
    private string? _activeMangaDiscoveryProviderId;
    private string? _activeMangaDiscoveryQuery;
    private bool _mangaDiscoveryOptionsReady;
    private bool _mangaDiscoveryInitialized;
    private CancellationTokenSource? _mangaDiscoveryRequestCts;
    private CancellationTokenSource? _mangaDiscoveryPosterCts;
    private readonly SemaphoreSlim _mangaDiscoveryPosterGate = new(6, 6);

    public MangaLibraryPageViewModel(
        IMangaLibraryService library,
        IMangaReaderWindowService readerWindow,
        ISuwayomiService suwayomi,
        IMihonExtensionService mihon,
        IDialogService dialogs,
        INotificationService notifications,
        IMangaDiscoveryService? mangaDiscovery = null)
    {
        _library = library;
        _readerWindow = readerWindow;
        _suwayomi = suwayomi;
        _mihon = mihon;
        _mangaDiscovery = mangaDiscovery;
        _dialogs = dialogs;
        _notifications = notifications;
        RebuildMihonRepositorySourceItems([]);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial ObservableCollection<MangaLibraryItemViewModel> Books { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; set; }

    public bool IsEmpty => !IsLoading && Books.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibrarySectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsDiscoverSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsBrowseSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsSourcesSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsBrowseSourceDirectoryVisible))]
    [NotifyPropertyChangedFor(nameof(IsBrowseResultsVisible))]
    [NotifyPropertyChangedFor(nameof(IsLocalLibraryVisible))]
    [NotifyPropertyChangedFor(nameof(IsOnlineLibraryVisible))]
    [NotifyPropertyChangedFor(nameof(IsMangaRemoteSurfaceVisible))]
    [NotifyPropertyChangedFor(nameof(ShowLocalLibraryActions))]
    [NotifyPropertyChangedFor(nameof(ShowOnlineLibraryActions))]
    public partial MangaHomeSection SelectedSection { get; set; } =
        MangaHomeSection.Library;

    public bool IsLibrarySectionSelected =>
        SelectedSection == MangaHomeSection.Library;
    public bool IsDiscoverSectionSelected =>
        SelectedSection == MangaHomeSection.Discover;
    public bool IsBrowseSectionSelected =>
        SelectedSection == MangaHomeSection.Browse;
    public bool IsSourcesSectionSelected =>
        SelectedSection == MangaHomeSection.Sources;
    public bool IsSettingsSectionSelected =>
        SelectedSection == MangaHomeSection.Settings;
    public bool IsBrowseSourceDirectoryVisible =>
        IsBrowseSectionSelected && !IsBrowseResultsOpen;
    public bool IsBrowseResultsVisible =>
        IsBrowseSectionSelected && IsBrowseResultsOpen;
    public bool IsMangaRemoteSurfaceVisible => !IsLibrarySectionSelected;
    public bool IsRemoteSuwayomiSurfaceSelected =>
        IsDiscoverSectionSelected || IsBrowseSectionSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalSelected))]
    [NotifyPropertyChangedFor(nameof(IsLocalLibraryVisible))]
    [NotifyPropertyChangedFor(nameof(IsOnlineLibraryVisible))]
    [NotifyPropertyChangedFor(nameof(ShowLocalLibraryActions))]
    [NotifyPropertyChangedFor(nameof(ShowOnlineLibraryActions))]
    public partial bool IsOnlineSelected { get; set; }

    public bool IsLocalSelected => !IsOnlineSelected;
    public bool IsLocalLibraryVisible =>
        IsLibrarySectionSelected && IsLocalSelected;
    public bool IsOnlineLibraryVisible =>
        IsLibrarySectionSelected && IsOnlineSelected;
    public bool ShowLocalLibraryActions => IsLocalLibraryVisible;
    public bool ShowOnlineLibraryActions => IsOnlineLibraryVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOnlinePlaceholder))]
    public partial ObservableCollection<RemoteMangaLibraryItemViewModel> OnlineBooks { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOnlinePlaceholder))]
    public partial bool IsOnlineLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOnlinePlaceholder))]
    public partial bool IsOnlineConnected { get; set; }

    [ObservableProperty]
    public partial string OnlineStatusMessage { get; set; } =
        ResourceStringHelper.GetString(
            "MangaOnlineInitialStatus",
            "Connect to Suwayomi Server to view your online manga library.");

    public bool ShowOnlinePlaceholder =>
        !IsOnlineLoading && OnlineBooks.Count == 0;

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = "http://127.0.0.1:4567";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthModeIndex))]
    public partial SuwayomiAuthMode AuthMode { get; set; }

    public int AuthModeIndex
    {
        get => (int)AuthMode;
        set
        {
            if (Enum.IsDefined(typeof(SuwayomiAuthMode), value))
                AuthMode = (SuwayomiAuthMode)value;
        }
    }

    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Secret { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSuwayomiSourceKind))]
    [NotifyPropertyChangedFor(nameof(IsMihonSourceKind))]
    [NotifyPropertyChangedFor(nameof(ShowBrowsePlaceholder))]
    public partial MangaRemoteSourceKind SelectedSourceKind { get; set; } =
        MangaRemoteSourceKind.Suwayomi;

    public bool IsSuwayomiSourceKind =>
        SelectedSourceKind == MangaRemoteSourceKind.Suwayomi;
    public bool IsMihonSourceKind =>
        SelectedSourceKind == MangaRemoteSourceKind.Mihon;

    [ObservableProperty]
    public partial ObservableCollection<SuwayomiSource> Sources { get; set; } = [];

    [ObservableProperty]
    public partial SuwayomiSource? SelectedSource { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBrowseSources))]
    [NotifyPropertyChangedFor(nameof(HasNoBrowseSources))]
    public partial ObservableCollection<MangaBrowseSourceGroupViewModel>
        BrowseSourceGroups { get; set; } = [];

    public bool HasBrowseSources => BrowseSourceGroups.Count > 0;
    public bool HasNoBrowseSources => !HasBrowseSources;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowseSourceDirectoryVisible))]
    [NotifyPropertyChangedFor(nameof(IsBrowseResultsVisible))]
    public partial bool IsBrowseResultsOpen { get; set; }

    [ObservableProperty]
    public partial string BrowseResultsTitle { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBrowsePlaceholder))]
    public partial ObservableCollection<RemoteMangaLibraryItemViewModel> BrowseBooks { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMangaDiscoverPlaceholder))]
    public partial ObservableCollection<MangaDiscoverSectionViewModel>
        MangaDiscoverSections { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMangaDiscoverPlaceholder))]
    public partial ObservableCollection<MangaDiscoveryCardViewModel>
        MangaDiscoverItems { get; set; } = [];

    public ObservableCollection<MangaDiscoveryProvider>
        MangaDiscoveryProviders { get; } = [];

    public ObservableCollection<MangaDiscoveryFeed>
        MangaDiscoveryFeeds { get; } = [];

    [ObservableProperty]
    public partial MangaDiscoveryProvider? SelectedMangaDiscoveryProvider
    {
        get;
        set;
    }

    [ObservableProperty]
    public partial MangaDiscoveryFeed? SelectedMangaDiscoveryFeed
    {
        get;
        set;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMangaDiscoverPlaceholder))]
    public partial bool IsMangaDiscoverLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMangaDiscoverPlaceholder))]
    public partial bool IsMangaDiscoverLoadingMore { get; set; }

    [ObservableProperty]
    public partial string MangaDiscoverQuery { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMangaDiscoverPlaceholder))]
    public partial bool IsMangaDiscoverSearchMode { get; set; }

    [ObservableProperty]
    public partial string MangaDiscoverStatusMessage { get; set; } =
        ResourceStringHelper.GetString(
            "MangaDiscoverInitialStatus",
            "Discover manga from installed online sources.");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBrowsePlaceholder))]
    public partial bool IsSuwayomiBusy { get; set; }

    [ObservableProperty]
    public partial string SuwayomiStatusMessage { get; set; } =
        ResourceStringHelper.GetString(
            "SuwayomiInitialStatus",
            "Connect to a user-managed Suwayomi Server to use its installed Mihon sources.");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMihonRepositories))]
    [NotifyPropertyChangedFor(nameof(HasNoMihonRepositories))]
    public partial ObservableCollection<MihonRepositoryItemViewModel>
        MihonRepositories { get; set; } = [];

    public bool HasMihonRepositories => MihonRepositories.Count > 0;
    public bool HasNoMihonRepositories => !HasMihonRepositories;

    [ObservableProperty]
    public partial ObservableCollection<MihonExtensionSource> MihonRepositorySources
    {
        get;
        set;
    } = [];

    [ObservableProperty]
    public partial ObservableCollection<MihonRepositorySourceItemViewModel>
        VisibleMihonRepositorySources
    {
        get;
        set;
    } = [];

    [ObservableProperty]
    public partial ObservableCollection<MihonRepositorySourceGroup>
        MihonRepositorySourceGroups
    {
        get;
        set;
    } = [];

    [ObservableProperty]
    public partial ObservableCollection<MihonLanguageFilterOption>
        MihonRepositoryLanguageOptions
    {
        get;
        set;
    } = [];

    [ObservableProperty]
    public partial MihonLanguageFilterOption?
        SelectedMihonRepositoryLanguage { get; set; }

    [ObservableProperty]
    public partial string MihonRepositorySearchText { get; set; } =
        string.Empty;

    [ObservableProperty]
    public partial string MihonRepositoryResultsSummary { get; set; } =
        string.Empty;

    [ObservableProperty]
    public partial ObservableCollection<MihonInstalledExtension> MihonInstalledSources
    {
        get;
        set;
    } = [];

    [ObservableProperty]
    public partial MihonInstalledExtension? SelectedMihonSource { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBrowsePlaceholder))]
    public partial bool IsMihonBusy { get; set; }

    [ObservableProperty]
    public partial bool IsBrowseLoadingMore { get; set; }

    [ObservableProperty]
    public partial string MihonStatusMessage { get; set; } =
        ResourceStringHelper.GetString(
            "MihonInitialStatus",
            "The bundled Mihon runtime starts automatically when an extension is used.");

    public bool ShowBrowsePlaceholder =>
        !(IsMihonSourceKind ? IsMihonBusy : IsSuwayomiBusy)
        && BrowseBooks.Count == 0;

    public bool ShowMangaDiscoverPlaceholder =>
        !IsMangaDiscoverLoading
        && MangaDiscoverSections.Count == 0
        && MangaDiscoverItems.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemoteMangaDetails))]
    public partial RemoteMangaDetailViewModel? SelectedRemoteMangaDetails
    {
        get;
        set;
    }

    public bool HasRemoteMangaDetails => SelectedRemoteMangaDetails is not null;

    public async Task InitializeAsync()
    {
        SelectedSection = MangaHomeSection.Library;
        if (!_isSubscribedToReader)
        {
            _readerWindow.LibraryChanged += OnReaderLibraryChanged;
            _isSubscribedToReader = true;
        }
        _pageCts.Cancel();
        CancelMangaDiscoveryPosterRequests();
        _pageCts.Dispose();
        CancelMangaDiscoveryRequest();
        _pageCts = new CancellationTokenSource();
        await LoadAsync(_pageCts.Token);
    }

    public async Task InitializeBrowseAsync(
        MangaHomeSection section = MangaHomeSection.Discover)
    {
        _pageCts.Cancel();
        CancelMangaDiscoveryPosterRequests();
        _pageCts.Dispose();
        CancelMangaDiscoveryRequest();
        _pageCts = new CancellationTokenSource();
        await SelectBrowseSectionAsync(section);
    }

    public async Task SelectBrowseSectionAsync(MangaHomeSection section)
    {
        switch (section)
        {
            case MangaHomeSection.Sources:
                await SelectSourcesAsync();
                break;
            case MangaHomeSection.Settings:
                await SelectSettingsAsync();
                break;
            case MangaHomeSection.Browse:
                await SelectBrowseAsync();
                break;
            default:
                await SelectDiscoverAsync();
                break;
        }
    }

    public void OnNavigatedFrom()
    {
        CloseRemoteMangaDetails();
        _pageCts.Cancel();
        CancelMangaDiscoveryRequest();
        CancelMangaDiscoveryPosterRequests();
        if (_isSubscribedToReader)
        {
            _readerWindow.LibraryChanged -= OnReaderLibraryChanged;
            _isSubscribedToReader = false;
        }
    }

    [RelayCommand]
    private async Task ImportFileAsync()
    {
        var path = await _dialogs.OpenFilePickerAsync(".cbz", ".zip", ".epub", ".mokuro");
        if (path is not null)
            await ImportPathAsync(path);
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        var path = await _dialogs.OpenFolderPickerAsync();
        if (path is not null)
            await ImportPathAsync(path);
    }

    [RelayCommand]
    private async Task ImportDroppedAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(path =>
                     Directory.Exists(path)
                     || IsSupportedFile(path)))
        {
            await ImportPathAsync(path);
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(_pageCts.Token);

    [RelayCommand]
    private void SelectLibrary() => SelectedSection = MangaHomeSection.Library;

    [RelayCommand]
    private async Task SelectDiscoverAsync()
    {
        SelectedSection = MangaHomeSection.Discover;
        RebuildBrowseSourceGroups();
        ConfigureMangaDiscoveryOptions();
        if (!_mangaDiscoveryInitialized && !IsMangaDiscoverLoading)
            await LoadMangaDiscoveryHomeAsync();
    }

    [RelayCommand]
    private async Task SelectBrowseAsync()
    {
        SelectedSection = MangaHomeSection.Browse;
        await EnsureMihonSettingsAsync(_pageCts.Token);
        await EnsureSuwayomiSettingsAsync(_pageCts.Token);
        if (Sources.Count == 0)
            await ConnectSuwayomiInternalAsync(saveConfiguration: false);
        RebuildBrowseSourceGroups();
    }

    [RelayCommand]
    private Task RefreshMangaDiscoverAsync()
    {
        _mangaDiscovery?.ClearCache();
        return IsMangaDiscoverSearchMode
            ? SearchMangaDiscoveryAsync()
            : LoadMangaDiscoveryHomeAsync();
    }

    [RelayCommand]
    private Task SearchMangaDiscoverAsync() => SearchMangaDiscoveryAsync();

    [RelayCommand]
    private Task LoadMoreMangaDiscoverAsync() =>
        LoadMangaDiscoverySearchPageAsync(append: true);

    [RelayCommand]
    private void CloseBrowseResults()
    {
        IsBrowseResultsOpen = false;
        SearchQuery = string.Empty;
        ResetBrowsePagination(clearBooks: true);
    }

    [RelayCommand]
    private void CloseRemoteMangaDetails()
    {
        _remoteDetailsCts?.Cancel();
        _remoteDetailsCts?.Dispose();
        _remoteDetailsCts = null;
        _selectedSuwayomiManga = null;
        _selectedSuwayomiChapters = [];
        _selectedDetailMihonSource = null;
        _selectedMihonManga = null;
        _selectedMihonChapters = [];
        SelectedRemoteMangaDetails = null;
    }

    [RelayCommand]
    private async Task ContinueRemoteMangaAsync()
    {
        if (_selectedSuwayomiManga is not null)
        {
            var chapter = _selectedSuwayomiChapters
                .OrderByDescending(item => item.Index)
                .ThenByDescending(item => item.Id)
                .FirstOrDefault(item => !item.Read)
                ?? _selectedSuwayomiChapters
                    .OrderByDescending(item => item.Index)
                    .ThenByDescending(item => item.Id)
                    .FirstOrDefault();
            if (chapter is not null)
                await OpenSuwayomiChapterAsync(_selectedSuwayomiManga, chapter);
            return;
        }

        if (_selectedDetailMihonSource is not null
            && _selectedMihonManga is not null)
        {
            var chapter = _selectedMihonChapters
                .OrderByDescending(item => item.ChapterNumber)
                .ThenByDescending(item => item.UploadDate)
                .FirstOrDefault();
            if (chapter is not null)
            {
                await OpenMihonChapterAsync(
                    _selectedDetailMihonSource,
                    _selectedMihonManga,
                    chapter);
            }
        }
    }

    [RelayCommand]
    private async Task ToggleRemoteMangaLibraryAsync()
    {
        var details = SelectedRemoteMangaDetails;
        if (details is null
            || !details.SupportsOnlineLibrary
            || details.IsActionBusy
            || (_selectedSuwayomiManga is null
                && (_selectedDetailMihonSource is null
                    || _selectedMihonManga is null)))
        {
            return;
        }

        details.IsActionBusy = true;
        details.ActionStatus = string.Empty;
        details.ErrorMessage = string.Empty;
        var target = !details.IsInOnlineLibrary;
        try
        {
            if (_selectedSuwayomiManga is not null
                && _onlineConfiguration is not null)
            {
                await _suwayomi.SetLibraryAsync(
                    _onlineConfiguration,
                    IsRemoteSuwayomiSurfaceSelected ? Secret : null,
                    _selectedSuwayomiManga.Id,
                    target,
                    _pageCts.Token);
                _selectedSuwayomiManga.InLibrary = target;
            }
            else if (_selectedDetailMihonSource is not null
                && _selectedMihonManga is not null)
            {
                var configuration = CreateMihonConfiguration();
                configuration.Library.RemoveAll(entry =>
                    IsMihonLibraryEntry(
                        entry,
                        _selectedDetailMihonSource,
                        _selectedMihonManga));
                if (target)
                {
                    configuration.Library.Add(CreateMihonLibraryEntry(
                        _selectedDetailMihonSource,
                        _selectedMihonManga));
                }
                await _mihon.SaveConfigurationAsync(
                    configuration,
                    _pageCts.Token);
                ApplyMihonConfiguration(configuration);
            }
            else
            {
                return;
            }

            details.IsInOnlineLibrary = target;
            _onlineInitialized = false;
            details.ActionStatus = target
                ? ResourceStringHelper.GetString(
                    "MangaRemoteDetailsAddedLibraryStatus",
                    "Added to the manga library.")
                : ResourceStringHelper.GetString(
                    "MangaRemoteDetailsRemovedLibraryStatus",
                    "Removed from the manga library.");
        }
        catch (OperationCanceledException) when (_pageCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            details.ErrorMessage = ex.Message;
        }
        finally
        {
            details.IsActionBusy = false;
        }
    }

    private async Task OpenSuwayomiSourceAsync(SuwayomiSource source)
    {
        SelectedSourceKind = MangaRemoteSourceKind.Suwayomi;
        SelectedSource = source;
        BrowseResultsTitle = source.DisplayName;
        IsBrowseResultsOpen = true;
        await BrowseSuwayomiAsync(query: null);
    }

    private async Task OpenMihonSourceAsync(MihonInstalledExtension source)
    {
        SelectedSourceKind = MangaRemoteSourceKind.Mihon;
        SelectedMihonSource = source;
        BrowseResultsTitle = source.SourceName;
        IsBrowseResultsOpen = true;
        await BrowseMihonAsync(query: null);
    }

    private void RebuildBrowseSourceGroups()
    {
        var items = Sources
            .Select(source => new MangaBrowseSourceItemViewModel(
                source.DisplayName,
                source.Lang,
                "Suwayomi",
                () => OpenSuwayomiSourceAsync(source),
                () => LoadSuwayomiSourceIconAsync(source)))
            .Concat(MihonInstalledSources.Select(source =>
                new MangaBrowseSourceItemViewModel(
                    source.SourceName,
                    source.Lang,
                    "Mihon APK",
                    () => OpenMihonSourceAsync(source),
                    () => LoadMihonInstalledSourceIconAsync(source),
                    () => RemoveMihonInstalledSourceAsync(source))))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        BrowseSourceGroups =
            new ObservableCollection<MangaBrowseSourceGroupViewModel>(
                items
                    .GroupBy(item => item.LanguageLabel)
                    .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
                    .Select(group => new MangaBrowseSourceGroupViewModel(
                        group.Key,
                        group)));
    }

    partial void OnSourcesChanged(ObservableCollection<SuwayomiSource> value) =>
        RebuildBrowseSourceGroups();

    partial void OnMihonInstalledSourcesChanged(
        ObservableCollection<MihonInstalledExtension> value) =>
        RebuildBrowseSourceGroups();

    private void ConfigureMangaDiscoveryOptions()
    {
        if (_mangaDiscovery is null)
            return;

        _mangaDiscoveryOptionsReady = false;
        var selectedProviderId = SelectedMangaDiscoveryProvider?.Id ?? "bangumi";
        MangaDiscoveryProviders.Clear();
        foreach (var provider in _mangaDiscovery.Providers)
        {
            MangaDiscoveryProviders.Add(provider with
            {
                DisplayName = ResourceStringHelper.GetString(
                    $"MangaDiscoverProvider_{provider.Id}",
                    provider.DisplayName),
            });
        }
        SelectedMangaDiscoveryProvider = MangaDiscoveryProviders.FirstOrDefault(
            provider => string.Equals(
                provider.Id,
                selectedProviderId,
                StringComparison.OrdinalIgnoreCase))
            ?? MangaDiscoveryProviders.FirstOrDefault();
        ConfigureMangaDiscoveryFeeds();
        _mangaDiscoveryOptionsReady = true;
    }

    private void ConfigureMangaDiscoveryFeeds()
    {
        MangaDiscoveryFeeds.Clear();
        if (_mangaDiscovery is null || SelectedMangaDiscoveryProvider is null)
            return;

        var selectedFeedId = SelectedMangaDiscoveryFeed?.Id;
        foreach (var feed in _mangaDiscovery.GetFeeds(
                     SelectedMangaDiscoveryProvider.Id,
                     MangaDiscoveryFeedKind.Recommendation))
        {
            MangaDiscoveryFeeds.Add(feed with
            {
                DisplayName = ResourceStringHelper.GetString(
                    $"MangaDiscoverFeed_{feed.ProviderId}_{feed.Id}",
                    feed.DisplayName),
            });
        }
        SelectedMangaDiscoveryFeed = MangaDiscoveryFeeds.FirstOrDefault(
            feed => string.Equals(
                feed.Id,
                selectedFeedId,
                StringComparison.OrdinalIgnoreCase))
            ?? MangaDiscoveryFeeds.FirstOrDefault();
    }

    partial void OnSelectedMangaDiscoveryProviderChanged(
        MangaDiscoveryProvider? value)
    {
        var optionsWereReady = _mangaDiscoveryOptionsReady;
        _mangaDiscoveryOptionsReady = false;
        ConfigureMangaDiscoveryFeeds();
        _mangaDiscoveryOptionsReady = optionsWereReady;
        if (optionsWereReady && IsDiscoverSectionSelected)
            _ = LoadMangaDiscoveryHomeAsync();
    }

    partial void OnSelectedMangaDiscoveryFeedChanged(
        MangaDiscoveryFeed? value)
    {
        if (!_mangaDiscoveryOptionsReady
            || !IsDiscoverSectionSelected
            || value is null
            || MangaDiscoverSections.Count < 2)
        {
            return;
        }

        SynchronizeMangaDiscoverySections(
            MangaDiscoverSections
                .OrderBy(section => !string.Equals(
                    section.FeedId,
                    value.Id,
                    StringComparison.OrdinalIgnoreCase))
                .ToList());
    }

    private async Task LoadMangaDiscoveryHomeAsync()
    {
        if (_mangaDiscovery is null)
            return;

        if (SelectedMangaDiscoveryProvider is null)
            ConfigureMangaDiscoveryOptions();
        if (SelectedMangaDiscoveryProvider is null)
            return;

        BeginMangaDiscoveryPosterRequests();
        var requestCts = BeginMangaDiscoveryRequest();

        IsMangaDiscoverLoading = true;
        IsMangaDiscoverSearchMode = false;
        _mangaDiscoveryInitialized = false;
        _activeMangaDiscoveryProviderId = null;
        _activeMangaDiscoveryQuery = null;
        _mangaDiscoveryPage = 0;
        _mangaDiscoveryHasMore = false;
        MangaDiscoverItems = [];
        MangaDiscoverSections = [];
        MangaDiscoverStatusMessage = ResourceStringHelper.GetString(
            "MangaDiscoverLoadingStatus",
            "Loading manga recommendations…");
        try
        {
            var selectedFeedId = SelectedMangaDiscoveryFeed?.Id;
            var feeds = MangaDiscoveryFeeds
                .OrderBy(feed => !string.Equals(
                    feed.Id,
                    selectedFeedId,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            var providerId = SelectedMangaDiscoveryProvider.Id;
            var results = await LoadMangaDiscoveryHomeSectionsAsync(
                providerId,
                feeds,
                requestCts);

            if (!IsCurrentMangaDiscoveryRequest(requestCts))
                return;
            var sections = results
                .Where(result => result.Section is not null)
                .Select(result => result.Section!)
                .ToList();
            var failures = results
                .Where(result => result.Failure is not null)
                .Select(result => result.Failure!)
                .ToList();
            SynchronizeMangaDiscoverySections(sections);
            MangaDiscoverStatusMessage = sections.Count == 0
                ? failures.Count > 0
                    ? failures[0].Message
                    : ResourceStringHelper.GetString(
                        "MangaDiscoverEmptyStatus",
                        "No manga was returned by this metadata source.")
                : ResourceStringHelper.FormatString(
                    "MangaDiscoverLoadedStatus",
                     "Loaded {0} manga discovery sections.",
                     sections.Count);
            _mangaDiscoveryInitialized = sections.Count > 0
                || failures.Count < feeds.Count;
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentMangaDiscoveryRequest(requestCts))
            {
                MangaDiscoverSections = [];
                MangaDiscoverStatusMessage = ex.Message;
                _mangaDiscoveryInitialized = false;
            }
        }
        finally
        {
            EndMangaDiscoveryRequest(requestCts);
        }
    }

    private async Task<IReadOnlyList<MangaDiscoverySectionLoadResult>>
        LoadMangaDiscoveryHomeSectionsAsync(
            string providerId,
            IReadOnlyList<MangaDiscoveryFeed> feeds,
            CancellationTokenSource requestCts)
    {
        if (providerId.Equals("anilist", StringComparison.OrdinalIgnoreCase)
            && _mangaDiscovery is IMangaDiscoveryBatchService batchService)
        {
            try
            {
                var pages = await batchService.GetPagesAsync(
                    providerId,
                    feeds.Select(feed => new MangaDiscoveryRequest(feed.Id)).ToList(),
                    requestCts.Token);
                if (pages.Count != feeds.Count)
                    throw new InvalidOperationException("AniList returned an incomplete recommendation batch.");

                var batchedResults = feeds
                    .Select((feed, index) => CreateMangaDiscoverySectionResult(
                        feed,
                        pages[index]))
                    .ToList();
                PublishMangaDiscoverySections(batchedResults, requestCts);
                return batchedResults;
            }
            catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Fall back to isolated feed requests so one rejected batch does not
                // leave the entire discovery page empty.
            }
        }

        var results = new MangaDiscoverySectionLoadResult?[feeds.Count];
        var pending = feeds
            .Select((feed, index) => LoadIndexedMangaDiscoverySectionAsync(
                providerId,
                feed,
                index,
                requestCts.Token))
            .ToList();
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            var (index, result) = await completed;
            results[index] = result;
            PublishMangaDiscoverySections(
                results.Where(item => item is not null).Select(item => item!).ToList(),
                requestCts);
        }

        return results.Where(item => item is not null).Select(item => item!).ToList();
    }

    private async Task<(int Index, MangaDiscoverySectionLoadResult Result)>
        LoadIndexedMangaDiscoverySectionAsync(
            string providerId,
            MangaDiscoveryFeed feed,
            int index,
            CancellationToken ct) =>
        (index, await LoadMangaDiscoverySectionAsync(providerId, feed, ct));

    private void PublishMangaDiscoverySections(
        IReadOnlyList<MangaDiscoverySectionLoadResult> results,
        CancellationTokenSource requestCts)
    {
        if (!IsCurrentMangaDiscoveryRequest(requestCts))
            return;

        var sections = results
            .Where(result => result.Section is not null)
            .Select(result => result.Section!)
            .ToList();
        if (sections.Count == 0)
            return;

        SynchronizeMangaDiscoverySections(sections);
        MangaDiscoverStatusMessage = ResourceStringHelper.FormatString(
            "MangaDiscoverLoadedStatus",
            "Loaded {0} manga discovery sections.",
            sections.Count);
    }

    private void SynchronizeMangaDiscoverySections(
        IReadOnlyList<MangaDiscoverSectionViewModel> desiredSections)
    {
        for (var desiredIndex = 0;
             desiredIndex < desiredSections.Count;
             desiredIndex++)
        {
            var desired = desiredSections[desiredIndex];
            var existingIndex = -1;
            for (var index = 0; index < MangaDiscoverSections.Count; index++)
            {
                if (string.Equals(
                    MangaDiscoverSections[index].FeedId,
                    desired.FeedId,
                    StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = index;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                MangaDiscoverSections.Insert(desiredIndex, desired);
            }
            else if (existingIndex != desiredIndex)
            {
                MangaDiscoverSections.Move(existingIndex, desiredIndex);
            }
        }

        while (MangaDiscoverSections.Count > desiredSections.Count)
            MangaDiscoverSections.RemoveAt(MangaDiscoverSections.Count - 1);

        OnPropertyChanged(nameof(MangaDiscoverSections));
        OnPropertyChanged(nameof(ShowMangaDiscoverPlaceholder));
    }

    private async Task<MangaDiscoverySectionLoadResult>
        LoadMangaDiscoverySectionAsync(
            string providerId,
            MangaDiscoveryFeed feed,
            CancellationToken ct)
    {
        try
        {
            var page = await _mangaDiscovery!.GetPageAsync(
                providerId,
                new MangaDiscoveryRequest(feed.Id),
                ct);
            return CreateMangaDiscoverySectionResult(feed, page);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MangaDiscoverySectionLoadResult(null, ex);
        }
    }

    private MangaDiscoverySectionLoadResult CreateMangaDiscoverySectionResult(
        MangaDiscoveryFeed feed,
        MangaDiscoveryPage page)
    {
        var cards = page.Items
            .Select(item => CreateMangaDiscoveryCard(item))
            .ToList();
        return cards.Count == 0
            ? new MangaDiscoverySectionLoadResult(null, null)
            : new MangaDiscoverySectionLoadResult(
                new MangaDiscoverSectionViewModel(feed.Id, feed.DisplayName, cards),
                null);
    }

    private Task SearchMangaDiscoveryAsync()
    {
        var query = MangaDiscoverQuery.Trim();
        return string.IsNullOrWhiteSpace(query)
            ? LoadMangaDiscoveryHomeAsync()
            : LoadMangaDiscoverySearchPageAsync(append: false);
    }

    private async Task LoadMangaDiscoverySearchPageAsync(bool append)
    {
        if (_mangaDiscovery is null
            || SelectedMangaDiscoveryProvider is null
            || (append && (!_mangaDiscoveryHasMore || IsMangaDiscoverLoadingMore))
            || (append && IsMangaDiscoverLoading))
        {
            return;
        }

        var providerId = append
            ? _activeMangaDiscoveryProviderId
            : SelectedMangaDiscoveryProvider.Id;
        var query = append
            ? _activeMangaDiscoveryQuery
            : MangaDiscoverQuery.Trim();
        if (string.IsNullOrWhiteSpace(providerId)
            || string.IsNullOrWhiteSpace(query))
        {
            if (!append)
                await LoadMangaDiscoveryHomeAsync();
            return;
        }

        query = query.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            await LoadMangaDiscoveryHomeAsync();
            return;
        }

        if (!append)
            BeginMangaDiscoveryPosterRequests();
        var requestCts = BeginMangaDiscoveryRequest();
        if (append)
            IsMangaDiscoverLoadingMore = true;
        else
        {
            IsMangaDiscoverLoading = true;
            IsMangaDiscoverSearchMode = true;
            MangaDiscoverSections = [];
            MangaDiscoverItems = [];
            _mangaDiscoveryPage = 1;
            _mangaDiscoveryHasMore = false;
            _activeMangaDiscoveryProviderId = providerId;
            _activeMangaDiscoveryQuery = query;
        }

        try
        {
            var requestedPage = append ? _mangaDiscoveryPage + 1 : 1;
            var page = await _mangaDiscovery.SearchAsync(
                providerId,
                query,
                requestedPage,
                requestCts.Token);
            if (!IsCurrentMangaDiscoveryRequest(requestCts))
                return;
            var cards = page.Items
                .Select(item => CreateMangaDiscoveryCard(item))
                .ToList();
            if (append)
            {
                foreach (var card in cards)
                    MangaDiscoverItems.Add(card);
            }
            else
            {
                MangaDiscoverItems = new ObservableCollection<MangaDiscoveryCardViewModel>(cards);
            }
            _mangaDiscoveryPage = requestedPage;
            _mangaDiscoveryHasMore = page.HasMore && cards.Count > 0;
            MangaDiscoverStatusMessage = MangaDiscoverItems.Count == 0
                ? ResourceStringHelper.GetString(
                    "MangaDiscoverSearchEmptyStatus",
                    "No manga matched this search.")
                : ResourceStringHelper.FormatString(
                    "MangaDiscoverSearchLoadedStatus",
                    "Showing {0} manga search results.",
                    MangaDiscoverItems.Count);
            _mangaDiscoveryInitialized = true;
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentMangaDiscoveryRequest(requestCts))
            {
                if (!append)
                    MangaDiscoverItems = [];
                MangaDiscoverStatusMessage = ex.Message;
                _mangaDiscoveryHasMore = false;
            }
        }
        finally
        {
            EndMangaDiscoveryRequest(requestCts);
        }
    }

    private CancellationTokenSource BeginMangaDiscoveryRequest()
    {
        _mangaDiscoveryRequestCts?.Cancel();
        _mangaDiscoveryRequestCts?.Dispose();
        _mangaDiscoveryRequestCts = CancellationTokenSource.CreateLinkedTokenSource(
            _pageCts.Token);
        return _mangaDiscoveryRequestCts;
    }

    private bool IsCurrentMangaDiscoveryRequest(CancellationTokenSource requestCts) =>
        ReferenceEquals(_mangaDiscoveryRequestCts, requestCts);

    private void EndMangaDiscoveryRequest(CancellationTokenSource requestCts)
    {
        if (!IsCurrentMangaDiscoveryRequest(requestCts))
            return;
        _mangaDiscoveryRequestCts = null;
        IsMangaDiscoverLoadingMore = false;
        IsMangaDiscoverLoading = false;
        requestCts.Dispose();
    }

    private void CancelMangaDiscoveryRequest()
    {
        _mangaDiscoveryRequestCts?.Cancel();
    }

    private MangaDiscoveryCardViewModel CreateMangaDiscoveryCard(
        MangaDiscoveryItem item) =>
        new(item, () => OpenMangaDiscoveryItemAsync(item));

    public Task EnsureMangaDiscoveryPosterAsync(
        MangaDiscoveryCardViewModel card)
    {
        var ct = _mangaDiscoveryPosterCts?.Token ?? _pageCts.Token;
        return LoadMangaDiscoveryPosterAsync(card, ct);
    }

    private void BeginMangaDiscoveryPosterRequests()
    {
        CancelMangaDiscoveryPosterRequests();
        _mangaDiscoveryPosterCts =
            CancellationTokenSource.CreateLinkedTokenSource(_pageCts.Token);
    }

    private void CancelMangaDiscoveryPosterRequests()
    {
        _mangaDiscoveryPosterCts?.Cancel();
        _mangaDiscoveryPosterCts?.Dispose();
        _mangaDiscoveryPosterCts = null;
    }

    private async Task LoadMangaDiscoveryPostersAsync(
        IReadOnlyList<MangaDiscoveryCardViewModel> cards,
        CancellationToken ct)
    {
        if (_mangaDiscovery is null)
            return;

        await Task.WhenAll(cards.Select(card =>
            LoadMangaDiscoveryPosterAsync(card, ct)));
    }

    private async Task LoadMangaDiscoveryPosterAsync(
        MangaDiscoveryCardViewModel card,
        CancellationToken ct)
    {
        if (_mangaDiscovery is null || !card.TryBeginPosterLoad())
            return;

        var enteredGate = false;
        try
        {
            await _mangaDiscoveryPosterGate.WaitAsync(ct);
            enteredGate = true;
            card.SetPosterPath(
                await _mangaDiscovery!.GetPosterPathAsync(card.Item, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            card.ResetPosterLoad();
        }
        catch
        {
            card.ResetPosterLoad();
            // Keep the standard poster placeholder when one artwork request fails.
        }
        finally
        {
            if (enteredGate)
                _mangaDiscoveryPosterGate.Release();
        }
    }

    private async Task OpenMangaDiscoveryItemAsync(MangaDiscoveryItem item)
    {
        try
        {
            var details = new RemoteMangaDetailViewModel(
                GetMangaDiscoveryProviderName(item.ProviderId),
                item.ProviderItemId,
                item.Title,
                supportsOnlineLibrary: false);
            details.ApplyDiscoveryDetails(item);
            details.ActionStatus = ResourceStringHelper.GetString(
                "MangaDiscoverMatchingExtensionStatus",
                "Matching installed manga extensions…");
            var ct = BeginRemoteDetailsLoad(details);
            _ = LoadMangaDiscoveryDetailsAsync(details, item, ct);
        }
        catch (OperationCanceledException) when (_pageCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _notifications.ShowError(
                ex.Message,
                ResourceStringHelper.GetString(
                    "MangaDiscoverOpenFailedTitle",
                    "Open manga failed"));
        }
    }

    private async Task LoadMangaDiscoveryDetailsAsync(
        RemoteMangaDetailViewModel details,
        MangaDiscoveryItem item,
        CancellationToken ct)
    {
        try
        {
            _ = LoadMangaDiscoveryDetailPosterAsync(details, item, ct);
            await EnsureMihonSettingsAsync(ct);

            if (!IsCurrentRemoteDetails(details, ct))
                return;

            details.SetExtensionOptions(MihonInstalledSources, selected: null);
            if (MihonInstalledSources.Count == 0)
            {
                details.IsLoading = false;
                details.ActionStatus = ResourceStringHelper.GetString(
                    "MangaDiscoverNoExtensionDetailStatus",
                    "Install an extension or choose one from the extension list to open chapters.");
                return;
            }

            var configuration = _mihonConfiguration ?? CreateMihonConfiguration();
            foreach (var source in MihonInstalledSources)
            {
                try
                {
                    var manga = await FindMihonMangaByTitlesAsync(
                        configuration,
                        source,
                        details.SearchTitles,
                        ct);
                    if (manga is not null)
                    {
                        await ShowMatchedMihonMangaDetailsAsync(
                            source,
                            manga,
                            item);
                        return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Continue matching against the remaining installed extensions.
                }
            }

            if (IsCurrentRemoteDetails(details, ct))
            {
                details.IsLoading = false;
                details.ActionStatus = ResourceStringHelper.FormatString(
                    "MangaDiscoverNoMatchDetailStatus",
                    "No installed extension matched \"{0}\". Select an extension to try it directly.",
                    item.Title);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentRemoteDetails(details, ct))
            {
                details.IsLoading = false;
                details.ErrorMessage = ex.Message;
            }
        }
    }

    private async Task LoadMangaDiscoveryDetailPosterAsync(
        RemoteMangaDetailViewModel details,
        MangaDiscoveryItem item,
        CancellationToken ct,
        bool onlyIfMissing = false)
    {
        if (_mangaDiscovery is null)
            return;

        try
        {
            var posterPath = await _mangaDiscovery.GetPosterPathAsync(item, ct);
            if (IsCurrentRemoteDetails(details, ct)
                && (!onlyIfMissing || !details.HasCover))
                details.SetCoverPath(posterPath ?? string.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch
        {
            // Metadata details remain usable when the poster CDN is unavailable.
        }
    }

    private static string GetMangaDiscoveryProviderName(string providerId) =>
        ResourceStringHelper.GetString(
            $"MangaDiscoverProvider_{providerId}",
            providerId);

    private sealed record MangaDiscoverySectionLoadResult(
        MangaDiscoverSectionViewModel? Section,
        Exception? Failure);

    private async Task<string?> LoadSuwayomiSourceIconAsync(
        SuwayomiSource source)
    {
        await EnsureSuwayomiSettingsAsync(_pageCts.Token);
        return await _suwayomi.GetSourceIconPathAsync(
            CreateSuwayomiConfiguration(),
            Secret,
            source,
            _pageCts.Token);
    }

    private async Task<string?> LoadMihonInstalledSourceIconAsync(
        MihonInstalledExtension source)
    {
        await EnsureMihonSettingsAsync(_pageCts.Token);
        return await _mihon.GetRepositorySourceIconPathAsync(
            CreateMihonConfiguration(),
            new MihonExtensionSource
            {
                Id = source.SourceId,
                Name = source.SourceName,
                Lang = source.Lang,
                BaseUrl = source.BaseUrl,
                PackageName = source.PackageName,
                Version = source.Version,
                IconDownloadUrl = string.IsNullOrWhiteSpace(
                    source.IconDownloadUrl)
                    ? MihonRepositorySources
                        .FirstOrDefault(candidate =>
                            string.Equals(
                                candidate.PackageName,
                                source.PackageName,
                                StringComparison.Ordinal)
                            && string.Equals(
                                candidate.Id,
                                source.SourceId,
                                StringComparison.Ordinal))
                        ?.IconDownloadUrl
                        ?? string.Empty
                    : source.IconDownloadUrl,
            },
            _pageCts.Token);
    }

    private Task RemoveMihonInstalledSourceAsync(
        MihonInstalledExtension source) =>
        RemoveMihonSourceAsync(new MihonExtensionSource
        {
            Id = source.SourceId,
            Name = source.SourceName,
            Lang = source.Lang,
            BaseUrl = source.BaseUrl,
            PackageName = source.PackageName,
            PackageDisplayName = source.SourceName,
            Version = source.Version,
            IconDownloadUrl = source.IconDownloadUrl,
            IsInstalled = true,
        });

    [RelayCommand]
    private async Task SelectSourcesAsync()
    {
        SelectedSection = MangaHomeSection.Sources;
        await EnsureMihonSettingsAsync(_pageCts.Token);
        if (MihonRepositorySources.Count == 0
            && HasMihonRepositories)
        {
            await RefreshMihonRepositoryAsync();
        }
    }

    [RelayCommand]
    private async Task SelectSettingsAsync()
    {
        SelectedSection = MangaHomeSection.Settings;
        await EnsureSuwayomiSettingsAsync(_pageCts.Token);
        await EnsureMihonSettingsAsync(_pageCts.Token);
    }

    [RelayCommand]
    private void SelectLocal() => IsOnlineSelected = false;

    [RelayCommand]
    private async Task SelectOnlineAsync()
    {
        IsOnlineSelected = true;
        await LoadOnlineLibraryAsync(force: false, _pageCts.Token);
    }

    [RelayCommand]
    public Task RefreshOnlineLibraryAsync() =>
        LoadOnlineLibraryAsync(force: true, _pageCts.Token);

    [RelayCommand]
    private Task ConnectSuwayomiAsync() =>
        ConnectSuwayomiInternalAsync(saveConfiguration: true);

    [RelayCommand]
    private Task BrowsePopularAsync() =>
        IsMihonSourceKind
            ? BrowseMihonAsync(query: null)
            : BrowseSuwayomiAsync(query: null);

    [RelayCommand]
    private Task SearchMangaSourceAsync() =>
        IsMihonSourceKind
            ? BrowseMihonAsync(SearchQuery)
            : BrowseSuwayomiAsync(SearchQuery);

    [RelayCommand]
    private Task LoadNextBrowsePageAsync()
    {
        if (!_browseHasNextPage
            || IsBrowseLoadingMore
            || IsMihonBusy
            || IsSuwayomiBusy
            || BrowseBooks.Count == 0)
        {
            return Task.CompletedTask;
        }

        return IsMihonSourceKind
            ? BrowseMihonAsync(_activeBrowseQuery, append: true)
            : BrowseSuwayomiAsync(_activeBrowseQuery, append: true);
    }

    [RelayCommand]
    private async Task SelectSuwayomiSourceKindAsync()
    {
        SelectedSourceKind = MangaRemoteSourceKind.Suwayomi;
        await EnsureSuwayomiSettingsAsync(_pageCts.Token);
    }

    [RelayCommand]
    private async Task SelectMihonSourceKindAsync()
    {
        SelectedSourceKind = MangaRemoteSourceKind.Mihon;
        await EnsureMihonSettingsAsync(_pageCts.Token);
    }

    [RelayCommand]
    private async Task AddMihonRepositoryAsync()
    {
        var url = await _dialogs.PromptTextAsync(
            ResourceStringHelper.GetString(
                "MihonAddRepositoryDialogTitle",
                "Add repository URL"),
            ResourceStringHelper.GetString(
                "MihonRepositoryUrlPlaceholder",
                "URL must end with .json"),
            ResourceStringHelper.GetString(
                "MihonAddRepositoryAction",
                "Add"),
            ResourceStringHelper.GetString(
                "CancelButton",
                "Cancel"));
        if (string.IsNullOrWhiteSpace(url))
            return;

        var repositories = MihonRepositories
            .Select(item => item.ToConfiguration())
            .Append(new MihonRepositoryConfiguration
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = MihonExtensionService.GetRepositoryDisplayName(url),
                IndexUrl = url,
            });
        await SaveMihonRepositoriesAsync(repositories);
    }

    private async Task EditMihonRepositoryAsync(
        MihonRepositoryItemViewModel item)
    {
        var url = await _dialogs.PromptTextAsync(
            ResourceStringHelper.GetString(
                "MihonEditRepositoryDialogTitle",
                "Edit repository URL"),
            ResourceStringHelper.GetString(
                "MihonRepositoryUrlPlaceholder",
                "URL must end with .json"),
            ResourceStringHelper.GetString(
                "SaveButton",
                "Save"),
            ResourceStringHelper.GetString(
                "CancelButton",
                "Cancel"),
            item.IndexUrl);
        if (string.IsNullOrWhiteSpace(url))
            return;

        var repositories = MihonRepositories.Select(current =>
            current.Id == item.Id
                ? new MihonRepositoryConfiguration
                {
                    Id = current.Id,
                    Name = MihonExtensionService.GetRepositoryDisplayName(url),
                    IndexUrl = url,
                }
                : current.ToConfiguration());
        await SaveMihonRepositoriesAsync(repositories);
    }

    private async Task RemoveMihonRepositoryAsync(
        MihonRepositoryItemViewModel item)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            ResourceStringHelper.GetString(
                "MihonRemoveRepositoryDialogTitle",
                "Remove repository?"),
            ResourceStringHelper.FormatString(
                "MihonRemoveRepositoryDialogMessage",
                "Remove {0} from the repository list? Installed APKs will be kept.",
                item.Name),
            ResourceStringHelper.GetString(
                "MihonRemoveRepositoryAction",
                "Remove"),
            ResourceStringHelper.GetString(
                "CancelButton",
                "Cancel"));
        if (!confirmed)
            return;

        await SaveMihonRepositoriesAsync(
            MihonRepositories
                .Where(current => current.Id != item.Id)
                .Select(current => current.ToConfiguration()));
    }

    private async Task SaveMihonRepositoriesAsync(
        IEnumerable<MihonRepositoryConfiguration> repositories)
    {
        if (IsMihonBusy)
            return;
        try
        {
            var configuration = CreateMihonConfiguration();
            configuration.Repositories = repositories.ToList();
            await _mihon.SaveConfigurationAsync(configuration, _pageCts.Token);
            ApplyMihonConfiguration(configuration);
            await RefreshMihonRepositoryAsync();
        }
        catch (OperationCanceledException) when (_pageCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MihonStatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RefreshMihonRepositoryAsync()
    {
        if (IsMihonBusy)
            return;
        try
        {
            IsMihonBusy = true;
            MihonStatusMessage = ResourceStringHelper.GetString(
                "MihonLoadingRepositoryStatus",
                "Loading the Mihon extension repository…");
            var configuration = CreateMihonConfiguration();
            await _mihon.SaveConfigurationAsync(configuration, _pageCts.Token);
            var result = await _mihon.RefreshRepositoriesAsync(
                configuration,
                _pageCts.Token);
            var sources = result.Sources;
            MihonRepositorySources =
                new ObservableCollection<MihonExtensionSource>(sources);
            RebuildMihonRepositorySourceItems(sources);
            _mihonConfiguration = configuration;
            if (configuration.Repositories.Count == 0)
            {
                MihonStatusMessage = ResourceStringHelper.GetString(
                    "MihonNoRepositoriesStatus",
                    "Add a Mihon extension repository to browse extensions.");
            }
            else if (result.Failures.Count == 0)
            {
                MihonStatusMessage = ResourceStringHelper.FormatString(
                    "MihonRepositoriesLoadedStatus",
                    "{0} source(s) found across {1} repository/repositories.",
                    sources.Count,
                    configuration.Repositories.Count);
            }
            else
            {
                MihonStatusMessage = ResourceStringHelper.FormatString(
                    "MihonRepositoriesLoadedWithFailuresStatus",
                    "{0} source(s) loaded; {1} repository/repositories failed: {2}",
                    sources.Count,
                    result.Failures.Count,
                    string.Join(
                        ", ",
                        result.Failures.Select(failure =>
                            failure.RepositoryName)));
            }
        }
        catch (OperationCanceledException) when (_pageCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MihonRepositorySources.Clear();
            RebuildMihonRepositorySourceItems([]);
            MihonStatusMessage = ex.Message;
        }
        finally
        {
            IsMihonBusy = false;
        }
    }

    private async Task InstallMihonSourceAsync(
        MihonExtensionSource requestedSource)
    {
        if (IsMihonBusy)
            return;
        try
        {
            IsMihonBusy = true;
            MihonStatusMessage = ResourceStringHelper.GetString(
                "MihonInstallingStatus",
                "Downloading and validating the Mihon extension…");
            var configuration = CreateMihonConfiguration();
            var installed = await _mihon.InstallAsync(
                configuration,
                requestedSource,
                _pageCts.Token);
            await _mihon.SaveConfigurationAsync(configuration, _pageCts.Token);
            await ReloadInstalledMihonSourcesAsync(_pageCts.Token);
            foreach (var source in MihonRepositorySources)
            {
                source.IsInstalled =
                    string.Equals(
                        source.PackageName,
                        installed.PackageName,
                        StringComparison.Ordinal)
                    && string.Equals(
                        source.Id,
                        installed.SourceId,
                        StringComparison.Ordinal)
                    || source.IsInstalled;
            }
            MihonRepositorySources =
                new ObservableCollection<MihonExtensionSource>(
                    MihonRepositorySources);
            RebuildMihonRepositorySourceItems(MihonRepositorySources);
            MihonStatusMessage = ResourceStringHelper.FormatString(
                "MihonInstalledStatus",
                "{0} installed.",
                installed.Label);
        }
        catch (OperationCanceledException) when (_pageCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MihonStatusMessage = ex.Message;
        }
        finally
        {
            IsMihonBusy = false;
        }
    }

    private async Task RemoveMihonSourceAsync(
        MihonExtensionSource requestedSource)
    {
        if (IsMihonBusy)
            return;

        var sourceName = string.IsNullOrWhiteSpace(requestedSource.Name)
            ? requestedSource.PackageDisplayName
            : requestedSource.Name;
        var confirmed = await _dialogs.ConfirmAsync(
            ResourceStringHelper.GetString(
                "MihonRemoveSourceDialogTitle",
                "Remove extension?"),
            ResourceStringHelper.FormatString(
                "MihonRemoveSourceDialogMessage",
                "Remove {0}? Its APK will be kept only while another source uses it.",
                sourceName),
            ResourceStringHelper.GetString(
                "MihonRemoveSourceAction",
                "Remove"),
            ResourceStringHelper.GetString(
                "CancelButton",
                "Cancel"));
        if (!confirmed)
            return;

        try
        {
            IsMihonBusy = true;
            MihonStatusMessage = ResourceStringHelper.GetString(
                "MihonRemovingStatus",
                "Removing the Mihon extension…");
            await _mihon.RemoveAsync(
                requestedSource.PackageName,
                requestedSource.Id,
                _pageCts.Token);
            await ReloadInstalledMihonSourcesAsync(_pageCts.Token);

            foreach (var source in MihonRepositorySources)
            {
                if (string.Equals(
                        source.PackageName,
                        requestedSource.PackageName,
                        StringComparison.Ordinal)
                    && string.Equals(
                        source.Id,
                        requestedSource.Id,
                        StringComparison.Ordinal))
                {
                    source.IsInstalled = false;
                }
            }
            RebuildMihonRepositorySourceItems(MihonRepositorySources);
            MihonStatusMessage = ResourceStringHelper.FormatString(
                "MihonRemovedStatus",
                "{0} removed.",
                sourceName);
        }
        catch (OperationCanceledException) when (_pageCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MihonStatusMessage = ex.Message;
        }
        finally
        {
            IsMihonBusy = false;
        }
    }

    partial void OnMihonRepositorySearchTextChanged(string value) =>
        ApplyMihonRepositoryFilters();

    partial void OnSelectedMihonRepositoryLanguageChanged(
        MihonLanguageFilterOption? value) =>
        ApplyMihonRepositoryFilters();

    private void RebuildMihonRepositorySourceItems(
        IEnumerable<MihonExtensionSource> sources)
    {
        var selectedLanguage =
            SelectedMihonRepositoryLanguage?.Code ?? string.Empty;
        _allMihonRepositorySourceItems.Clear();
        _allMihonRepositorySourceItems.AddRange(
            sources.Select(source =>
                new MihonRepositorySourceItemViewModel(
                    source,
                    InstallMihonSourceAsync,
                    LoadMihonRepositorySourceIconAsync,
                    RemoveMihonSourceAsync)));

        var allLanguages = new MihonLanguageFilterOption(
            string.Empty,
            ResourceStringHelper.GetString(
                "MihonAllLanguagesFilter",
                "All languages"));
        var languageOptions = _allMihonRepositorySourceItems
            .Select(item => item.Language)
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(language => language, StringComparer.OrdinalIgnoreCase)
            .Select(language => new MihonLanguageFilterOption(
                language,
                language.ToUpperInvariant()))
            .Prepend(allLanguages);
        MihonRepositoryLanguageOptions =
            new ObservableCollection<MihonLanguageFilterOption>(
                languageOptions);
        SelectedMihonRepositoryLanguage =
            MihonRepositoryLanguageOptions.FirstOrDefault(option =>
                string.Equals(
                    option.Code,
                    selectedLanguage,
                    StringComparison.OrdinalIgnoreCase))
            ?? allLanguages;
        ApplyMihonRepositoryFilters();
    }

    private async Task<string?> LoadMihonRepositorySourceIconAsync(
        MihonExtensionSource source)
    {
        await EnsureMihonSettingsAsync(_pageCts.Token);
        return await _mihon.GetRepositorySourceIconPathAsync(
            CreateMihonConfiguration(),
            source,
            _pageCts.Token);
    }

    private void ApplyMihonRepositoryFilters()
    {
        var language = SelectedMihonRepositoryLanguage?.Code;
        var filtered = _allMihonRepositorySourceItems
            .Where(item =>
                item.Matches(MihonRepositorySearchText)
                && (string.IsNullOrWhiteSpace(language)
                    || string.Equals(
                        item.Language,
                        language,
                        StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.IsInstalled)
            .ThenBy(item => item.Language, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        VisibleMihonRepositorySources =
            new ObservableCollection<MihonRepositorySourceItemViewModel>(
                filtered);
        var installedLabel = ResourceStringHelper.GetString(
            "MihonInstalledSourceGroup",
            "Installed");
        var groups = filtered
            .GroupBy(item => item.IsInstalled
                ? $"\u0000{installedLabel}"
                : item.LanguageLabel)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new MihonRepositorySourceGroup(
                group.Key.Length > 0 && group.Key[0] == '\u0000'
                    ? group.Key[1..]
                    : group.Key,
                group));
        MihonRepositorySourceGroups =
            new ObservableCollection<MihonRepositorySourceGroup>(groups);
        MihonRepositoryResultsSummary = ResourceStringHelper.FormatString(
            "MihonRepositoryFilteredStatus",
            "Showing {0} of {1} source(s).",
            filtered.Count,
            _allMihonRepositorySourceItems.Count);
    }

    private async Task EnsureMihonSettingsAsync(CancellationToken ct)
    {
        if (_mihonSettingsInitialized)
            return;
        var configuration = await _mihon.LoadConfigurationAsync(ct);
        ApplyMihonConfiguration(configuration);
        await ReloadInstalledMihonSourcesAsync(ct);
        _mihonSettingsInitialized = true;
    }

    private void ApplyMihonConfiguration(
        MihonExtensionConfiguration configuration)
    {
        MihonRepositories =
            new ObservableCollection<MihonRepositoryItemViewModel>(
                configuration.Repositories.Select(repository =>
                    new MihonRepositoryItemViewModel(
                        new MihonRepositoryConfiguration
                        {
                            Id = repository.Id,
                            Name = repository.Name,
                            IndexUrl = repository.IndexUrl,
                        },
                        EditMihonRepositoryAsync,
                        RemoveMihonRepositoryAsync)));
        _mihonConfiguration = configuration;
    }

    private async Task ReloadInstalledMihonSourcesAsync(CancellationToken ct)
    {
        var installed = await _mihon.GetInstalledSourcesAsync(ct);
        MihonInstalledSources =
            new ObservableCollection<MihonInstalledExtension>(installed);
        SelectedMihonSource = installed.FirstOrDefault();
    }

    private MihonExtensionConfiguration CreateMihonConfiguration() => new()
    {
        Repositories = MihonRepositories
            .Select(item => item.ToConfiguration())
            .ToList(),
        Library = (_mihonConfiguration?.Library ?? [])
            .Select(CloneMihonLibraryEntry)
            .ToList(),
        BridgeUrl = _mihonConfiguration?.BridgeUrl
            ?? "http://127.0.0.1:48981",
        JavaExecutablePath =
            _mihonConfiguration?.JavaExecutablePath ?? string.Empty,
        ServerJarPath = _mihonConfiguration?.ServerJarPath ?? string.Empty,
    };

    private static MihonLibraryEntry CloneMihonLibraryEntry(
        MihonLibraryEntry entry) =>
        new()
        {
            SourceId = entry.SourceId,
            SourceName = entry.SourceName,
            SourceLang = entry.SourceLang,
            SourceBaseUrl = entry.SourceBaseUrl,
            PackageName = entry.PackageName,
            Manga = CloneMihonManga(entry.Manga),
            AddedAt = entry.AddedAt,
        };

    private static MihonManga CloneMihonManga(MihonManga manga) =>
        new()
        {
            Url = manga.Url,
            Title = manga.Title,
            Artist = manga.Artist,
            Author = manga.Author,
            Description = manga.Description,
            Genres = [.. (manga.Genres ?? [])],
            Status = manga.Status,
            ThumbnailUrl = manga.ThumbnailUrl,
        };

    private static MihonLibraryEntry CreateMihonLibraryEntry(
        MihonInstalledExtension source,
        MihonManga manga) =>
        new()
        {
            SourceId = source.SourceId,
            SourceName = source.SourceName,
            SourceLang = source.Lang,
            SourceBaseUrl = source.BaseUrl,
            PackageName = source.PackageName,
            Manga = CloneMihonManga(manga),
            AddedAt = DateTimeOffset.UtcNow,
        };

    private static bool IsMihonLibraryEntry(
        MihonLibraryEntry entry,
        MihonInstalledExtension source,
        MihonManga manga) =>
        string.Equals(
            entry.PackageName,
            source.PackageName,
            StringComparison.Ordinal)
        && string.Equals(
            entry.SourceId,
            source.SourceId,
            StringComparison.Ordinal)
        && string.Equals(
            entry.Manga.Url,
            manga.Url,
            StringComparison.Ordinal);

    private async Task EnsureSuwayomiSettingsAsync(CancellationToken ct)
    {
        if (_suwayomiSettingsInitialized)
            return;

        var configuration = await _suwayomi.LoadConfigurationAsync(ct);
        ServerUrl = configuration.ServerUrl;
        AuthMode = configuration.AuthMode;
        Username = configuration.Username;
        _suwayomiCredentialId = configuration.CredentialId;
        RememberCredentialConfiguration(configuration);
        _onlineConfiguration = configuration;
        _suwayomiSettingsInitialized = true;
    }

    private async Task ConnectSuwayomiInternalAsync(bool saveConfiguration)
    {
        if (IsSuwayomiBusy)
            return;

        try
        {
            IsSuwayomiBusy = true;
            SuwayomiStatusMessage = ResourceStringHelper.GetString(
                "SuwayomiLoadingStatus",
                "Loading…");
            await EnsureSuwayomiSettingsAsync(_pageCts.Token);
            var configuration = CreateSuwayomiConfiguration();
            var sources = await _suwayomi.ConnectAsync(
                configuration,
                Secret,
                _pageCts.Token);
            if (saveConfiguration)
            {
                await _suwayomi.SaveConfigurationAsync(
                    configuration,
                    Secret,
                    _pageCts.Token);
                _suwayomiCredentialId = configuration.CredentialId;
                RememberCredentialConfiguration(configuration);
                Secret = string.Empty;
            }

            Sources = new ObservableCollection<SuwayomiSource>(sources);
            SelectedSource = Sources.FirstOrDefault();
            _onlineConfiguration = configuration;
            _onlineInitialized = false;
            IsOnlineConnected = true;
            SuwayomiStatusMessage = sources.Count == 0
                ? ResourceStringHelper.GetString(
                    "SuwayomiNoSourcesStatus",
                    "Connected, but no installed sources were found.")
                : ResourceStringHelper.FormatString(
                    "SuwayomiConnectedStatus",
                    "Connected. {0} installed source(s).",
                    sources.Count);
        }
        catch (OperationCanceledException) when (_pageCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Sources.Clear();
            SelectedSource = null;
            IsOnlineConnected = false;
            SuwayomiStatusMessage = ex.Message;
        }
        finally
        {
            IsSuwayomiBusy = false;
        }
    }

    private async Task BrowseSuwayomiAsync(
        string? query,
        bool append = false)
    {
        if (SelectedSource is null)
        {
            SuwayomiStatusMessage = ResourceStringHelper.GetString(
                "SuwayomiSelectSourceStatus",
                "Select an installed source first.");
            return;
        }
        if (IsSuwayomiBusy || IsBrowseLoadingMore)
            return;

        var selectedSource = SelectedSource;
        var sourceIdentity = $"Suwayomi\u001f{selectedSource.Id}";
        if (append
            && (!_browseHasNextPage
                || SelectedSourceKind != MangaRemoteSourceKind.Suwayomi
                || !string.Equals(
                    _activeBrowseSourceIdentity,
                    sourceIdentity,
                    StringComparison.Ordinal)))
        {
            return;
        }

        try
        {
            if (append)
            {
                IsBrowseLoadingMore = true;
            }
            else
            {
                ResetBrowsePagination(clearBooks: true);
                IsSuwayomiBusy = true;
                SuwayomiStatusMessage = ResourceStringHelper.GetString(
                    "SuwayomiLoadingStatus",
                    "Loading…");
                _activeBrowseQuery = NormalizeBrowseQuery(query);
                _activeBrowseSourceIdentity = sourceIdentity;
            }

            var requestedPage = append ? _nextBrowsePage : 1;
            var configuration = CreateSuwayomiConfiguration();
            var page = await _suwayomi.BrowseAsync(
                configuration,
                Secret,
                selectedSource.Id,
                _activeBrowseQuery,
                requestedPage,
                _pageCts.Token);
            if (SelectedSourceKind != MangaRemoteSourceKind.Suwayomi
                || SelectedSource?.Id != selectedSource.Id
                || !string.Equals(
                    _activeBrowseSourceIdentity,
                    sourceIdentity,
                    StringComparison.Ordinal))
            {
                return;
            }

            var existingIds = BrowseBooks
                .Where(item => item.Provider == "Suwayomi")
                .Select(item => item.Id)
                .ToHashSet(StringComparer.Ordinal);
            var additions = page.MangaList
                .Where(manga => existingIds.Add(
                    manga.Id.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)))
                .ToList();
            var items = additions
                .Select(manga => new RemoteMangaLibraryItemViewModel(
                    "Suwayomi",
                    manga.Id.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    manga.Title,
                    () => ShowSuwayomiMangaDetailsAsync(manga)))
                .ToList();
            if (append)
            {
                foreach (var item in items)
                    BrowseBooks.Add(item);
            }
            else
            {
                BrowseBooks =
                    new ObservableCollection<RemoteMangaLibraryItemViewModel>(items);
            }
            _browseHasNextPage = page.HasNextPage && additions.Count > 0;
            _nextBrowsePage = _browseHasNextPage
                ? requestedPage + 1
                : requestedPage;
            _onlineConfiguration = configuration;
            SuwayomiStatusMessage = BrowseBooks.Count == 0
                ? ResourceStringHelper.GetString(
                    "SuwayomiNoMangaStatus",
                    "No manga matched this request.")
                : ResourceStringHelper.FormatString(
                    "SuwayomiMangaLoadedStatus",
                    "{0} manga loaded.",
                    BrowseBooks.Count);
            _ = LoadRemoteCoversAsync(
                items,
                additions,
                configuration,
                Secret,
                _pageCts.Token);
        }
        catch (OperationCanceledException) when (_pageCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!append)
                BrowseBooks.Clear();
            _browseHasNextPage = false;
            SuwayomiStatusMessage = ex.Message;
        }
        finally
        {
            if (append)
                IsBrowseLoadingMore = false;
            else
                IsSuwayomiBusy = false;
        }
    }

    private async Task BrowseMihonAsync(
        string? query,
        bool append = false)
    {
        await EnsureMihonSettingsAsync(_pageCts.Token);
        if (SelectedMihonSource is null)
        {
            MihonStatusMessage = ResourceStringHelper.GetString(
                "MihonSelectInstalledSourceStatus",
                "Install and select a Mihon source first.");
            return;
        }
        if (IsMihonBusy || IsBrowseLoadingMore)
            return;

        var selectedSource = SelectedMihonSource;
        var sourceIdentity =
            $"Mihon\u001f{selectedSource.PackageName}\u001f{selectedSource.SourceId}";
        if (append
            && (!_browseHasNextPage
                || SelectedSourceKind != MangaRemoteSourceKind.Mihon
                || !string.Equals(
                    _activeBrowseSourceIdentity,
                    sourceIdentity,
                    StringComparison.Ordinal)))
        {
            return;
        }

        try
        {
            if (append)
            {
                IsBrowseLoadingMore = true;
            }
            else
            {
                ResetBrowsePagination(clearBooks: true);
                IsMihonBusy = true;
                MihonStatusMessage = ResourceStringHelper.GetString(
                    "SuwayomiLoadingStatus",
                    "Loading…");
                _activeBrowseQuery = NormalizeBrowseQuery(query);
                _activeBrowseSourceIdentity = sourceIdentity;
            }

            var requestedPage = append ? _nextBrowsePage : 1;
            var configuration = CreateMihonConfiguration();
            var page = await _mihon.BrowseAsync(
                configuration,
                selectedSource,
                _activeBrowseQuery,
                requestedPage,
                _pageCts.Token);
            if (SelectedSourceKind != MangaRemoteSourceKind.Mihon
                || SelectedMihonSource?.PackageName != selectedSource.PackageName
                || SelectedMihonSource.SourceId != selectedSource.SourceId
                || !string.Equals(
                    _activeBrowseSourceIdentity,
                    sourceIdentity,
                    StringComparison.Ordinal))
            {
                return;
            }

            var existingIds = BrowseBooks
                .Where(item => item.Provider == "Mihon")
                .Select(item => item.Id)
                .ToHashSet(StringComparer.Ordinal);
            var additions = page.MangaList
                .Where(manga => existingIds.Add(Sha256Identity(
                    $"{selectedSource.PackageName}\u001f{selectedSource.SourceId}\u001f{manga.Url}")))
                .ToList();
            var items = additions
                .Select(manga => new RemoteMangaLibraryItemViewModel(
                    "Mihon",
                    Sha256Identity(
                        $"{selectedSource.PackageName}\u001f{selectedSource.SourceId}\u001f{manga.Url}"),
                    manga.Title,
                    () => ShowMihonMangaDetailsAsync(selectedSource, manga)))
                .ToList();
            if (append)
            {
                foreach (var item in items)
                    BrowseBooks.Add(item);
            }
            else
            {
                BrowseBooks =
                    new ObservableCollection<RemoteMangaLibraryItemViewModel>(items);
            }
            _browseHasNextPage = page.HasNextPage && additions.Count > 0;
            _nextBrowsePage = _browseHasNextPage
                ? requestedPage + 1
                : requestedPage;
            _mihonConfiguration = configuration;
            MihonStatusMessage = BrowseBooks.Count == 0
                ? ResourceStringHelper.GetString(
                    "MihonNoMangaStatus",
                    "No manga matched this request.")
                : ResourceStringHelper.FormatString(
                    "MihonMangaLoadedStatus",
                    "{0} manga loaded from the Mihon extension.",
                    BrowseBooks.Count);
            _ = LoadMihonCoversAsync(
                items,
                additions,
                selectedSource,
                _pageCts.Token);
        }
        catch (OperationCanceledException) when (_pageCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!append)
                BrowseBooks.Clear();
            _browseHasNextPage = false;
            MihonStatusMessage = ex.Message;
        }
        finally
        {
            if (append)
                IsBrowseLoadingMore = false;
            else
                IsMihonBusy = false;
        }
    }

    partial void OnSelectedSourceKindChanged(MangaRemoteSourceKind value) =>
        ResetBrowsePagination(clearBooks: true);

    partial void OnSelectedSourceChanged(SuwayomiSource? value)
    {
        if (IsSuwayomiSourceKind)
            ResetBrowsePagination(clearBooks: true);
    }

    partial void OnSelectedMihonSourceChanged(MihonInstalledExtension? value)
    {
        if (IsMihonSourceKind)
            ResetBrowsePagination(clearBooks: true);
    }

    private void ResetBrowsePagination(bool clearBooks)
    {
        _nextBrowsePage = 1;
        _browseHasNextPage = false;
        _activeBrowseQuery = null;
        _activeBrowseSourceIdentity = null;
        if (clearBooks)
            BrowseBooks = [];
    }

    private static string? NormalizeBrowseQuery(string? query) =>
        string.IsNullOrWhiteSpace(query) ? null : query.Trim();

    private SuwayomiServerConfiguration CreateSuwayomiConfiguration()
    {
        var preservesCredential =
            string.Equals(
                ServerUrl.TrimEnd('/'),
                _credentialServerUrl.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase)
            && AuthMode == _credentialAuthMode
            && string.Equals(
                Username,
                _credentialUsername,
                StringComparison.Ordinal);
        return new SuwayomiServerConfiguration
        {
            ServerUrl = ServerUrl,
            AuthMode = AuthMode,
            Username = Username,
            CredentialId = preservesCredential
                ? _suwayomiCredentialId
                : null,
        };
    }

    private void RememberCredentialConfiguration(
        SuwayomiServerConfiguration configuration)
    {
        _credentialServerUrl = configuration.ServerUrl;
        _credentialAuthMode = configuration.AuthMode;
        _credentialUsername = configuration.Username;
    }

    private async Task LoadOnlineLibraryAsync(
        bool force,
        CancellationToken ct)
    {
        if (_onlineInitialized && !force)
            return;

        IsOnlineLoading = true;
        OnlineStatusMessage = ResourceStringHelper.GetString(
            "SuwayomiLoadingStatus",
            "Loading…");
        var items = new List<RemoteMangaLibraryItemViewModel>();
        var mihonItems = new List<(
            RemoteMangaLibraryItemViewModel Item,
            MihonLibraryEntry Entry,
            MihonInstalledExtension Source)>();
        Exception? loadError = null;
        var suwayomiConnected = false;
        try
        {
            try
            {
                await EnsureMihonSettingsAsync(ct);
                var sources = MihonInstalledSources
                    .GroupBy(
                        source =>
                            $"{source.PackageName}\u001f{source.SourceId}",
                        StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First(),
                        StringComparer.Ordinal);
                foreach (var entry in _mihonConfiguration?.Library ?? [])
                {
                    if (!sources.TryGetValue(
                            $"{entry.PackageName}\u001f{entry.SourceId}",
                            out var source))
                    {
                        continue;
                    }
                    var selectedSource = source;
                    var selectedManga = CloneMihonManga(entry.Manga);
                    var item = new RemoteMangaLibraryItemViewModel(
                        "Mihon",
                        Sha256Identity(
                            $"{entry.PackageName}\u001f{entry.SourceId}\u001f{entry.Manga.Url}"),
                        entry.Manga.Title,
                        () => ShowMihonMangaDetailsAsync(
                            selectedSource,
                            selectedManga));
                    items.Add(item);
                    mihonItems.Add((item, entry, source));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                loadError = ex;
            }

            IReadOnlyList<SuwayomiManga> suwayomiManga = [];
            SuwayomiServerConfiguration? configuration = null;
            try
            {
                configuration = await _suwayomi.LoadConfigurationAsync(ct);
                await _suwayomi.ConnectAsync(
                    configuration,
                    secret: null,
                    ct);
                suwayomiManga = await _suwayomi.GetLibraryAsync(
                    configuration,
                    secret: null,
                    ct);
                items.AddRange(suwayomiManga.Select(manga =>
                    new RemoteMangaLibraryItemViewModel(
                        "Suwayomi",
                        manga.Id.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        manga.Title,
                        () => ShowSuwayomiMangaDetailsAsync(manga))));
                _onlineConfiguration = configuration;
                suwayomiConnected = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _onlineConfiguration = null;
                loadError ??= ex;
            }

            OnlineBooks =
                new ObservableCollection<RemoteMangaLibraryItemViewModel>(items);
            _onlineInitialized = true;
            IsOnlineConnected = suwayomiConnected || mihonItems.Count > 0;
            OnlineStatusMessage = items.Count == 0
                ? loadError?.Message
                    ?? ResourceStringHelper.GetString(
                        "MangaOnlineEmptyStatus",
                        "Your manga library is empty. Add manga from Browse.")
                : ResourceStringHelper.FormatString(
                    "MangaOnlineLoadedStatus",
                    "{0} online manga loaded.",
                    items.Count);

            if (configuration is not null && suwayomiManga.Count > 0)
            {
                var suwayomiItems = items
                    .Where(item => item.Provider == "Suwayomi")
                    .ToList();
                _ = LoadRemoteCoversAsync(
                    suwayomiItems,
                    suwayomiManga,
                    configuration,
                    secret: null,
                    ct);
            }
            foreach (var group in mihonItems.GroupBy(pair =>
                         $"{pair.Source.PackageName}\u001f{pair.Source.SourceId}",
                         StringComparer.Ordinal))
            {
                var groupItems = group.ToList();
                _ = LoadMihonCoversAsync(
                    groupItems.Select(pair => pair.Item).ToList(),
                    groupItems.Select(pair => pair.Entry.Manga).ToList(),
                    groupItems[0].Source,
                    ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _onlineInitialized = false;
            IsOnlineConnected = false;
            OnlineStatusMessage = ex.Message;
        }
        finally
        {
            IsOnlineLoading = false;
        }
    }

    private async Task LoadRemoteCoversAsync(
        IReadOnlyList<RemoteMangaLibraryItemViewModel> items,
        IReadOnlyList<SuwayomiManga> sourceManga,
        SuwayomiServerConfiguration configuration,
        string? secret,
        CancellationToken ct)
    {
        for (var index = 0;
             index < Math.Min(items.Count, sourceManga.Count);
             index++)
        {
            try
            {
                var item = items[index];
                var manga = sourceManga[index];
                var path = await _suwayomi.GetThumbnailPathAsync(
                    configuration,
                    secret,
                    manga.Id,
                    ct);
                item.SetCoverPath(path);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Keep the standard bookshelf placeholder when one cover fails.
            }
        }
    }

    private async Task LoadMihonCoversAsync(
        IReadOnlyList<RemoteMangaLibraryItemViewModel> items,
        IReadOnlyList<MihonManga> manga,
        MihonInstalledExtension source,
        CancellationToken ct)
    {
        for (var index = 0; index < Math.Min(items.Count, manga.Count); index++)
        {
            try
            {
                var path = await _mihon.GetThumbnailPathAsync(
                    source,
                    manga[index],
                    ct);
                items[index].SetCoverPath(path);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Keep the shared bookshelf placeholder when one cover fails.
            }
        }
    }

    private async Task ShowMihonMangaDetailsAsync(
        MihonInstalledExtension source,
        MihonManga manga) =>
        await ShowMihonMangaDetailsCoreAsync(source, manga, discoveryItem: null);

    private async Task ShowMatchedMihonMangaDetailsAsync(
        MihonInstalledExtension source,
        MihonManga manga,
        MangaDiscoveryItem discoveryItem) =>
        await ShowMihonMangaDetailsCoreAsync(source, manga, discoveryItem);

    private async Task ShowMihonMangaDetailsCoreAsync(
        MihonInstalledExtension source,
        MihonManga manga,
        MangaDiscoveryItem? discoveryItem)
    {
        var configuration = _mihonConfiguration ?? CreateMihonConfiguration();
        var details = new RemoteMangaDetailViewModel(
            "Mihon",
            Sha256Identity(
                $"{source.PackageName}\u001f{source.SourceId}\u001f{manga.Url}"),
            manga.Title,
            supportsOnlineLibrary: true);
        details.SetExtensionOptions(
            MihonInstalledSources.Append(source),
            source);
        var isInLibrary = configuration.Library.Any(entry =>
            IsMihonLibraryEntry(entry, source, manga));
        if (discoveryItem is not null)
            details.ApplyDiscoveryDetails(discoveryItem);
        details.ApplyDetails(
            manga.Title,
            manga.Author,
            string.IsNullOrWhiteSpace(manga.Description)
                ? details.Description
                : manga.Description,
            manga.Genres,
            isInOnlineLibrary: isInLibrary);
        var ct = BeginRemoteDetailsLoad(details);
        if (discoveryItem is not null)
        {
            _ = LoadMangaDiscoveryDetailPosterAsync(
                details,
                discoveryItem,
                ct,
                onlyIfMissing: true);
        }
        _selectedDetailMihonSource = source;
        _selectedMihonManga = manga;
        await LoadMihonDetailsAsync(details, source, manga, ct);
    }

    [RelayCommand]
    private async Task SelectRemoteMangaExtensionAsync(
        RemoteMangaExtensionOptionViewModel? option)
    {
        var details = SelectedRemoteMangaDetails;
        if (details is null
            || option is null
            || details.IsActionBusy)
        {
            return;
        }

        var previousSource = _selectedDetailMihonSource;
        var previousManga = _selectedMihonManga;
        if (previousSource is not null
            && previousManga is not null
            && string.Equals(
                RemoteMangaExtensionOptionViewModel.GetKey(previousSource),
                option.Id,
                StringComparison.Ordinal))
        {
            details.SelectExtension(option.Id);
            return;
        }

        details.IsActionBusy = true;
        details.IsLoading = true;
        details.ActionStatus = ResourceStringHelper.GetString(
            "MangaRemoteDetailsSwitchingExtensionStatus",
            "Loading from the selected extension…");
        details.ErrorMessage = string.Empty;
        var ct = BeginRemoteDetailsLoad(details);
        try
        {
            var configuration = _mihonConfiguration ?? CreateMihonConfiguration();
            var manga = previousSource is not null
                && previousManga is not null
                && string.Equals(
                    RemoteMangaExtensionOptionViewModel.GetKey(previousSource),
                    option.Id,
                    StringComparison.Ordinal)
                ? previousManga
                : await FindMihonMangaByTitlesAsync(
                    configuration,
                    option.Source,
                    details.SearchTitles.Count > 0
                        ? details.SearchTitles
                        : [details.Title],
                    ct);
            if (manga is null)
            {
                details.IsLoading = false;
                details.ErrorMessage = ResourceStringHelper.FormatString(
                    "MangaRemoteDetailsExtensionNoMatch",
                    "The selected extension did not find \"{0}\".",
                    details.Title);
                return;
            }

            details.SetExtensionOptions(
                MihonInstalledSources.Append(option.Source),
                option.Source);
            _selectedDetailMihonSource = option.Source;
            _selectedMihonManga = manga;
            await LoadMihonDetailsAsync(details, option.Source, manga, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentRemoteDetails(details, ct))
            {
                details.IsLoading = false;
                details.ErrorMessage = ex.Message;
            }
        }
        finally
        {
            details.IsActionBusy = false;
            details.ActionStatus = string.Empty;
        }
    }

    private async Task<MihonManga?> FindMihonMangaAsync(
        MihonExtensionConfiguration configuration,
        MihonInstalledExtension source,
        string title,
        CancellationToken ct)
    {
        var page = await _mihon.BrowseAsync(
            configuration,
            source,
            title,
            1,
            ct);
        var candidates = page.MangaList
            .Where(item => !string.IsNullOrWhiteSpace(item.Url))
            .ToList();
        if (candidates.Count == 0)
            return null;

        var normalizedTitle = NormalizeMangaTitle(title);
        return candidates.FirstOrDefault(item =>
                   string.Equals(
                       NormalizeMangaTitle(item.Title),
                       normalizedTitle,
                       StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(item =>
                NormalizeMangaTitle(item.Title).Contains(
                    normalizedTitle,
                    StringComparison.OrdinalIgnoreCase)
                || normalizedTitle.Contains(
                    NormalizeMangaTitle(item.Title),
                    StringComparison.OrdinalIgnoreCase));
    }

    private async Task<MihonManga?> FindMihonMangaByTitlesAsync(
        MihonExtensionConfiguration configuration,
        MihonInstalledExtension source,
        IEnumerable<string> titles,
        CancellationToken ct)
    {
        foreach (var title in titles
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(value => value.Trim())
                     .Distinct(StringComparer.CurrentCultureIgnoreCase))
        {
            var manga = await FindMihonMangaAsync(
                configuration,
                source,
                title,
                ct);
            if (manga is not null)
                return manga;
        }

        return null;
    }

    private static string NormalizeMangaTitle(string? title) =>
        new(
            (title ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());

    private async Task LoadMihonDetailsAsync(
        RemoteMangaDetailViewModel details,
        MihonInstalledExtension source,
        MihonManga manga,
        CancellationToken ct)
    {
        try
        {
            var configuration = _mihonConfiguration ?? CreateMihonConfiguration();
            var resolvedMangaTask = _mihon.GetMangaDetailsAsync(
                configuration,
                source,
                manga,
                ct);
            var chaptersTask = _mihon.GetChaptersAsync(
                configuration,
                source,
                manga,
                ct);
            await Task.WhenAll(resolvedMangaTask, chaptersTask);
            if (!IsCurrentRemoteDetails(details, ct))
                return;

            var resolvedManga = await resolvedMangaTask;
            if (string.IsNullOrWhiteSpace(resolvedManga.Url))
                resolvedManga.Url = manga.Url;
            if (string.IsNullOrWhiteSpace(resolvedManga.Title))
                resolvedManga.Title = manga.Title;
            var chapters = await chaptersTask;
            _selectedDetailMihonSource = source;
            _selectedMihonManga = resolvedManga;
            _selectedMihonChapters = chapters;
            var isInLibrary = (_mihonConfiguration ?? configuration).Library.Any(entry =>
                IsMihonLibraryEntry(entry, source, resolvedManga));
            details.ApplyDetails(
                resolvedManga.Title,
                resolvedManga.Author,
                string.IsNullOrWhiteSpace(resolvedManga.Description)
                    ? details.Description
                    : resolvedManga.Description,
                resolvedManga.Genres,
                isInOnlineLibrary: isInLibrary);
            details.Chapters = new ObservableCollection<RemoteMangaChapterItemViewModel>(
                chapters
                    .OrderByDescending(item => item.ChapterNumber)
                    .ThenByDescending(item => item.UploadDate)
                    .Select(chapter => new RemoteMangaChapterItemViewModel(
                        Sha256Identity(
                            $"{chapter.Url}\u001f{chapter.Name}\u001f{chapter.ChapterNumber}"),
                        chapter.Name,
                        chapter.Scanlator?.Trim() ?? string.Empty,
                        isRead: false,
                        () => OpenMihonChapterAsync(
                            source,
                            resolvedManga,
                            chapter))));
            details.IsLoading = false;
            await TryLoadMihonDetailCoverAsync(
                details,
                source,
                resolvedManga,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentRemoteDetails(details, ct))
            {
                details.IsLoading = false;
                details.ErrorMessage = ex.Message;
            }
        }
    }

    private async Task ShowSuwayomiMangaDetailsAsync(SuwayomiManga manga)
    {
        if (_onlineConfiguration is null)
            return;

        var details = new RemoteMangaDetailViewModel(
            "Suwayomi",
            manga.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            manga.Title,
            supportsOnlineLibrary: true);
        details.SetExtensionOptions(MihonInstalledSources, selected: null);
        details.ApplyDetails(
            manga.Title,
            manga.Author,
            manga.MangaDescription,
            manga.Genre,
            manga.InLibrary);
        var ct = BeginRemoteDetailsLoad(details);
        try
        {
            var secret = IsRemoteSuwayomiSurfaceSelected ? Secret : null;
            var resolvedMangaTask = _suwayomi.GetMangaDetailsAsync(
                _onlineConfiguration,
                secret,
                manga.Id,
                ct);
            var chaptersTask = _suwayomi.GetChaptersAsync(
                _onlineConfiguration,
                secret,
                manga.Id,
                ct);
            await Task.WhenAll(resolvedMangaTask, chaptersTask);
            if (!IsCurrentRemoteDetails(details, ct))
                return;

            var resolvedManga = await resolvedMangaTask;
            var chapters = await chaptersTask;
            _selectedSuwayomiManga = resolvedManga;
            _selectedSuwayomiChapters = chapters;
            details.ApplyDetails(
                resolvedManga.Title,
                resolvedManga.Author,
                resolvedManga.MangaDescription,
                resolvedManga.Genre,
                resolvedManga.InLibrary);
            details.Chapters = new ObservableCollection<RemoteMangaChapterItemViewModel>(
                chapters
                    .OrderByDescending(item => item.Index)
                    .ThenByDescending(item => item.Id)
                    .Select(chapter =>
                    {
                        var metadata = string.Join(
                            " · ",
                            new[]
                            {
                                chapter.Read
                                    ? ResourceStringHelper.GetString(
                                        "MangaRemoteDetailsReadChapterStatus",
                                        "Read")
                                    : null,
                                chapter.Scanlator?.Trim(),
                            }.Where(value => !string.IsNullOrWhiteSpace(value)));
                        return new RemoteMangaChapterItemViewModel(
                            chapter.Id.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            chapter.Name,
                            metadata,
                            chapter.Read,
                            () => OpenSuwayomiChapterAsync(
                                resolvedManga,
                                chapter));
                    }));
            details.IsLoading = false;
            await TryLoadSuwayomiDetailCoverAsync(
                details,
                _onlineConfiguration,
                secret,
                resolvedManga.Id,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentRemoteDetails(details, ct))
            {
                details.IsLoading = false;
                details.ErrorMessage = ex.Message;
            }
        }
    }

    private CancellationToken BeginRemoteDetailsLoad(
        RemoteMangaDetailViewModel details)
    {
        _remoteDetailsCts?.Cancel();
        _remoteDetailsCts?.Dispose();
        _remoteDetailsCts =
            CancellationTokenSource.CreateLinkedTokenSource(_pageCts.Token);
        _selectedSuwayomiManga = null;
        _selectedSuwayomiChapters = [];
        _selectedDetailMihonSource = null;
        _selectedMihonManga = null;
        _selectedMihonChapters = [];
        SelectedRemoteMangaDetails = details;
        return _remoteDetailsCts.Token;
    }

    private bool IsCurrentRemoteDetails(
        RemoteMangaDetailViewModel details,
        CancellationToken ct) =>
        !ct.IsCancellationRequested
        && ReferenceEquals(SelectedRemoteMangaDetails, details);

    private async Task TryLoadSuwayomiDetailCoverAsync(
        RemoteMangaDetailViewModel details,
        SuwayomiServerConfiguration configuration,
        string? secret,
        int mangaId,
        CancellationToken ct)
    {
        try
        {
            var path = await _suwayomi.GetThumbnailPathAsync(
                configuration,
                secret,
                mangaId,
                ct);
            if (IsCurrentRemoteDetails(details, ct))
                details.SetCoverPath(path);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch
        {
            // The detail surface keeps its stable poster placeholder.
        }
    }

    private async Task TryLoadMihonDetailCoverAsync(
        RemoteMangaDetailViewModel details,
        MihonInstalledExtension source,
        MihonManga manga,
        CancellationToken ct)
    {
        try
        {
            var path = await _mihon.GetThumbnailPathAsync(source, manga, ct);
            if (IsCurrentRemoteDetails(details, ct))
                details.SetCoverPath(path);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch
        {
            // The detail surface keeps its stable poster placeholder.
        }
    }

    private async Task OpenSuwayomiChapterAsync(
        SuwayomiManga manga,
        SuwayomiChapter chapter)
    {
        var details = SelectedRemoteMangaDetails;
        if (details is null
            || details.IsActionBusy
            || _onlineConfiguration is null)
        {
            return;
        }

        details.IsActionBusy = true;
        details.ActionStatus = ResourceStringHelper.GetString(
            "SuwayomiPreparingChapterStatus",
            "Preparing chapter…");
        details.ErrorMessage = string.Empty;
        try
        {
            var book = await _suwayomi.CreateReaderBookAsync(
                _onlineConfiguration,
                    IsRemoteSuwayomiSurfaceSelected ? Secret : null,
                manga,
                chapter,
                _pageCts.Token);
            await _readerWindow.OpenAsync(book, _pageCts.Token);
            details.ActionStatus = ResourceStringHelper.GetString(
                "SuwayomiChapterReadyStatus",
                "Chapter ready.");
        }
        catch (OperationCanceledException) when (_pageCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            details.ErrorMessage = ex.Message;
            details.ActionStatus = string.Empty;
        }
        finally
        {
            details.IsActionBusy = false;
        }
    }

    private async Task OpenMihonChapterAsync(
        MihonInstalledExtension source,
        MihonManga manga,
        MihonChapter chapter)
    {
        var details = SelectedRemoteMangaDetails;
        if (details is null || details.IsActionBusy)
            return;

        details.IsActionBusy = true;
        details.ActionStatus = ResourceStringHelper.GetString(
            "MihonPreparingChapterStatus",
            "Preparing a Mihon chapter…");
        details.ErrorMessage = string.Empty;
        try
        {
            var book = await _mihon.CreateReaderBookAsync(
                _mihonConfiguration ?? CreateMihonConfiguration(),
                source,
                manga,
                chapter,
                _pageCts.Token);
            await _readerWindow.OpenAsync(book, _pageCts.Token);
            details.ActionStatus = ResourceStringHelper.GetString(
                "MihonChapterReadyStatus",
                "Mihon chapter ready.");
        }
        catch (OperationCanceledException) when (_pageCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            details.ErrorMessage = ex.Message;
            details.ActionStatus = string.Empty;
        }
        finally
        {
            details.IsActionBusy = false;
        }
    }

    private async Task ImportPathAsync(string path)
    {
        IsLoading = true;
        var result = await _library.ImportAsync(path, _pageCts.Token);
        if (result.IsSuccess)
        {
            _notifications.ShowSuccess(
                ResourceStringHelper.FormatString(
                    "MangaLibraryImportedMessage",
                    "Imported {0}.",
                    result.Value!.DisplayTitle),
                ResourceStringHelper.GetString(
                    "MangaLibraryImportedTitle",
                    "Manga imported"));
            await LoadAsync(_pageCts.Token);
        }
        else if (!result.IsCancelled)
        {
            IsLoading = false;
            _notifications.ShowError(
                result.Error ?? ResourceStringHelper.GetString(
                    "MangaLibraryImportFailed",
                    "The manga could not be imported."),
                result.ErrorTitle ?? ResourceStringHelper.GetString(
                    "MangaLibraryImportTitle",
                    "Manga import"));
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = true;
        var result = await _library.GetBooksAsync(ct);
        if (result.IsSuccess)
        {
            Books = new ObservableCollection<MangaLibraryItemViewModel>(
                result.Value!.Select(book => new MangaLibraryItemViewModel(
                    book,
                    OpenAsync,
                    RenameAsync,
                    MarkReadAsync,
                    RemoveAsync)));
        }
        else if (!result.IsCancelled)
        {
            _notifications.ShowError(
                result.Error ?? ResourceStringHelper.GetString(
                    "MangaLibraryLoadFailed",
                    "The manga library could not be loaded."),
                result.ErrorTitle ?? ResourceStringHelper.GetString(
                    "MangaLibraryDialogTitle",
                    "Manga library"));
        }

        IsLoading = false;
    }

    private async Task OpenAsync(MangaBook book)
    {
        await _readerWindow.OpenAsync(book, _pageCts.Token);
    }

    public Task OpenBookAsync(MangaBook book) =>
        _readerWindow.OpenAsync(book, _pageCts.Token);

    private async Task RenameAsync(MangaBook book)
    {
        var title = await _dialogs.PromptTextAsync(
            ResourceStringHelper.GetString(
                "MangaLibraryRenameTitle",
                "Rename manga"),
            book.DisplayTitle,
            ResourceStringHelper.GetString("MangaLibrarySave", "Save"),
            ResourceStringHelper.GetString("MangaLibraryCancel", "Cancel"));
        if (title is null)
            return;

        var result = await _library.RenameAsync(book.Id, title, _pageCts.Token);
        await HandleMutationAsync(
            result,
            ResourceStringHelper.GetString(
                "MangaLibraryRenameFailed",
                "Unable to rename manga."));
    }

    private async Task MarkReadAsync(MangaBook book)
    {
        var result = await _library.MarkReadAsync(book.Id, _pageCts.Token);
        await HandleMutationAsync(
            result,
            ResourceStringHelper.GetString(
                "MangaLibraryMarkReadFailed",
                "Unable to mark manga as read."));
    }

    private async Task RemoveAsync(MangaBook book)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            ResourceStringHelper.GetString(
                "MangaLibraryRemoveTitle",
                "Remove manga?"),
            ResourceStringHelper.GetString(
                "MangaLibraryRemoveMessage",
                "This removes the library card only. The original files will not be changed."),
            ResourceStringHelper.GetString("MangaLibraryRemove", "Remove"),
            ResourceStringHelper.GetString("MangaLibraryCancel", "Cancel"));
        if (!confirmed)
            return;

        var result = await _library.RemoveAsync(book.Id, _pageCts.Token);
        await HandleMutationAsync(
            result,
            ResourceStringHelper.GetString(
                "MangaLibraryRemoveFailed",
                "Unable to remove manga."));
    }

    private async Task HandleMutationAsync(
        Niratan.Models.Common.Result result,
        string fallbackMessage)
    {
        if (result.IsSuccess)
        {
            await LoadAsync(_pageCts.Token);
        }
        else if (!result.IsCancelled)
        {
            _notifications.ShowError(
                result.Error ?? fallbackMessage,
                result.ErrorTitle ?? ResourceStringHelper.GetString(
                    "MangaLibraryDialogTitle",
                    "Manga library"));
        }
    }

    private void OnReaderLibraryChanged(object? sender, EventArgs e) =>
        _ = LoadAsync(_pageCts.Token);

    private static bool IsSupportedFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".cbz", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".epub", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mokuro", StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha256Identity(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
