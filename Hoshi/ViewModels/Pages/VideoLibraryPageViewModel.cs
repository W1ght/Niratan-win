using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoshi.Services.UI;
using Hoshi.Services.Video;
using Hoshi.ViewModels.Components;

namespace Hoshi.ViewModels.Pages;

public partial class VideoLibraryPageViewModel : ObservableObject
{
    private readonly IVideoLibraryService _videoLibraryService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    private readonly IVideoPlayerWindowService _playerWindowService;
    private CancellationTokenSource _cts = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoVideos))]
    public partial ObservableCollection<VideoItemViewModel> Videos { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoVideos))]
    public partial bool IsContentLoading { get; set; }

    public bool NoVideos => !IsContentLoading && Videos.Count == 0;

    public VideoLibraryPageViewModel(
        IVideoLibraryService videoLibraryService,
        IDialogService dialogService,
        INotificationService notificationService,
        IVideoPlayerWindowService playerWindowService)
    {
        _videoLibraryService = videoLibraryService;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _playerWindowService = playerWindowService;
    }

    public async Task InitializeAsync() => await LoadVideosAsync();

    public void OnNavigatedFrom()
    {
        _cts.Cancel();
    }

    [RelayCommand]
    private async Task ImportVideoAsync()
    {
        var filePath = await _dialogService.OpenFilePickerAsync(".mkv", ".mp4", ".webm", ".avi", ".mov");
        if (filePath == null)
            return;

        var result = await _videoLibraryService.ImportVideoAsync(filePath, _cts.Token);
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        _notificationService.ShowSuccess("Video imported.", "Video imported");
        await LoadVideosAsync();
    }

    [RelayCommand]
    private async Task OpenVideoAsync(VideoItemViewModel item)
    {
        var result = await _videoLibraryService.MarkOpenedAsync(item.Video.Id, _cts.Token);
        if (!result.IsSuccess && !result.IsCancelled)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        await _playerWindowService.OpenAsync(item.Video, _cts.Token);
    }

    [RelayCommand]
    private async Task DeleteVideoAsync(VideoItemViewModel item)
    {
        var confirmed = await _dialogService.ConfirmAsync(
            "Delete video",
            $"Delete '{item.Video.Title}'? This only removes it from Hoshi.");
        if (!confirmed)
            return;

        var result = await _videoLibraryService.DeleteVideoAsync(item.Video.Id, _cts.Token);
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
                _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        _notificationService.ShowSuccess("Video deleted.");
        await LoadVideosAsync();
    }

    private async Task LoadVideosAsync()
    {
        try
        {
            _cts.Cancel();
        }
        finally
        {
            _cts.Dispose();
        }
        _cts = new CancellationTokenSource();

        IsContentLoading = true;
        var result = await _videoLibraryService.GetVideosAsync(ct: _cts.Token);
        if (result.IsSuccess)
        {
            Videos = new ObservableCollection<VideoItemViewModel>(
                result.Value!.Select(video => new VideoItemViewModel(video)));
        }
        else if (!result.IsCancelled)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
        }

        IsContentLoading = false;
    }
}
