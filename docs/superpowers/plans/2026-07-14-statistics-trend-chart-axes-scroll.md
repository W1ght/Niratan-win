# Statistics Trend Chart Axes and Range Scrolling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Dashboard anchor-date picker with a synchronized historical range slider and render readable numeric axes in a fixed-height trend chart.

**Architecture:** A pure calculator builds ordered calendar-aligned date windows. `NovelStatisticsDashboardViewModel` owns the selected discrete window and display-ready Y-axis ticks, while the WinUI view binds an always-visible horizontal integer-snapping `Slider` and the UI-only chart control renders axes around the existing points. All Dashboard calculations continue to consume one `NovelStatisticsDateRange`; statistics sidecars, cache, SQLite, and Google Drive contracts do not change.

**Tech Stack:** C#/.NET 10, WinUI 3, Windows App SDK, CommunityToolkit.Mvvm, xUnit v3, FluentAssertions.

## Global Constraints

- Target Windows 10+ x64; do not build ARM64.
- Preserve the View → ViewModel → Service layering and keep chart drawing UI-only.
- Do not modify `native/hoshidicts/` or any Niratan reference repository.
- The trend chart height is exactly 260 effective pixels.
- The anchor-date picker and public `AnchorDate` ViewModel property are removed.
- The range slider selects discrete day, Monday-aligned week, calendar-month, or recent-year windows and synchronizes every Dashboard card.
- No new package dependency and no statistics/cache/Google Drive schema change.

---

### Task 1: Calendar-aligned selectable date windows

**Files:**
- Modify: `Niratan/Services/Novels/NovelStatisticsDashboardCalculator.cs`
- Test: `Niratan.Tests/Services/Novels/NovelStatisticsDashboardSummaryTests.cs`

**Interfaces:**
- Produces: `NovelStatisticsDashboardCalculator.SelectableRanges(NovelStatisticsRangeMode mode, NovelStatisticsDateRange window) : IReadOnlyList<NovelStatisticsDateRange>`
- Consumes: existing `NovelStatisticsRangeMode`, `NovelStatisticsDateRange`, and Monday week alignment.

- [ ] **Step 1: Write failing range-window tests**

Add tests that assert exact results rather than only counts:

```csharp
[Fact]
public void SelectableRanges_ReturnOrderedClippedCalendarPeriods()
{
    var window = new NovelStatisticsDateRange(new(2026, 1, 29), new(2026, 3, 3));

    NovelStatisticsDashboardCalculator.SelectableRanges(
        NovelStatisticsRangeMode.Month, window).Should().Equal(
        new NovelStatisticsDateRange(new(2026, 1, 29), new(2026, 1, 31)),
        new NovelStatisticsDateRange(new(2026, 2, 1), new(2026, 2, 28)),
        new NovelStatisticsDateRange(new(2026, 3, 1), new(2026, 3, 3)));

    NovelStatisticsDashboardCalculator.SelectableRanges(
        NovelStatisticsRangeMode.Week, new(new(2026, 7, 1), new(2026, 7, 14)))
        .Should().Equal(
            new NovelStatisticsDateRange(new(2026, 7, 1), new(2026, 7, 5)),
            new NovelStatisticsDateRange(new(2026, 7, 6), new(2026, 7, 12)),
            new NovelStatisticsDateRange(new(2026, 7, 13), new(2026, 7, 14)));
}

[Theory]
[InlineData(NovelStatisticsRangeMode.Year, 1)]
[InlineData(NovelStatisticsRangeMode.Day, 3)]
public void SelectableRanges_HandleWholeWindowAndDailySteps(
    NovelStatisticsRangeMode mode,
    int expectedCount)
{
    var window = new NovelStatisticsDateRange(new(2026, 7, 1), new(2026, 7, 3));
    var ranges = NovelStatisticsDashboardCalculator.SelectableRanges(mode, window);
    ranges.Should().HaveCount(expectedCount);
    ranges[0].Start.Should().Be(window.Start);
    ranges[^1].End.Should().Be(window.End);
}
```

- [ ] **Step 2: Run the focused tests and confirm RED**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardSummaryTests"
```

Expected: compile failure because `SelectableRanges` does not exist.

- [ ] **Step 3: Implement the pure range generator**

Add a method that returns `[]` for an invalid `DateOnly.MinValue` window, returns `[window]` for `Year`, and otherwise advances by day, Monday-aligned week, or month. Use the existing `SelectedRange` method to clip boundaries and suppress duplicate clipped ranges:

```csharp
public static IReadOnlyList<NovelStatisticsDateRange> SelectableRanges(
    NovelStatisticsRangeMode mode,
    NovelStatisticsDateRange window)
{
    if (window.Start == DateOnly.MinValue || window.End < window.Start)
        return [];
    if (mode == NovelStatisticsRangeMode.Year)
        return [window];

    var cursor = mode switch
    {
        NovelStatisticsRangeMode.Month => new DateOnly(window.Start.Year, window.Start.Month, 1),
        NovelStatisticsRangeMode.Week => MondayStartOfWeek(window.Start),
        _ => window.Start,
    };
    var ranges = new List<NovelStatisticsDateRange>();
    while (cursor <= window.End)
    {
        var range = SelectedRange(mode, cursor, window);
        if (ranges.Count == 0 || ranges[^1] != range)
            ranges.Add(range);
        cursor = mode switch
        {
            NovelStatisticsRangeMode.Month => cursor.AddMonths(1),
            NovelStatisticsRangeMode.Week => cursor.AddDays(7),
            _ => cursor.AddDays(1),
        };
    }
    return ranges;
}
```

- [ ] **Step 4: Run the focused tests and confirm GREEN**

Run the command from Step 2. Expected: all summary tests pass.

- [ ] **Step 5: Commit Task 1**

```powershell
git add -- Niratan/Services/Novels/NovelStatisticsDashboardCalculator.cs Niratan.Tests/Services/Novels/NovelStatisticsDashboardSummaryTests.cs
git commit -m "feat(statistics): add scrollable calendar ranges"
```

---

### Task 2: ViewModel range scrolling and axis projections

**Files:**
- Modify: `Niratan/Models/Novel/NovelStatisticsDashboardModels.cs`
- Modify: `Niratan/ViewModels/Pages/NovelStatisticsDashboardViewModel.cs`
- Test: `Niratan.Tests/ViewModels/Pages/NovelStatisticsDashboardViewModelTests.cs`
- Test: `Niratan.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`

**Interfaces:**
- Consumes: `SelectableRanges(...)` from Task 1.
- Produces: `NovelStatisticsAxisTickDisplay(double NormalizedValue, string Label)`.
- Produces ViewModel bindings: `SelectedRangeOffsetValue`, `RangeScrollMaximum`, `RangeScrollLargeChange`, `CanScrollRange`, `RangeScrollAccessibleText`, and `TrendAxisTicks`.

- [ ] **Step 1: Write failing ViewModel tests**

Replace the anchor-based calendar assertion and add scrolling/axis coverage:

```csharp
[Fact]
public async Task RangeScrollbar_DefaultsNewestAndMovesEveryProjection()
{
    var sut = CreateSut(out _, out _);
    await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);
    sut.SelectedRangeMode = NovelStatisticsRangeMode.Day;

    sut.SelectedRangeOffsetValue.Should().Be(sut.RangeScrollMaximum);
    sut.SelectedDateRange.Should().Be(new NovelStatisticsDateRange(Today, Today));
    sut.RangeText.Should().Contain("1,200");

    sut.SelectedRangeOffsetValue = sut.RangeScrollMaximum - 1;

    sut.SelectedDateRange.Should().Be(new NovelStatisticsDateRange(Today.AddDays(-1), Today.AddDays(-1)));
    sut.RangeText.Should().Contain("600");
    sut.TrendPoints.Should().ContainSingle();
    sut.BookRankingRows.Should().ContainSingle(row => row.Id == "b");
}

[Fact]
public async Task CalendarSelection_MovesScrollbarToContainingPeriodAndUpdatesDetail()
{
    var sut = CreateSut(out _, out _);
    await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);
    sut.SelectedRangeMode = NovelStatisticsRangeMode.Day;

    sut.SelectedCalendarDay = sut.CalendarDays.Single(day => day.Characters == 600);

    sut.SelectedDateRange.Start.Should().Be(Today.AddDays(-1));
    sut.SelectedRangeOffsetValue.Should().Be(sut.RangeScrollMaximum - 1);
    sut.CalendarDetail.Characters.Should().Be(600);
}

[Theory]
[InlineData(NovelStatisticsTrendMetric.Characters, "chars")]
[InlineData(NovelStatisticsTrendMetric.Duration, "m")]
[InlineData(NovelStatisticsTrendMetric.Speed, "/ h")]
public async Task TrendAxis_ExposesFiveMetricSpecificTicks(
    NovelStatisticsTrendMetric metric,
    string expectedUnit)
{
    var sut = CreateSut(out _, out _);
    await sut.ActivateAsync(Books(), Shelves(), CancellationToken.None);
    sut.SelectedTrendMetric = metric;

    sut.TrendAxisTicks.Should().HaveCount(5);
    sut.TrendAxisTicks.Select(tick => tick.NormalizedValue).Should().BeInAscendingOrder();
    sut.TrendAxisTicks[0].NormalizedValue.Should().Be(0);
    sut.TrendAxisTicks[^1].NormalizedValue.Should().Be(1);
    sut.TrendAxisTicks.Should().Contain(tick => tick.Label.Contains(expectedUnit));
}
```

Update `NovelLibraryPageViewModelTests.StatisticsControls_ReprojectRangeMetricsCalendarAndCorruptWarning` to select `Day`, rely on the newest default offset, and remove the `AnchorDate` assignment.

- [ ] **Step 2: Run focused ViewModel tests and confirm RED**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardViewModelTests|FullyQualifiedName~StatisticsControls_ReprojectRangeMetricsCalendarAndCorruptWarning"
```

Expected: compile failures for the new range-slider and tick properties and for the removed behavior expectation.

- [ ] **Step 3: Add display model and discrete window state**

Add:

```csharp
public sealed record NovelStatisticsAxisTickDisplay(
    double NormalizedValue,
    string Label);
```

Replace `AnchorDate` with a private range list and integer offset adapter:

```csharp
private IReadOnlyList<NovelStatisticsDateRange> _selectableRanges = [];
private bool _isUpdatingProjection;

[ObservableProperty]
public partial int SelectedRangeOffset { get; set; }

public double SelectedRangeOffsetValue
{
    get => SelectedRangeOffset;
    set => SelectedRangeOffset = Math.Clamp(
        (int)Math.Round(value), 0, Math.Max(_selectableRanges.Count - 1, 0));
}

public double RangeScrollMaximum => Math.Max(_selectableRanges.Count - 1, 0);
public double RangeScrollLargeChange => SelectedRangeMode switch
{
    NovelStatisticsRangeMode.Day => 7,
    NovelStatisticsRangeMode.Week => 4,
    NovelStatisticsRangeMode.Month => 3,
    _ => 1,
};
public bool CanScrollRange => _selectableRanges.Count > 1;
public string RangeScrollAccessibleText => RangeTitle;

[ObservableProperty]
public partial ObservableCollection<NovelStatisticsAxisTickDisplay> TrendAxisTicks { get; set; } = [];
```

Implement one helper that regenerates ranges, selects the newest period on mode changes, and preserves the prior period start on snapshot refresh. Raise property changes for every range-slider binding.

- [ ] **Step 4: Resolve the selected range before every Dashboard projection**

In `Recalculate`, use `_selectableRanges[SelectedRangeOffset]` instead of `SelectedRange(mode, anchor, window)`. On a calendar selection, find the range containing the day, update `SelectedRangeOffset`, recalculate, and then update detail. Guard programmatic `SelectedCalendarDay` changes with `_isUpdatingProjection` so projection refreshes cannot recursively move the range.

Create five axis ticks after computing `trendMaximum`:

```csharp
TrendAxisTicks = new(Enumerable.Range(0, 5).Select(index =>
{
    var normalized = index / 4d;
    return new NovelStatisticsAxisTickDisplay(
        normalized,
        FormatTrendAxisValue(trendMaximum * normalized, SelectedTrendMetric));
}));
```

Use compact localized `k`/`M` character and speed labels, and `m`/`h` duration labels. Keep the existing full value formatting for tooltips.

- [ ] **Step 5: Run focused tests and confirm GREEN**

Run the command from Step 2. Expected: all selected tests pass.

- [ ] **Step 6: Commit Task 2**

```powershell
git add -- Niratan/Models/Novel/NovelStatisticsDashboardModels.cs Niratan/ViewModels/Pages/NovelStatisticsDashboardViewModel.cs Niratan.Tests/ViewModels/Pages/NovelStatisticsDashboardViewModelTests.cs Niratan.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs
git commit -m "feat(statistics): scroll synchronized date windows"
```

---

### Task 3: Fixed-height chart, numeric axes, and horizontal range slider

**Files:**
- Modify: `Niratan/Views/Controls/NovelStatisticsTrendChart.xaml`
- Modify: `Niratan/Views/Controls/NovelStatisticsTrendChart.xaml.cs`
- Modify: `Niratan/Views/Controls/NovelStatisticsDashboardView.xaml`
- Modify: `Niratan/Strings/en-US/Resources.resw`
- Modify: `Niratan/Strings/zh-CN/Resources.resw`
- Test: `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Consumes: `TrendAxisTicks` and range-slider properties from Task 2.
- Produces: `AxisTicks` dependency property on `NovelStatisticsTrendChart`.
- Produces automation id: `NovelStatisticsRangeScrollBar`.

- [ ] **Step 1: Write failing XAML/control contract assertions**

Extend `NovelLibraryPage_ExposesStatisticsDashboard`:

```csharp
dashboardXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelStatisticsRangeScrollBar\"");
dashboardXaml.Should().Contain("Value=\"{Binding SelectedRangeOffsetValue, Mode=TwoWay}\"");
dashboardXaml.Should().Contain("AutomationProperties.HelpText=\"{Binding RangeScrollAccessibleText}\"");
dashboardXaml.Should().Contain("AxisTicks=\"{Binding TrendAxisTicks}\"");
dashboardXaml.Should().NotContain("NovelStatisticsAnchorDate");
dashboardXaml.Should().NotContain("CalendarDatePicker");
trendChartXaml.Should().Contain("Height=\"260\"");
trendChartCode.Should().Contain("AxisTicksProperty");
trendChartCode.Should().Contain("DrawXAxisLabels");
trendChartCode.Should().Contain("DrawYAxisTicks");
```

Remove `NovelStatisticsAnchorLabel` from the required resource UID list and add `NovelStatisticsRangeScrollBar`.

- [ ] **Step 2: Run the asset test and confirm RED**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPage_ExposesStatisticsDashboard"
```

Expected: assertions fail because the anchor picker still exists and axes/range slider do not.

- [ ] **Step 3: Replace the anchor picker with the range slider**

Delete the anchor label and `CalendarDatePicker`. Bind the chart and always-visible range slider:

```xml
<controls:NovelStatisticsTrendChart x:Uid="NovelStatisticsTrendChartControl"
                                    Height="260"
                                    ItemsSource="{Binding TrendPoints}"
                                    AxisTicks="{Binding TrendAxisTicks}"
                                    ChartStyle="{Binding SelectedTrendStyle}" />
<Slider x:Uid="NovelStatisticsRangeScrollBar"
        AutomationProperties.AutomationId="NovelStatisticsRangeScrollBar"
        AutomationProperties.HelpText="{Binding RangeScrollAccessibleText}"
        Orientation="Horizontal"
        Minimum="0"
        Maximum="{Binding RangeScrollMaximum}"
        StepFrequency="1"
        SnapsTo="StepValues"
        SmallChange="1"
        LargeChange="{Binding RangeScrollLargeChange}"
        IsEnabled="{Binding CanScrollRange}"
        Value="{Binding SelectedRangeOffsetValue, Mode=TwoWay}" />
```

Add localized accessible names in both resource files and remove the unused anchor-label resources.

- [ ] **Step 4: Render axes around a stable plot rectangle**

Change the chart root from `MinHeight="220"` to `Height="260"`. Register `AxisTicks` as an `IEnumerable` dependency property. Use constants for an 88-pixel left gutter, 8-pixel right/top gutters, and 28-pixel bottom gutter. Draw the five Y grid lines and their real `TextBlock` labels at `plot.Top + (1 - tick.NormalizedValue) * plot.Height`.

Draw the first, middle, and last distinct X labels at the plot bottom. Update bars and lines to use the plot rectangle rather than the full canvas:

```csharp
var plot = new Rect(
    LeftGutter,
    TopGutter,
    Math.Max(width - LeftGutter - RightGutter, 0),
    Math.Max(height - TopGutter - BottomGutter, 0));
DrawYAxisTicks(plot, axisTicks, gridBrush);
DrawXAxisLabels(points, plot);
```

Keep tooltip and automation metadata on bars/markers and keep the hidden point accessibility list.

- [ ] **Step 5: Run asset and Dashboard tests and confirm GREEN**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPage_ExposesStatisticsDashboard|FullyQualifiedName~NovelStatisticsDashboard"
```

Expected: all matching tests pass.

- [ ] **Step 6: Commit Task 3**

```powershell
git add -- Niratan/Views/Controls/NovelStatisticsTrendChart.xaml Niratan/Views/Controls/NovelStatisticsTrendChart.xaml.cs Niratan/Views/Controls/NovelStatisticsDashboardView.xaml Niratan/Strings/en-US/Resources.resw Niratan/Strings/zh-CN/Resources.resw Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "feat(statistics): label and scroll trend chart"
```

---

### Task 4: Documentation and end-to-end verification

**Files:**
- Modify: `docs/CHANGELOG.md`
- Modify: `docs/VERIFICATION.md`

**Interfaces:**
- Consumes the completed feature; produces no runtime API.

- [ ] **Step 1: Document behavior and verification coverage**

Add a changelog entry that records the prior limitations and the new fixed-height axes/range slider. Update Dashboard verification to require removal of the anchor picker, 260-pixel height, metric-aware Y labels, first/middle/last X labels, range synchronization, and disabled year behavior.

- [ ] **Step 2: Run focused and full automated verification**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboard|FullyQualifiedName~NovelLibraryPage_ExposesStatisticsDashboard"
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64
dotnet build -p:Platform=x64
git diff --check
```

Expected: zero failed tests, zero build errors, and no whitespace errors. The known `SQLitePCLRaw.lib.e_sqlite3` NU1903 warning may remain.

- [ ] **Step 3: Run Niratan and verify the real Dashboard**

Run `./build-and-run.ps1`, open Statistics, and verify the scenarios in the design specification. Confirm `かがみの孤城` remains present in the rebuilt Dashboard cache and its 153,371 imported characters are not changed by scrolling.

- [ ] **Step 4: Commit Task 4**

```powershell
git add -- docs/CHANGELOG.md docs/VERIFICATION.md
git commit -m "docs: record statistics chart scrolling"
```

- [ ] **Step 5: Review branch scope**

Run:

```powershell
git status --short
git log --oneline main..HEAD
git diff --stat main...HEAD
```

Expected: clean feature worktree and only the cache-recovery plus trend-chart commits planned in this branch.
