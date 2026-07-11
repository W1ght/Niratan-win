using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hoshi.Views.Controls;

public sealed partial class NovelStatisticsDashboardView : UserControl
{
    public NovelStatisticsDashboardView()
    {
        InitializeComponent();
        Loaded += (_, _) => QueueAdaptiveLayout();
        SizeChanged += (_, _) => QueueAdaptiveLayout();
    }

    private void QueueAdaptiveLayout() =>
        DispatcherQueue.TryEnqueue(() => ApplyAdaptiveLayout(ActualWidth));

    private void ApplyAdaptiveLayout(double width)
    {
        if (width >= 1260)
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

        if (width >= 840)
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
