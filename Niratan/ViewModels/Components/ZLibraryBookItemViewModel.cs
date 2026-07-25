using CommunityToolkit.Mvvm.ComponentModel;
using Niratan.Models.ZLibrary;
using Niratan.Helpers;
using System.Linq;

namespace Niratan.ViewModels.Components;

public partial class ZLibraryBookItemViewModel : ObservableObject
{
    public ZLibraryBook Book { get; }

    public string Title => Book.Title;
    public string Author => Book.Author;
    public string Metadata => string.Join(
        " · ",
        new[] { Book.Language, Book.Extension.ToUpperInvariant(), Book.Size }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    public partial bool IsImported { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    public bool CanDownload => !IsDownloading
        && !IsImported
        && string.Equals(Book.Extension, "EPUB", System.StringComparison.OrdinalIgnoreCase);

    public ZLibraryBookItemViewModel(ZLibraryBook book)
    {
        Book = book;
        if (!string.Equals(Book.Extension, "EPUB", System.StringComparison.OrdinalIgnoreCase))
            Status = UnsupportedFormatText;
    }

    public static string DownloadingText => ResourceStringHelper.GetString(
        "ZLibraryStatusDownloading",
        "Downloading…");

    public static string ImportFailedText => ResourceStringHelper.GetString(
        "ZLibraryStatusImportFailed",
        "Import failed");

    public static string AddedToShelfText => ResourceStringHelper.GetString(
        "ZLibraryStatusAddedToShelf",
        "Added to shelf");

    public static string UnsupportedFormatText => ResourceStringHelper.GetString(
        "ZLibraryStatusUnsupportedFormat",
        "Only EPUB can be added to the shelf");
}
