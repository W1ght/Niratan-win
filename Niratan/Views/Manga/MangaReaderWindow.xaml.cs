using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Niratan.Helpers;
using Niratan.Models.Manga;
using Niratan.Models.Anki;
using Niratan.Services.Dictionary;
using Niratan.Services.Profiles;
using Niratan.Services.Settings;
using Niratan.ViewModels.Pages;
using Niratan.Views.Dictionary;
using Serilog;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;

namespace Niratan.Views.Manga;

public sealed partial class MangaReaderWindow : Window
{
    private readonly DispatcherTimer _zoomSaveTimer = new();
    private bool _updatingChrome;
    private long _lastWheelTurnAt;
    private DictionaryPopupOverlay? _popupOverlay;
    private readonly AnkiMiningFeedbackPresenter _miningFeedbackPresenter;
    private CancellationTokenSource? _lookupCts;

    public MangaReaderViewModel ViewModel { get; }
    public event EventHandler? ReadingStateSaved;

    public MangaReaderWindow()
    {
        InitializeComponent();
        _miningFeedbackPresenter = new AnkiMiningFeedbackPresenter(MangaMiningToast);
        ViewModel = App.GetService<MangaReaderViewModel>();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.ReaderStateChanged += ViewModel_ReaderStateChanged;

        _updatingChrome = true;
        ZoomSlider.Maximum = 200;
        ZoomSlider.Minimum = 50;
        ZoomSlider.StepFrequency = 5;
        _updatingChrome = false;

        Title = ResourceStringHelper.GetString(
            "MangaReaderWindowTitle",
            "Niratan Manga");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(ReaderTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.SetPresenter(OverlappedPresenter.Create());
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 820));

        _zoomSaveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _zoomSaveTimer.Tick += ZoomSaveTimer_Tick;
        Activated += OnActivated;
        Closed += OnClosed;
        RootGrid.Loaded += (_, _) => RootGrid.Focus(FocusState.Programmatic);
    }

    public async Task OpenAsync(MangaBook book, CancellationToken ct = default)
    {
        await App.GetService<IProfileRuntimeService>().ActivateGlobalAsync(ct);
        await ViewModel.InitializeAsync(book, ct);
        Title = $"{ViewModel.Title} — Niratan";
        ApplyReaderState(bringCurrentPageIntoView: true);
    }

    private async void OnActivated(
        object sender,
        WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            return;
        try
        {
            await App.GetService<IProfileRuntimeService>().ActivateGlobalAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Manga] Failed to reactivate the global profile.");
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModel.VisiblePages)
            or nameof(ViewModel.Layout)
            or nameof(ViewModel.Direction))
        {
            ApplyReaderState(bringCurrentPageIntoView: true);
        }
        if (e.PropertyName is nameof(ViewModel.IsRecognizingText)
            or nameof(ViewModel.OcrCompletedPageCount)
            or nameof(ViewModel.OcrTotalPageCount)
            or nameof(ViewModel.OcrStatusMessage)
            or nameof(ViewModel.IsGoogleOcrEnabled)
            or nameof(ViewModel.IsOcrRecognitionPaused))
        {
            UpdateOcrChrome();
        }
    }

    private void ViewModel_ReaderStateChanged(object? sender, EventArgs e) =>
        ApplyReaderState(bringCurrentPageIntoView: true);

    private void ApplyReaderState(bool bringCurrentPageIntoView)
    {
        if (_updatingChrome)
            return;

        _updatingChrome = true;
        try
        {
            _popupOverlay?.Dismiss();
            var continuous = ViewModel.Layout == MangaReaderLayout.Continuous;
            PagedScrollViewer.Visibility = continuous ? Visibility.Collapsed : Visibility.Visible;
            ContinuousScrollViewer.Visibility = continuous ? Visibility.Visible : Visibility.Collapsed;
            RenderPagedCanvas();

            SinglePageLayoutItem.IsChecked = ViewModel.Layout == MangaReaderLayout.SinglePage;
            DoublePageLayoutItem.IsChecked = ViewModel.Layout == MangaReaderLayout.DoublePage;
            ContinuousLayoutItem.IsChecked = continuous;
            RightToLeftDirectionItem.IsChecked =
                ViewModel.Direction == MangaReadingDirection.RightToLeft;
            LeftToRightDirectionItem.IsChecked =
                ViewModel.Direction == MangaReadingDirection.LeftToRight;

            ZoomSlider.Value = ViewModel.ZoomPercentage;
            PageNumberBox.Maximum = Math.Max(1, ViewModel.PageCount);
            PageNumberBox.Value = ViewModel.PageCount == 0
                ? 1
                : ViewModel.CurrentPageIndex + 1;
            ApplyZoom();

            if (continuous && bringCurrentPageIntoView)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    ContinuousRepeater.TryGetElement(ViewModel.CurrentPageIndex)
                        ?.StartBringIntoView(new BringIntoViewOptions
                        {
                            AnimationDesired = false,
                            VerticalAlignmentRatio = 0,
                        });
                });
            }
        }
        finally
        {
            _updatingChrome = false;
        }
    }

    private void RenderPagedCanvas()
    {
        PagedCanvas.Children.Clear();
        PagedCanvas.ColumnDefinitions.Clear();
        var pages = ViewModel.VisiblePages;
        if (pages.Count == 0)
            return;

        for (var index = 0; index < pages.Count; index++)
        {
            PagedCanvas.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
            var page = pages[index];
            var host = new MangaPageView
            {
                Page = page,
                Margin = new Thickness(pages.Count > 1 ? 4 : 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            host.LookupRequested += MangaPageView_LookupRequested;
            host.PanRequested += MangaPageView_PanRequested;
            host.ContextMenuRequested += MangaPageView_ContextMenuRequested;

            Grid.SetColumn(host, index);
            PagedCanvas.Children.Add(host);
        }
    }

    private void MangaPageView_PanRequested(
        object? sender,
        MangaPagePanRequestedEventArgs e)
    {
        var scrollViewer = ViewModel.Layout == MangaReaderLayout.Continuous
            ? ContinuousScrollViewer
            : PagedScrollViewer;
        scrollViewer.ChangeView(
            scrollViewer.HorizontalOffset - e.Delta.X,
            scrollViewer.VerticalOffset - e.Delta.Y,
            null,
            disableAnimation: true);
        _popupOverlay?.Dismiss();
    }

    private void MangaPageView_ContextMenuRequested(
        object? sender,
        MangaPageContextMenuRequestedEventArgs e)
    {
        if (sender is not MangaPageView pageView)
            return;
        var flyout = new MenuFlyout();
        var copy = new MenuFlyoutItem
        {
            Text = ResourceStringHelper.GetString(
                "MangaReaderCopyPageImage",
                "Copy page image"),
        };
        copy.Click += async (_, _) => await CopyPageImageAsync(e.Page);
        var save = new MenuFlyoutItem
        {
            Text = ResourceStringHelper.GetString(
                "MangaReaderSavePageImage",
                "Save page image…"),
        };
        save.Click += async (_, _) => await SavePageImageAsync(e.Page);
        flyout.Items.Add(copy);
        flyout.Items.Add(save);
        flyout.ShowAt(
            pageView,
            new FlyoutShowOptions
            {
                Position = e.Point,
                ShowMode = FlyoutShowMode.Transient,
            });
    }

    private static async Task CopyPageImageAsync(
        MangaReaderPageItemViewModel page)
    {
        var path = page.Image?.UriSource?.LocalPath;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            return;
        var file = await StorageFile.GetFileFromPathAsync(path);
        var package = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy,
        };
        package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private async Task SavePageImageAsync(MangaReaderPageItemViewModel page)
    {
        var path = page.Image?.UriSource?.LocalPath;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            return;
        var extension = System.IO.Path.GetExtension(path);
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = ResourceStringHelper.FormatString(
                "MangaReaderSuggestedPageFileName",
                "Page {0}",
                page.Index + 1),
            DefaultFileExtension = extension,
        };
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add(
            ResourceStringHelper.GetString(
                "MangaReaderPageFileType",
                "Manga page"),
            new System.Collections.Generic.List<string> { extension });
        var target = await picker.PickSaveFileAsync();
        if (target is null)
            return;
        var source = await StorageFile.GetFileFromPathAsync(path);
        await source.CopyAndReplaceAsync(target);
    }

    private async void MangaPageView_LookupRequested(
        object? sender,
        MangaTextLookupRequestedEventArgs e)
    {
        if (sender is not MangaPageView pageView)
            return;

        _lookupCts?.Cancel();
        _lookupCts?.Dispose();
        _lookupCts = new CancellationTokenSource();
        var ct = _lookupCts.Token;
        try
        {
            var offset = Math.Clamp(e.Region.Utf16Offset, 0, e.Region.Sentence.Length);
            var settings = App.GetService<ISettingsService>().Current;
            var candidate = TextSelectionResolver.LookupCandidate(
                e.Region.Sentence,
                offset,
                settings.DictionaryDisplaySettings.ScanLength,
                App.GetService<IProfileRuntimeService>().ActiveLanguage);
            if (candidate is null)
                return;
            var pagePath = pageView.Page?.ImagePath;
            var request = await App.GetService<IDictionaryPopupRequestService>().CreateAsync(
                candidate.Text,
                new AnkiMiningContext
                {
                    Sentence = e.Region.Sentence,
                    SentenceOffset = candidate.Utf16Start,
                    DocumentTitle = ViewModel.Title,
                    MangaPagePath = pagePath,
                },
                $"manga-{pageView.Page?.Index}-{Environment.TickCount64:x}",
                ct);
            if (request is null)
                return;

            _popupOverlay ??= CreatePopupOverlay();
            var origin = pageView.TransformToVisual(MangaPopupCanvas)
                .TransformPoint(new Windows.Foundation.Point(e.Anchor.X, e.Anchor.Y));
            await _popupOverlay.ShowLookupAsync(
                request.Results,
                request.Styles,
                request.DisplaySettings,
                origin.X,
                origin.Y,
                e.Anchor.Width,
                e.Anchor.Height,
                RootGrid.XamlRoot,
                e.Region.IsVertical,
                request.Theme,
                request.AudioSettings,
                request.AnkiSettings,
                request.MiningContext,
                request.TraceId,
                ct,
                MangaPopupCanvas.ActualWidth,
                MangaPopupCanvas.ActualHeight);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private DictionaryPopupOverlay CreatePopupOverlay()
    {
        var overlay = new DictionaryPopupOverlay();
        overlay.MiningFeedbackRequested += OnMiningFeedbackRequested;
        overlay.MiningFeedbackCleared += OnMiningFeedbackCleared;
        overlay.UseCanvas(
            MangaPopupCanvas,
            DictionaryPopupCanvasInputMode.VisibleHostsOnly);
        return overlay;
    }

    private void OnMiningFeedbackRequested(
        object? sender,
        DictionaryPopupMiningFeedbackEventArgs e) =>
        _miningFeedbackPresenter.Show(e);

    private void OnMiningFeedbackCleared(object? sender, EventArgs e) =>
        _miningFeedbackPresenter.Clear();

    private async void LayoutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag }
            && Enum.TryParse<MangaReaderLayout>(tag, out var layout))
        {
            await ViewModel.SetLayoutAsync(layout);
        }
    }

    private async void DirectionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag }
            && Enum.TryParse<MangaReadingDirection>(tag, out var direction))
        {
            await ViewModel.SetDirectionAsync(direction);
        }
    }

    private void ZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingChrome)
            return;

        ViewModel.ZoomPercentage = Math.Clamp((int)Math.Round(e.NewValue), 50, 200);
        ApplyZoom();
        _zoomSaveTimer.Stop();
        _zoomSaveTimer.Start();
    }

    private async void ZoomSaveTimer_Tick(object? sender, object e)
    {
        _zoomSaveTimer.Stop();
        await ViewModel.SetZoomAsync(ViewModel.ZoomPercentage);
    }

    private void ApplyZoom()
    {
        var factor = ViewModel.ZoomPercentage / 100f;
        if (ViewModel.Layout == MangaReaderLayout.Continuous)
            ContinuousScrollViewer.ChangeView(null, null, factor, disableAnimation: true);
        else
            PagedScrollViewer.ChangeView(null, null, factor, disableAnimation: true);
    }

    private void ApplyWheelZoom(int wheelDelta)
    {
        var target = Math.Clamp(
            ViewModel.ZoomPercentage + (wheelDelta > 0 ? 5 : -5),
            50,
            200);
        if (target == ViewModel.ZoomPercentage)
            return;
        _updatingChrome = true;
        ZoomSlider.Value = target;
        _updatingChrome = false;
        ViewModel.ZoomPercentage = target;
        ApplyZoom();
        _popupOverlay?.Dismiss();
        _zoomSaveTimer.Stop();
        _zoomSaveTimer.Start();
    }

    private async void PageNumberBox_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (_updatingChrome || double.IsNaN(args.NewValue) || ViewModel.PageCount == 0)
            return;

        await ViewModel.NavigateToAsync((int)Math.Round(args.NewValue) - 1);
    }

    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Left)
        {
            await ViewModel.PhysicalLeftCommand.ExecuteAsync(null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Right)
        {
            await ViewModel.PhysicalRightCommand.ExecuteAsync(null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Home)
        {
            await ViewModel.NavigateToAsync(0);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.End)
        {
            await ViewModel.NavigateToAsync(Math.Max(0, ViewModel.PageCount - 1));
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            _popupOverlay?.Dismiss();
            e.Handled = true;
        }
    }

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_popupOverlay is null || IsDescendantOf(e.OriginalSource as DependencyObject, MangaPopupCanvas))
            return;
        _popupOverlay.Dismiss();
    }

    private static bool IsDescendantOf(DependencyObject? element, DependencyObject ancestor)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, ancestor))
                return true;
            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private async void PagedScrollViewer_PointerWheelChanged(
        object sender,
        PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(PagedScrollViewer).Properties;
        if (properties.IsHorizontalMouseWheel)
            return;
        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            ApplyWheelZoom(properties.MouseWheelDelta);
            e.Handled = true;
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastWheelTurnAt < 250)
        {
            e.Handled = true;
            return;
        }

        _lastWheelTurnAt = now;
        if (properties.MouseWheelDelta < 0)
            await ViewModel.GoForwardCommand.ExecuteAsync(null);
        else if (properties.MouseWheelDelta > 0)
            await ViewModel.GoBackwardCommand.ExecuteAsync(null);
        e.Handled = true;
    }

    private void ContinuousScrollViewer_PointerWheelChanged(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
            return;
        ApplyWheelZoom(
            e.GetCurrentPoint(ContinuousScrollViewer).Properties.MouseWheelDelta);
        e.Handled = true;
    }

    private async void OcrButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsRecognizingText)
        {
            ViewModel.CancelOcrRecognition();
            UpdateOcrChrome();
            return;
        }
        if (ViewModel.IsGoogleOcrEnabled)
        {
            if (ViewModel.IsOcrRecognitionPaused)
            {
                await ViewModel.ResumeOcrRecognitionAsync();
                UpdateOcrChrome();
                return;
            }
            await ViewModel.HideGoogleOcrAsync();
            UpdateOcrChrome();
            return;
        }

        var accepted = ViewModel.GoogleOcrDisclosureAccepted;
        if (!accepted)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = ResourceStringHelper.GetString(
                    "MangaOcrDisclosureTitle",
                    "Use Google Lens text recognition?"),
                Content = ResourceStringHelper.GetString(
                    "MangaOcrDisclosureContent",
                    "Recognizing this manga sends a reduced copy (maximum 1500 px) "
                    + "of every page without Mokuro text to Google. Results are "
                    + "cached locally so unchanged pages are not uploaded again."),
                PrimaryButtonText = ResourceStringHelper.GetString(
                    "MangaOcrDisclosureContinue",
                    "Continue"),
                CloseButtonText = ResourceStringHelper.GetString(
                    "MangaOcrDisclosureCancel",
                    "Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            accepted = await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        if (!accepted)
            return;
        await ViewModel.EnableGoogleOcrAsync(disclosureAccepted: true);
        UpdateOcrChrome();
    }

    private void UpdateOcrChrome()
    {
        OcrButton.Content = ViewModel.IsRecognizingText
            ? ResourceStringHelper.GetString("MangaOcrCancel", "Cancel OCR")
            : ViewModel.IsOcrRecognitionPaused
                ? ResourceStringHelper.GetString("MangaOcrResume", "Resume OCR")
            : ViewModel.IsGoogleOcrEnabled
                ? ResourceStringHelper.GetString("MangaOcrHide", "Hide OCR")
                : ResourceStringHelper.GetString("MangaOcrButton", "OCR");
        var hasStatus = ViewModel.IsRecognizingText
            || !string.IsNullOrWhiteSpace(ViewModel.OcrStatusMessage);
        OcrStatusPanel.Visibility = hasStatus
            ? Visibility.Visible
            : Visibility.Collapsed;
        OcrStatusText.Text = ViewModel.IsRecognizingText
            ? ResourceStringHelper.FormatString(
                "MangaOcrProgress",
                "OCR {0} / {1}",
                ViewModel.OcrCompletedPageCount,
                ViewModel.OcrTotalPageCount)
            : ViewModel.OcrStatusMessage ?? "";
        OcrProgressBar.Visibility = ViewModel.IsRecognizingText
            ? Visibility.Visible
            : Visibility.Collapsed;
        OcrProgressBar.Value = ViewModel.OcrTotalPageCount == 0
            ? 0
            : (double)ViewModel.OcrCompletedPageCount / ViewModel.OcrTotalPageCount;
    }

    private async void ContinuousScrollViewer_ViewChanged(
        object sender,
        ScrollViewerViewChangedEventArgs e)
    {
        if (_updatingChrome || e.IsIntermediate || ViewModel.PageCount == 0)
            return;

        var bestIndex = ViewModel.CurrentPageIndex;
        var bestDistance = double.MaxValue;
        for (var index = 0; index < ViewModel.PageCount; index++)
        {
            var element = ContinuousRepeater.TryGetElement(index);
            if (element is null)
                continue;
            var point = element.TransformToVisual(ContinuousScrollViewer)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var distance = Math.Abs(point.Y);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }

        await ViewModel.UpdateContinuousProgressAsync(bestIndex);
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        _zoomSaveTimer.Stop();
        _lookupCts?.Cancel();
        _lookupCts?.Dispose();
        _lookupCts = null;
        if (_popupOverlay != null)
        {
            _popupOverlay.MiningFeedbackRequested -= OnMiningFeedbackRequested;
            _popupOverlay.MiningFeedbackCleared -= OnMiningFeedbackCleared;
        }
        _popupOverlay?.Dispose();
        _popupOverlay = null;
        _miningFeedbackPresenter.Dispose();
        await ViewModel.SaveAsync();
        ReadingStateSaved?.Invoke(this, EventArgs.Empty);
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.ReaderStateChanged -= ViewModel_ReaderStateChanged;
    }
}
