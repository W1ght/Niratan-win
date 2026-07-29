using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Niratan.Models.Manga;

namespace Niratan.ViewModels.Components;

public sealed partial class SuwayomiLibraryItemViewModel : ObservableObject
{
    public SuwayomiLibraryItemViewModel(
        SuwayomiManga manga,
        Func<SuwayomiManga, Task> open)
    {
        Manga = manga;
        OpenCommand = new AsyncRelayCommand(() => open(Manga));
    }

    public SuwayomiManga Manga { get; }
    public string Title => Manga.Title;
    public string AutomationId => $"SuwayomiManga_{Manga.Id}";
    public IAsyncRelayCommand OpenCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    public partial BitmapImage? CoverImage { get; set; }

    public bool HasCover => CoverImage is not null;

    public void SetCoverPath(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            CoverImage = new BitmapImage(new Uri(Path.GetFullPath(path)));
    }
}
