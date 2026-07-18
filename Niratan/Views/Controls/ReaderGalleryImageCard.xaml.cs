using System;
using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Niratan.Views.Controls;

public sealed partial class ReaderGalleryImageCard
{
    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
        nameof(FilePath),
        typeof(string),
        typeof(ReaderGalleryImageCard),
        new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

    public static readonly DependencyProperty ThumbnailProperty = DependencyProperty.Register(
        nameof(Thumbnail),
        typeof(ImageSource),
        typeof(ReaderGalleryImageCard),
        new PropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty IsBlurredProperty = DependencyProperty.Register(
        nameof(IsBlurred),
        typeof(bool),
        typeof(ReaderGalleryImageCard),
        new PropertyMetadata(false, OnVisualPropertyChanged));

    private LoadedImageSurface? _loadedSurface;
    private SpriteVisual? _blurVisual;

    public ReaderGalleryImageCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    public string FilePath
    {
        get => (string)GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    public ImageSource? Thumbnail
    {
        get => (ImageSource?)GetValue(ThumbnailProperty);
        set => SetValue(ThumbnailProperty, value);
    }

    public bool IsBlurred
    {
        get => (bool)GetValue(IsBlurredProperty);
        set => SetValue(IsBlurredProperty, value);
    }

    private static void OnVisualPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is ReaderGalleryImageCard card && card.IsLoaded)
            card.UpdateVisual();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateVisual();

    private void OnUnloaded(object sender, RoutedEventArgs e) => ClearBlurVisual();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_blurVisual != null)
            _blurVisual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
    }

    private void UpdateVisual()
    {
        PreviewImage.Source = Thumbnail;
        PreviewImage.Visibility = IsBlurred ? Visibility.Collapsed : Visibility.Visible;
        UnreadBadge.Visibility = IsBlurred ? Visibility.Visible : Visibility.Collapsed;

        ClearBlurVisual();
        if (!IsBlurred
            || string.IsNullOrWhiteSpace(FilePath)
            || ActualWidth <= 0
            || ActualHeight <= 0)
        {
            return;
        }

        var compositor = ElementCompositionPreview.GetElementVisual(BlurHost).Compositor;
        _loadedSurface = LoadedImageSurface.StartLoadFromUri(new Uri(FilePath));
        var sourceBrush = compositor.CreateSurfaceBrush(_loadedSurface);
        sourceBrush.Stretch = CompositionStretch.Uniform;

        using var blur = new GaussianBlurEffect
        {
            BlurAmount = 22,
            BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter("source"),
        };
        var effectBrush = compositor.CreateEffectFactory(blur).CreateBrush();
        effectBrush.SetSourceParameter("source", sourceBrush);

        _blurVisual = compositor.CreateSpriteVisual();
        _blurVisual.Brush = effectBrush;
        _blurVisual.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
        ElementCompositionPreview.SetElementChildVisual(BlurHost, _blurVisual);
    }

    private void ClearBlurVisual()
    {
        ElementCompositionPreview.SetElementChildVisual(BlurHost, null);
        _blurVisual = null;
        _loadedSurface?.Dispose();
        _loadedSurface = null;
    }
}
