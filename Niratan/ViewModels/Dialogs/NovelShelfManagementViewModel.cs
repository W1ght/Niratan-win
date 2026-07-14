using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Models.Common;
using Niratan.Models.Novel;
using Niratan.Services.Novels;
using Niratan.Services.UI;

namespace Niratan.ViewModels.Dialogs;

public sealed record NovelShelfRenameRequest(string OldName, string NewName);

public partial class NovelShelfManagementViewModel : ObservableObject
{
    private readonly INovelShelfService _shelfService;
    private readonly INotificationService _notificationService;
    private readonly CancellationTokenSource _cts = new();

    [ObservableProperty]
    public partial ObservableCollection<NovelShelf> Shelves { get; set; } = new();

    public NovelShelfManagementViewModel(
        INovelShelfService shelfService,
        INotificationService notificationService)
    {
        _shelfService = shelfService;
        _notificationService = notificationService;
    }

    public async Task InitializeAsync()
    {
        var result = await _shelfService.LoadAsync(_cts.Token);
        Apply(result);
    }

    [RelayCommand]
    private async Task CreateShelfAsync(string name) =>
        Apply(await _shelfService.CreateAsync(name, _cts.Token));

    [RelayCommand]
    private async Task RenameShelfAsync(NovelShelfRenameRequest request) =>
        Apply(await _shelfService.RenameAsync(
            request.OldName,
            request.NewName,
            _cts.Token));

    [RelayCommand]
    private async Task DeleteShelfAsync(string name) =>
        Apply(await _shelfService.DeleteAsync(name, _cts.Token));

    [RelayCommand]
    private async Task ReorderShelvesAsync(IReadOnlyList<string> names) =>
        Apply(await _shelfService.ReorderShelvesAsync(names, _cts.Token));

    private void Apply(Result<NovelShelfState> result)
    {
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
            {
                var title = result.ErrorTitle is null or "Error"
                    ? "Shelf update failed"
                    : result.ErrorTitle;
                _notificationService.ShowError(result.Error!, title);
            }
            return;
        }

        Shelves = new ObservableCollection<NovelShelf>(result.Value!.Shelves);
    }
}
