using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Niratan.ViewModels.Components;

public sealed partial class RemoteMangaLibraryItemViewModel : ObservableObject
{
    public RemoteMangaLibraryItemViewModel(
        string provider,
        string id,
        string title,
        Func<Task> open)
    {
        Provider = provider;
        Id = id;
        Title = title;
        OpenCommand = new AsyncRelayCommand(open);
    }

    public string Provider { get; }
    public string Id { get; }
    public string Title { get; }
    public string AutomationId => $"{Provider}Manga_{Id}";
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
