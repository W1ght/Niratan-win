# Reader Navigation Transaction Design

## Context

Reader progress, adjacent-chapter restoration, programmatic navigation, lifecycle checkpoints, and Sasayaki auto-scroll currently coordinate through independent state in `NovelReaderPage`, `ReaderProgrammaticNavigationTracker`, `NovelReaderPageViewModel`, and the statistics writer tail. The split permits a navigation generation to be cancelled or replaced while another component still saves, publishes, resets a baseline, reloads a chapter, or reveals the WebView.

The user-visible failure is most obvious when moving from chapter B's first page back to chapter A: the reader must directly restore A's last visible page without publishing a temporary `1.0`, flashing progress, double-counting statistics, or later persisting a stale source/destination position.

## Goals

- Treat adjacent and ordinary programmatic navigation as one transaction with one owner.
- Keep the source Reader position immutable and authoritative until destination persistence succeeds.
- Resolve `Start` and `End` targets in WebView after final pagination; `End` uses `contentLastPageScroll` and returns one page-aligned progress value.
- Save the destination bookmark, reset the matching statistics baseline, publish the destination position, and reveal the destination exactly once and in that order.
- Reject stale and duplicate bridge completions without changing ViewModel state or persistence.
- Give lifecycle, bridge errors, and Sasayaki deterministic behavior while navigation is active.
- Preserve the existing WebView2 multi-column renderer, MVVM/service layering, and TTU/Google Drive behavior.

## Non-goals

- No new EPUB renderer or changes under `native/hoshidicts/`.
- No database schema migration.
- No queue of multiple pending navigations; a new navigation is rejected while a destination commit is in progress.
- No compensating Google Drive write for a cancelled local navigation.

## Considered Approaches

### 1. Continue adding Page-level gates

This is the smallest diff, but it has already failed across multiple review cycles. A gate in `pageChanged` does not cover lifecycle, bridge errors, Sasayaki callbacks, or persistence completion. This approach is rejected.

### 2. Single transaction with a point of no return

One transaction owns generation, source, destination intent, state transitions, completion, and cancellation. Before persistence begins it may be cancelled and recovered to the source. Once persistence begins, close/background/new navigation wait for the result instead of cancelling it. This is the selected approach because it prevents stale writes without requiring compensating writes.

### 3. Fully cancellable persistence with compensating rollback

Cancellation could remain available while a bookmark save is in flight, followed by a source bookmark rewrite if the destination write wins the race. This adds two-write failure modes, complicates lifecycle shutdown, and can produce remote sync churn. It is rejected unless a future storage transaction API makes rollback atomic.

## Architecture

### Transaction owner

Create a focused `ReaderNavigationTransactionCoordinator` in `Niratan/Services/Novels`. It contains no WebView2 dependency and no database access. `NovelReaderPageViewModel` owns one transient coordinator instance and remains the business-facing API. `NovelReaderPage` renders instructions returned by the ViewModel and forwards validated bridge events.

The Page must not independently decide whether a navigation is pending, committing, cancelled, or complete. It may retain UI-only facts such as whether `chapterReady` arrived and WebView opacity.

### Immutable position and destination models

`ReaderNavigationSource` captures:

- book identity;
- chapter index;
- chapter progress;
- current character count;
- total character count;
- ViewModel position revision.

`ReaderNavigationDestination` contains a chapter index and either:

- `ReaderChapterRestoreTarget.Start`;
- `ReaderChapterRestoreTarget.End`;
- an exact progress target for ordinary programmatic navigation.

`ReaderNavigationTransaction` contains generation, source, destination, phase, and a completion task. The source snapshot never changes.

### State machine

Allowed phases are:

1. `Rendering`: source remains published; WebView renders a hidden destination.
2. `Committing`: a matching bridge completion resolved the final destination and persistence owns the point of no return.
3. `Committed`: bookmark save and destination baseline reset succeeded; destination was atomically published.
4. `Recovering`: a pre-commit cancellation, bridge error, invalid result, or save failure requires source reload.
5. `Completed`: Page received a terminal instruction to reveal the committed destination or restored source.

Only these transitions are valid:

- `Rendering -> Committing`
- `Rendering -> Recovering`
- `Committing -> Committed`
- `Committing -> Recovering` when persistence reports failure before destination publication
- `Committed -> Completed`
- `Recovering -> Completed`

Generation and expected destination chapter are validated before `Rendering -> Committing`. Duplicate or stale messages leave state unchanged.

### Point-of-no-return semantics

Before `Committing`, cancellation invalidates the generation and returns an instruction to reload the immutable source.

After `Committing` starts, the transaction is not cancelled. Background, close, or a new navigation awaits its completion. This prevents an in-flight destination save from becoming stale. A new navigation request during this phase is rejected rather than queued.

Once `Committing` starts, persistence uses a non-navigation-cancellable token and app shutdown awaits the transaction. Page navigation cancellation must not cancel an admitted destination write.

## Data Flow

### Adjacent backward navigation

1. The manual page-turn handler saves/checkpoints the current source page once.
2. ViewModel begins a transaction with destination chapter A and restore target `End`, capturing chapter B's current source tuple.
3. Page hides WebView and loads A without calling `SetChapter`, `UpdateProgress`, or refreshing visible progress.
4. Bridge waits for fonts, images, and pagination, resolves `contentLastPageScroll`, stabilizes it across animation frames, and emits one `restoreCompleted(generation, resolvedProgress)`.
5. ViewModel validates generation/chapter and enters `Committing`.
6. The writer tail saves A's resolved bookmark. On success it resets the baseline to A's resolved character count and atomically publishes A's chapter/progress/character tuple.
7. The transaction becomes `Committed`; Page refreshes chrome and reveals WebView once.

There is no temporary progress sentinel in ViewModel or bookmark state.

### Ordinary programmatic navigation

Ordinary same-chapter and cross-chapter jumps use the same transaction. Loading or restoring the target does not mutate the published ViewModel position. The matching bridge result supplies the exact destination; persistence and publication follow the same ordered commit path as adjacent navigation.

### Persistence failure

If bookmark persistence returns failure before destination publication:

- no destination baseline reset occurs;
- no destination ViewModel state is published;
- no auto-sync is scheduled;
- the transaction enters `Recovering` with the immutable source;
- Page reloads the source hidden and reveals it after source `chapterReady`;
- the transaction then becomes `Completed` and Reader input resumes.

### Bridge error

A bridge error while `Rendering` enters source recovery. A bridge error while `Committing` does not cancel persistence; it waits for the terminal commit result, then reloads the committed destination or original source as dictated by that result. Every error path produces a terminal render instruction, so WebView cannot remain permanently hidden.

### Lifecycle

- If lifecycle begins during `Rendering`, cancel to source, await recovery state, then checkpoint/save the source.
- If lifecycle begins during `Committing`, await transaction completion, then checkpoint/save the resulting committed destination or recovered source.
- Lifecycle never enqueues an independent source baseline reset behind a destination writer.
- Close/background use the same serialized writer boundary after transaction settlement.

### Sasayaki and other position mutations

While any transaction is not `Completed`:

- playback UI and non-positional cue highlighting may continue;
- Sasayaki auto-scroll, chapter load, progress application, and debounced progress save are rejected;
- `pageChanged`, internal links, search/TOC jumps, and other position-changing commands are rejected or deferred by the ViewModel transaction gate;
- statistics projection ticks may update elapsed display only if they do not checkpoint against a changing position; persisted position mutation waits for settlement.

The Page also disables Sasayaki auto-scroll for hidden destination rendering as defense in depth.

## Page / ViewModel Boundary

ViewModel APIs return typed instructions rather than exposing mutable transaction flags:

- begin navigation: render destination with generation;
- accept bridge completion: ignore, wait, render source, or reveal destination;
- handle bridge error: wait or render a terminal position;
- settle lifecycle: terminal position snapshot.

Page responsibilities are limited to:

- sending typed bridge messages;
- loading the specified chapter while hidden;
- tracking `chapterReady` for the current generation;
- applying terminal reveal/reload instructions;
- forwarding UI events.

Bookmark persistence, position revision, statistics baseline, and auto-sync remain in ViewModel/services.

## Error Handling

- Recovery is invoked exactly once per generation.
- Recovery callback exceptions do not cause a second recovery call; the primary error is logged and a final best-effort reveal/reload instruction is retained.
- Destination publication occurs only after bookmark save and the deterministic in-memory baseline reset succeed. If publication itself fails after the bookmark is durable, recovery reloads the durable destination rather than the source.
- UI refresh failure after a committed transaction does not roll back persisted state; the next terminal render reloads the committed destination.
- Invalid finite progress, wrong chapter, stale generation, and duplicate completion are ignored without mutation.

## Testing

### Coordinator state-machine tests

- happy backward `End` commit ordering;
- stale and duplicate generation;
- pre-commit cancellation and source recovery;
- save returns false;
- save throws;
- bridge error in `Rendering` and `Committing`;
- lifecycle during `Rendering` and `Committing`;
- new navigation while `Committing`;
- recovery callback throws and is invoked once;
- terminal state always releases the Reader gate.

### ViewModel integration tests

- source tuple remains published through hidden rendering;
- exact destination bookmark is saved before baseline reset/publication;
- failed save leaves source tuple and baseline unchanged;
- lifecycle waits for an in-flight commit and persists only the terminal tuple;
- no cancelled destination is resurrected by background/close;
- all position-changing entry points are blocked during an active transaction;
- successful transaction schedules one auto-sync, failed transaction schedules none.

### Page/bridge contract tests

- typed `Start`/`End` payloads omit approximate progress;
- `End` is checked before progress fallback and emits one completion;
- destination WebView stays hidden until terminal reveal;
- bridge error follows typed recovery;
- Sasayaki hidden rendering uses `allowAutoScroll: false` and live callbacks cannot load/save while gated.

### Final verification

- focused coordinator and Reader tests;
- statistics/TTU/Google Drive targeted suite;
- full x64 test suite;
- x64 build;
- changed-file format and `git diff --check`;
- live exact-worktree Reader boundary check when safe automation can identify the correct process.

No real Google Drive mutation is performed without explicit authorization.
