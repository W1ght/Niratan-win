using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Niratan.Models.Anki;
using Niratan.Helpers;
using Niratan.Models.Games;
using Niratan.Models.Profiles;
using Niratan.Services.Dictionary;
using Niratan.Services.Games;
using Niratan.Services.Profiles;
using Niratan.Services.Settings;
using Niratan.ViewModels.Pages;
using Windows.Foundation;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;

namespace Niratan.Views.Pages;

public sealed partial class GamesPage : Page
{
    private DispatcherQueueTimer? _captureTimer;
    private CancellationTokenSource? _lookupCts;
    private readonly GalGameTextOverlayService _textOverlay;
    private bool _wasCaptureActive;
    private string? _selectedLineId;
    private int _selectedTextOffset;
    private string? _selectedText;

    public GamesPageViewModel ViewModel { get; }

    public GamesPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<GamesPageViewModel>();
        _textOverlay = App.GetService<GalGameTextOverlayService>();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
        ApplySelectedSection();
        _ = PrewarmLookupPopupAsync();
        EnsureCaptureTimer().Start();
        await ViewModel.PollCaptureAsync();
        UpdateTextOverlay();
    }

    private void GamesSectionNavigation_ItemInvoked(
        NavigationView sender,
        NavigationViewItemInvokedEventArgs args)
    {
        _ = sender;
        if (args.InvokedItemContainer?.Tag is string tag
            && int.TryParse(tag, out var index))
        {
            ViewModel.SelectedSectionIndex = Math.Clamp(index, 0, 3);
            ApplySelectedSection();
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName == nameof(GamesPageViewModel.SelectedSectionIndex))
            DispatcherQueue.TryEnqueue(ApplySelectedSection);
    }

    private void ApplySelectedSection()
    {
        if (GamesLibrarySectionPanel is null)
            return;
        var index = Math.Clamp(ViewModel.SelectedSectionIndex, 0, 3);
        GamesLibrarySectionPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        GamesWorkbenchSectionPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        GamesImportSectionPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        GamesSettingsSectionPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        GamesSectionNavigation.SelectedItem = index switch
        {
            1 => GamesWorkbenchNavigationItem,
            2 => GamesImportNavigationItem,
            3 => GamesSettingsNavigationItem,
            _ => GamesLibraryNavigationItem,
        };
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _captureTimer?.Stop();
        _lookupCts?.Cancel();
        _textOverlay.Hide();
        base.OnNavigatedFrom(e);
    }

    private DispatcherQueueTimer EnsureCaptureTimer()
    {
        if (_captureTimer is not null)
            return _captureTimer;

        _captureTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _captureTimer.Interval = TimeSpan.FromMilliseconds(250);
        _captureTimer.Tick += async (_, _) =>
        {
            await ViewModel.PollCaptureAsync();
            UpdateTextOverlay();
        };
        return _captureTimer;
    }

    private async void CapturedLineTextSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RichTextBlock textBlock || textBlock.Tag is not GalGameTextLine line)
            return;

        var offset = Math.Clamp(textBlock.SelectionStart?.Offset ?? 0, 0, line.Text.Length);
        var selectionEnd = Math.Clamp(textBlock.SelectionEnd?.Offset ?? 0, 0, line.Text.Length);
        var selectedText = selectionEnd > offset
            ? line.Text[offset..selectionEnd].Trim()
            : null;
        var shouldLookup = !string.IsNullOrWhiteSpace(selectedText)
            && (!string.Equals(_selectedLineId, line.Id, StringComparison.Ordinal)
                || _selectedTextOffset != offset
                || !string.Equals(_selectedText, selectedText, StringComparison.Ordinal));
        _selectedLineId = line.Id;
        _selectedTextOffset = offset;
        _selectedText = selectedText;
        SelectedLineSource.Text = line.SourceLabel;
        SelectedLineText.Text = string.IsNullOrWhiteSpace(_selectedText)
            ? line.Text
            : _selectedText;

        if (shouldLookup)
        {
            await OpenLookupAsync(
                line,
                offset,
                selectedText,
                GetScreenBounds(textBlock));
        }
    }

    private async void CapturedLineTextPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var currentPoint = sender is RichTextBlock richTextBlock
            ? e.GetCurrentPoint(richTextBlock)
            : null;
        if (sender is not RichTextBlock textBlock
            || textBlock.Tag is not GalGameTextLine line
            || currentPoint is null
            || !currentPoint.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var hit = GetTextHit(textBlock, currentPoint.Position, line.Text);
        if (hit is null)
            return;

        var offset = hit.Value.Offset;
        _selectedLineId = line.Id;
        _selectedTextOffset = offset;
        _selectedText = null;
        SelectedLineSource.Text = line.SourceLabel;
        SelectedLineText.Text = line.Text;
        await OpenLookupAsync(
            line,
            offset,
            null,
            GetScreenBounds(textBlock, hit.Value.Bounds));
    }

    private async void CapturedLineLookupClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GalGameTextLine line })
            return;

        var hasSelection = string.Equals(_selectedLineId, line.Id, StringComparison.Ordinal);
        await OpenLookupAsync(
            line,
            hasSelection ? _selectedTextOffset : 0,
            hasSelection ? _selectedText : null,
            GetScreenBounds((FrameworkElement)sender));
    }

    private async void SelectedLineLookupClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (_selectedLineId is null)
            return;

        var line = ViewModel.CapturedLines.FirstOrDefault(item => item.Id == _selectedLineId);
        if (line is null)
            return;

        await OpenLookupAsync(
            line,
            _selectedTextOffset,
            _selectedText,
            GetScreenBounds(SelectedLineLookupButton));
    }

    private async Task OpenLookupAsync(
        GalGameTextLine line,
        int requestedOffset,
        string? selectedText,
        RectInt32 anchor)
    {
        var settings = App.GetService<ISettingsService>().Current.DictionaryDisplaySettings;
        var language = App.GetService<IProfileRuntimeService>().ActiveLanguage;
        var candidate = ResolveCandidate(
            line.Text,
            requestedOffset,
            selectedText,
            settings.ScanLength,
            language);
        if (candidate is null)
            return;

        _lookupCts?.Cancel();
        _lookupCts?.Dispose();
        _lookupCts = new CancellationTokenSource();
        var ct = _lookupCts.Token;
        try
        {
            var context = ViewModel.CreateDeferredMiningContext(line)
                ?? new AnkiMiningContext { Sentence = line.Text, SentenceOffset = candidate.Utf16Start };
            context.SentenceOffset = candidate.Utf16Start;
            IEnumerable<string> queries = string.IsNullOrWhiteSpace(selectedText)
                ? DictionaryLookupService.EnumerateLookupCandidates(candidate.Text, settings.ScanLength)
                : new[] { candidate.Text };
            foreach (var query in queries)
            {
                var request = await App.GetService<IDictionaryPopupRequestService>().CreateAsync(
                    query,
                    context,
                    $"galgame-{line.ProcessId}-{line.Sequence:x}",
                    ct);
                if (request is null)
                    continue;

                await App.GetService<IGlobalLookupPopupService>().ShowAsync(request, anchor, ct);
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task OpenOverlayLookupAsync(
        GalGameTextLine line,
        int offset,
        string? selectedText,
        RectInt32 anchor) =>
        await OpenLookupAsync(line, offset, selectedText, anchor);

    private Task SelectOverlayThreadAsync(GalGameThreadPreview preview) =>
        ViewModel.SelectThreadFromOverlayAsync(preview);

    private Task RefreshOverlayAsync() =>
        RefreshCaptureAsync();

    private Task StopOverlayAsync() =>
        ViewModel.StopCaptureAsync();

    private async Task RefreshCaptureAsync()
    {
        await ViewModel.PollCaptureAsync();
        UpdateTextOverlay();
    }

    private async Task PrewarmLookupPopupAsync()
    {
        try
        {
            await App.GetService<IGlobalLookupPopupService>().PrewarmAsync();
        }
        catch (Exception)
        {
            // The popup service retries during the first lookup. Warming must
            // never delay opening the games page or the capture timer.
        }
    }

    private void UpdateTextOverlay()
    {
        _textOverlay.UpdateSnapshot(
            ViewModel.ThreadPreviews,
            ViewModel.CapturedLines,
            ViewModel.SessionStatusText,
            ViewModel.SelectedThreadId);
        if (!ViewModel.IsCaptureActive)
        {
            _wasCaptureActive = false;
            return;
        }

        if (ViewModel.CapturedLines.Count == 0)
            return;

        if (!_wasCaptureActive)
            _textOverlay.ResetDismissal();
        _wasCaptureActive = true;

        _textOverlay.Show(
            OpenOverlayLookupAsync,
            SelectOverlayThreadAsync,
            RefreshOverlayAsync,
            StopOverlayAsync,
            HandleOverlayToolbarActionAsync);
    }

    private void ShowCaptureWindowClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _textOverlay.UpdateSnapshot(
            ViewModel.ThreadPreviews,
            ViewModel.CapturedLines,
            ViewModel.SessionStatusText,
            ViewModel.SelectedThreadId);
        _textOverlay.Show(
            OpenOverlayLookupAsync,
            SelectOverlayThreadAsync,
            RefreshOverlayAsync,
            StopOverlayAsync,
            HandleOverlayToolbarActionAsync,
            force: true);
    }

    private async void AttachCaptureClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var choices = new List<RunningProcessChoice>();
        foreach (var process in Process.GetProcesses().OrderBy(item => item.ProcessName))
        {
            try
            {
                if (process.Id == Environment.ProcessId || string.IsNullOrWhiteSpace(process.MainWindowTitle))
                    continue;
                choices.Add(new RunningProcessChoice(
                    process.Id,
                    process.ProcessName,
                    process.MainWindowTitle));
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }

        var picker = new ComboBox
        {
            ItemsSource = choices,
            DisplayMemberPath = nameof(RunningProcessChoice.Label),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = ResourceStringHelper.GetString(
                "GamesAttachProcessPlaceholder",
                "Select a running game"),
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ResourceStringHelper.GetString("GamesAttachProcessTitle", "Attach and capture"),
            Content = picker,
            PrimaryButtonText = ResourceStringHelper.GetString("GamesAttachProcessConfirm", "Attach"),
            CloseButtonText = ResourceStringHelper.GetString("DialogCancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = choices.Count > 0,
        };
        picker.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = picker.SelectedItem is not null;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary
            && picker.SelectedItem is RunningProcessChoice choice)
        {
            await ViewModel.AttachToProcessAsync(choice.ProcessId);
        }
    }

    private async void ShowAudioPolicyClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ResourceStringHelper.GetString("GamesAudioFallbackTitle", "Audio fallback policy"),
            Content = ResourceStringHelper.GetString(
                "GamesAudioFallbackDescription",
                "Use paired game resources first, then engine PCM, then system loopback. A genuinely unvoiced line can still create a text-and-screenshot card."),
            CloseButtonText = ResourceStringHelper.GetString("DialogClose", "Close"),
        };
        await dialog.ShowAsync();
    }

    private async Task HandleOverlayToolbarActionAsync(string action)
    {
        switch (action)
        {
            case "openWorkbench":
                ViewModel.SelectedSectionIndex = 1;
                return;
            case "refresh":
                await RefreshCaptureAsync();
                return;
            case "stop":
                await ViewModel.StopCaptureAsync();
                return;
            // Voice replay/recapture are intentionally kept as toolbar actions
            // until the session exposes a stable per-line audio playback
            // contract. The buttons remain in the Fushi-compatible surface;
            // they must not manufacture audio by rescanning the first line.
            case "replayVoice":
            case "recaptureVoice":
            case "togglePassThrough":
                return;
            default:
                return;
        }
    }

    private async void RefreshCaptureClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RefreshCaptureAsync();
    }

    private async void ThreadSelectorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ThreadSelector.SelectedItem is GalGameThreadPreview preview)
            await ViewModel.SelectThreadFromOverlayAsync(preview);
    }

    private void OpenWorkbenchClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.SelectedSectionIndex = 1;
    }

    private void OpenImportClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.SelectedSectionIndex = 2;
    }

    private void OpenSettingsClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ViewModel.SelectedSectionIndex = 3;
    }

    private async void ChooseGameExecutablesClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".exe");
        if (App.MainWindow is not null)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        }
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0)
            return;
        if (await ViewModel.ImportPathsAsync(files.Select(file => file.Path)) > 0)
            ViewModel.SelectedSectionIndex = 0;
    }

    private void GamesPage_DragOver(object sender, DragEventArgs e)
    {
        _ = sender;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = ResourceStringHelper.GetString(
            "GamesImportDropCaption",
            "Import game executable");
        e.DragUIOverride.IsContentVisible = true;
    }

    private async void GamesPage_Drop(object sender, DragEventArgs e)
    {
        _ = sender;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        var items = await e.DataView.GetStorageItemsAsync().AsTask();
        var paths = items.OfType<StorageFile>()
            .Where(file => string.Equals(file.FileType, ".exe", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Path)
            .ToArray();
        if (paths.Length == 0)
            return;
        if (await ViewModel.ImportPathsAsync(paths) > 0)
            ViewModel.SelectedSectionIndex = 0;
    }

    private async void GameCoverImageLoaded(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Image image || image.Tag is not GalGameEntry game)
            return;

        try
        {
            BitmapImage bitmap = new();
            var coverPath = game.CoverPath;
            if (!string.IsNullOrWhiteSpace(coverPath)
                && System.IO.File.Exists(coverPath))
            {
                var coverFile = await StorageFile.GetFileFromPathAsync(coverPath);
                using var coverStream = await coverFile.OpenReadAsync();
                await bitmap.SetSourceAsync(coverStream);
                if (ReferenceEquals(image.Tag, game))
                {
                    image.Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill;
                    image.Source = bitmap;
                }
                return;
            }

            if (!System.IO.File.Exists(game.ExePath))
                return;
            var executable = await StorageFile.GetFileFromPathAsync(game.ExePath);
            using var thumbnail = await executable.GetThumbnailAsync(
                ThumbnailMode.SingleItem,
                256,
                ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is null || thumbnail.Size == 0)
                return;
            await bitmap.SetSourceAsync(thumbnail);
            if (ReferenceEquals(image.Tag, game))
            {
                image.Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform;
                image.Source = bitmap;
            }
        }
        catch (Exception)
        {
            // The neutral placeholder remains visible when a cover or shell
            // thumbnail cannot be decoded. Library loading must never fail for
            // a broken user-supplied image.
        }
    }

    private sealed record RunningProcessChoice(int ProcessId, string ProcessName, string WindowTitle)
    {
        public string Label => $"{ProcessName} · {WindowTitle} (PID {ProcessId})";
    }

    private RectInt32 GetScreenBounds(FrameworkElement element)
    {
        return GetScreenBounds(
            element,
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    }

    private RectInt32 GetScreenBounds(FrameworkElement element, Rect localBounds)
    {
        var point = element.TransformToVisual(RootGrid).TransformPoint(
            new Point(localBounds.X, localBounds.Y));
        var scale = Math.Max(0.01, RootGrid.XamlRoot?.RasterizationScale ?? 1);
        var windowPosition = App.MainWindow?.AppWindow.Position ?? new PointInt32();
        return new RectInt32(
            windowPosition.X + (int)Math.Round(point.X * scale),
            windowPosition.Y + (int)Math.Round(point.Y * scale),
            Math.Max(1, (int)Math.Round(localBounds.Width * scale)),
            Math.Max(1, (int)Math.Round(localBounds.Height * scale)));
    }

    private static TextHit? GetTextHit(
        RichTextBlock textBlock,
        Point point,
        string text)
    {
        if (text.Length == 0)
            return null;

        var bestOffset = 0;
        var bestBounds = new Rect();
        var bestDistance = double.MaxValue;
        var contentStart = GetTextContentStart(textBlock);
        for (var offset = 0; offset < text.Length; offset++)
        {
            var character = contentStart.GetPositionAtOffset(
                offset,
                LogicalDirection.Forward);
            if (character is null)
                break;

            var bounds = character.GetCharacterRect(LogicalDirection.Forward);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                continue;

            var distance = DistanceToRect(point, bounds);
            if (distance < bestDistance)
            {
                bestOffset = offset;
                bestBounds = bounds;
                bestDistance = distance;
            }

            if (distance == 0)
                return new TextHit(offset, bounds);
        }

        if (bestDistance < double.MaxValue)
            return new TextHit(bestOffset, bestBounds);

        var fallback = textBlock.GetPositionFromPoint(point);
        if (fallback is null)
            return null;

        var fallbackOffset = Math.Clamp(
            fallback.Offset - contentStart.Offset,
            0,
            text.Length - 1);
        return new TextHit(
            fallbackOffset,
            fallback.GetCharacterRect(LogicalDirection.Forward));
    }

    private static TextPointer GetTextContentStart(RichTextBlock textBlock)
    {
        if (textBlock.Blocks.FirstOrDefault() is Paragraph paragraph
            && paragraph.Inlines.FirstOrDefault() is Run run)
        {
            return run.ContentStart;
        }

        return textBlock.ContentStart;
    }

    private static double DistanceToRect(Point point, Rect bounds)
    {
        var dx = point.X < bounds.Left
            ? bounds.Left - point.X
            : point.X > bounds.Right
                ? point.X - bounds.Right
                : 0;
        var dy = point.Y < bounds.Top
            ? bounds.Top - point.Y
            : point.Y > bounds.Bottom
                ? point.Y - bounds.Bottom
                : 0;
        return dx * dx + dy * dy;
    }

    private readonly record struct TextHit(int Offset, Rect Bounds);

    private static TextLookupCandidate? ResolveCandidate(
        string text,
        int requestedOffset,
        string? selectedText,
        int scanLength,
        ContentLanguageProfile language)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (!string.IsNullOrWhiteSpace(selectedText))
        {
            var selected = selectedText.Trim();
            var selectedStart = Math.Clamp(requestedOffset, 0, Math.Max(0, text.Length - selected.Length));
            return new TextLookupCandidate(selected, selectedStart);
        }

        // Match Reader/Manga/Video: a click is a UTF-16 offset, and the
        // dictionary receives the configured forward scan beginning there.
        // Only leading punctuation/whitespace is skipped; never restart at
        // the beginning of the captured sentence.
        var clamped = Math.Clamp(requestedOffset, 0, Math.Max(0, text.Length - 1));
        var candidate = TextSelectionResolver.LookupCandidate(text, clamped, scanLength, language);
        if (candidate is not null && !IsLookupBoundary(candidate.Text))
            return candidate;

        for (var offset = clamped; offset < text.Length; offset++)
        {
            candidate = TextSelectionResolver.LookupCandidate(text, offset, scanLength, language);
            if (candidate is not null && !IsLookupBoundary(candidate.Text))
                return candidate;
        }

        return null;
    }

    private static bool IsLookupBoundary(string candidate) =>
        string.IsNullOrWhiteSpace(candidate)
        || char.IsWhiteSpace(candidate[0])
        || char.IsPunctuation(candidate[0]);

}
