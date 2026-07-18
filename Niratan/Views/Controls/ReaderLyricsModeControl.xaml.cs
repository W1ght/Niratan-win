using System;
using System.ComponentModel;
using System.IO;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Niratan.Helpers;
using Niratan.Models.Shortcuts;
using Niratan.Models.Sasayaki;
using Niratan.Services.Sasayaki;
using Niratan.ViewModels.Components;
using Windows.Foundation;

namespace Niratan.Views.Controls;

public sealed class ReaderLyricsLookupRequestedEventArgs(
    SasayakiMatch cue,
    int cueIndex,
    int characterIndex,
    Rect bounds,
    bool isHoverLookup) : EventArgs
{
    public SasayakiMatch Cue { get; } = cue;
    public int CueIndex { get; } = cueIndex;
    public int CharacterIndex { get; } = characterIndex;
    public Rect Bounds { get; } = bounds;
    public bool IsHoverLookup { get; } = isHoverLookup;
}

public sealed partial class ReaderLyricsModeControl : UserControl
{
    private int _lastHoverCueIndex = -1;
    private int _lastHoverCharacterIndex = -1;

    public ReaderLyricsViewModel ViewModel { get; } = new();

    public event EventHandler? ExitRequested;
    public event EventHandler? PlayPauseRequested;
    public event EventHandler? PreviousCueRequested;
    public event EventHandler? NextCueRequested;
    public event EventHandler? StatisticsRequested;
    public event EventHandler? DismissLookupRequested;
    public event EventHandler<ReaderLyricsLookupRequestedEventArgs>? LookupRequested;

    public ReaderLyricsModeControl()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.CoverPath))
            ApplyCoverImage();

        if (e.PropertyName == nameof(ViewModel.IsPlaying))
        {
            LyricsPlayPauseIcon.Glyph = ViewModel.IsPlaying ? "\uE769" : "\uE768";
            LyricsPlayPauseIcon.FontSize = ViewModel.IsPlaying ? 44 : 40;
        }

        if (e.PropertyName == nameof(ViewModel.IsMaskEnabled))
            LyricsMaskIcon.Glyph = ViewModel.IsMaskEnabled ? "\uED1A" : "\uE890";

        if (e.PropertyName == nameof(ViewModel.IsVertical))
            VerticalLyricsIcon.Glyph = ViewModel.IsVertical ? "\u25AD" : "\u25AF";

        if (e.PropertyName == nameof(ViewModel.ShowStatistics))
            LyricsStatisticsButton.Visibility = ViewModel.ShowStatistics
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (e.PropertyName == nameof(ViewModel.IsStatisticsTracking))
            LyricsStatisticsIcon.Glyph = ViewModel.IsStatisticsTracking
                ? "\uE823"
                : "\uE9D2";

        LyricsCanvas.Invalidate();
    }

    private void ApplyCoverImage()
    {
        var path = ViewModel.CoverPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            LyricsCoverImage.Source = null;
            LyricsBackgroundImage.Source = null;
            LyricsCoverFallbackIcon.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var source = new BitmapImage(new Uri(path, UriKind.Absolute));
            LyricsCoverImage.Source = source;
            LyricsBackgroundImage.Source = source;
            LyricsCoverFallbackIcon.Visibility = Visibility.Collapsed;
        }
        catch
        {
            LyricsCoverImage.Source = null;
            LyricsBackgroundImage.Source = null;
            LyricsCoverFallbackIcon.Visibility = Visibility.Visible;
        }
    }

    private ReaderLyricsCanvasRenderOptions CreateRenderOptions() => new(
        ViewModel.Cues,
        ViewModel.CurrentCueIndex,
        ViewModel.PositionSeconds,
        ViewModel.DelaySeconds,
        ViewModel.IsVertical,
        ViewModel.ShouldBlurLyrics,
        ViewModel.SelectedCueId,
        ViewModel.SelectionStart,
        ViewModel.SelectionLength,
        ResourceStringHelper.GetString("NovelReaderLyricsNoMatch", "No lyrics match"));

    private void LyricsCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        ReaderLyricsCanvasRenderer.Draw(
            args.DrawingSession,
            new Size(sender.ActualWidth, sender.ActualHeight),
            CreateRenderOptions());
    }

    private void LyricsCanvas_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.IsPointerOverLyrics = true;
    }

    private void LyricsCanvas_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.IsPointerOverLyrics = false;
        _lastHoverCueIndex = -1;
        _lastHoverCharacterIndex = -1;
    }

    private void LyricsCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!ShortcutInputMapper.GetCurrentModifiers().HasFlag(KeyboardShortcutModifiers.Shift))
        {
            _lastHoverCueIndex = -1;
            _lastHoverCharacterIndex = -1;
            return;
        }

        RequestLookup(e.GetCurrentPoint(LyricsCanvas).Position, isHoverLookup: true);
    }

    private void LyricsCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        RequestLookup(e.GetCurrentPoint(LyricsCanvas).Position, isHoverLookup: false);
    }

    private void LyricsRoot_PointerPressed(object sender, PointerRoutedEventArgs e) =>
        DismissLookupRequested?.Invoke(this, EventArgs.Empty);

    private void RequestLookup(Point point, bool isHoverLookup)
    {
        if (!ReaderLyricsCanvasRenderer.TryHitTest(
                CanvasDevice.GetSharedDevice(),
                new Size(LyricsCanvas.ActualWidth, LyricsCanvas.ActualHeight),
                CreateRenderOptions(),
                point,
                out var hit))
        {
            if (!isHoverLookup)
                DismissLookupRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (isHoverLookup
            && hit.CueIndex == _lastHoverCueIndex
            && hit.CharacterIndex == _lastHoverCharacterIndex)
        {
            return;
        }

        _lastHoverCueIndex = hit.CueIndex;
        _lastHoverCharacterIndex = hit.CharacterIndex;
        var canvasOffset = LyricsCanvas.TransformToVisual(this).TransformPoint(
            new Point(hit.Bounds.X, hit.Bounds.Y));
        LookupRequested?.Invoke(this, new ReaderLyricsLookupRequestedEventArgs(
            hit.Cue,
            hit.CueIndex,
            hit.CharacterIndex,
            new Rect(
                canvasOffset.X,
                canvasOffset.Y,
                hit.Bounds.Width,
                hit.Bounds.Height),
            isHoverLookup));
    }

    private void LyricsExitButton_Click(object sender, RoutedEventArgs e) =>
        ExitRequested?.Invoke(this, EventArgs.Empty);

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) =>
        PlayPauseRequested?.Invoke(this, EventArgs.Empty);

    private void PreviousCueButton_Click(object sender, RoutedEventArgs e) =>
        PreviousCueRequested?.Invoke(this, EventArgs.Empty);

    private void NextCueButton_Click(object sender, RoutedEventArgs e) =>
        NextCueRequested?.Invoke(this, EventArgs.Empty);

    private void StatisticsButton_Click(object sender, RoutedEventArgs e) =>
        StatisticsRequested?.Invoke(this, EventArgs.Empty);
}
