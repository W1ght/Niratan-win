namespace Hoshi.Views.Controls;

public enum NovelStatisticsDashboardLayoutMode
{
    Narrow,
    Medium,
    Wide,
}

public static class NovelStatisticsDashboardLayout
{
    public static NovelStatisticsDashboardLayoutMode Select(double width) =>
        width >= 1260
            ? NovelStatisticsDashboardLayoutMode.Wide
            : width >= 840
                ? NovelStatisticsDashboardLayoutMode.Medium
                : NovelStatisticsDashboardLayoutMode.Narrow;
}
