using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoshi.Models.Dictionary;
using Hoshi.Services.Dictionary;

namespace Hoshi.ViewModels.Windowing;

public partial class GlobalLookupWindowViewModel : ObservableObject
{
    private readonly IDictionaryPopupRequestService _popupRequestService;

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Enter text to look up.";

    [ObservableProperty]
    public partial bool IsLookupInProgress { get; set; }

    public event Action<DictionaryPopupRequest>? LookupReady;
    public event Action? LookupCleared;

    public GlobalLookupWindowViewModel(IDictionaryPopupRequestService popupRequestService)
    {
        _popupRequestService = popupRequestService;
    }

    public async Task InitializeAsync(string? initialQuery = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(initialQuery))
            return;

        Query = initialQuery.Trim();
        await LookupAsync(ct);
    }

    [RelayCommand]
    private async Task LookupAsync(CancellationToken ct = default)
    {
        var query = Query.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            LookupCleared?.Invoke();
            StatusText = "Enter text to look up.";
            return;
        }

        Query = query;
        LookupCleared?.Invoke();
        IsLookupInProgress = true;
        StatusText = "Looking up...";

        try
        {
            var request = await _popupRequestService.CreateAsync(
                query,
                traceId: $"global-{Guid.NewGuid():N}",
                ct: ct);
            if (request is null)
            {
                LookupCleared?.Invoke();
                StatusText = "No results.";
                return;
            }

            LookupReady?.Invoke(request);
            StatusText = "Lookup ready.";
        }
        catch (OperationCanceledException)
        {
            LookupCleared?.Invoke();
            StatusText = "Lookup canceled.";
        }
        catch (Exception ex)
        {
            LookupCleared?.Invoke();
            StatusText = ex.Message;
        }
        finally
        {
            IsLookupInProgress = false;
        }
    }
}
