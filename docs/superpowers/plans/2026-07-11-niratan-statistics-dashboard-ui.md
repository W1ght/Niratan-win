# Niratan Statistics Dashboard UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the embedded novel statistics panel with Niratan's full-page, adaptive, fully interactive Statistics Dashboard.

**Architecture:** Extract dashboard state and formatting from `NovelLibraryPageViewModel` into a dedicated `NovelStatisticsDashboardViewModel`. `NovelLibraryPage` switches exclusively between the bookshelf and a new `NovelStatisticsDashboardView`; the view uses native WinUI controls plus one narrow UI-only trend chart control, while the existing repository and pure calculator remain the source of all statistics.

**Tech Stack:** C# 14 / .NET 10, WinUI 3 / Windows App SDK, CommunityToolkit.Mvvm, System.Text.Json sidecars, xUnit v3, FluentAssertions, Moq.

## Global Constraints

- Match `docs/reference/hoshi/Niratan/Features/Bookshelf/StatisticsDashboardView.swift` and its full-page switch in `NativeReuseViews.swift`.
- Do not change `statistics.json`, `bookinfo.json`, `metadata.json`, `shelves.json`, or cache schemas.
- Do not add a chart package or any other dependency.
- ViewModels must not access SQLite, sidecar files, or WinUI controls directly.
- Only the video subsystem retains SQLite.
- Use one vertical dashboard scroll owner; selector rows and the calendar may scroll horizontally.
- Use theme resources and stable AutomationIds; all interactive controls must be keyboard accessible.
- Build and test x64 only.

---

### Task 1: Extract the Dashboard Presentation ViewModel

**Files:**
- Create: `Hoshi/ViewModels/Pages/NovelStatisticsDashboardViewModel.cs`
- Modify: `Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs`
- Modify: `Hoshi/Models/Novel/NovelStatisticsDashboardModels.cs`
- Modify: `Hoshi/App.xaml.cs`
- Create: `Hoshi.Tests/ViewModels/Pages/NovelStatisticsDashboardViewModelTests.cs`
- Modify: `Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`

**Interfaces:**
- Consumes: `INovelStatisticsDashboardService`, `ISettingsService`, visible `IReadOnlyList<NovelBook>`, and `NovelShelfState`.
- Produces: `ActivateAsync`, `Deactivate`, selector state, loading/error state, and display collections for every module.
- `NovelLibraryPageViewModel` produces `StatisticsDashboard`, `ShowBookshelf`, `ShowStatisticsDashboard`, `EnterStatisticsCommand`, and `ReturnToBookshelfCommand`.

- [ ] **Step 1: Write failing extraction and projection tests**

Add tests that construct the dashboard ViewModel directly and assert activation, selector isolation, loading flags, refresh events, goal persistence, calendar anchor changes, and formatting:

```csharp
[Fact]
public async Task ActivateAsync_ProjectsAllReferenceModules()
{
    var sut = CreateDashboard(snapshot);

    await sut.ActivateAsync([book], shelfState, CancellationToken.None);

    sut.HasData.Should().BeTrue();
    sut.Today.Should().NotBeNull();
    sut.WeekDays.Should().HaveCount(7);
    sut.SelectedRange.Should().NotBeNull();
    sut.SpeedMetrics.Should().HaveCount(6);
    sut.TrendPoints.Should().NotBeEmpty();
    sut.CalendarDays.Should().HaveCount(365);
    sut.BookRankingRows.Should().HaveCountLessThanOrEqualTo(12);
    sut.ShelfComparisonRows.Should().NotBeEmpty();
}

[Fact]
public async Task TrendStyleChange_DoesNotRecalculateRangeSummary()
{
    var sut = CreateDashboard(snapshot);
    await sut.ActivateAsync([book], shelfState, CancellationToken.None);
    var before = sut.SelectedRange;

    sut.SelectedTrendStyle = NovelStatisticsTrendChartStyle.Line;

    sut.SelectedRange.Should().BeSameAs(before);
    sut.SelectedTrendStyle.Should().Be(NovelStatisticsTrendChartStyle.Line);
}

[Fact]
public async Task CalendarSelection_UpdatesAnchorAndSelectedDetail()
{
    var sut = CreateDashboard(snapshot);
    await sut.ActivateAsync([book], shelfState, CancellationToken.None);

    sut.SelectedCalendarDay = sut.CalendarDays.Single(day => day.Characters > 0);

    DateOnly.FromDateTime(sut.AnchorDate!.Value.LocalDateTime)
        .Should().Be(sut.SelectedCalendarDay.Date);
    sut.CalendarDetail.Characters.Should().Be(sut.SelectedCalendarDay.Characters);
}
```

Update library tests so entering Statistics activates the child and returning deactivates it.

- [ ] **Step 2: Run the tests and verify RED**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardViewModelTests|FullyQualifiedName~NovelLibraryPageViewModelTests"
```

Expected: compilation failures because `NovelStatisticsDashboardViewModel`, `NovelStatisticsTrendChartStyle`, and the new parent commands do not exist.

- [ ] **Step 3: Add display records and the dedicated ViewModel**

Add the missing enum and immutable display records:

```csharp
public enum NovelStatisticsTrendChartStyle { Bar, Line }

public sealed record NovelStatisticsMetricDisplay(string Label, string Value);

public sealed record NovelStatisticsWeekDayDisplay(
    DateOnly Date,
    string Weekday,
    string PercentText,
    bool IsToday,
    bool IsFuture,
    bool MetTarget);

public sealed record NovelStatisticsTrendDisplayPoint(
    string Id,
    string Label,
    string ValueText,
    double NormalizedValue,
    string ToolTipText);

public sealed record NovelStatisticsCalendarDayDisplay(
    DateOnly Date,
    int Characters,
    double ReadingTime,
    int ActiveBookCount,
    string AccessibleText,
    double HeatOpacity,
    bool IsInSelectedRange,
    bool IsToday);

public sealed record NovelStatisticsBookRankingDisplayRow(
    string Id,
    string Title,
    string ValueText,
    double NormalizedValue);

public sealed record NovelStatisticsShelfComparisonDisplayRow(
    string Id,
    string Name,
    string DetailText,
    string SpeedText,
    double RecordedProgress,
    double NormalizedVolume);
```

Implement `NovelStatisticsDashboardViewModel` with this activation boundary:

```csharp
public async Task ActivateAsync(
    IReadOnlyList<NovelBook> books,
    NovelShelfState shelfState,
    CancellationToken ct)
{
    _books = books.ToArray();
    _shelfState = shelfState;
    _statisticsDashboardService.SnapshotRefreshed -= OnSnapshotRefreshed;
    _statisticsDashboardService.SnapshotRefreshed += OnSnapshotRefreshed;
    IsLoading = true;
    try
    {
        ApplySnapshot(await _statisticsDashboardService.LoadSnapshotAsync(_books, ct));
    }
    finally
    {
        IsLoading = false;
    }
}

public void Deactivate()
{
    _statisticsDashboardService.SnapshotRefreshed -= OnSnapshotRefreshed;
    IsRefreshing = false;
}
```

Move all dashboard properties, formatting, selector change handlers, and target persistence out of `NovelLibraryPageViewModel`. Preserve the existing pure calculator calls. Register the new ViewModel as transient in `App.xaml.cs` and inject it into the library ViewModel.

- [ ] **Step 4: Run extraction tests and verify GREEN**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardViewModelTests|FullyQualifiedName~NovelLibraryPageViewModelTests|FullyQualifiedName~NovelStatisticsDashboard"
```

Expected: all selected tests pass.

- [ ] **Step 5: Commit**

```powershell
git add Hoshi/ViewModels/Pages/NovelStatisticsDashboardViewModel.cs Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs Hoshi/Models/Novel/NovelStatisticsDashboardModels.cs Hoshi/App.xaml.cs Hoshi.Tests/ViewModels/Pages/NovelStatisticsDashboardViewModelTests.cs Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs
git commit -m "refactor(statistics): isolate dashboard presentation"
```

### Task 2: Replace the Embedded Panel with a Full-Page Surface

**Files:**
- Create: `Hoshi/Views/Controls/NovelStatisticsDashboardView.xaml`
- Create: `Hoshi/Views/Controls/NovelStatisticsDashboardView.xaml.cs`
- Modify: `Hoshi/Views/Pages/NovelLibraryPage.xaml`
- Modify: `Hoshi/Views/Pages/NovelLibraryPage.xaml.cs`
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Consumes: `NovelLibraryPageViewModel.StatisticsDashboard` and the two surface-switch commands.
- Produces: exclusive Bookshelf/Statistics visibility, a dashboard header, return command, loading/empty/corrupt states, and one vertical scroll owner.

- [ ] **Step 1: Write failing full-page contract tests**

Extend the asset test with exact assertions:

```csharp
libraryXaml.Should().Contain("Command=\"{x:Bind ViewModel.EnterStatisticsCommand}\"");
libraryXaml.Should().Contain("Command=\"{x:Bind ViewModel.ReturnToBookshelfCommand}\"");
libraryXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelStatisticsDashboardView\"");
libraryXaml.Should().Contain("Visibility=\"{x:Bind ViewModel.ShowBookshelf");
libraryXaml.Should().NotContain("AutomationProperties.AutomationId=\"NovelLibraryStatisticsDashboard\"");

dashboardXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelStatisticsDashboardScrollViewer\"");
dashboardXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelStatisticsLoadingStatus\"");
dashboardXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelStatisticsEmptyState\"");
dashboardXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelStatisticsCorruptWarning\"");
```

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPage_ExposesStatisticsDashboard"
```

Expected: failure because the dashboard is still embedded and the new view does not exist.

- [ ] **Step 3: Implement exclusive surface switching**

Replace the toggle with commands:

```xml
<AppBarButton AutomationProperties.AutomationId="NovelLibraryStatisticsButton"
              Command="{x:Bind ViewModel.EnterStatisticsCommand}">
    <AppBarButton.Icon><SymbolIcon Symbol="Document" /></AppBarButton.Icon>
</AppBarButton>

<AppBarButton AutomationProperties.AutomationId="NovelStatisticsBackToBookshelfButton"
              Command="{x:Bind ViewModel.ReturnToBookshelfCommand}"
              Visibility="{x:Bind ViewModel.ShowStatisticsDashboard, Converter={StaticResource BooleanToVisibilityConverter}, Mode=OneWay}">
    <AppBarButton.Icon><SymbolIcon Symbol="Library" /></AppBarButton.Icon>
</AppBarButton>
```

Wrap all existing shelf warnings and rails in a `Grid` bound to `ShowBookshelf`. Place `NovelStatisticsDashboardView` as a sibling bound to `ShowStatisticsDashboard`. Remove the embedded dashboard Border and all dashboard templates from `NovelLibraryPage.xaml`.

Start the new control with header, warning, loading overlay, empty state, and a single root `ScrollViewer`. Use `{Binding}` inside the control with the child ViewModel as DataContext; code-behind remains constructor-only.

- [ ] **Step 4: Build and run the contract test**

```powershell
dotnet build Hoshi/Hoshi.csproj -c Debug -p:Platform=x64
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPage_ExposesStatisticsDashboard"
```

Expected: build succeeds and the contract test passes.

- [ ] **Step 5: Commit**

```powershell
git add Hoshi/Views/Controls/NovelStatisticsDashboardView.xaml Hoshi/Views/Controls/NovelStatisticsDashboardView.xaml.cs Hoshi/Views/Pages/NovelLibraryPage.xaml Hoshi/Views/Pages/NovelLibraryPage.xaml.cs Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "feat(statistics): add full-page dashboard surface"
```

### Task 3: Add the Trend Chart and Summary Cards

**Files:**
- Create: `Hoshi/Views/Controls/NovelStatisticsTrendChart.xaml`
- Create: `Hoshi/Views/Controls/NovelStatisticsTrendChart.xaml.cs`
- Modify: `Hoshi/Views/Controls/NovelStatisticsDashboardView.xaml`
- Modify: `Hoshi/Models/Novel/NovelStatisticsDashboardModels.cs`
- Modify: `Hoshi/ViewModels/Pages/NovelStatisticsDashboardViewModel.cs`
- Modify: `Hoshi.Tests/ViewModels/Pages/NovelStatisticsDashboardViewModelTests.cs`
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Consumes: normalized trend display points and summary display records.
- Produces: Bar/Line trend visualization with accessible text plus Today, Goal, Week, Selected Range, and Speed cards.

- [ ] **Step 1: Write failing chart projection and module tests**

```csharp
[Theory]
[InlineData(NovelStatisticsTrendMetric.Characters, "chars")]
[InlineData(NovelStatisticsTrendMetric.Duration, "m")]
[InlineData(NovelStatisticsTrendMetric.Speed, "/ h")]
public async Task TrendMetric_ProjectsNormalizedValuesAndUnits(
    NovelStatisticsTrendMetric metric,
    string unit)
{
    var sut = await ActivatedDashboard();
    sut.SelectedTrendMetric = metric;

    sut.TrendPoints.Max(point => point.NormalizedValue).Should().Be(1);
    sut.TrendPoints.Should().OnlyContain(point => point.NormalizedValue is >= 0 and <= 1);
    sut.TrendPoints.Should().Contain(point => point.ValueText.Contains(unit));
}

[Fact]
public async Task SpeedCard_ExposesAllSixNiratanMetrics()
{
    var sut = await ActivatedDashboard();
    sut.SpeedMetrics.Select(metric => metric.Label).Should().Equal(
        "Weighted", "Median Active Day", "Last 7 Active Days",
        "Change", "Fastest", "Slowest");
}
```

Add static assertions for `NovelStatisticsTrendChartStyle`, `Polyline`, the five card AutomationIds, and all seven week cells.

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardViewModelTests|FullyQualifiedName~NovelLibraryPage_ExposesStatisticsDashboard"
```

Expected: projection/card assertions fail.

- [ ] **Step 3: Implement normalized chart projections**

For the selected metric, compute raw plot values and normalize by the maximum positive value:

```csharp
var values = trend.Select(TrendRawValue).ToArray();
var maximum = Math.Max(values.DefaultIfEmpty().Max(), 1);
TrendPoints = new(trend.Select((point, index) =>
    new NovelStatisticsTrendDisplayPoint(
        point.Id,
        point.Label,
        FormatTrendValue(point, SelectedTrendMetric),
        Math.Clamp(values[index] / maximum, 0, 1),
        BuildTrendToolTip(point))));
```

Project the Today ring percentage/fill, seven week cells, selected-range metrics, and all six speed metrics. Missing speed stays `—`.

- [ ] **Step 4: Implement the UI-only chart and cards**

`NovelStatisticsTrendChart` accepts `ItemsSource` and `ChartStyle` dependency properties. On size/items/style changes it clears a Canvas and renders either themed Rectangles or a Polyline plus point Ellipses. Each rendered point receives the display model's tooltip. A visually hidden but accessibility-visible ItemsControl exposes `Label` and `ValueText`.

Add the full-width Range & Trend card and native controls for Range, Anchor, Time Grain, Metric, and Style. Add the Today goal ring, Goal controls, seven-cell Week card, Selected Range metrics, and Speed Summary card with stable AutomationIds.

- [ ] **Step 5: Build, run focused tests, and commit**

```powershell
dotnet build Hoshi/Hoshi.csproj -c Debug -p:Platform=x64
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardViewModelTests|FullyQualifiedName~NovelLibraryPage_ExposesStatisticsDashboard"
git add Hoshi/Views/Controls/NovelStatisticsTrendChart.xaml Hoshi/Views/Controls/NovelStatisticsTrendChart.xaml.cs Hoshi/Views/Controls/NovelStatisticsDashboardView.xaml Hoshi/Models/Novel/NovelStatisticsDashboardModels.cs Hoshi/ViewModels/Pages/NovelStatisticsDashboardViewModel.cs Hoshi.Tests/ViewModels/Pages/NovelStatisticsDashboardViewModelTests.cs Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "feat(statistics): render niratan trend and summaries"
```

### Task 4: Add Calendar, Ranking, Shelves, and Adaptive Layout

**Files:**
- Modify: `Hoshi/Views/Controls/NovelStatisticsDashboardView.xaml`
- Modify: `Hoshi/ViewModels/Pages/NovelStatisticsDashboardViewModel.cs`
- Modify: `Hoshi/Models/Novel/NovelStatisticsDashboardModels.cs`
- Modify: `Hoshi.Tests/ViewModels/Pages/NovelStatisticsDashboardViewModelTests.cs`
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Consumes: calendar, ranking, and shelf display records.
- Produces: selectable heatmap, metric-relative bars, and wide/medium/narrow layout states.

- [ ] **Step 1: Write failing display and adaptive contract tests**

```csharp
[Fact]
public async Task Calendar_ProjectsRecentYearHeatAndSelectedRange()
{
    var sut = await ActivatedDashboard();

    sut.CalendarDays.Should().HaveCount(365);
    sut.CalendarDays.Should().OnlyContain(day => day.HeatOpacity is >= 0.08 and <= 1);
    sut.CalendarDays.Should().Contain(day => day.IsInSelectedRange);
    sut.CalendarDays.Single(day => day.Characters == sut.CalendarDays.Max(x => x.Characters))
        .HeatOpacity.Should().Be(1);
}

[Fact]
public async Task RankingAndShelves_NormalizeVisibleBars()
{
    var sut = await ActivatedDashboard();

    sut.BookRankingRows.Max(row => row.NormalizedValue).Should().Be(1);
    sut.ShelfComparisonRows.Max(row => row.NormalizedVolume).Should().Be(1);
    sut.ShelfComparisonRows.Should().OnlyContain(row => row.RecordedProgress is >= 0 and <= 1);
}
```

Static tests assert `WideDashboardState`, `MediumDashboardState`, `NarrowDashboardState`, thresholds `1260` and `840`, all three column definitions, horizontal calendar scrolling, and absence of nested vertical `ScrollViewer` controls.

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardViewModelTests|FullyQualifiedName~NovelLibraryPage_ExposesStatisticsDashboard"
```

Expected: heat/bar/adaptive assertions fail.

- [ ] **Step 3: Implement calendar, ranking, and shelf projections**

Use these normalization rules:

```csharp
var maxCharacters = Math.Max(calendar.Max(day => day.Characters), 1);
var heat = day.Characters <= 0
    ? 0.08
    : 0.16 + 0.84 * day.Characters / maxCharacters;

var recordedProgress = row.TotalBookCharacters <= 0
    ? 0
    : Math.Clamp(row.RecordedCharacters / (double)row.TotalBookCharacters, 0, 1);
```

Set `IsInSelectedRange` from the current range, use the selected ranking metric for ranking normalization, and normalize shelf volume by recorded characters.

- [ ] **Step 4: Implement the remaining cards and adaptive visual states**

Add:

- a seven-row horizontal `ListView`/`ItemsWrapGrid` heatmap with selected item binding, accessible date text, selected-range outline, and selected-day footer;
- ranking rows with formatted values and accent bars;
- shelf rows with count/detail/speed and two normalized bars;
- a three-column card Grid with `AdaptiveTrigger` states at 1260 and 840 effective pixels. Set each card's row, column, and span exactly as defined in the approved design; collapse the third column below 1260 and both extra columns below 840.

- [ ] **Step 5: Build, test, and commit**

```powershell
dotnet build Hoshi/Hoshi.csproj -c Debug -p:Platform=x64
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardViewModelTests|FullyQualifiedName~NovelLibraryPage_ExposesStatisticsDashboard"
git add Hoshi/Views/Controls/NovelStatisticsDashboardView.xaml Hoshi/ViewModels/Pages/NovelStatisticsDashboardViewModel.cs Hoshi/Models/Novel/NovelStatisticsDashboardModels.cs Hoshi.Tests/ViewModels/Pages/NovelStatisticsDashboardViewModelTests.cs Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "feat(statistics): complete adaptive dashboard modules"
```

### Task 5: Finalize Lifecycle, Accessibility, Documentation, and Runtime Verification

**Files:**
- Modify: `Hoshi/Views/Controls/NovelStatisticsDashboardView.xaml`
- Modify: `Hoshi/ViewModels/Pages/NovelStatisticsDashboardViewModel.cs`
- Modify: `Hoshi/Strings/en-US/Resources.resw`
- Modify: `Hoshi/Strings/zh-CN/Resources.resw`
- Modify: `Hoshi.Tests/ViewModels/Pages/NovelStatisticsDashboardViewModelTests.cs`
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`
- Modify: `docs/ARCHITECTURE.md`
- Modify: `docs/VERIFICATION.md`
- Modify: `docs/CHANGELOG.md`

**Interfaces:**
- Produces: complete localization, deterministic activation/deactivation, explicit refresh state, and final verification evidence.

- [ ] **Step 1: Write failing lifecycle and localization tests**

Add tests that cancel initial load, deactivate before a background refresh, retain cached data after refresh failure, and ensure only one refresh subscription is active. Add asset assertions for every new resource and AutomationId.

```csharp
[Fact]
public async Task Deactivate_IgnoresLaterSnapshotRefresh()
{
    var service = new RecordingDashboardService(snapshot);
    var sut = CreateDashboard(service);
    await sut.ActivateAsync([book], shelfState, CancellationToken.None);
    sut.Deactivate();

    service.Publish(replacementSnapshot);

    sut.Today.Characters.Should().Be(snapshot.Days.Sum(day => day.Characters));
}
```

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardViewModelTests|FullyQualifiedName~NovelReaderWebAssetTests"
```

Expected: lifecycle/resource assertions fail.

- [ ] **Step 3: Complete lifecycle and resources**

Use an activation generation plus a linked cancellation source so stale foreground loads and refresh events cannot replace a newer dashboard. Preserve the last valid snapshot on refresh failure. Add English and Simplified Chinese resources for headers, metrics, empty/loading/warning text, selector names, and accessibility labels. Replace all new hard-coded user-facing dashboard strings with `x:Uid` resources.

- [ ] **Step 4: Update architecture and verification documentation**

Document the parent/child ViewModel boundary, full-page switching, UI-only chart, adaptive thresholds, cache-refresh lifecycle, module list, and manual verification matrix. Changelog should record that the previous inline panel was a projection gap rather than a statistics-engine defect.

- [ ] **Step 5: Run complete verification**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
dotnet build Hoshi/Hoshi.csproj -c Debug -p:Platform=x64
git diff --check
```

Expected: every test passes; build has zero errors. The retained video SQLite dependency may emit the already documented `SQLitePCLRaw.lib.e_sqlite3` advisory.

Run `build-and-run.ps1`, verify a responsive `Hoshi` window, open Statistics, exercise selectors and calendar selection, and resize across 1260 and 840 effective pixels. Return to Bookshelf and confirm book rails remain interactive. Leave the final verified app instance running.

- [ ] **Step 6: Commit**

```powershell
git add Hoshi/Views/Controls/NovelStatisticsDashboardView.xaml Hoshi/ViewModels/Pages/NovelStatisticsDashboardViewModel.cs Hoshi/Strings/en-US/Resources.resw Hoshi/Strings/zh-CN/Resources.resw Hoshi.Tests/ViewModels/Pages/NovelStatisticsDashboardViewModelTests.cs Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs docs/ARCHITECTURE.md docs/VERIFICATION.md docs/CHANGELOG.md
git commit -m "docs: verify full niratan statistics dashboard"
```
