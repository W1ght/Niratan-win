using System;
using System.IO;
using Microsoft.UI.Xaml.Media.Imaging;
using Hoshi.Models;

namespace Hoshi.ViewModels.Components;

public class NovelBookItemViewModel
{
    public NovelBook Book { get; }
    public string AutomationId => $"NovelBookCard_{Book.Id}";

    public BitmapImage? CoverImage { get; }
    public bool HasCover => CoverImage != null;

    public NovelBookItemViewModel(NovelBook book)
    {
        Book = book;
        CoverImage = LoadCover(book.CoverPath);
    }

    private static BitmapImage? LoadCover(string? coverPath)
    {
        if (string.IsNullOrEmpty(coverPath) || !File.Exists(coverPath))
            return null;

        try
        {
            return new BitmapImage(new Uri(coverPath));
        }
        catch
        {
            return null;
        }
    }
}
