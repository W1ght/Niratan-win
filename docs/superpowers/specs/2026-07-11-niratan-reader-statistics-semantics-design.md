# Niratan Reader Statistics Semantics Design

## Goal

Make Windows Reader statistics match Niratan's current reading boundaries so the stored history and dashboard are trustworthy.

## Session Ownership

`IReaderStatisticsSession` owns tracking state, timestamps, raw-character baselines, daily/session/all-time projections, checkpoint decisions, and sidecar writes. `NovelReaderPageViewModel` exposes that state and commands. Code-behind forwards typed events such as reader restored, actual page move, programmatic destination resolved, Sasayaki movement, pause, and close.

The JavaScript bridge still only renders, extracts coordinates and positions, and reports typed events. No dictionary or statistics policy moves into JavaScript.

## Autostart Modes

- `Off`: tracking begins only from the explicit Reader command or shortcut.
- `Page Turn`: tracking begins on the first actual manual page/scroll movement. A page-turn request that does not move the viewport is not sufficient.
- `On`: tracking begins after the Reader has loaded and restored the current position.

Sasayaki starts `Page Turn` tracking only when auto-scroll returns a new progress value or when playback moves into another chapter. Highlighting the current cue without moving the viewport has no statistics side effect.

## Reading Checkpoints

Ordinary page turns, continuous-scroll progress, and adjacent chapter-boundary reading persist the bookmark and flush one statistics checkpoint. The session also flushes on explicit pause/stop, Reader lifecycle close, and the app's background transition.

While tracking and not paused, the session updates display projections every second. Time keeps accumulating under Niratan's current semantics until tracking is paused or stopped; no new idle heuristic is introduced.

## Programmatic Navigation

Programmatic destinations include chapter-list selection, character jumps, highlights, history back/forward, internal EPUB links, and non-natural Sasayaki or lyrics positioning.

Their sequence is:

1. Flush the old reading position once.
2. Change or resolve the destination.
3. Persist the destination bookmark without another statistics flush.
4. Reset the statistics time and raw-character baseline at the destination.

Same-chapter internal links do not reload the chapter. Cross-chapter links wait for the WebView2 bridge to report resolved progress before persisting and resetting. Natural adjacent chapter transitions remain normal reading rather than programmatic jumps.

## Daily Statistics Contract

The stored payload remains TTU v1.6 compatible:

- `title`
- `dateKey`
- `charactersRead`
- `readingTime`
- `minReadingSpeed`
- `altMinReadingSpeed`
- `lastReadingSpeed`
- `maxReadingSpeed`
- `lastStatisticModified`

Entries deduplicate by `dateKey`, keeping the record with the newer modification timestamp. Counts are stored as raw characters; language/Profile display conversion happens only in UI formatting.

`dateKey` and day rollover use the current Windows local time zone. A rollover archives the prior daily record and establishes the current local date record, matching Niratan's current checkpoint behavior.

Negative raw-character movement cannot reduce a session below zero. Speed fields update with the same TTU-compatible formulas and modification timestamps as Niratan.

## Reader UI Projection

The Reader exposes session, today, and all-time characters, speed, and duration, plus estimated time to finish the chapter and book using the current session speed. The bottom Reader chrome honors the existing show-speed and show-time switches and the active content-language display conversion.

## Testing and Acceptance

- Deterministic clock tests cover start, tick, flush, pause, stop, rollover, negative movement, and reload.
- Event-matrix tests cover manual pagination, continuous scroll, adjacent chapters, same/cross-chapter internal links, chapter list, highlights, history, Sasayaki, lyrics, and no-op page requests.
- Lifecycle tests prove close and background flush once.
- Sidecar tests prove TTU field and deduplication compatibility.
- Acceptance requires normal reading to advance time and characters while every programmatic jump preserves totals except for the flushed pre-jump interval.
