# Niratan Statistics Dashboard UI Design

## Goal

Replace the current compact statistics panel embedded above the novel rails with a full-page, adaptive dashboard that matches Niratan's visible modules and interaction model while preserving the existing typed sidecar repository and pure calculator architecture.

## Reference and Scope

The behavioral and layout reference is `docs/reference/hoshi/Niratan/Features/Bookshelf/StatisticsDashboardView.swift` together with the dashboard switching behavior in `docs/reference/hoshi/Niratan/NativeMac/NativeReuseViews.swift`.

This design covers:

- switching between Bookshelf and Statistics inside the novel library surface;
- all Niratan dashboard modules and their interactive selectors;
- wide, medium, and narrow adaptive layouts;
- loading, empty, stale-cache-refresh, and corrupt-sidecar states;
- WinUI accessibility, keyboard navigation, and test automation;
- separation of dashboard projection from the bookshelf ViewModel.

It does not change the statistics sidecar schema, reader session formulas, SQLite boundary, video storage, or the dashboard repository/calculator algorithms already implemented.

## Chosen Approach

The novel library keeps one shell page and replaces its content when Statistics is active, matching Niratan. The CommandBar changes to a Bookshelf return action, and the book rails are removed from layout and hit testing rather than covered by an expanded inline panel.

A separate `NovelStatisticsDashboardView` and `NovelStatisticsDashboardViewModel` own dashboard-only presentation. The parent `NovelLibraryPageViewModel` remains responsible for books, shelves, import, sorting, and selecting the active library surface. The dashboard ViewModel consumes `INovelStatisticsDashboardService`, `INovelShelfService`, and `ISettingsService`; it never reads sidecar files or SQLite directly.

Rejected alternatives:

- A separate navigation page creates a different back-stack model from Niratan and duplicates library refresh coordination.
- Keeping the inline panel cannot provide the reference layout, full module density, or correct narrow-window behavior.

## Surface Switching

`NovelLibraryPageViewModel` exposes a two-state surface: Bookshelf or Statistics. Activating Statistics hides the sort, shelf-management, import, and remote-refresh commands and shows a Bookshelf return command. Returning restores the prior bookshelf scroll and sort state without reinitializing the application shell.

Only one content surface is visible and hit-testable at a time:

```text
NovelLibraryPage
  ├─ CommandBar: bookshelf commands OR return-to-bookshelf
  └─ ContentPresenter
      ├─ Bookshelf surface
      └─ NovelStatisticsDashboardView
```

The existing Statistics toggle becomes a normal command that enters the dashboard. The dashboard header reads `Statistics` and shows the selected range title beneath it.

## Component Boundaries

### NovelLibraryPageViewModel

- Owns `SelectedLibrarySurface` and the command that changes it.
- Supplies the current visible books and shelf state to the dashboard ViewModel.
- Does not calculate dashboard summaries or format statistics values.
- Continues to own bookshelf import, sort, shelf, drag/drop, and remote-book behavior.

### NovelStatisticsDashboardViewModel

- Owns the immutable dashboard snapshot and all selected UI state: range mode, anchor, trend grain, trend metric, trend style, ranking metric, and selected calendar date.
- Projects Today, Week, Selected Range, Speed, Trend, Calendar, Ranking, and Shelf rows from `NovelStatisticsDashboardCalculator`.
- Persists goal type and target values through `ISettingsService` using Niratan snapping rules.
- Subscribes to `SnapshotRefreshed` only while active and applies refreshed snapshots on the UI synchronization context.
- Exposes explicit `IsLoading`, `IsRefreshing`, `HasData`, `HasCorruptBooks`, and empty-state properties.

### NovelStatisticsDashboardView

- Contains WinUI-only layout and visual state logic.
- Uses native `ScrollViewer`, `Grid`, `ComboBox`, `CalendarDatePicker`, `NumberBox`, `Button`, `ProgressRing`, `ItemsRepeater`/`ItemsControl`, `ToolTip`, and theme resources.
- Contains no repository, sidecar, settings, or calculation logic.
- Uses visual states for wide, medium, and narrow layouts; code-behind is limited to UI sizing or focus behavior that cannot be expressed in XAML.

### Display models

Small immutable display records expose formatted text and normalized visual values. They do not duplicate raw statistics calculations. Examples include trend plot points, week-day cells, speed metrics, ranking bars, shelf comparison bars, and calendar heat cells.

## Dashboard Modules

### Range and Trend

This is the full-width first card.

- Range: Year, Month, Week, Day.
- Anchor date clipped to the recent-year window.
- Time grain: Day, Week, Month.
- Metric: Characters, Duration, Speed.
- Style: Bar, Line.
- Changing range recalculates every dashboard module.
- Changing grain, metric, or style affects only the trend visualization.
- Tooltips show period label, selected metric, characters, duration, valid speed, and up to five top contributing books.
- Empty periods inside the active trend span remain visible; leading and trailing empty periods stay trimmed according to the existing calculator.

The bar view uses normalized column heights. The line view uses a WinUI `Polyline` over the same normalized point set. No chart dependency is introduced.

### Today

- Goal ring with uncapped percentage text; ring fill is visually capped at 100 percent.
- Duration, characters, valid speed, and daily streak.
- Header detail shows the active daily goal.

### Goal

- Goal type: Characters or Duration.
- Character target: 500 through 20,000 in steps of 500.
- Duration target: 5 through 240 minutes in steps of 5.
- Weekly target: 1 through 7 days.
- Changes persist immediately and recalculate historical goal percentages and streaks without modifying statistics sidecars.

### This Week

- Duration, characters, average characters per elapsed day, and valid speed.
- Seven Monday-to-Sunday cells.
- Future cells show no percentage.
- Met cells and the current day have distinct theme-aware visual states.
- Daily and weekly streak details remain visible.

### Selected Range

- Range title, duration, characters, valid speed, target-day count, and single-day goal progress where applicable.
- Uses the same selected range as Trend, Speed, Ranking, Calendar selection outline, and Shelf Comparison.

### Speed Summary

- Weighted valid speed.
- Median active-day speed.
- Last seven active days.
- Non-overlapping 14-active-day change percentage.
- Fastest and slowest valid days.
- Missing speed values render as an em dash, never `0 / h`.

### Reading Calendar

- Covers the entire recent-year window in a seven-row horizontal heatmap.
- Intensity is based on characters relative to the maximum day in the loaded window.
- The selected range has an outline when the range mode is not Year.
- The selected day has a stronger accent outline.
- Selecting a date updates the range anchor and shows date, characters, duration, and active-book count.
- Inactive selected dates explicitly show `No reading records` with zero metrics.

### Book Ranking

- Up to twelve rows.
- Metric: Characters, Duration, Speed.
- Each row shows title, formatted metric, and a normalized bar relative to the largest visible row.
- Invalid speed-only books are excluded in Speed mode by the calculator.

### Shelf Comparison

- Includes custom shelves and Unshelved.
- Shows book count, total book characters, recorded characters, duration, and valid speed.
- Shows normalized recorded-progress and reading-volume bars without changing shelf order or membership.

## Adaptive Layout

The dashboard owns a single vertical `ScrollViewer`.

- Wide (`>= 1260` effective pixels): full-width Trend followed by three columns. Column one contains Today, Goal, and Week. The other two columns contain Shelf Comparison, Selected Range, Calendar, Speed, and Ranking in the same grouping as Niratan.
- Medium (`840..1259`): full-width Trend followed by two columns. The left column contains Today, Goal, Week, Calendar, and Shelf Comparison; the right contains Selected Range, Speed, and Ranking.
- Narrow (`< 840`): one column ordered Today, Goal, Week, Calendar, Selected Range, Speed, Ranking, Shelf Comparison.

Selector rows use horizontal scrolling rather than clipping. No dashboard collection owns vertical scrolling independently. The calendar owns horizontal scrolling only.

## Loading, Cache, and Error States

- First load: show a redacted module skeleton and a `Loading Statistics` status pill; controls are not hit-testable.
- Cache hit: show cached data immediately with a non-blocking refreshing pill while sidecars rebuild in the background.
- Empty snapshot: show the Goal card plus a `No Reading Records` state explaining how to start statistics.
- Corrupt or unavailable sidecars: show a persistent warning above the dashboard modules. Healthy books continue to aggregate, and affected files remain unchanged.
- Refresh failure after a cache hit: retain the cached snapshot and warning state; do not blank the dashboard.
- Cancellation on leaving the dashboard stops foreground loading and detaches UI refresh handling.

## Accessibility and Localization

- Every interactive control has a stable AutomationId and accessible name.
- Range, metric, style, ranking, goal, calendar, and Bookshelf return controls are keyboard reachable in logical order.
- Heat cells expose full date and character count; selected state is announced.
- Charts expose an accessible tabular list of period label and value even when the visual is a line or bar plot.
- Theme resources provide light, dark, and high-contrast behavior; no hard-coded light-only colors.
- New user-visible strings use `.resw` resources and tolerate expansion.

## Testing Strategy

### Pure projection tests

`NovelStatisticsDashboardViewModelTests` cover:

- range changes reproject every dependent module;
- trend grain/metric/style changes do not alter unrelated summaries;
- goal changes snap, persist, and recalculate;
- cache refresh replaces the displayed snapshot;
- empty and corrupt states;
- calendar selection updates anchor and detail;
- speed missing values and ranking units.

### XAML/static contract tests

Asset tests assert:

- full-page surface switching and Bookshelf return command;
- all module AutomationIds;
- Bar and Line style controls;
- three adaptive layout states and thresholds;
- one vertical dashboard scroll owner;
- absence of the old embedded statistics Border.

### Runtime verification

- Open/close the full-page dashboard from the novel CommandBar.
- Verify cached data appears before background refresh completes.
- Exercise every selector and goal control.
- Select calendar dates and verify range anchoring and details.
- Resize across wide, medium, and narrow thresholds.
- Verify light, dark, high contrast, keyboard navigation, and Narrator names.
- Confirm returning to Bookshelf restores usable rails and book-card interaction.
- Confirm no novel/statistics SQLite tables are recreated and video SQLite remains untouched.

## Acceptance Criteria

- Dashboard replaces the bookshelf surface and returns through a dedicated Bookshelf command.
- All nine reference modules are visible and driven by the existing typed calculator.
- Range, trend grain, trend metric, trend style, ranking metric, goal settings, and calendar selection behave as specified.
- Wide, medium, and narrow layouts match the reference grouping without clipped controls or nested vertical scrolling.
- Cache, empty, refresh, and corrupt-sidecar states are explicit and non-destructive.
- No new database or chart dependency is added.
- ViewModels do not access SQLite, sidecar files, or WinUI controls directly.
- Full x64 build and test suite pass, followed by real WinUI launch verification.
