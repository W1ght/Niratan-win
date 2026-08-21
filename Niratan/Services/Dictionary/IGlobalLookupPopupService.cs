using System;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Dictionary;
using Windows.Graphics;

namespace Niratan.Services.Dictionary;

public readonly record struct SelectedTextSnapshot(
    string Text,
    RectInt32? ScreenBounds = null);

public interface IGlobalLookupPopupService
{
    Task PrewarmAsync(CancellationToken ct = default);
    Task ShowAsync(SelectedTextSnapshot selection, CancellationToken ct = default);
    Task ShowAsync(
        DictionaryPopupRequest request,
        RectInt32 anchorScreenBounds,
        CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException(
            "This lookup popup implementation does not support prebuilt requests."));
}
