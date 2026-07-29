using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Niratan.Helpers;

namespace Niratan.ViewModels.Components;

public sealed partial class RemoteMangaDetailViewModel : ObservableObject
{
    public RemoteMangaDetailViewModel(
        string provider,
        string id,
        string title,
        bool supportsOnlineLibrary)
    {
        Provider = provider;
        Id = id;
        Title = title;
        SupportsOnlineLibrary = supportsOnlineLibrary;
    }

    public string Provider { get; }
    public string Id { get; }
    public bool SupportsOnlineLibrary { get; }
    public string AutomationId => $"{Provider}MangaDetails_{Id}";
    public string LibraryActionText => IsInOnlineLibrary
        ? ResourceStringHelper.GetString(
            "MangaRemoteDetailsRemoveLibraryAction",
            "Remove from manga library")
        : ResourceStringHelper.GetString(
            "MangaRemoteDetailsAddLibraryAction",
            "Add to manga library");
    public bool HasCover => CoverImage is not null;
    public bool HasAuthor => !string.IsNullOrWhiteSpace(Author);
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasGenres => !string.IsNullOrWhiteSpace(Genres);
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasStatus => !string.IsNullOrWhiteSpace(ActionStatus);
    public bool HasChapters => Chapters.Count > 0;
    public bool HasNoChapters => !IsLoading && !HasChapters;

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAuthor))]
    public partial string Author { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGenres))]
    public partial string Genres { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    public partial BitmapImage? CoverImage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoChapters))]
    public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    public partial bool IsActionBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LibraryActionText))]
    public partial bool IsInOnlineLibrary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string ActionStatus { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChapters))]
    [NotifyPropertyChangedFor(nameof(HasNoChapters))]
    public partial ObservableCollection<RemoteMangaChapterItemViewModel> Chapters
    {
        get;
        set;
    } = [];

    public void ApplyDetails(
        string? title,
        string? author,
        string? description,
        IEnumerable<string>? genres,
        bool isInOnlineLibrary)
    {
        if (!string.IsNullOrWhiteSpace(title))
            Title = title.Trim();
        Author = author?.Trim() ?? string.Empty;
        Description = description?.Trim() ?? string.Empty;
        Genres = string.Join(
            " · ",
            (genres ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.CurrentCultureIgnoreCase));
        IsInOnlineLibrary = isInOnlineLibrary;
    }

    public void SetCoverPath(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            CoverImage = new BitmapImage(new Uri(Path.GetFullPath(path)));
    }
}

public sealed class RemoteMangaChapterItemViewModel
{
    public RemoteMangaChapterItemViewModel(
        string id,
        string title,
        string metadata,
        bool isRead,
        Func<System.Threading.Tasks.Task> open)
    {
        Id = id;
        Title = title;
        Metadata = metadata;
        IsRead = isRead;
        OpenCommand = new AsyncRelayCommand(open);
    }

    public string Id { get; }
    public string Title { get; }
    public string Metadata { get; }
    public bool IsRead { get; }
    public bool HasMetadata => !string.IsNullOrWhiteSpace(Metadata);
    public string AutomationId => $"RemoteMangaChapter_{Id}";
    public IAsyncRelayCommand OpenCommand { get; }
}
