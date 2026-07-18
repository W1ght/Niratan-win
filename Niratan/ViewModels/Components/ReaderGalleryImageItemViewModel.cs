using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Niratan.Models.Novel;

namespace Niratan.ViewModels.Components;

public sealed class ReaderGalleryImageItemViewModel : ObservableObject
{
    public ReaderGalleryImageItemViewModel(ReaderGalleryImage image, int index)
    {
        ArgumentNullException.ThrowIfNull(image);
        RelativePath = image.RelativePath;
        FilePath = image.FilePath;
        SpineIndex = image.SpineIndex;
        ChapterProgress = image.ChapterProgress;
        AutomationId = $"NovelReaderGalleryImage_{index}";
        Thumbnail = new BitmapImage
        {
            UriSource = new Uri(image.FilePath),
            DecodePixelWidth = 480,
        };
    }

    public string RelativePath { get; }
    public string FilePath { get; }
    public int SpineIndex { get; }
    public double ChapterProgress { get; }
    public string AutomationId { get; }
    public BitmapImage Thumbnail { get; }

    private bool _isBlurred;

    public bool IsBlurred
    {
        get => _isBlurred;
        private set => SetProperty(ref _isBlurred, value);
    }

    public void SetBlurred(bool value) => IsBlurred = value;
}
