using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Niratan.Models;

namespace Niratan.ViewModels.Components;

public sealed class NovelStatisticsBookRankingItemViewModel : ObservableObject
{
    private readonly NovelStatisticsBookCoverCache _coverCache;
    private readonly string? _coverPath;
    private BitmapImage? _coverImage;

    public NovelBook Book { get; }
    public string Id => Book.Id;
    public string Title { get; }
    public string ValueText { get; }
    public double NormalizedValue { get; }
    public BitmapImage? CoverImage
    {
        get => _coverImage;
        private set
        {
            if (!SetProperty(ref _coverImage, value))
                return;

            OnPropertyChanged(nameof(HasCover));
            OnPropertyChanged(nameof(HasNoCover));
        }
    }
    public bool HasCover => CoverImage != null;
    public bool HasNoCover => CoverImage == null;
    public string AccessibleText => $"{Title}: {ValueText}";

    internal NovelStatisticsBookRankingItemViewModel(
        NovelBook book,
        string title,
        string valueText,
        double normalizedValue,
        NovelStatisticsBookCoverCache coverCache)
    {
        _coverCache = coverCache;
        _coverPath = book.CoverPath;
        Book = book;
        Title = title;
        ValueText = valueText;
        NormalizedValue = normalizedValue;
        CoverImage = coverCache.Get(book.CoverPath);
    }

    public void MarkCoverFailed()
    {
        _coverCache.MarkFailed(_coverPath);
        CoverImage = null;
    }
}

internal sealed class NovelStatisticsBookCoverCache
{
    private readonly Dictionary<string, BitmapImage?> _images =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, BitmapImage?> _loader;

    public NovelStatisticsBookCoverCache()
        : this(LoadCover)
    {
    }

    internal NovelStatisticsBookCoverCache(Func<string, BitmapImage?> loader)
    {
        _loader = loader;
    }

    public BitmapImage? Get(string? coverPath)
    {
        var key = CacheKey(coverPath);
        if (key == null)
            return null;

        if (_images.TryGetValue(key, out var cached))
            return cached;

        var loaded = _loader(key);
        _images[key] = loaded;
        return loaded;
    }

    public void MarkFailed(string? coverPath)
    {
        var key = CacheKey(coverPath);
        if (key != null)
            _images[key] = null;
    }

    public void Clear() => _images.Clear();

    private static string? CacheKey(string? coverPath)
    {
        if (string.IsNullOrWhiteSpace(coverPath))
            return null;

        try
        {
            return Path.GetFullPath(coverPath);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapImage? LoadCover(string fullPath)
    {
        if (!File.Exists(fullPath))
            return null;

        try
        {
            return new BitmapImage(new Uri(fullPath));
        }
        catch
        {
            return null;
        }
    }
}

public sealed record NovelStatisticsBookDayDisplay(
    string DateKey,
    string DateText,
    int CharactersRead,
    double ReadingTime,
    string CharactersText,
    string DurationText,
    string SpeedText,
    string AccessibleText);

public sealed class NovelStatisticsBookDetailViewModel
{
    public NovelStatisticsBookRankingItemViewModel Book { get; }
    public string Title => Book.Title;
    public BitmapImage? CoverImage => Book.CoverImage;
    public bool HasCover => Book.HasCover;
    public bool HasNoCover => Book.HasNoCover;
    public string TotalCharactersText { get; }
    public string TotalDurationText { get; }
    public string AverageSpeedText { get; }
    public string ActiveDaysText { get; }
    public IReadOnlyList<NovelStatisticsBookDayDisplay> Days { get; }
    public bool HasDays => Days.Count > 0;
    public bool HasNoDays => !HasDays;
    public string? ErrorMessage { get; }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public NovelStatisticsBookDetailViewModel(
        NovelStatisticsBookRankingItemViewModel book,
        string totalCharactersText,
        string totalDurationText,
        string averageSpeedText,
        string activeDaysText,
        IReadOnlyList<NovelStatisticsBookDayDisplay> days,
        string? errorMessage = null)
    {
        Book = book;
        TotalCharactersText = totalCharactersText;
        TotalDurationText = totalDurationText;
        AverageSpeedText = averageSpeedText;
        ActiveDaysText = activeDaysText;
        Days = days;
        ErrorMessage = errorMessage;
    }

    public static string FormatDate(string dateKey)
    {
        return DateOnly.TryParseExact(
            dateKey,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
                ? date.ToString("D", CultureInfo.CurrentCulture)
                : dateKey;
    }
}
