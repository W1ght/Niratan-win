# Bookshelf Mark Read Design

## Goal

Add a localized, icon-free **Mark Read** action to each local novel card's context menu, matching Niratan's visible behavior and bookmark semantics.

## User experience

- The action is available from every local novel card context menu, including books already at 100%.
- The menu item has no icon, matching the current Hoshi Windows bookshelf menu treatment.
- Selecting it opens a localized confirmation whose title is `Mark "{book title}" as read?` / `将“{书名}”标记为已读？`.
- The dialog uses localized `Confirm` / `Cancel` buttons and defaults to cancel.
- Cancelling performs no write and no catalog refresh.
- Success silently refreshes the local catalog and shelf projections; no success notification is shown.
- Failure shows the existing error notification surface.

## Bookmark behavior

`INovelLibraryService.MarkReadAsync(bookId, ct)` owns the operation. The service:

1. Rejects writes while the novel library is in read-only recovery mode.
2. Resolves the book's private root and loads `bookinfo.json` through `INovelBookSidecarService`.
3. If `bookinfo.json` is absent, returns success without writing, matching Niratan's early-return behavior.
4. Writes one canonical `bookmark.json` with:
   - `ChapterIndex`: maximum non-null `SpineIndex` from `ChapterInfo`, or `0` when none exist.
   - `Progress`: `1`.
   - `CharacterCount`: the book-level `CharacterCount` from `bookinfo.json`.
   - `LastModified`: current UTC time.
5. Does not mutate statistics and does not trigger sync.

## Architecture

- XAML declares the localized menu item and routes its click through UI-only code-behind.
- `NovelLibraryPageViewModel` owns confirmation, service execution, errors, and refresh.
- `NovelLibraryService` owns sidecar IO and read-only enforcement.
- `DialogService` gains a four-argument confirmation overload so non-delete confirmations can supply correct button labels while the existing two-argument delete behavior remains compatible.

## Verification

- Service tests cover exact bookmark values, maximum-spine selection, and absent `bookinfo.json`.
- ViewModel tests cover cancellation, successful refresh without notification, and failures.
- asset tests cover context-menu wiring, absence of icons, and English/Chinese resources.
- The complete x64 test suite, x64 build, and real WinUI launch must pass.
