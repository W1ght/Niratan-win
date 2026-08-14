using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Niratan.Helpers;
using Niratan.ViewModels.Components;
using Niratan.ViewModels.Pages;

namespace Niratan.Views.Controls;

public sealed partial class NovelStatisticsBookDetailPanel : UserControl
{
    private const double CompactMetricsBreakpoint = 700;
    private const double CompactEditorBreakpoint = 820;
    private readonly NovelStatisticsDashboardViewModel? _dashboardViewModel;
    private readonly string? _bookId;
    private CancellationTokenSource? _mutationCts;
    private bool? _usesCompactMetricsLayout;
    private bool? _usesCompactEditorLayout;
    private DeleteRequest _deleteRequest;

    public NovelStatisticsBookDetailPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _mutationCts?.Cancel();
            _mutationCts?.Dispose();
            _mutationCts = null;
        };
        SizeChanged += (_, args) => ApplyAdaptiveLayout(args.NewSize.Width);
    }

    public NovelStatisticsBookDetailPanel(
        NovelStatisticsDashboardViewModel dashboardViewModel,
        string bookId,
        NovelStatisticsBookDetailViewModel viewModel)
        : this()
    {
        _dashboardViewModel = dashboardViewModel;
        _bookId = bookId;
        ApplyDetail(viewModel, preferredDateKey: null);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyAdaptiveLayout(ActualWidth);
        if (BookStatisticsDaysList.SelectedItem == null
            && DataContext is NovelStatisticsBookDetailViewModel detail)
        {
            SelectDay(detail.Days.LastOrDefault());
        }
    }

    private void BookCover_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (DataContext is NovelStatisticsBookDetailViewModel viewModel)
            viewModel.Book.MarkCoverFailed();
    }

    private void BookStatisticsDaysList_ItemClick(
        object sender,
        ItemClickEventArgs e) =>
        SelectDay(e.ClickedItem as NovelStatisticsBookDayDisplay);

    private async void SaveSelectedDay_Click(object sender, RoutedEventArgs e)
    {
        if (BookStatisticsDaysList.SelectedItem is not NovelStatisticsBookDayDisplay day
            || _dashboardViewModel == null
            || _bookId == null)
        {
            return;
        }

        await RunMutationAsync(
            ct => _dashboardViewModel.UpdateBookStatisticsDayAsync(
                _bookId,
                day.DateKey,
                NonNegativeInt(CharactersInput.Value),
                NonNegativeInt(HoursInput.Value),
                Math.Clamp(NonNegativeInt(MinutesInput.Value), 0, 59),
                ct),
            day.DateKey);
    }

    private void DeleteSelectedDay_Click(object sender, RoutedEventArgs e)
    {
        if (BookStatisticsDaysList.SelectedItem is not NovelStatisticsBookDayDisplay)
            return;

        ShowDeleteConfirmation(
            DeleteRequest.SelectedDay,
            ResourceStringHelper.GetString(
                "NovelStatisticsBookDetailDeleteDayConfirmation",
                "Delete the selected day's statistics? This cannot be undone."));
    }

    private void DeleteAllStatistics_Click(object sender, RoutedEventArgs e) =>
        ShowDeleteConfirmation(
            DeleteRequest.All,
            ResourceStringHelper.GetString(
                "NovelStatisticsBookDetailDeleteAllConfirmation",
                "Delete all statistics for this book? This cannot be undone."));

    private void CancelDelete_Click(object sender, RoutedEventArgs e) =>
        HideDeleteConfirmation();

    private async void ConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_dashboardViewModel == null || _bookId == null)
            return;

        var request = _deleteRequest;
        HideDeleteConfirmation();
        if (request == DeleteRequest.SelectedDay
            && BookStatisticsDaysList.SelectedItem is NovelStatisticsBookDayDisplay day)
        {
            await RunMutationAsync(
                ct => _dashboardViewModel.DeleteBookStatisticsDayAsync(
                    _bookId,
                    day.DateKey,
                    ct),
                preferredDateKey: null);
        }
        else if (request == DeleteRequest.All)
        {
            await RunMutationAsync(
                ct => _dashboardViewModel.DeleteAllBookStatisticsAsync(_bookId, ct),
                preferredDateKey: null);
        }
    }

    private async Task RunMutationAsync(
        Func<CancellationToken, Task<NovelStatisticsBookDetailViewModel?>> mutation,
        string? preferredDateKey)
    {
        _mutationCts?.Cancel();
        _mutationCts?.Dispose();
        var cts = new CancellationTokenSource();
        _mutationCts = cts;
        SetBusy(true);
        BookStatisticsErrorBar.IsOpen = false;
        try
        {
            var updated = await mutation(cts.Token);
            if (!cts.IsCancellationRequested && updated != null)
                ApplyDetail(updated, preferredDateKey);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                BookStatisticsErrorBar.Message = ResourceStringHelper.FormatString(
                    "NovelStatisticsBookDetailMutationFailedFormat",
                    "Unable to update statistics: {0}",
                    ex.Message);
                BookStatisticsErrorBar.IsOpen = true;
            }
        }
        finally
        {
            if (ReferenceEquals(_mutationCts, cts))
            {
                _mutationCts = null;
                SetBusy(false);
            }
            cts.Dispose();
        }
    }

    private void ApplyDetail(
        NovelStatisticsBookDetailViewModel detail,
        string? preferredDateKey)
    {
        DataContext = detail;
        var selected = preferredDateKey == null
            ? detail.Days.LastOrDefault()
            : detail.Days.FirstOrDefault(day =>
                string.Equals(day.DateKey, preferredDateKey, StringComparison.Ordinal))
                ?? detail.Days.LastOrDefault();
        SelectDay(selected);
        NoDaysPanel.Visibility = detail.HasNoDays
            ? Visibility.Visible
            : Visibility.Collapsed;
        BookStatisticsDaysList.Visibility = detail.HasDays
            ? Visibility.Visible
            : Visibility.Collapsed;
        DeleteAllStatisticsButton.IsEnabled = detail.HasDays;
    }

    private void SelectDay(NovelStatisticsBookDayDisplay? day)
    {
        BookStatisticsDaysList.SelectedItem = day;
        var hasSelection = day != null;
        BookStatisticsEditorContent.Visibility = hasSelection
            ? Visibility.Visible
            : Visibility.Collapsed;
        NoSelectionPanel.Visibility = hasSelection
            ? Visibility.Collapsed
            : Visibility.Visible;
        HideDeleteConfirmation();
        if (day == null)
            return;

        SelectedDateText.Text = day.DateText;
        CharactersInput.Value = day.CharactersRead;
        var totalMinutes = Math.Max((int)Math.Round(day.ReadingTime / 60d), 0);
        HoursInput.Value = totalMinutes / 60;
        MinutesInput.Value = totalMinutes % 60;
    }

    private void ShowDeleteConfirmation(DeleteRequest request, string text)
    {
        _deleteRequest = request;
        DeleteConfirmationText.Text = text;
        DeleteConfirmationPanel.Visibility = Visibility.Visible;
    }

    private void HideDeleteConfirmation()
    {
        _deleteRequest = DeleteRequest.None;
        DeleteConfirmationPanel.Visibility = Visibility.Collapsed;
    }

    private void SetBusy(bool busy)
    {
        BookStatisticsEditorContent.IsHitTestVisible = !busy;
        BookStatisticsEditorContent.Opacity = busy ? 0.55 : 1;
        BookStatisticsDaysList.IsEnabled = !busy;
        MutationProgressRing.IsActive = busy;
        MutationProgressRing.Visibility = busy
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyAdaptiveLayout(double width)
    {
        ApplyMetricsLayout(width < CompactMetricsBreakpoint);
        ApplyEditorLayout(width < CompactEditorBreakpoint);
    }

    private void ApplyMetricsLayout(bool compact)
    {
        if (_usesCompactMetricsLayout == compact)
            return;
        _usesCompactMetricsLayout = compact;
        BookStatisticsMetricsGrid.ColumnDefinitions.Clear();
        BookStatisticsMetricsGrid.RowDefinitions.Clear();
        for (var index = 0; index < (compact ? 2 : 4); index++)
        {
            BookStatisticsMetricsGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (var index = 0; index < (compact ? 2 : 1); index++)
            BookStatisticsMetricsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        BookStatisticsMetricsGrid.RowSpacing = compact ? 10 : 0;
        PlaceMetric(CharactersMetricCard, 0, 0);
        PlaceMetric(DurationMetricCard, 0, 1);
        PlaceMetric(SpeedMetricCard, compact ? 1 : 0, compact ? 0 : 2);
        PlaceMetric(ActiveDaysMetricCard, compact ? 1 : 0, compact ? 1 : 3);
    }

    private void ApplyEditorLayout(bool compact)
    {
        if (_usesCompactEditorLayout == compact)
            return;
        _usesCompactEditorLayout = compact;
        BookStatisticsEditorGrid.ColumnDefinitions.Clear();
        BookStatisticsEditorGrid.RowDefinitions.Clear();
        if (compact)
        {
            BookStatisticsEditorGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            BookStatisticsEditorGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(260) });
            BookStatisticsEditorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            BookStatisticsEditorGrid.RowSpacing = 14;
            Grid.SetRow(DaysPane, 0);
            Grid.SetColumn(DaysPane, 0);
            Grid.SetRow(EditorPane, 1);
            Grid.SetColumn(EditorPane, 0);
            return;
        }

        BookStatisticsEditorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        BookStatisticsEditorGrid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        BookStatisticsEditorGrid.RowDefinitions.Add(
            new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        BookStatisticsEditorGrid.RowSpacing = 0;
        Grid.SetRow(DaysPane, 0);
        Grid.SetColumn(DaysPane, 0);
        Grid.SetRow(EditorPane, 0);
        Grid.SetColumn(EditorPane, 1);
    }

    private static void PlaceMetric(FrameworkElement card, int row, int column)
    {
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
    }

    private static int NonNegativeInt(double value)
    {
        if (double.IsNaN(value) || value <= 0)
            return 0;
        return value >= int.MaxValue ? int.MaxValue : (int)Math.Round(value);
    }

    private enum DeleteRequest
    {
        None,
        SelectedDay,
        All,
    }
}
