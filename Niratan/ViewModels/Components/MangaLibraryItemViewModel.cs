using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Niratan.Helpers;
using Niratan.Models.Manga;

namespace Niratan.ViewModels.Components;

public sealed partial class MangaLibraryItemViewModel : ObservableObject
{
    public MangaLibraryItemViewModel(
        MangaBook book,
        Func<MangaBook, System.Threading.Tasks.Task> open,
        Func<MangaBook, System.Threading.Tasks.Task> rename,
        Func<MangaBook, System.Threading.Tasks.Task> markRead,
        Func<MangaBook, System.Threading.Tasks.Task> remove)
    {
        Book = book;
        OpenCommand = new AsyncRelayCommand(() => open(Book));
        RenameCommand = new AsyncRelayCommand(() => rename(Book));
        MarkReadCommand = new AsyncRelayCommand(() => markRead(Book));
        RemoveCommand = new AsyncRelayCommand(() => remove(Book));
    }

    public MangaBook Book { get; }
    public IAsyncRelayCommand OpenCommand { get; }
    public IAsyncRelayCommand RenameCommand { get; }
    public IAsyncRelayCommand MarkReadCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }

    public string Title => Book.DisplayTitle;
    public bool HasCover => CoverImage is not null;
    public string PageCountText => ResourceStringHelper.FormatString(
        "MangaLibraryPageCount",
        "{0} pages",
        Book.PageCount);
    public double ProgressPercent => Book.Progress * 100;
    public string ProgressText => Book.CurrentPageIndex <= 0
        ? ResourceStringHelper.GetString("MangaLibraryUnread", "Unread")
        : Book.CurrentPageIndex >= Book.PageCount - 1
            ? ResourceStringHelper.GetString("MangaLibraryRead", "Read")
            : $"{Book.Progress:P0}";
    public string AutomationId => $"MangaItem_{Book.Id}";

    public BitmapImage? CoverImage
    {
        get
        {
            try
            {
                return !string.IsNullOrWhiteSpace(Book.CoverCachePath)
                    && File.Exists(Book.CoverCachePath)
                    ? new BitmapImage(new Uri(Book.CoverCachePath))
                    : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
