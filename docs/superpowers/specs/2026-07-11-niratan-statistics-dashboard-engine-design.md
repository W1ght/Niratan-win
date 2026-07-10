# Niratan Statistics Dashboard Engine Design

## Goal

Port Niratan's complete dashboard data model and calculations into typed, testable C# services without binding calculations to WinUI controls.

## Snapshot Repository

`INovelStatisticsDashboardService` loads a sendable/lightweight list of visible books, their `bookinfo.json`, and their `statistics.json`. It returns:

- Daily aggregates with per-book contributions.
- Book records with current title, cover, and total character count.
- IDs of books whose statistics could not be decoded.

Missing statistics means no recorded contribution. Invalid date keys are ignored. Corrupt statistics are reported per book and never overwritten by dashboard loading.

## Target Settings

- Daily target type: characters or duration.
- Character target: `500...20,000`, snapped to `500`.
- Duration target: `5...240` minutes, snapped to `5`.
- Weekly target days: `1...7`.
- Defaults: 5,000 characters, 30 minutes, and four weekly days.

Changing a target immediately recalculates historical goal percentages and streaks; it does not rewrite statistics.

## Range and Summary Calculations

- Dashboard window: the recent year ending today.
- Selectable range: year, month, Monday-based week, or day, clipped to the dashboard window.
- Today: goal percent, characters, duration, valid weighted speed, and daily streak.
- This Week: full Monday-Sunday cells, elapsed-day averages, met/target days, daily streak, and weekly streak.
- Selected Range: characters, duration, valid weighted speed, target days, and goal progress for a one-day selection.

Percentages are rounded and may exceed 100. Future week cells have no percentage.

## Speed Rules

Only contributions with positive characters and at least 60 seconds of reading time are valid speed samples. Invalid short bursts remain in character/time totals but do not become `0/h` speed samples.

Speed Summary contains:

- Weighted average for the selected range.
- Median active-day speed.
- Weighted speed for the last seven active days.
- Percent change between the first and latest non-overlapping fourteen active-day windows; absent until both windows exist.
- Fastest and slowest valid day with deterministic date tie-breaking.

## Trend, Calendar, Ranking, and Shelves

- Trend grain: day, week, or month, independent of selected range mode.
- Trend metric: characters, duration, or valid speed.
- Daily trends fill inactive interior dates but trim inactive leading and trailing edges.
- Weekly/monthly trends fill every period touched by the active range.
- Trend points carry the top per-book character contributors for tooltips.
- The reading calendar covers the dashboard year and exposes selected-day characters, duration, and active-book count.
- Book ranking supports characters, duration, and valid weighted speed, with a limit of twelve rows.
- Shelf comparison reports shelf name, book count, total book characters, recorded characters, duration, and valid weighted speed, including an Unshelved row.

The removed legacy `By Book` distribution module is not retained.

## Cache

The dashboard uses a schema-versioned in-memory and disk cache. The cache key includes the visible local book identity projection. File-storage events for metadata, statistics, book info, shelves, and book removal invalidate the cache. Target changes invalidate derived ViewModel projections rather than the raw snapshot.

The cache is never a source of truth. Invalid schema, key mismatch, or corrupt JSON deletes the derived cache and triggers an asynchronous rebuild. Initial entry may show a cached or placeholder snapshot while fresh IO completes off the UI thread.

## Testing and Acceptance

- Port Niratan's calculation fixtures for today, week, daily/weekly streaks, target snapping, range clipping, speed filtering, speed windows, trend filling, ranking, shelf comparison, corrupt data, and cache keys.
- Add cancellation and cache-invalidation tests.
- Verify all calculation APIs are independent of WinUI types.
- Acceptance requires result parity with the local Niratan calculation fixtures and no dashboard file IO on the UI thread.
