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

    public MangaLibraryPageViewModel(
        IMangaLibraryService library,
        IMangaReaderWindowService readerWindow,
        ISuwayomiService suwayomi,
        IMihonExtensionService mihon,
        IDialogService dialogs,
        INotificationService notifications)
    {
        _library = library;
        _readerWindow = readerWindow;
        _suwayomi = suwayomi;
        _mihon = mihon;
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
    [NotifyPropertyChangedFor(nameof(IsBrowseSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsSourcesSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsBrowseSourceDirectoryVisible))]
    [NotifyPropertyChangedFor(nameof(IsBrowseResultsVisible))]
    [NotifyPropertyChangedFor(nameof(IsLocalLibraryVisible))]
    [NotifyPropertyChangedFor(nameof(IsOnlineLibraryVisible))]
    [NotifyPropertyChangedFor(nameof(ShowLocalLibraryActions))]
    [NotifyPropertyChangedFor(nameof(ShowOnlineLibraryActions))]
    public partial MangaHomeSection SelectedSection { get; set; } =
        MangaHomeSection.Library;

    public bool IsLibrarySectionSelected =>
        SelectedSection == MangaHomeSection.Library;
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
        _pageCts.Dispose();
        _pageCts = new CancellationTokenSource();
        await LoadAsync(_pageCts.Token);
    }

    public async Task InitializeBrowseAsync()
    {
        _pageCts.Cancel();
        _pageCts.Dispose();
        _pageCts = new CancellationTokenSource();
        SelectedSection = MangaHomeSection.Browse;
        await SelectBrowseAsync();
    }

    public void OnNavigatedFrom()
    {
        CloseRemoteMangaDetails();
        _pageCts.Cancel();
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
                    IsBrowseSectionSelected ? Secret : null,
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
                    () => OpenMihonSourceAsync(source))))
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
                    LoadMihonRepositorySourceIconAsync)));

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
        MihonManga manga)
    {
        var configuration = _mihonConfiguration ?? CreateMihonConfiguration();
        var details = new RemoteMangaDetailViewModel(
            "Mihon",
            Sha256Identity(
                $"{source.PackageName}\u001f{source.SourceId}\u001f{manga.Url}"),
            manga.Title,
            supportsOnlineLibrary: true);
        var isInLibrary = configuration.Library.Any(entry =>
            IsMihonLibraryEntry(entry, source, manga));
        details.ApplyDetails(
            manga.Title,
            manga.Author,
            manga.Description,
            manga.Genres,
            isInOnlineLibrary: isInLibrary);
        var ct = BeginRemoteDetailsLoad(details);
        _selectedDetailMihonSource = source;
        _selectedMihonManga = manga;
        try
        {
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
            isInLibrary = (_mihonConfiguration ?? configuration).Library.Any(entry =>
                IsMihonLibraryEntry(entry, source, resolvedManga));
            details.ApplyDetails(
                resolvedManga.Title,
                resolvedManga.Author,
                resolvedManga.Description,
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
        details.ApplyDetails(
            manga.Title,
            manga.Author,
            manga.MangaDescription,
            manga.Genre,
            manga.InLibrary);
        var ct = BeginRemoteDetailsLoad(details);
        try
        {
            var secret = IsBrowseSectionSelected ? Secret : null;
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
                IsBrowseSectionSelected ? Secret : null,
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
