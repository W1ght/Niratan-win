# Niratan Bookshelf Regression Fixes Design

## Goal

Restore a usable Niratan-aligned novel bookshelf by making every local book card open reliably, always projecting the derived Reading shelf when it has books, rendering every shelf as an adaptive multi-row grid, adding Google Drive covers and parallel imports, and preventing the statistics dashboard from blocking the UI.

## Confirmed User Experience

- Section order is Reading, custom shelves, Google Drive, then Unshelved.
- Reading appears whenever at least one book is started but unfinished. It is not controlled by `BookshelfShowReading` or another visibility setting.
- A Reading book remains in its custom shelf or Unshelved section as well. Reading is a derived projection, matching Niratan rather than an exclusive storage location.
- Every section is expanded as an adaptive multi-row grid. The Windows implementation keeps Niratan's section and card semantics but does not use Niratan's default compact single-row collapsed presentation.
- Google Drive cards show cached remote covers when available and retain a placeholder when no usable cover exists.
- Up to three different Google Drive books can download and import concurrently. Each card exposes its own progress and failure state.
- Entering statistics immediately shows a responsive loading surface. Sidecar loading and snapshot construction do not occupy the UI thread.

## Root Causes and Boundaries

### Local book activation

The shared local-book template currently handles `Button.Click` in code-behind and recovers the item from `Button.DataContext`. A UI Automation invocation of an Unshelved card reaches the button but remains on the bookshelf, proving that this implicit data-context handoff is not a reliable activation contract for the current `ItemsRepeater` surface.

The replacement binds the button directly to `NovelLibraryPageViewModel.OpenNovelCommand` and passes the typed `NovelBookItemViewModel` through `CommandParameter`. Code-behind no longer discovers business input from the visual tree.

### Reading shelf

`NovelLibraryPageViewModel` already computes started-but-unfinished books, but only when `AppSettings.BookshelfShowReading` is true. The setting defaults to false and the Windows shelf-management UI provides no visible toggle. The derived shelf therefore does not appear in normal use.

The projection will always evaluate Reading and add it only when non-empty. The obsolete setting is no longer consulted by bookshelf projection. Existing serialized settings remain readable; removal of the persisted property is unnecessary for this fix.

### Google Drive covers and imports

The remote file model already identifies `cover_*` files and carries `thumbnailLink`, but no Windows service downloads or caches those images and `RemoteNovelBookItemViewModel` has no cover state.

`DownloadRemoteBookCommand` is one `AsyncRelayCommand` whose default execution policy rejects overlapping invocations. Its work also shares the same cancellation source that `LoadNovelsAsync` replaces and cancels, so simply enabling concurrent command execution would cause one completed import to cancel other downloads.

### Statistics responsiveness

Statistics activation awaits cache-key creation, sidecar parsing, snapshot construction, and initial projection from a UI command. The newest dashboard view also has both XAML adaptive triggers and a `SizeChanged` code path that rewrites the same grid widths and placements. The current small local dataset does not remain hung, but these two paths create avoidable UI-thread and layout pressure and explain the data- and size-dependent freeze report.

The fix treats this as a responsiveness regression: expensive snapshot work runs away from the UI thread, and only one guarded adaptive-layout path remains.

## Architecture

### Unified bookshelf sections

`NovelLibraryPageViewModel` remains responsible for projecting domain books into display sections. `NovelShelfSectionViewModel` is expanded to distinguish local and Google Drive sections without untyped dictionaries. The projection produces:

1. `reading`, when non-empty;
2. each persisted custom shelf in persisted order;
3. `google-drive`, when remote books are available;
4. `unshelved`, including an empty section when needed for stable page structure.

Local Reading entries use the current library sort option. Custom shelves and Unshelved retain their existing manual-order behavior. Google Drive uses title order when the selected option is Manual, matching Niratan.

The page owns one vertical `ScrollViewer`. Each section uses an `ItemsRepeater` with `UniformGridLayout`, a minimum card width of 180 effective pixels, and row and column gaps consistent with the existing local card. There are no nested horizontal shelf scrollers. Narrow widths naturally reduce the column count to one or two without introducing a second vertical scroll owner.

### Explicit card commands

The local card template binds its root button to the page ViewModel command and uses the template item as its command parameter. Context actions use the same explicit parameter contract. Keyboard activation and UI Automation Invoke therefore follow the same command path as pointer activation.

The page code-behind retains UI-only responsibilities such as drag/drop and dialogs. It does not inspect a card's `DataContext` to decide which book to open.

### Remote cover cache

A focused Google Drive cover-cache service owns remote image IO and cache paths. It consumes the existing `TtuRemoteFile` cover metadata, normalizes thumbnail size to Niratan's 768-pixel request, sends authenticated requests when necessary, validates that a non-empty image response was received, and writes through a unique temporary file followed by atomic replacement.

Cache identity includes the remote cover file ID. Replacing a cover therefore produces a new entry instead of silently reusing stale bytes. Invalid or unreadable cache files are removed and fetched again. Missing covers and failed cover requests return no path and do not fail the remote-book listing.

`RefreshRemoteBooksAsync` publishes the filtered remote-book collection first. It then hydrates covers with bounded parallelism and updates each `RemoteNovelBookItemViewModel` on the captured UI context. Cover hydration uses its own cancellation source tied to the page lifetime, not the local catalog-load token.

### Parallel Google Drive imports

The generated download command enables concurrent executions. A `SemaphoreSlim` limits active imports to three while preserving one state object per remote card:

- waiting for a slot;
- downloading/importing with progress;
- completed and removed from the remote section;
- failed and available for retry.

Page lifetime cancellation stops queued and active imports. Local catalog loads, remote listing, cover hydration, and imports use separate cancellation sources so replacing one operation cannot cancel unrelated work.

Each successful import refreshes the file-backed local catalog and shelf projection through a serialized refresh boundary. Multiple near-simultaneous completions may coalesce or run sequentially, but they never cancel an import that is still active. A failed import resets only its own card and reports the existing visible notification.

### Responsive statistics activation

`EnterStatisticsAsync` switches surfaces immediately and lets the loading indicator render before expensive work starts. Cache lookup, sidecar reads, deserialization, deduplication, and snapshot aggregation run without a UI synchronization-context dependency. Only `ApplySnapshot` and observable display-collection replacement run on the UI thread.

The dashboard keeps an activation generation and linked cancellation token. Leaving the dashboard or re-entering invalidates prior work, and a stale completion cannot update the active view.

The dashboard has one width-driven adaptive implementation. It records the current one-, two-, or three-column mode and changes grid widths and placements only when crossing the 840 or 1260 effective-pixel breakpoints. The duplicate XAML/code-behind layout ownership is removed. This code remains UI-only and contains no statistics business logic.

## Failure Handling

- A missing local command parameter performs no navigation and does not crash.
- A cover failure leaves the cloud card usable with its placeholder and does not stop other cover tasks.
- A corrupt cached cover is deleted and retried once through the normal cache-miss path.
- One remote import failure does not cancel queued or active imports for other books.
- Navigation away cancels page-owned cloud work and prevents later collection mutation.
- Statistics cancellation is silent when caused by navigation. Other load failures leave the dashboard responsive and surface a recoverable error instead of trapping the user on a busy indicator.
- No operation modifies `native/hoshidicts/`, introduces a second database, or moves IO into a ViewModel.

## Verification

### Automated tests

- Projection tests assert the exact section order and that Reading is present without `BookshelfShowReading` when `0 < overall progress < completion`.
- Projection tests assert completed and untouched books do not appear in Reading, while Reading books remain in their persisted or Unshelved section.
- XAML asset tests assert all sections use wrapping `UniformGridLayout`, horizontal shelf scrollers are absent, and local card activation uses an explicit command parameter.
- UI Automation invokes cards from Reading, a custom shelf, and Unshelved and waits for the reader surface.
- Cover-cache tests cover cache hits, authenticated misses, atomic writes, invalid-cache recovery, missing covers, and isolated failures under parallel hydration.
- Remote import tests hold three fake imports open simultaneously, prove a fourth waits, and prove completion or failure of one does not cancel the others.
- Statistics tests prove activation yields a loading state, stale generations are ignored, work is cancelable, and repeated same-breakpoint size changes do not reapply card placement.

### Runtime verification

1. Build with `dotnet build -p:Platform=x64`.
2. Run `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64`.
3. Launch with `build-and-run.ps1` and confirm a responsive top-level Hoshi window.
4. Verify the Reading, custom, Google Drive, and Unshelved ordering and multi-row wrapping at wide, medium, and narrow widths.
5. Invoke local cards through UI Automation and verify the reader opens.
6. Refresh Google Drive, confirm covers appear progressively, start at least four downloads, and confirm three active per-card progress indicators with the fourth queued.
7. Enter and leave statistics repeatedly with and without a cache, resize across both breakpoints, and confirm the window remains responsive and old loads do not overwrite the current surface.
8. Check light, dark, keyboard, and UI Automation behavior for all changed controls.

## Non-Goals

- Changing the EPUB renderer, reader bridge, dictionary pipeline, SQLite/video storage, or TTU file formats.
- Adding a user-configurable download concurrency setting.
- Recreating Niratan's compact collapsed single-row shelf preview, because the confirmed Windows behavior requires every section to be an expanded multi-row grid.
- Downloading full EPUB contents merely to derive a missing Google Drive cover.
