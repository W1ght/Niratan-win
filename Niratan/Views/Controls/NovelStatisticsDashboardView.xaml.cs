using System;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Niratan.Helpers;
using Niratan.ViewModels.Components;
using Niratan.ViewModels.Pages;
using Serilog;

namespace Niratan.Views.Controls;

public sealed partial class NovelStatisticsDashboardView : UserControl
{
    private NovelStatisticsDashboardLayoutMode? _layoutMode;
    private CancellationTokenSource? _bookDetailCts;
    private ContentDialog? _bookStatisticsDialog;

    public NovelStatisticsDashboardView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyAdaptiveLayout(ActualWidth);
        SizeChanged += (_, args) => ApplyAdaptiveLayout(args.NewSize.Width);
        Unloaded += (_, _) => CloseBookStatisticsDialog();
    }

    private void MoreBooks_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is NovelStatisticsDashboardViewModel viewModel)
            viewModel.ShowMoreBookRankings();
    }

    private async void BookRanking_Click(object sender, RoutedEventArgs e)
    {
        if (_bookStatisticsDialog != null
            || DataContext is not NovelStatisticsDashboardViewModel viewModel
            || sender is not FrameworkElement { DataContext: NovelStatisticsBookRankingItemViewModel item }
            || XamlRoot is not { } xamlRoot)
        {
            return;
        }

        _bookDetailCts?.Cancel();
        _bookDetailCts?.Dispose();
        var cts = new CancellationTokenSource();
        _bookDetailCts = cts;
        var contentWidth = Math.Min(
            980,
            Math.Max(280, xamlRoot.Size.Width - 112));
        var contentHeight = Math.Min(
            680,
            Math.Max(360, xamlRoot.Size.Height - 190));
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = ResourceStringHelper.GetString(
                "NovelStatisticsBookDetailDialogTitle",
                "Book Statistics"),
            CloseButtonText = ResourceStringHelper.GetString(
                "NovelStatisticsBookDetailClose",
                "Close"),
            DefaultButton = ContentDialogButton.Close,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = Math.Min(contentWidth + 48, Math.Max(0, xamlRoot.Size.Width - 32)),
            MinHeight = Math.Min(contentHeight + 96, Math.Max(0, xamlRoot.Size.Height - 32)),
            MinWidth = 0,
            MaxWidth = Math.Max(0, xamlRoot.Size.Width - 32),
            MaxHeight = Math.Max(0, xamlRoot.Size.Height - 32),
            Content = CreateBookDetailLoadingContent(contentWidth, contentHeight),
        };
        dialog.Resources["ContentDialogMaxWidth"] = Math.Min(
            contentWidth + 96,
            Math.Max(0, xamlRoot.Size.Width - 24));
        dialog.Resources["ContentDialogMaxHeight"] = Math.Min(
            contentHeight + 128,
            Math.Max(0, xamlRoot.Size.Height - 24));
        _bookStatisticsDialog = dialog;

        void OnDialogClosed(ContentDialog _, ContentDialogClosedEventArgs __) =>
            cts.Cancel();

        dialog.Closed += OnDialogClosed;
        try
        {
            var showOperation = dialog.ShowAsync();
            NovelStatisticsBookDetailViewModel? detail;
            try
            {
                detail = await viewModel.LoadBookStatisticsAsync(item.Id, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Statistics] Failed to load book statistics detail");
                if (ReferenceEquals(_bookStatisticsDialog, dialog)
                    && !cts.IsCancellationRequested)
                {
                    dialog.Content = CreateBookDetailFailureContent();
                    await showOperation;
                }
                return;
            }

            if (!ReferenceEquals(_bookStatisticsDialog, dialog)
                || cts.IsCancellationRequested)
            {
                return;
            }

            if (detail == null)
            {
                dialog.Content = CreateBookDetailFailureContent();
                await showOperation;
                return;
            }

            dialog.Content = new NovelStatisticsBookDetailPanel(
                viewModel,
                item.Id,
                detail)
            {
                Width = contentWidth,
                Height = contentHeight,
            };
            await showOperation;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Statistics] Failed to open book statistics detail");
        }
        finally
        {
            dialog.Closed -= OnDialogClosed;
            if (ReferenceEquals(_bookStatisticsDialog, dialog))
                _bookStatisticsDialog = null;
            if (ReferenceEquals(_bookDetailCts, cts))
                _bookDetailCts = null;
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void BookCover_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is FrameworkElement
            {
                DataContext: NovelStatisticsBookRankingItemViewModel item,
            })
        {
            item.MarkCoverFailed();
        }
    }

    private static FrameworkElement CreateBookDetailLoadingContent(
        double width,
        double height)
    {
        var text = ResourceStringHelper.GetString(
            "NovelStatisticsBookDetailLoading",
            "Loading book statistics…");
        var content = new StackPanel
        {
            MinWidth = 0,
            Width = width,
            Height = height,
            Padding = new Thickness(24),
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(
            content,
            "NovelStatisticsBookDetailLoadingStatus");
        AutomationProperties.SetName(content, text);
        content.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 32,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = text,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        return content;
    }

    private static FrameworkElement CreateBookDetailFailureContent()
    {
        var message = ResourceStringHelper.GetString(
            "NovelStatisticsBookDetailUnavailable",
            "This book's statistics could not be read. The sidecar was left unchanged.");
        return new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Message = message,
        };
    }

    private void CloseBookStatisticsDialog()
    {
        _bookDetailCts?.Cancel();
        _bookStatisticsDialog?.Hide();
    }

    private void ApplyAdaptiveLayout(double width)
    {
        var layoutMode = NovelStatisticsDashboardLayout.Select(width);
        if (_layoutMode == layoutMode)
            return;
        _layoutMode = layoutMode;

        if (layoutMode == NovelStatisticsDashboardLayoutMode.Wide)
        {
            SetColumnCount(3);
            Place(TodayCard, 0, 0);
            Place(GoalCard, 1, 0);
            Place(WeekCard, 2, 0);
            Place(CalendarCard, 0, 1);
            Place(ShelfCard, 1, 1);
            Place(SelectedRangeCard, 0, 2);
            Place(SpeedCard, 1, 2);
            Place(RankingCard, 2, 1, 2);
            return;
        }

        if (layoutMode == NovelStatisticsDashboardLayoutMode.Medium)
        {
            SetColumnCount(2);
            Place(TodayCard, 0, 0);
            Place(GoalCard, 1, 0);
            Place(WeekCard, 2, 0);
            Place(CalendarCard, 3, 0);
            Place(ShelfCard, 4, 0);
            Place(SelectedRangeCard, 0, 1);
            Place(SpeedCard, 1, 1);
            Place(RankingCard, 2, 1);
            return;
        }

        SetColumnCount(1);
        Place(TodayCard, 0, 0);
        Place(GoalCard, 1, 0);
        Place(WeekCard, 2, 0);
        Place(CalendarCard, 3, 0);
        Place(SelectedRangeCard, 4, 0);
        Place(SpeedCard, 5, 0);
        Place(RankingCard, 6, 0);
        Place(ShelfCard, 7, 0);
    }

    private void SetColumnCount(int count)
    {
        DashboardPrimaryColumn.Width = new GridLength(1, GridUnitType.Star);
        DashboardSecondaryColumn.Width = count >= 2
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        DashboardTertiaryColumn.Width = count >= 3
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
    }

    private static void Place(
        FrameworkElement card,
        int row,
        int column,
        int columnSpan = 1)
    {
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        Grid.SetColumnSpan(card, columnSpan);
    }
}
