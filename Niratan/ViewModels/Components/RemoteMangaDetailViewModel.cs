using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Niratan.Helpers;
using Niratan.Models.Manga;

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
    public bool HasOriginalTitle => !string.IsNullOrWhiteSpace(OriginalTitle);
    public bool HasMetadata => !string.IsNullOrWhiteSpace(Metadata);
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasStatus => !string.IsNullOrWhiteSpace(ActionStatus);
    public bool HasChapters => Chapters.Count > 0;
    public bool HasNoChapters => !IsLoading && !HasChapters;
    public bool HasExtensionOptions => ExtensionOptions.Count > 0;

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAuthor))]
    public partial string Author { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOriginalTitle))]
    public partial string OriginalTitle { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMetadata))]
    public partial string Metadata { get; set; } = string.Empty;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExtensionOptions))]
    public partial ObservableCollection<RemoteMangaExtensionOptionViewModel>
        ExtensionOptions
    {
        get;
        set;
    } = [];

    [ObservableProperty]
    public partial string SelectedExtensionId { get; set; } = string.Empty;

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

    public IReadOnlyList<string> SearchTitles { get; private set; } = [];

    public void ApplyDiscoveryDetails(MangaDiscoveryItem item)
    {
        ApplyDetails(
            item.Title,
            author: null,
            item.Overview,
            genres: null,
            isInOnlineLibrary: false);
        OriginalTitle = item.OriginalTitle?.Trim() ?? string.Empty;
        Metadata = string.Join(
            " · ",
            new[]
            {
                item.Year?.ToString(System.Globalization.CultureInfo.CurrentCulture),
                item.Score is double score
                    ? score.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)
                    : null,
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        SearchTitles = new[]
        {
            item.Title,
            item.OriginalTitle,
        }
            .Concat(item.Aliases ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public void SetCoverPath(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            CoverImage = new BitmapImage(new Uri(Path.GetFullPath(path)));
    }

    public void SetExtensionOptions(
        IEnumerable<MihonInstalledExtension> sources,
        MihonInstalledExtension? selected)
    {
        var selectedKey = selected is null
            ? string.Empty
            : RemoteMangaExtensionOptionViewModel.GetKey(selected);
        ExtensionOptions = new ObservableCollection<RemoteMangaExtensionOptionViewModel>(
            sources
                .Where(source =>
                    !string.IsNullOrWhiteSpace(source.SourceId)
                    && !string.IsNullOrWhiteSpace(source.PackageName))
                .GroupBy(
                    RemoteMangaExtensionOptionViewModel.GetKey,
                    StringComparer.Ordinal)
                .Select(group =>
                {
                    var option = new RemoteMangaExtensionOptionViewModel(group.First());
                    option.IsSelected = string.Equals(
                        option.Id,
                        selectedKey,
                        StringComparison.Ordinal);
                    return option;
                }));
        SelectedExtensionId = selectedKey;
    }

    public void SelectExtension(string id)
    {
        foreach (var option in ExtensionOptions)
        {
            option.IsSelected = string.Equals(
                option.Id,
                id,
                StringComparison.Ordinal);
        }

        SelectedExtensionId = id;
    }
}

public sealed partial class RemoteMangaExtensionOptionViewModel
    : ObservableObject
{
    public RemoteMangaExtensionOptionViewModel(MihonInstalledExtension source)
    {
        Source = source;
    }

    public MihonInstalledExtension Source { get; }
    public string Id => GetKey(Source);
    public string Name => Source.SourceName;
    public string Metadata => string.IsNullOrWhiteSpace(Source.Lang)
        ? Source.PackageName
        : $"{Source.Lang} · {Source.PackageName}";
    public string AutomationId =>
        $"MangaRemoteDetailsExtension_{Sanitize(Source.PackageName)}_{Sanitize(Source.SourceId)}";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public static string GetKey(MihonInstalledExtension source) =>
        $"{source.PackageName}\u001f{source.SourceId}";

    private static string Sanitize(string value) =>
        new(value.Select(character =>
            char.IsLetterOrDigit(character) ? character : '_').ToArray());
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
