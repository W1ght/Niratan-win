using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Niratan.Messages;
using Niratan.Helpers;
using Niratan.Models.ZLibrary;
using Niratan.Services.UI;
using Niratan.Services.ZLibrary;
using Niratan.ViewModels.Components;

namespace Niratan.ViewModels.Dialogs;

public sealed record ZLibraryChoice<T>(string Label, T Value);

public partial class ZLibraryDialogViewModel : ObservableObject, IDisposable
{
    private readonly IZLibraryService _service;
    private readonly INotificationService _notifications;
    private readonly IMessenger _messenger;
    private readonly CancellationTokenSource _cts = new();

    [ObservableProperty]
    public partial string BaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ExactMatching { get; set; }

    [ObservableProperty]
    public partial ZLibraryChoice<int?> SelectedYearFrom { get; set; }

    [ObservableProperty]
    public partial ZLibraryChoice<int?> SelectedYearTo { get; set; }

    [ObservableProperty]
    public partial ZLibraryChoice<string?> SelectedLanguage { get; set; }

    [ObservableProperty]
    public partial ZLibraryChoice<string?> SelectedExtension { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSearch))]
    [NotifyPropertyChangedFor(nameof(CanChangeConnection))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSearch))]
    [NotifyPropertyChangedFor(nameof(CanChangeConnection))]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial string ConnectionStatus { get; set; } = ResourceStringHelper.GetString(
        "ZLibraryStatusNotConnected",
        "Not connected");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    public partial ObservableCollection<ZLibraryBookItemViewModel> Results { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoPrevious))]
    public partial int CurrentPage { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    public partial int? TotalCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    public partial int? TotalPages { get; set; }

    [ObservableProperty]
    public partial string TotalCountLabel { get; set; } = string.Empty;

    public IReadOnlyList<ZLibraryChoice<int?>> YearOptions { get; }
    public IReadOnlyList<ZLibraryChoice<string?>> LanguageOptions { get; }
    public IReadOnlyList<ZLibraryChoice<string?>> ExtensionOptions { get; }

    public bool HasResults => Results.Count > 0;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool CanSearch => IsConnected && !IsBusy;
    public bool CanChangeConnection => !IsBusy;
    public bool CanGoPrevious => CanSearch && CurrentPage > 1;
    public bool CanGoNext => CanSearch
        && Results.Count > 0
        && (TotalPages is null || CurrentPage < TotalPages.Value);

    public string ResultSummary => !string.IsNullOrWhiteSpace(TotalCountLabel)
        ? ResourceStringHelper.FormatString(
            "ZLibraryResultSummaryWithTotalLabel",
            "Page {0} · {1} matches",
            CurrentPage,
            TotalCountLabel)
        : HasResults
            ? ResourceStringHelper.FormatString(
                "ZLibraryResultSummaryPage",
                "Page {0}",
                CurrentPage)
            : string.Empty;

    public ZLibraryDialogViewModel(
        IZLibraryService service,
        INotificationService notifications,
        IMessenger messenger)
    {
        _service = service;
        _notifications = notifications;
        _messenger = messenger;

        YearOptions = BuildYearOptions();
        LanguageOptions =
        [
            new(ResourceStringHelper.GetString("ZLibraryFilterAllLanguages", "All languages"), null),
            new(ResourceStringHelper.GetString("ZLibraryLanguageJapanese", "Japanese"), "japanese"),
            new(ResourceStringHelper.GetString("ZLibraryLanguageChinese", "Chinese"), "chinese"),
            new(ResourceStringHelper.GetString("ZLibraryLanguageEnglish", "English"), "english"),
            new(ResourceStringHelper.GetString("ZLibraryLanguageKorean", "Korean"), "korean"),
            new(ResourceStringHelper.GetString("ZLibraryLanguageRussian", "Russian"), "russian"),
            new(ResourceStringHelper.GetString("ZLibraryLanguageGerman", "German"), "german"),
            new(ResourceStringHelper.GetString("ZLibraryLanguageFrench", "French"), "french"),
            new(ResourceStringHelper.GetString("ZLibraryLanguageSpanish", "Spanish"), "spanish"),
        ];
        ExtensionOptions =
        [
            new(ResourceStringHelper.GetString("ZLibraryFilterAllFormats", "All formats"), null),
            new("EPUB", "EPUB"),
            new("PDF", "PDF"),
            new("MOBI", "MOBI"),
            new("AZW3", "AZW3"),
            new("FB2", "FB2"),
            new("TXT", "TXT"),
            new("RTF", "RTF"),
        ];
        SelectedYearFrom = YearOptions[0];
        SelectedYearTo = YearOptions[0];
        SelectedLanguage = LanguageOptions[0];
        SelectedExtension = ExtensionOptions[1];
    }

    public async Task InitializeAsync()
    {
        var credentials = await _service.LoadCredentialsAsync(_cts.Token);
        if (credentials is null)
            return;

        BaseUrl = credentials.BaseUrl;
        Email = credentials.Email;
        Password = credentials.Password;
        await ConnectCoreAsync();
    }

    [RelayCommand]
    private async Task ConnectAsync() => await ConnectCoreAsync();

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var result = await _service.DisconnectAsync(_cts.Token);
            if (!result.IsSuccess)
            {
                if (!result.IsCancelled)
                    ErrorMessage = result.Error ?? "Could not disconnect the account.";
                return;
            }

            IsConnected = false;
            ConnectionStatus = ResourceStringHelper.GetString(
                "ZLibraryStatusNotConnected",
                "Not connected");
            Password = string.Empty;
            Results.Clear();
            TotalCount = null;
            TotalPages = null;
            TotalCountLabel = string.Empty;
            OnPropertyChanged(nameof(HasResults));
            NotifyNavigationStateChanged();
        }
        finally
        {
            IsBusy = false;
            NotifyNavigationStateChanged();
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        CurrentPage = 1;
        await SearchPageAsync(CurrentPage);
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (!CanGoPrevious)
            return;
        await SearchPageAsync(CurrentPage - 1);
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (!CanGoNext)
            return;
        await SearchPageAsync(CurrentPage + 1);
    }

    [RelayCommand]
    private async Task DownloadBookAsync(ZLibraryBookItemViewModel? item)
    {
        if (item is null || !item.CanDownload)
            return;

        item.IsDownloading = true;
        item.Status = ZLibraryBookItemViewModel.DownloadingText;
        ErrorMessage = string.Empty;
        try
        {
            var result = await _service.DownloadAndImportAsync(item.Book, _cts.Token);
            if (!result.IsSuccess)
            {
                if (!result.IsCancelled)
                {
                    item.Status = ZLibraryBookItemViewModel.ImportFailedText;
                    ErrorMessage = result.Error ?? "The book could not be imported.";
                }
                return;
            }

            item.IsImported = true;
            item.Status = ZLibraryBookItemViewModel.AddedToShelfText;
            _messenger.Send(new NovelLibraryChangedMessage());
            _notifications.ShowSuccess(
                ResourceStringHelper.FormatString(
                    "ZLibraryImportSuccessMessage",
                    "“{0}” was added to the novel shelf.",
                    result.Value!.Title),
                ResourceStringHelper.GetString(
                    "ZLibraryImportSuccessTitle",
                    "Z-Library import complete"));
        }
        finally
        {
            item.IsDownloading = false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        Password = string.Empty;
    }

    private async Task ConnectCoreAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        ErrorMessage = string.Empty;
        ConnectionStatus = ResourceStringHelper.GetString(
            "ZLibraryStatusConnecting",
            "Connecting…");
        try
        {
            var result = await _service.ConnectAsync(
                new ZLibraryCredentials(BaseUrl, Email, Password),
                _cts.Token);
            if (!result.IsSuccess)
            {
                IsConnected = false;
                ConnectionStatus = ResourceStringHelper.GetString(
                    "ZLibraryStatusNotConnected",
                    "Not connected");
                if (!result.IsCancelled)
                    ErrorMessage = result.Error ?? "Could not connect the account.";
                return;
            }

            IsConnected = true;
            ConnectionStatus = ResourceStringHelper.FormatString(
                "ZLibraryStatusConnectedAs",
                "Connected as {0}",
                Email.Trim());
        }
        finally
        {
            IsBusy = false;
            NotifyNavigationStateChanged();
        }
    }

    private async Task SearchPageAsync(int page)
    {
        if (!CanSearch || string.IsNullOrWhiteSpace(SearchQuery))
            return;

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var options = new ZLibrarySearchOptions(
                SearchQuery,
                ExactMatching,
                SelectedYearFrom.Value,
                SelectedYearTo.Value,
                SelectedLanguage.Value,
                SelectedExtension.Value);
            var result = await _service.SearchAsync(options, page, _cts.Token);
            if (!result.IsSuccess)
            {
                if (!result.IsCancelled)
                    ErrorMessage = result.Error ?? "Search failed.";
                return;
            }

            CurrentPage = result.Value!.Page;
            TotalCount = result.Value.TotalCount;
            TotalPages = result.Value.TotalPages;
            TotalCountLabel = result.Value.TotalCountLabel ?? string.Empty;
            Results = new ObservableCollection<ZLibraryBookItemViewModel>(
                result.Value.Books.Select(book => new ZLibraryBookItemViewModel(book)));
            OnPropertyChanged(nameof(ResultSummary));
        }
        finally
        {
            IsBusy = false;
            NotifyNavigationStateChanged();
        }
    }

    partial void OnCurrentPageChanged(int value)
    {
        OnPropertyChanged(nameof(ResultSummary));
        NotifyNavigationStateChanged();
    }

    partial void OnResultsChanged(ObservableCollection<ZLibraryBookItemViewModel> value)
    {
        OnPropertyChanged(nameof(ResultSummary));
        NotifyNavigationStateChanged();
    }

    partial void OnIsBusyChanged(bool value) => NotifyNavigationStateChanged();

    partial void OnIsConnectedChanged(bool value) => NotifyNavigationStateChanged();

    private static IReadOnlyList<ZLibraryChoice<int?>> BuildYearOptions()
    {
        var values = new List<ZLibraryChoice<int?>>
        {
            new(ResourceStringHelper.GetString("ZLibraryFilterAnyYear", "Any year"), null),
        };
        for (var year = DateTime.UtcNow.Year; year >= 1800; year--)
            values.Add(new(year.ToString(), year));
        return values;
    }

    private void NotifyNavigationStateChanged()
    {
        OnPropertyChanged(nameof(CanSearch));
        OnPropertyChanged(nameof(CanChangeConnection));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
    }
}
