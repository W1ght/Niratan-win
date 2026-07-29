using System;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Manga;
using Niratan.Views.Manga;

namespace Niratan.Services.Manga;

internal sealed class MangaReaderWindowService : IMangaReaderWindowService
{
    private MangaReaderWindow? _window;

    public event EventHandler? LibraryChanged;

    public async Task OpenAsync(MangaBook book, CancellationToken ct = default)
    {
        if (_window is null)
        {
            _window = new MangaReaderWindow();
            _window.ReadingStateSaved += OnReadingStateSaved;
            _window.Closed += OnClosed;
        }

        _window.Activate();
        await _window.OpenAsync(book, ct);
    }

    private void OnReadingStateSaved(object? sender, EventArgs e) =>
        LibraryChanged?.Invoke(this, EventArgs.Empty);

    private void OnClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        if (_window is not null)
        {
            _window.ReadingStateSaved -= OnReadingStateSaved;
            _window.Closed -= OnClosed;
        }

        _window = null;
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }
}
