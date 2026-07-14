# Statistics Dashboard Cache Recovery Design

## Problem

Google Drive statistics import succeeds, but opening the statistics dashboard can fail before the imported rows are displayed. The imported book retains its `statistics.json`; the failure occurs while loading the derived dashboard cache.

The observed exception is a `NotSupportedException` from `System.Text.Json` while deserializing `NovelStatisticsBookContribution`. The record has both a primary constructor and a convenience constructor, but neither is identified as the JSON constructor. Existing cache coverage only round-trips snapshots whose day aggregates have no book contributions, so it does not exercise the failing shape.

Because `NovelStatisticsDashboardCache.TryLoadAsync` does not handle model-compatibility exceptions from its JSON store, this derived-cache failure reaches the WinUI unhandled-exception boundary instead of falling back to source sidecars.

## Goals

- Make persisted dashboard snapshots with non-empty book contributions deserialize reliably.
- Treat an incompatible dashboard cache as disposable derived data.
- Rebuild the dashboard from per-book `statistics.json` files after cache invalidation.
- Preserve imported statistics, bookmarks, EPUB data, and other book sidecars.
- Prevent the dashboard from crashing the application for this class of cache incompatibility.

## Non-goals

- Changing Google Drive download or statistics-sync settings semantics.
- Adding a new manual statistics backfill command.
- Modifying the format of per-book `statistics.json` files.
- Broadly swallowing serialization errors in the shared JSON sidecar store.

## Design

### Model serialization

Mark the six-parameter primary constructor of `NovelStatisticsBookContribution` with `[method: JsonConstructor]`. The five-parameter convenience constructor remains available to callers and continues deriving `IsValidSpeedSample`, while persisted snapshots deserialize all six stored fields through the explicit primary constructor.

### Cache recovery

Keep recovery local to `NovelStatisticsDashboardCache`, because its file is derived and safely reproducible. If the cache store throws `NotSupportedException` during a read, the cache will:

1. clear its in-memory key and snapshot;
2. delete only `statistics_dashboard_cache_v1.json` when present;
3. return a cache miss.

`NovelStatisticsDashboardService.LoadSnapshotAsync` already rebuilds a cache miss from each book's source sidecars, so no new rebuild path is required.

The shared `NiratanJsonFileStore` will not be changed. A `NotSupportedException` in durable source sidecars can indicate a programming or schema error and should not be silently converted to invalid data globally.

### Data safety

Recovery may delete only the dashboard cache file. It must not delete or rewrite `statistics.json`, `bookmark.json`, `bookinfo.json`, `metadata.json`, EPUB files, or Sasayaki sidecars. If cache deletion itself fails, the filesystem exception remains visible rather than claiming recovery succeeded.

## Tests

Add focused tests to `NovelStatisticsDashboardCacheTests`:

1. Persist and reload a snapshot containing a non-empty `NovelStatisticsBookContribution`, asserting every contribution field survives the round trip.
2. Simulate a cache store that throws `NotSupportedException`, assert `TryLoadAsync` returns `null`, and assert only the derived cache file is deleted.
3. Retain the existing corrupt-cache and source-sidecar preservation tests.

Run the focused statistics cache tests, the full x64 test suite, and the x64 build. Launch Niratan and open the statistics dashboard with the already imported `かがみの孤城` data to confirm the panel rebuilds without an unhandled exception.

## Success criteria

- Opening the statistics dashboard does not emit the observed `NovelStatisticsBookContribution` deserialization crash.
- The existing `かがみの孤城/statistics.json` contributes data to the dashboard without another Drive download.
- An incompatible derived cache is automatically discarded and recreated.
- All automated tests and the x64 build pass.

