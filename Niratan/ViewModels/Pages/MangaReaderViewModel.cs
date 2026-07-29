using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Niratan.Helpers;
using Niratan.Models.Manga;
using Niratan.Services.Manga;

namespace Niratan.ViewModels.Pages;

public sealed partial class MangaReaderPageItemViewModel : ObservableObject
{
    public MangaReaderPageItemViewModel(int index)
    {
        Index = index;
    }

    public int Index { get; }
    public string PageNumberText => $"{Index + 1}";
    public string? ImagePath { get; private set; }

    [ObservableProperty]
    public partial BitmapImage? Image { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<MangaTextRegion> TextRegions { get; set; } = [];

    public void SetPath(string path)
    {
        ImagePath = Path.GetFullPath(path);
        Image = new BitmapImage(new Uri(ImagePath));
        IsLoading = false;
        ErrorMessage = null;
    }
}

public partial class MangaReaderViewModel : ObservableObject
{
    private readonly IMangaLibraryService _library;
    private readonly IMangaPageProvider _pageProvider;
    private readonly IMangaTextRegionService _textRegionService;
    private readonly IMangaOcrService _ocrService;
    private readonly ISuwayomiService _suwayomi;
    private CancellationTokenSource _loadCts = new();
    private CancellationTokenSource? _ocrCts;
    private Task? _ocrScanTask;
    private long _ocrGeneration;
    private MangaBook? _book;

    public MangaReaderViewModel(
        IMangaLibraryService library,
        IMangaPageProvider pageProvider,
        IMangaTextRegionService textRegionService,
        IMangaOcrService ocrService,
        ISuwayomiService suwayomi)
    {
        _library = library;
        _pageProvider = pageProvider;
        _textRegionService = textRegionService;
        _ocrService = ocrService;
        _suwayomi = suwayomi;
    }

    [ObservableProperty]
    public partial string Title { get; set; } =
        ResourceStringHelper.GetString("MangaLibraryTitle", "Manga");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageStatus))]
    [NotifyPropertyChangedFor(nameof(CanGoBackward))]
    [NotifyPropertyChangedFor(nameof(CanGoForward))]
    public partial int CurrentPageIndex { get; set; }

    [ObservableProperty]
    public partial int PageCount { get; set; }

    [ObservableProperty]
    public partial MangaReaderLayout Layout { get; set; } = MangaReaderLayout.SinglePage;

    [ObservableProperty]
    public partial MangaReadingDirection Direction { get; set; } = MangaReadingDirection.RightToLeft;

    [ObservableProperty]
    public partial int ZoomPercentage { get; set; } = 100;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsGoogleOcrEnabled { get; set; }

    [ObservableProperty]
    public partial bool GoogleOcrDisclosureAccepted { get; set; }

    [ObservableProperty]
    public partial bool IsRecognizingText { get; set; }

    [ObservableProperty]
    public partial bool IsOcrRecognitionPaused { get; set; }

    [ObservableProperty]
    public partial int OcrCompletedPageCount { get; set; }

    [ObservableProperty]
    public partial int OcrTotalPageCount { get; set; }

    [ObservableProperty]
    public partial string? OcrStatusMessage { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<MangaReaderPageItemViewModel> VisiblePages { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<MangaReaderPageItemViewModel> ContinuousPages { get; set; } = [];

    public string PageStatus => PageCount == 0
        ? "0 / 0"
        : $"{CurrentPageIndex + 1} / {PageCount}";
    public bool CanGoBackward => CurrentPageIndex > 0;
    public bool CanGoForward => CurrentPageIndex < PageCount - 1;
    public bool IsContinuous => Layout == MangaReaderLayout.Continuous;

    public event EventHandler? ReaderStateChanged;

    public async Task InitializeAsync(MangaBook book, CancellationToken ct = default)
    {
        _loadCts.Cancel();
        _loadCts.Dispose();
        InvalidateOcrRecognition();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _loadCts.Token;
        IsLoading = true;
        ErrorMessage = null;

        var sessionResult = await _library.CreateReaderSessionAsync(book, token);
        if (!sessionResult.IsSuccess || sessionResult.Value is null)
        {
            ErrorMessage = sessionResult.Error ?? ResourceStringHelper.GetString(
                "MangaReaderOpenFailed",
                "Manga could not be opened.");
            IsLoading = false;
            return;
        }

        var session = sessionResult.Value;
        _book = session.Book;
        Title = _book.DisplayTitle;
        PageCount = _book.PageCount;
        CurrentPageIndex = Math.Clamp(_book.CurrentPageIndex, 0, Math.Max(0, PageCount - 1));
        Layout = session.Preferences.Layout;
        Direction = session.Preferences.Direction;
        ZoomPercentage = Math.Clamp(session.Preferences.ZoomPercentage, 50, 200);
        IsGoogleOcrEnabled = session.Preferences.IsGoogleOcrEnabled;
        GoogleOcrDisclosureAccepted =
            session.Preferences.GoogleOcrDisclosureAccepted;
        ContinuousPages = new ObservableCollection<MangaReaderPageItemViewModel>(
            Enumerable.Range(0, PageCount).Select(index => new MangaReaderPageItemViewModel(index)));

        await LoadCurrentViewAsync(token);
        IsLoading = false;
        if (IsGoogleOcrEnabled && GoogleOcrDisclosureAccepted)
            StartOcrRecognition();
        ReaderStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task EnableGoogleOcrAsync(bool disclosureAccepted)
    {
        GoogleOcrDisclosureAccepted =
            GoogleOcrDisclosureAccepted || disclosureAccepted;
        IsGoogleOcrEnabled = true;
        StartOcrRecognition();
        await SavePreferencesAsync();
    }

    public async Task HideGoogleOcrAsync()
    {
        InvalidateOcrRecognition();
        IsGoogleOcrEnabled = false;
        OcrStatusMessage = null;
        await SavePreferencesAsync();
        await LoadCurrentViewAsync(_loadCts.Token);
        ReaderStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CancelOcrRecognition()
    {
        if (!IsRecognizingText)
            return;
        Interlocked.Increment(ref _ocrGeneration);
        _ocrCts?.Cancel();
        _ocrCts?.Dispose();
        _ocrCts = null;
        _ocrScanTask = null;
        IsRecognizingText = false;
        IsOcrRecognitionPaused = true;
        OcrStatusMessage = ResourceStringHelper.GetString(
            "MangaOcrPausedStatus",
            "Text recognition paused. Completed pages remain available.");
    }

    public Task ResumeOcrRecognitionAsync()
    {
        if (!IsGoogleOcrEnabled || !IsOcrRecognitionPaused)
            return Task.CompletedTask;
        StartOcrRecognition();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task RecognizeAllPagesAsync()
    {
        StartOcrRecognition();
        return Task.CompletedTask;
    }

    private void StartOcrRecognition()
    {
        if (_book is not { } book || PageCount == 0 || IsRecognizingText)
            return;

        IsGoogleOcrEnabled = true;
        _ocrCts?.Cancel();
        _ocrCts?.Dispose();
        _ocrCts = CancellationTokenSource.CreateLinkedTokenSource(_loadCts.Token);
        var ct = _ocrCts.Token;
        var generation = Interlocked.Increment(ref _ocrGeneration);
        IsRecognizingText = true;
        IsOcrRecognitionPaused = false;
        OcrCompletedPageCount = 0;
        OcrTotalPageCount = PageCount;
        OcrStatusMessage = null;
        var pageOrder = Enumerable.Range(CurrentPageIndex, PageCount - CurrentPageIndex)
            .Concat(Enumerable.Range(0, CurrentPageIndex))
            .ToList();
        _ocrScanTask = RunOcrScanAsync(book, pageOrder, generation, ct);
    }

    private async Task RunOcrScanAsync(
        MangaBook book,
        IReadOnlyList<int> pageOrder,
        long generation,
        CancellationToken ct)
    {
        var failedPages = 0;
        var requestedNetworkPage = false;
        var pageIdentities = GetPageIdentities();
        try
        {
            foreach (var pageIndex in pageOrder)
            {
                ct.ThrowIfCancellationRequested();
                if (generation != Volatile.Read(ref _ocrGeneration))
                    return;

                var embedded = await _textRegionService.GetRegionsAsync(
                    book,
                    pageIndex,
                    ct);
                IReadOnlyList<MangaTextRegion> regions = embedded;
                if (embedded.Count == 0)
                {
                    try
                    {
                        var key = CreateOcrKey(pageIndex, pageIdentities);
                        var cached = await _ocrService.GetCachedRegionsAsync(
                            key,
                            pageIdentities,
                            ct);
                        if (cached is not null)
                        {
                            regions = cached;
                        }
                        else
                        {
                            var pagePath = await _pageProvider.GetPagePathAsync(
                                book,
                                pageIndex,
                                ct);
                            requestedNetworkPage = true;
                            regions = await _ocrService.RecognizeAsync(
                                pagePath,
                                key,
                                pageIdentities,
                                ct);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        failedPages++;
                        regions = [];
                    }
                }

                if (generation != Volatile.Read(ref _ocrGeneration))
                    return;
                ApplyTextRegions(pageIndex, regions);
                OcrCompletedPageCount++;
            }

            OcrStatusMessage = failedPages > 0
                ? ResourceStringHelper.GetString(
                    "MangaOcrPendingStatus",
                    "Text recognition finished with some pages pending. They will be retried next time.")
                : requestedNetworkPage
                    ? ResourceStringHelper.GetString(
                        "MangaOcrCompleteStatus",
                        "Text recognition complete.")
                    : null;
            IsOcrRecognitionPaused = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (generation == Volatile.Read(ref _ocrGeneration))
            {
                IsRecognizingText = false;
                _ocrCts?.Dispose();
                _ocrCts = null;
                _ocrScanTask = null;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task GoForwardAsync() => NavigateToAsync(
        CurrentPageIndex + (Layout == MangaReaderLayout.DoublePage ? 2 : 1));

    [RelayCommand(CanExecute = nameof(CanGoBackward))]
    private Task GoBackwardAsync() => NavigateToAsync(
        CurrentPageIndex - (Layout == MangaReaderLayout.DoublePage ? 2 : 1));

    [RelayCommand]
    private Task PhysicalLeftAsync() =>
        Direction == MangaReadingDirection.RightToLeft
            ? GoForwardAsync()
            : GoBackwardAsync();

    [RelayCommand]
    private Task PhysicalRightAsync() =>
        Direction == MangaReadingDirection.RightToLeft
            ? GoBackwardAsync()
            : GoForwardAsync();

    public async Task NavigateToAsync(int pageIndex)
    {
        if (_book is null || PageCount == 0)
            return;

        CurrentPageIndex = Math.Clamp(pageIndex, 0, PageCount - 1);
        await LoadCurrentViewAsync(_loadCts.Token);
        await SaveProgressAsync(_loadCts.Token);
        NotifyNavigationState();
        ReaderStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task UpdateContinuousProgressAsync(int pageIndex)
    {
        if (_book is null || Layout != MangaReaderLayout.Continuous || PageCount == 0)
            return;

        var clamped = Math.Clamp(pageIndex, 0, PageCount - 1);
        if (CurrentPageIndex == clamped)
            return;

        CurrentPageIndex = clamped;
        await SaveProgressAsync(_loadCts.Token);
        NotifyNavigationState();
    }

    public async Task SetLayoutAsync(MangaReaderLayout layout)
    {
        if (Layout == layout)
            return;
        Layout = layout;
        OnPropertyChanged(nameof(IsContinuous));
        await SavePreferencesAsync();
        await LoadCurrentViewAsync(_loadCts.Token);
        ReaderStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetDirectionAsync(MangaReadingDirection direction)
    {
        if (Direction == direction)
            return;
        Direction = direction;
        await SavePreferencesAsync();
        await LoadCurrentViewAsync(_loadCts.Token);
        ReaderStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetZoomAsync(int percentage)
    {
        ZoomPercentage = Math.Clamp(percentage, 50, 200);
        await SavePreferencesAsync();
        ReaderStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync()
    {
        InvalidateOcrRecognition();
        if (_book is null)
            return;
        await SaveProgressAsync(CancellationToken.None);
        await SavePreferencesAsync();
    }

    private void InvalidateOcrRecognition()
    {
        Interlocked.Increment(ref _ocrGeneration);
        _ocrCts?.Cancel();
        _ocrCts?.Dispose();
        _ocrCts = null;
        _ocrScanTask = null;
        IsRecognizingText = false;
        IsOcrRecognitionPaused = false;
    }

    private async Task LoadCurrentViewAsync(CancellationToken ct)
    {
        if (_book is null)
            return;

        if (Layout == MangaReaderLayout.Continuous)
        {
            VisiblePages.Clear();
            await LoadPageAsync(ContinuousPages[CurrentPageIndex], ct);
            _ = LoadContinuousPagesAsync(ct);
            return;
        }

        var indices = Layout == MangaReaderLayout.DoublePage
            ? Enumerable.Range(CurrentPageIndex, Math.Min(2, PageCount - CurrentPageIndex)).ToList()
            : [CurrentPageIndex];
        if (Direction == MangaReadingDirection.RightToLeft && indices.Count > 1)
            indices.Reverse();

        var pages = indices.Select(index => new MangaReaderPageItemViewModel(index)).ToList();
        VisiblePages = new ObservableCollection<MangaReaderPageItemViewModel>(pages);
        await Task.WhenAll(pages.Select(page => LoadPageAsync(page, ct)));
    }

    private async Task LoadContinuousPagesAsync(CancellationToken ct)
    {
        foreach (var page in ContinuousPages)
        {
            if (page.Image is not null)
                continue;
            try
            {
                await LoadPageAsync(page, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task LoadPageAsync(MangaReaderPageItemViewModel page, CancellationToken ct)
    {
        if (_book is null || page.Image is not null)
            return;
        try
        {
            page.IsLoading = true;
            var pagePathTask = _pageProvider.GetPagePathAsync(_book, page.Index, ct);
            var regionsTask = _textRegionService.GetRegionsAsync(_book, page.Index, ct);
            await Task.WhenAll(pagePathTask, regionsTask);
            page.SetPath(await pagePathTask);
            var regions = await regionsTask;
            if (regions.Count == 0 && IsGoogleOcrEnabled)
            {
                regions = await _ocrService.GetCachedRegionsAsync(
                        CreateOcrKey(page.Index),
                        GetPageIdentities(),
                        ct)
                    ?? [];
            }
            page.TextRegions = regions;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            page.IsLoading = false;
            page.ErrorMessage = ex.Message;
        }
    }

    private Task SavePreferencesAsync() => _library.SaveReaderPreferencesAsync(
        new MangaReaderPreferences
        {
            Layout = Layout,
            Direction = Direction,
            ZoomPercentage = ZoomPercentage,
            IsGoogleOcrEnabled = IsGoogleOcrEnabled,
            GoogleOcrDisclosureAccepted = GoogleOcrDisclosureAccepted,
        });

    private async Task SaveProgressAsync(CancellationToken ct)
    {
        if (_book is null)
            return;
        _book.CurrentPageIndex = CurrentPageIndex;
        if (_book.ContainerKind == MangaContainerKind.Suwayomi)
        {
            try
            {
                await _suwayomi.UpdateProgressAsync(
                    _book,
                    CurrentPageIndex,
                    CurrentPageIndex >= PageCount - 1,
                    ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Reading remains available from cache when progress sync is offline.
            }
            return;
        }
        if (_book.ContainerKind == MangaContainerKind.Mihon)
            return;
        await _library.SaveProgressAsync(_book.Id, CurrentPageIndex, ct);
    }

    private MangaOcrCacheKey CreateOcrKey(int pageIndex) =>
        CreateOcrKey(pageIndex, GetPageIdentities());

    private MangaOcrCacheKey CreateOcrKey(
        int pageIndex,
        IReadOnlyList<string> identities)
    {
        var book = _book ?? throw new InvalidOperationException("No manga is open.");
        return new MangaOcrCacheKey(
            book.Id,
            pageIndex,
            identities[pageIndex],
            book.SourceModifiedAt);
    }

    private IReadOnlyList<string> GetPageIdentities() =>
        _book?.Pages.Select(page => page.Path).ToList() ?? [];

    private void ApplyTextRegions(
        int pageIndex,
        IReadOnlyList<MangaTextRegion> regions)
    {
        if (pageIndex >= 0 && pageIndex < ContinuousPages.Count)
            ContinuousPages[pageIndex].TextRegions = regions;
        foreach (var page in VisiblePages.Where(page => page.Index == pageIndex))
            page.TextRegions = regions;
    }

    private void NotifyNavigationState()
    {
        OnPropertyChanged(nameof(PageStatus));
        OnPropertyChanged(nameof(CanGoBackward));
        OnPropertyChanged(nameof(CanGoForward));
        GoForwardCommand.NotifyCanExecuteChanged();
        GoBackwardCommand.NotifyCanExecuteChanged();
    }
}
