# Niratan Reader Statistics Panel and Sync Parity Design

## Goal

Align the Niratan Windows Reader statistics behavior, its in-Reader `Session / Today / All Time` panel, and statistics-related Google Drive interactions with the current local Niratan reference.

The behavior source of truth is `docs/reference/Niratan` at Niratan `v1.3.0`, commit `e40ca3a`. This specification supersedes conflicting Reader-event and Reader-auto-sync details in the 2026-07-11 statistics specifications. It does not redesign the Bookshelf statistics dashboard.

## Confirmed Scope

- Fix ordinary same-chapter page turns so they update the bookmark and statistics character count.
- Match Niratan's statistics autostart, tick, checkpoint, stop, close, and programmatic-navigation boundaries.
- Replace the oversized Windows statistics dialog content with a compact Niratan-aligned Reader panel.
- Match Niratan's content-language display conversion for Japanese characters and approximate English words.
- Match Niratan's Google Drive open-import, debounced export, and final close/background flush behavior.
- Match the dependency between global Google Drive sync and statistics-specific sync settings.
- Preserve the existing TTU v1.6 statistics payload, sidecar names, dashboard calculator, and Bookshelf dashboard.

## Root Cause of the Reported Page-Turn Defect

`reader-bridge.js` returns `scrolled` after a successful same-chapter page move. `ReaderStatisticsEventClassifier.IsActualPageMovement` currently accepts only `moved`. The host therefore ignores successful same-chapter movement and neither updates `ViewModel.Progress` nor checkpoints statistics. Chapter boundaries return `limit` and use a separate adjacent-chapter branch, which explains why characters appear to update only when crossing chapters.

The repair keeps Niratan's `scrolled` bridge result and makes the native contract typed and consistent. A contract test must prove that every result emitted by the bridge is recognized by the host.

## Architecture

```text
reader-bridge.js
  -> versioned pageChanged message
  -> NovelReaderPage validates and converts to ReaderPageNavigationEvent
  -> NovelReaderPageViewModel applies Reader navigation policy
       -> IReaderStatisticsSession
       -> INovelLibraryService / sidecar services
       -> IReaderAutoSyncCoordinator

ReaderStatisticsPanelContent (WinUI-only)
  <- NovelReaderPageViewModel display properties and commands

IReaderAutoSyncCoordinator (one transient instance per Reader)
  -> ITtuSyncService
  -> ISettingsService
  -> IGoogleDriveAuthService credential state
```

`NovelReaderPage` remains responsible only for WebView2 message validation, dialog presentation, UI focus, and forwarding typed events. It does not decide whether movement counts as reading or perform synchronization. `NovelReaderPageViewModel` owns Reader state and commands. The statistics session owns calculation and persistence. The auto-sync coordinator owns network scheduling and concurrency.

No new database, chart library, or synchronization dependency is introduced.

## Typed Page-Turn Contract

The bridge result vocabulary is:

- `scrolled`: the viewport moved inside the current spine chapter;
- `limit`: the request reached a chapter boundary;
- an unknown value: invalid input that is logged and ignored without changing progress or statistics.

The host converts the string to a narrow enum before forwarding the event. A `scrolled` event counts only when the reported progress differs from the prior progress by the established tolerance. A `limit` event becomes natural adjacent-chapter reading only when the requested direction resolves to an in-range chapter. A final-book or first-book `limit` does not create a character checkpoint.

Niratan's `Page Turn` autostart is invoked for a valid manual page-turn request before applying its result. Thus a boundary request may start tracking even if no further chapter exists, but it cannot add characters without movement. Restore, reflow, settings reload, chapter list, search, highlight, history, internal link, and other programmatic destinations do not masquerade as manual page turns.

For a successful same-chapter page turn, the sequence is:

1. Start `Page Turn` tracking if configured and not already tracking.
2. Update progress and the derived raw character position.
3. Persist the bookmark.
4. Flush one `ReadingMovement` statistics checkpoint.
5. Schedule Google Drive auto-export.

For a natural adjacent-chapter transition, the old chapter position is checkpointed once before loading the adjacent chapter. The new chapter bookmark is then persisted without double-counting the same interval.

## Statistics Session Semantics

- `Off`: tracking starts only through the Reader panel command or shortcut.
- `Page Turn`: a valid manual page-turn request starts tracking, matching Niratan.
- `On`: tracking starts after restore and any opening import have established the final position.
- While tracking and not paused, display projections update once per second.
- Ordinary paged movement, continuous-scroll progress, natural adjacent chapters, and natural Sasayaki movement save the bookmark and flush statistics.
- Stop flushes the current interval and leaves tracking off. The panel uses a pause icon for this Niratan-compatible start/stop behavior; it does not introduce a second resumable paused state.
- Reader close and application background save the bookmark and statistics once before final synchronization.
- Programmatic navigation flushes the old position once, resolves and persists the destination, then resets time and character baselines so the jump itself does not count as reading.
- Daily rollover, negative movement clamping, speed formulas, and TTU-compatible daily fields remain owned by `IReaderStatisticsSession` and `ReaderStatisticsMath`.

Opening the statistics panel does not create a special persistence boundary. Its values are already current through the one-second projection loop, matching Niratan.

## Reader Statistics Panel

The panel is a native WinUI `ContentDialog` containing a focused `ReaderStatisticsPanelContent` control. It is the Windows equivalent of Niratan's Reader statistics sheet.

- Target content size is approximately 520 by 560 effective pixels, with width clamped to the available Reader window instead of the current fixed 1120-pixel layout.
- The title is `Statistics`; the dialog retains a native close action.
- The vertical content contains three clearly separated sections: `Session`, `Today`, and `All Time`.
- The Session header contains the start or stop button. Its icon and accessible name update with tracking state.
- Session rows are count, reading speed, reading time, time to finish the book, and time to finish the chapter.
- Today and All Time rows are count, reading speed, and reading time.
- Labels remain left-aligned and numeric values right-aligned with tabular/monospaced digits where WinUI permits.
- The dialog has one vertical scroll owner, supports keyboard and touch, and remains usable with text scaling and a narrow Reader window.
- Theme resources provide light, dark, and high-contrast colors; the panel introduces no custom acrylic or non-native chrome.

The panel's display unit follows the active book profile:

- Japanese: raw characters and `Characters Read`.
- English: `ContentLanguageProfile.DisplayUnitsFromRawCharacters` and `Approximate Words Read`.

Reading speed uses the same conversion as count. Stored sidecars always remain raw characters. Estimated remaining times continue to use raw remaining characters divided by raw session speed so language conversion cannot distort duration.

## Google Drive and Statistics Settings Interaction

Niratan treats statistics sync as a subordinate option of global TTU/Google Drive sync.

- When statistics are disabled, statistics-specific Autostart, goals, and Sync content is hidden; persisted preferences are retained.
- The statistics Sync section is visible only when global `TtuSyncSettings.EnableSync` is true.
- Disabling global sync hides and deactivates statistics sync but does not erase `NovelStatisticsSettings.EnableSync` or its Merge/Replace preference.
- Re-enabling global sync restores the prior statistics-sync selections.
- Runtime statistics synchronization is enabled only when global sync, auto sync, usable Google Drive credentials, and statistics sync are all enabled.
- Merge and Replace keep their existing TTU-compatible meanings. The global manual/automatic direction setting does not replace the statistics Merge/Replace choice.

Daily and weekly goal settings remain available in the Windows statistics settings and dashboard because they are already part of the shipped dashboard contract. Their visibility follows the statistics master switch, but this project does not remove or relocate them.

## Reader Auto-Sync Lifecycle

`IReaderAutoSyncCoordinator` is registered transiently so every Reader owns isolated debounce, cancellation, pending, and in-flight state.

### Open

After local book sidecars are available but before the final restore baseline and `On` autostart:

1. If auto-sync prerequisites are not met, return without network work and establish the local baseline.
2. Call `ITtuSyncService.SyncBookAsync` with `Direction=Auto` and `ImportOnly=true`.
3. Pass the current book-data, statistics, statistics-mode, and Sasayaki sync switches.
4. If remote progress is newer and imported, reload the bookmark and statistics sidecars into Reader state before restoring the WebView position.
5. Reset the statistics baseline at the resolved position.

Open sync never exports merely because local data is newer. Network, OAuth, corrupt-response, and cancellation failures leave local sidecars and the local starting position intact.

### Bookmark changes

Every successful Reader bookmark write marks auto-export pending. The coordinator starts one 30-second debounce window. Additional changes during that window coalesce into the same export.

When the delay expires, the coordinator performs an explicit `ExportToTtu` sync. Only one export for the book can run at a time. If a bookmark changes while an export is running, one pending follow-up pass runs with the newest sidecars. Stale completion cannot overwrite newer local files.

### Close and background

Close and background use the same ordered boundary:

1. Save the current bookmark.
2. Flush the statistics session.
3. Mark export pending.
4. Cancel the debounce delay.
5. Await the active export and then run one final pending export.

The existing lifecycle single-execution gate prevents duplicate close checkpoints. A failed final sync is reported through the existing notification/logging path but does not block closing indefinitely or roll back local data. Cancellation stops network work without partial local writes.

## Failure and Concurrency Rules

- Invalid WebView messages are ignored and logged; they cannot mutate Reader state.
- Statistics sidecar write failure leaves the last valid file intact through the existing atomic file service.
- Open-import applies decoded and validated remote data only; failure keeps the local bookmark, statistics, and baseline.
- Auto-sync never runs without credentials and never logs tokens, client secrets, or raw OAuth responses.
- The coordinator serializes exports per Reader/book and coalesces later changes into one follow-up pass.
- Leaving the Reader cancels delays and subscriptions after the final bounded flush.
- Merge deduplicates daily entries by `dateKey` and keeps the newer `lastStatisticModified`; Replace uses the resolved source set exactly.
- Imported statistics invalidate or refresh dashboard-derived cache through the existing statistics sidecar/cache path.

## Testing Strategy

### Page-turn regression

- A contract test reads `reader-bridge.js` and proves `scrolled` is accepted by the host result parser.
- Classifier tests cover `scrolled` with changed progress, `scrolled` without movement, adjacent `limit`, out-of-range `limit`, and unknown results.
- Reader event tests prove a same-chapter page turn updates progress, raw character position, bookmark, and exactly one statistics checkpoint.
- Continuous, adjacent-chapter, final-boundary, restore, reflow, and programmatic-navigation cases remain distinct.

### Panel and display

- ViewModel tests cover Japanese raw counts, English approximate-word counts, converted speed, raw-unit remaining-time estimates, start/stop state, and live projection updates.
- XAML contract tests cover compact sizing, the three sections, Session start/stop placement, one scroll owner, AutomationIds, and removal of the 1120-pixel fixed content width.
- Runtime checks cover keyboard focus, touch, text scaling, narrow windows, light, dark, and high contrast.

### Settings and synchronization

- Settings ViewModel tests cover master-statistics visibility, global-sync visibility, preserving subordinate preferences, and Merge/Replace persistence.
- Coordinator tests use a deterministic delay/clock and cover disabled prerequisites, open import-only, imported-state reload, 30-second coalescing, single-flight, pending follow-up, close flush, background flush, cancellation, and failures.
- Integration tests prove the coordinator passes `SyncStatistics` and `StatisticsSyncMode` correctly to the existing TTU service.
- Existing TTU tests continue to cover direction resolution, Merge/Replace, filenames, corrupt downloads, and atomic sidecar writes.

## Runtime Verification

1. Build and run Niratan on x64.
2. Open `C:\Users\Wight\Downloads\哈利波特1魔法石.epub` with statistics enabled.
3. In `Page Turn` mode, turn one same-chapter page and verify progress, current character, Session, Today, and `statistics.json` advance without crossing a chapter.
4. Verify backward movement, natural chapter transitions, first/last-book limits, continuous mode, resize/reflow, and reopen persistence.
5. Verify chapter list, search, highlight, history, internal link, and non-natural Sasayaki jumps do not inflate characters.
6. Open the Reader statistics panel and verify all sections, start/stop behavior, estimates, compact sizing, localization, both profile languages, themes, keyboard, and text scaling.
7. With Google Drive auto-sync enabled, verify newer remote data imports before the final Reader baseline.
8. Verify repeated page turns create one export after 30 seconds and that close/background waits for the final pending export.
9. Disable global sync and confirm statistics sync becomes inactive without losing its stored preference; re-enable it and confirm the preference returns.
10. Confirm offline, expired-auth, and remote-data failures preserve local progress and statistics.

## Acceptance Criteria

- A same-chapter page turn changes the stored and displayed character count before a chapter boundary.
- The bridge and host share one tested `scrolled` / `limit` result contract.
- Reader statistics autostart and checkpoint boundaries match Niratan `v1.3.0` without counting programmatic jumps.
- The in-Reader panel matches Niratan's content, hierarchy, compact dimensions, language conversion, and start/stop interaction.
- The Bookshelf statistics dashboard remains functionally unchanged.
- Statistics sync is subordinate to global Google Drive sync and preserves its stored preferences when temporarily inactive.
- Open import-only, 30-second debounced export, single-flight follow-up, and final close/background flush work without data loss.
- View code-behind contains no statistics or network policy, and ViewModels do not access SQLite or Google Drive directly.
- Targeted tests, the full x64 test suite, x64 build, and real WinUI launch/runtime verification pass.
