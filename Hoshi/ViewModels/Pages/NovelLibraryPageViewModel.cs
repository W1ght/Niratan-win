using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Hoshi.Enums;
using Hoshi.Messages;
using Hoshi.Models.DTO;
using Hoshi.Services.Novels;
using Hoshi.Services.UI;
using Hoshi.ViewModels.Components;

namespace Hoshi.ViewModels.Pages;

public partial class NovelLibraryPageViewModel : ObservableObject
{
    private readonly INovelLibraryService _novelLibraryService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    private readonly IMessenger _messenger;
    private CancellationTokenSource _cts = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoNovels))]
    public partial List<NovelBookItemViewModel> NovelBooks { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoNovels))]
    public partial bool IsContentLoading { get; set; }

    public bool NoNovels => !IsContentLoading && NovelBooks.Count == 0;

    public NovelLibraryPageViewModel(
        INovelLibraryService novelLibraryService,
        IDialogService dialogService,
        INotificationService notificationService,
        IMessenger messenger
    )
    {
        _novelLibraryService = novelLibraryService;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _messenger = messenger;
    }

    public async Task InitializeAsync() => await LoadNovelsAsync();

    public void OnNavigatedFrom()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    [RelayCommand]
    private async Task ImportNovelAsync()
    {
        var filePath = await _dialogService.OpenFilePickerAsync(".epub");
        if (filePath == null)
            return;

        var result = await _novelLibraryService.ImportEpubAsync(filePath);
        if (!result.IsSuccess)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        _notificationService.ShowSuccess("EPUB imported.", "Novel imported");
        await LoadNovelsAsync();
    }

    [RelayCommand]
    private void OpenNovel(NovelBookItemViewModel item)
    {
        _messenger.Send(
            new SwitchAppModeMessage(AppMode.NovelReader, new NovelReaderNavigationArgs(item.Book.Id))
        );
    }

    [RelayCommand]
    private async Task DeleteNovelAsync(NovelBookItemViewModel item)
    {
        var confirmed = await _dialogService.ConfirmAsync(
            "Delete novel",
            $"Delete '{item.Book.Title}'? This cannot be undone."
        );
        if (!confirmed)
            return;

        var result = await _novelLibraryService.DeleteNovelAsync(item.Book.Id);
        if (!result.IsSuccess)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        _notificationService.ShowSuccess("Novel deleted.");
        await LoadNovelsAsync();
    }

    private async Task LoadNovelsAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();

        IsContentLoading = true;
        var result = await _novelLibraryService.GetNovelBooksAsync(ct: _cts.Token);

        if (result.IsSuccess)
            NovelBooks = result.Value!.Select(book => new NovelBookItemViewModel(book)).ToList();
        else if (!result.IsCancelled)
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);

        IsContentLoading = false;
    }
}
