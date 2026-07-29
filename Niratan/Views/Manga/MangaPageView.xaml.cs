using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Niratan.Models.Manga;
using Niratan.ViewModels.Pages;
using Windows.Foundation;

namespace Niratan.Views.Manga;

public sealed class MangaTextLookupRequestedEventArgs(
    MangaTextRegion region,
    Rect anchor) : EventArgs
{
    public MangaTextRegion Region { get; } = region;
    public Rect Anchor { get; } = anchor;
}

public sealed class MangaPagePanRequestedEventArgs(Point delta) : EventArgs
{
    public Point Delta { get; } = delta;
}

public sealed class MangaPageContextMenuRequestedEventArgs(
    MangaReaderPageItemViewModel page,
    Point point) : EventArgs
{
    public MangaReaderPageItemViewModel Page { get; } = page;
    public Point Point { get; } = point;
}

public sealed partial class MangaPageView : UserControl
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page),
        typeof(MangaReaderPageItemViewModel),
        typeof(MangaPageView),
        new PropertyMetadata(null, OnPageChanged));

    private readonly Dictionary<string, Rect> _renderedRegionBounds = [];
    private string? _activeBlockId;
    private uint? _rightPointerId;
    private Point _rightStart;
    private Point _rightPrevious;
    private bool _rightDragging;

    public MangaReaderPageItemViewModel? Page
    {
        get => (MangaReaderPageItemViewModel?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public event EventHandler<MangaTextLookupRequestedEventArgs>? LookupRequested;
    public event EventHandler<MangaPagePanRequestedEventArgs>? PanRequested;
    public event EventHandler<MangaPageContextMenuRequestedEventArgs>?
        ContextMenuRequested;

    public MangaPageView()
    {
        InitializeComponent();
    }

    private static void OnPageChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var view = (MangaPageView)dependencyObject;
        if (args.OldValue is MangaReaderPageItemViewModel oldPage)
            oldPage.PropertyChanged -= view.Page_PropertyChanged;
        if (args.NewValue is MangaReaderPageItemViewModel newPage)
            newPage.PropertyChanged += view.Page_PropertyChanged;
        view.UpdatePage();
    }

    private void Page_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MangaReaderPageItemViewModel.Image)
            or nameof(MangaReaderPageItemViewModel.IsLoading)
            or nameof(MangaReaderPageItemViewModel.ErrorMessage)
            or nameof(MangaReaderPageItemViewModel.TextRegions))
        {
            UpdatePage();
        }
    }

    private void UpdatePage()
    {
        PageImage.Source = Page?.Image;
        LoadingIndicator.IsActive = Page?.IsLoading == true;
        LoadingIndicator.Visibility = Page?.IsLoading == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        ErrorText.Text = Page?.ErrorMessage ?? "";
        RenderTextRegions();
    }

    private void PageImage_ImageOpened(object sender, RoutedEventArgs e) => RenderTextRegions();

    private void PageRoot_SizeChanged(object sender, SizeChangedEventArgs e) => RenderTextRegions();

    private void RenderTextRegions()
    {
        HitCanvas.Children.Clear();
        ActiveTextCanvas.Children.Clear();
        _renderedRegionBounds.Clear();
        _activeBlockId = null;

        if (Page?.Image is not { PixelWidth: > 0, PixelHeight: > 0 } image
            || Page.TextRegions.Count == 0
            || PageRoot.ActualWidth <= 0
            || PageRoot.ActualHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(
            PageRoot.ActualWidth / image.PixelWidth,
            PageRoot.ActualHeight / image.PixelHeight);
        var renderedWidth = image.PixelWidth * scale;
        var renderedHeight = image.PixelHeight * scale;
        var offsetX = (PageRoot.ActualWidth - renderedWidth) / 2;
        var offsetY = (PageRoot.ActualHeight - renderedHeight) / 2;

        foreach (var region in Page.TextRegions)
        {
            var bounds = new Rect(
                offsetX + region.X * renderedWidth,
                offsetY + region.Y * renderedHeight,
                Math.Max(2, region.Width * renderedWidth),
                Math.Max(2, region.Height * renderedHeight));
            _renderedRegionBounds[region.Id] = bounds;

            var hit = new Border
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Tag = region,
            };
            hit.PointerEntered += Region_PointerEntered;
            hit.PointerPressed += Region_PointerPressed;
            Canvas.SetLeft(hit, bounds.X);
            Canvas.SetTop(hit, bounds.Y);
            HitCanvas.Children.Add(hit);
        }
    }

    private void Region_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MangaTextRegion region)
            ShowBlock(region.BlockId);
    }

    private void Region_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MangaTextRegion region
            || !_renderedRegionBounds.TryGetValue(region.Id, out var bounds))
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        ShowBlock(region.BlockId);
        LookupRequested?.Invoke(this, new MangaTextLookupRequestedEventArgs(region, bounds));
        e.Handled = true;
    }

    private void PageRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(PageRoot);
        if (!point.Properties.IsRightButtonPressed)
            return;

        _rightPointerId = e.Pointer.PointerId;
        _rightStart = point.Position;
        _rightPrevious = point.Position;
        _rightDragging = false;
        PageRoot.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void PageRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_rightPointerId != e.Pointer.PointerId)
            return;
        var point = e.GetCurrentPoint(PageRoot);
        if (!point.Properties.IsRightButtonPressed)
            return;

        var position = point.Position;
        if (!_rightDragging)
        {
            var distance = Math.Sqrt(
                Math.Pow(position.X - _rightStart.X, 2)
                + Math.Pow(position.Y - _rightStart.Y, 2));
            if (distance >= 4)
            {
                _rightDragging = true;
                _activeBlockId = null;
                ActiveTextCanvas.Children.Clear();
            }
        }

        if (_rightDragging)
        {
            PanRequested?.Invoke(
                this,
                new MangaPagePanRequestedEventArgs(new Point(
                    position.X - _rightPrevious.X,
                    position.Y - _rightPrevious.Y)));
        }
        _rightPrevious = position;
        e.Handled = true;
    }

    private void PageRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_rightPointerId != e.Pointer.PointerId)
            return;
        var position = e.GetCurrentPoint(PageRoot).Position;
        PageRoot.ReleasePointerCapture(e.Pointer);
        var showMenu = !_rightDragging && Page is not null;
        _rightPointerId = null;
        _rightDragging = false;
        if (showMenu)
        {
            ContextMenuRequested?.Invoke(
                this,
                new MangaPageContextMenuRequestedEventArgs(Page!, position));
        }
        e.Handled = true;
    }

    private void PageRoot_PointerCaptureLost(
        object sender,
        PointerRoutedEventArgs e)
    {
        _rightPointerId = null;
        _rightDragging = false;
    }

    private void ShowBlock(string blockId)
    {
        if (_activeBlockId == blockId || Page is null)
            return;
        _activeBlockId = blockId;
        ActiveTextCanvas.Children.Clear();

        foreach (var region in Page.TextRegions.Where(region => region.BlockId == blockId))
        {
            if (!_renderedRegionBounds.TryGetValue(region.Id, out var bounds))
                continue;
            var character = GetTextElement(region.Sentence, region.Utf16Offset);
            var text = new TextBlock
            {
                Text = character,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
                FontSize = Math.Clamp(Math.Min(bounds.Width, bounds.Height) * 0.78, 8, 48),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            var background = new Border
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Background = new SolidColorBrush(
                    Windows.UI.Color.FromArgb(205, 255, 255, 255)),
                Child = text,
            };
            Canvas.SetLeft(background, bounds.X);
            Canvas.SetTop(background, bounds.Y);
            ActiveTextCanvas.Children.Add(background);
        }
    }

    private void PageRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _activeBlockId = null;
        ActiveTextCanvas.Children.Clear();
    }

    private static string GetTextElement(string text, int utf16Offset)
    {
        if (utf16Offset < 0 || utf16Offset >= text.Length)
            return "";
        return StringInfo.GetNextTextElement(text, utf16Offset);
    }
}
