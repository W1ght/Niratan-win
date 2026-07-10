# Niratan Statistics Full Parity Design

## Goal

Align Hoshi Windows with the current local Niratan statistics experience and its novel-storage architecture. The result includes Niratan-compatible file-backed books and shelves, trustworthy Reader statistics, the complete statistics dashboard, and TTU statistics synchronization. SQLite remains only for the video catalog and read-only external audio databases.

The behavior source of truth is `docs/reference/hoshi/Niratan` at the revision present during design. WinUI uses native Windows controls and responsive conventions instead of mechanically copying SwiftUI visuals.

## Confirmed Product Decisions

- Full parity includes statistics collection, Reader controls, goals, dashboard calculations, dashboard UI, caching, corrupt-data handling, TTU/Google Drive synchronization, and auto-sync triggers.
- Full parity also includes Niratan shelves because `Shelf Comparison` depends on them.
- Novel metadata, progress, sort order, shelves, and statistics move out of SQLite.
- Video catalog persistence stays in SQLite for now.
- External Android/local-audio `.db` files remain read through SQLite in read-only mode.
- The dashboard uses Niratan's card order and responsive layout: full-width trend, three columns at wide width, two columns at medium width, and one column at narrow width.
- The normal bookshelf uses sections, a native command surface, explicit cross-shelf movement, in-section drag reorder, and a shelf-management dialog.

## Target Architecture

```text
EPUB import / legacy migration
  -> INovelBookStorageService
       -> Books/<folder>/metadata.json
       -> Books/<folder>/bookmark.json
       -> Books/<folder>/bookinfo.json
       -> Books/<folder>/statistics.json
       -> Books/<folder>/highlights.json and Sasayaki sidecars
       -> Books/shelves.json
       -> Books/book_order.json

NovelLibraryPageViewModel
  -> INovelBookStorageService
  -> INovelShelfService

NovelReaderPageViewModel
  -> IReaderStatisticsSession
  -> INovelBookStorageService / INovelStatisticsSidecarService

StatisticsDashboardViewModel
  -> INovelStatisticsDashboardService
  -> versioned derived cache

TTU sync
  -> file-backed novel services only

Video services
  -> IVideoDataService
  -> SQLite + Dapper
```

Code-behind remains UI-only. It validates and forwards WebView2 or WinUI events; it does not decide whether an action counts as reading, perform storage reconciliation, or calculate dashboard metrics.

## Delivery Decomposition

The feature is intentionally divided into five dependency-ordered specifications:

1. [Novel file storage and shelves](2026-07-11-niratan-novel-file-storage-shelves-design.md)
2. [Reader statistics semantics](2026-07-11-niratan-reader-statistics-semantics-design.md)
3. [Statistics dashboard engine](2026-07-11-niratan-statistics-dashboard-engine-design.md)
4. [Statistics and bookshelf WinUI](2026-07-11-niratan-statistics-winui-design.md)
5. [Statistics sync, reliability, and verification](2026-07-11-niratan-statistics-sync-reliability-design.md)

Each increment must keep the app buildable and preserve user data. The complete parity claim is made only after all five specifications pass their acceptance criteria.

## Compatibility Boundaries

- Preserve TTU v1.6's nine-field daily statistics payload and its filename calculations.
- Preserve existing Hoshi sidecar names.
- Treat all JSON and EPUB content as untrusted input.
- Use atomic file replacement and narrow, typed models.
- Do not change `native/hoshidicts/`.
- Do not move the video catalog out of SQLite in this project.
- Do not introduce a second novel database or an ORM.

## Complete Acceptance Criteria

- Novel runtime operations no longer read or write the SQLite novel tables.
- Existing users migrate without losing books, profiles, progress, character totals, statistics, highlights, Sasayaki data, or manual order.
- Shelves can be created, renamed, reordered, deleted, populated, and used by dashboard comparison.
- Normal reading advances statistics while programmatic position changes do not.
- The dashboard exposes all current Niratan modules and calculations, including goals, streaks, speed analysis, trends, calendar, ranking, and shelf comparison.
- Statistics sync matches Niratan merge/replace and direction behavior and uses file storage only.
- Corrupt per-book data cannot crash the complete library or silently overwrite the corrupt source.
- The app passes x64 build and tests, launches successfully, and completes the Reader, dashboard, shelf, and sync verification matrix.
