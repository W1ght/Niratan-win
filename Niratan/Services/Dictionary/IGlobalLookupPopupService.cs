using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;

namespace Niratan.Services.Dictionary;

public readonly record struct SelectedTextSnapshot(
    string Text,
    RectInt32? ScreenBounds = null);

public interface IGlobalLookupPopupService
{
    Task PrewarmAsync(CancellationToken ct = default);
    Task ShowAsync(SelectedTextSnapshot selection, CancellationToken ct = default);
}
