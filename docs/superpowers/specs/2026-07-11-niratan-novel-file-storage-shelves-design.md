# Niratan Novel File Storage and Shelves Design

## Goal

Replace the SQLite-backed novel repository with Niratan-compatible file storage and add the shelves required by the normal bookshelf and statistics dashboard. Video persistence remains unchanged.

## Canonical Storage

The Books directory is the sole novel catalog. Each child directory containing a valid `metadata.json` represents one book.

Per-book files:

- `metadata.json`: book identity and display metadata.
- `bookmark.json`: chapter, chapter-relative progress, raw character count, and last-modified time.
- `bookinfo.json`: total raw characters and per-spine character ranges.
- `statistics.json`: TTU-compatible daily statistics.
- `highlights.json`, `sasayaki_match.json`, and `sasayaki_playback.json`: existing feature sidecars.
- A private EPUB copy when available plus the controlled extracted/render content needed by WebView2.

Books-directory files:

- `shelves.json`: ordered `BookShelf` records with a name and ordered book IDs.
- `book_order.json`: ordered IDs for unshelved books.

`metadata.json` follows Niratan's `BookMetadata` contract: stable ID, source title, optional private EPUB path, optional cover path, folder name, last access, optional renamed title, optional profile ID, and optional book language. Windows-only transient paths are resolved by the storage service and are not a second catalog.

## Service Boundaries

- `INovelBookStorageService` scans, loads, imports, updates, and deletes file-backed books.
- `INovelShelfService` owns shelf CRUD, shelf order, membership, and in-section order.
- `ILegacyNovelMigrationService` is the only component allowed to read legacy SQLite novel rows.
- `IVideoDataService` owns the remaining app SQLite methods.
- Novel methods are removed from `IDataService`; video consumers no longer depend on a mixed novel/video interface.

ViewModels expose state and commands. Services own filesystem IO, atomic serialization, normalization, and recovery.

## Import and Delete

EPUB import creates the private book directory, copies the source EPUB when available, extracts through the existing zip-slip-safe parser path, writes `metadata.json` atomically, and then publishes the book to the library. A partial import is not visible as a valid book until metadata exists.

Deleting a book first removes its ID from every shelf and `book_order.json`, then removes the private directory. Failure before directory deletion leaves a recoverable catalog entry; failure after catalog cleanup is reported and the next scan reconciles missing paths.

## Legacy SQLite Migration

Migration is versioned, idempotent, and fail-closed:

1. Create a recoverable backup of the existing database.
2. Read all legacy novel rows through the migration-only source.
3. Resolve each existing extracted directory and write missing `metadata.json` fields.
4. Prefer valid existing sidecars. Use SQLite progress and character values only when `bookmark.json` or `bookinfo.json` is absent or invalid.
5. Generate `book_order.json` from `ManualSortOrder`; initialize `shelves.json` as an empty list when absent.
6. Rescan the Books directory and compare IDs, titles, folders, profiles, progress, and character totals against the migration manifest.
7. Mark file storage active only after the entire validation succeeds.
8. Retire and remove novel tables after successful cutover. A failed migration retains the database and exposes the novel module in a clear read-only recovery state.

Fresh installs initialize only the video schema. Historic migration compatibility may remain in migration-only code, but it is not a runtime novel repository.

## Shelf Behavior

- Custom shelf names are trimmed, non-empty, and unique under ordinal-ignore-case comparison.
- Shelf order is the array order in `shelves.json`.
- Shelf book order is the `bookIds` order for that shelf.
- Unshelved order is `book_order.json`.
- Missing imported IDs are appended deterministically; deleted or unknown IDs are removed during normalization.
- Drag reorder changes order only inside the current section.
- Moving between sections is an explicit command from a context menu or multi-selection surface.
- Deleting a shelf never deletes books; its books become unshelved.
- The derived Reading section does not become a stored shelf.

## Error Handling

- All JSON writes use a unique temporary file followed by atomic replacement.
- Invalid `metadata.json` produces a recoverable book warning and does not crash the scan.
- Invalid `shelves.json` or `book_order.json` is preserved for recovery; the UI uses an empty normalized projection until the user repairs or resets it explicitly.
- Concurrent writers are serialized per book or global shelf file.
- Cancellation cannot leave a valid filename containing partial JSON.

## Testing and Acceptance

- Round-trip tests cover all canonical models and relative path resolution.
- Migration fixtures cover missing source EPUBs, existing sidecars, conflicting progress, missing folders, duplicate IDs, interrupted migration, retry, and validation failure.
- Shelf tests cover create, rename, reorder, delete, membership, multi-move, in-section reorder, normalization, and book deletion.
- Import tests prove a partial book is not published and EPUB extraction remains zip-slip safe.
- Acceptance requires a migrated library to rescan with the same visible books and progress and no runtime novel SQL calls.
