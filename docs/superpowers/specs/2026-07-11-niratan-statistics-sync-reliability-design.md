# Niratan Statistics Sync, Reliability, and Verification Design

## Goal

Complete parity by making TTU statistics sync use the file-backed novel architecture, adding Niratan auto-sync triggers, defining failure behavior, and verifying the complete feature end to end.

## Sync Direction and Statistics Semantics

Sync direction is determined by explicit user direction or by comparing `bookmark.lastModified` with the timestamp encoded in the remote progress filename:

- Only remote exists or remote is newer: import.
- Only local exists or local is newer: export.
- Equal or both absent: already synchronized.

Statistics follow the resolved progress direction:

- Import Merge: merge local with remote per `dateKey`, keeping the newer `lastStatisticModified`.
- Export Merge: merge remote with local using the same rule, then upload.
- Replace: use the complete statistics set from the resolved source direction.

TTU filenames and version `1_6` summary fields remain compatible with the current service and Niratan.

The sync service reads and writes `metadata.json`, `bookmark.json`, `bookinfo.json`, `statistics.json`, and Sasayaki sidecars through typed file services. It has no novel SQLite dependency.

## Auto Sync

Auto sync is active only when global TTU sync, credentials, and auto sync are enabled. Statistics and book/audio payloads retain their independent switches.

Triggers match Niratan:

- Reader open may pull a newer remote bookmark before establishing the tracking baseline.
- Bookmark changes schedule a debounced export.
- Reader lifecycle close flushes local state and waits for the final export boundary.
- Application background schedules the same safe final synchronization.

Only one sync per book is active. Later changes set a pending flag and run one follow-up pass. Stale results cannot overwrite newer local sidecars.

## Failure Handling

- Network, OAuth, or remote-file failures do not roll back local JSON or delete remote files.
- Cancellation exits without writing partial local or cache files.
- Import writes downloaded JSON only after successful decode and validation.
- A sync failure remains retryable and is surfaced through the existing notification/status system without raw secrets or token data.
- Corrupt local statistics are preserved and excluded from automatic upload until the user explicitly repairs or replaces them.
- Cache invalidation follows successful local statistics writes, imported statistics, metadata changes, and shelf changes.

## Automated Verification

- Unit tests cover direction resolution, Merge and Replace in both directions, equal timestamps, missing files, newer daily records, corrupt downloads, cancellation, debounce, single-flight, pending follow-up, and cache invalidation.
- File-storage integration tests verify atomic writes and prove sync never calls novel SQL APIs.
- Migration tests verify that a pre-file-storage installation can sync immediately after successful cutover.
- Reader contract tests cover all statistics event boundaries.
- Dashboard calculation tests mirror the local Niratan fixtures.

## Runtime Verification

1. Run `dotnet build -p:Platform=x64`.
2. Run `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64`.
3. Launch Niratan and verify a responsive top-level window.
4. Open the configured test EPUB and verify manual pages, continuous scroll, adjacent chapters, resize/reflow, Reader statistics display, and persistence.
5. Confirm chapter-list, bookmark/highlight, history, internal-link, and manual Sasayaki jumps do not inflate characters.
6. Confirm Sasayaki natural movement and normal page turns trigger Page Turn autostart.
7. Verify shelf CRUD, drag order, explicit cross-shelf move, delete cleanup, and Shelf Comparison.
8. Verify dashboard wide/medium/narrow states, trend hover, heatmap selection, ranking, goal changes, loading, empty state, and corrupt-statistics warning.
9. Verify manual import/export plus automatic open, debounce, close, and background sync with Merge and Replace.
10. Verify light, dark, high contrast, keyboard, pointer, touch, and text scaling.

The final verified app instance remains running unless the user asks otherwise.

## Complete Acceptance

- All automated checks pass.
- The actual WinUI app launches and the affected screens are exercised.
- Real EPUB progress and statistics remain stable across close/reopen.
- TTU round trips preserve daily data and file naming.
- No unresolved data-loss, programmatic-jump inflation, UI-thread blocking, accessibility, or responsive-layout defect remains in the agreed scope.
