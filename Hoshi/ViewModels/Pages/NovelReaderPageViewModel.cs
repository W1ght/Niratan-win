using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Hoshi.Enums;
using Hoshi.Messages;
using Hoshi.Models;
using Hoshi.Models.DTO;
using Hoshi.Services.Novels;
using Hoshi.Services.UI;

namespace Hoshi.ViewModels.Pages;

public partial class NovelReaderPageViewModel : ObservableObject
{
    private readonly INovelLibraryService _novelLibraryService;
    private readonly INotificationService _notificationService;
    private readonly IMessenger _messenger;

    [ObservableProperty]
    public partial NovelBook? CurrentBook { get; set; }

    [ObservableProperty]
    public partial int CurrentChapterIndex { get; set; }

    [ObservableProperty]
    public partial int ChapterCount { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    public string ReaderTitle => CurrentBook?.Title ?? "Novel reader";
    public string ChapterTitle => $"Chapter {CurrentChapterIndex + 1} of {ChapterCount}";
    public bool CanGoNext => CurrentChapterIndex < ChapterCount - 1;
    public bool CanGoPrevious => CurrentChapterIndex > 0;

    private CancellationTokenSource? _saveCts;

    public NovelReaderPageViewModel(
        INovelLibraryService novelLibraryService,
        INotificationService notificationService,
        IMessenger messenger
    )
    {
        _novelLibraryService = novelLibraryService;
        _notificationService = notificationService;
        _messenger = messenger;
    }

    public async Task InitializeAsync(NovelReaderNavigationArgs args)
    {
        var result = await _novelLibraryService.GetNovelBookAsync(args.BookId);
        if (!result.IsSuccess)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        CurrentBook = result.Value;
        OnPropertyChanged(nameof(ReaderTitle));

        if (CurrentBook != null)
            await _novelLibraryService.MarkOpenedAsync(CurrentBook.Id);
    }

    public void SetChapter(int index, int count)
    {
        CurrentChapterIndex = index;
        ChapterCount = count;
        OnPropertyChanged(nameof(ChapterTitle));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
    }

    public void UpdateProgress(double progress)
    {
        Progress = Math.Clamp(progress, 0, 1);
    }

    public void SaveProgressDebounced()
    {
        if (CurrentBook == null) return;

        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        var token = _saveCts.Token;
        var bookId = CurrentBook.Id;
        var chapterIndex = CurrentChapterIndex;
        var progress = Progress;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token);
                if (!token.IsCancellationRequested)
                    await _novelLibraryService.SaveProgressAsync(bookId, chapterIndex, progress, token);
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    public async Task SaveProgressNowAsync()
    {
        if (CurrentBook == null) return;
        _saveCts?.Cancel();
        await _novelLibraryService.SaveProgressAsync(CurrentBook.Id, CurrentChapterIndex, Progress);
    }

    [RelayCommand]
    private async Task BackToLibrary()
    {
        await SaveProgressNowAsync();
        _messenger.Send(new SwitchAppModeMessage(AppMode.Navigation, null));
    }
}
