# Novel EPUB Module Design

Date: 2026-05-19
Status: Approved direction, ready for implementation planning

## Goal

Add a separate Novel module to Hoshi while keeping the existing manga/comic module intact. The first implementation phase supports local EPUB files only. The public product domain should be named `Novel`; EPUB should appear only in lower-level import, parsing, and rendering services.

The module should use Hoshi as the Windows shell: WinUI 3, MVVM, dependency injection, Dapper, SQLite, and the existing navigation style. It should not reuse comic services or comic database tables for novel behavior.

## Non-Goals

The first phase will not implement Yomitan dictionary lookup, hoshidicts interop, AnkiConnect, highlights, bookmarks, OPDS, DRM, cloud sync, or non-EPUB formats. It will also not replace the comic reader or merge comic and novel reader state.

## Naming

Use `Novel` for user-facing and domain-facing names:

- `NovelBook`
- `NovelReadingProgress`
- `NovelReaderSettings`
- `NovelLibraryPage`
- `NovelReaderPage`
- `INovelLibraryService`

Use `Epub` only where the code is format-specific:

- `INovelEpubImportService`
- `EpubMetadataExtractor`
- `EpubCoverExtractor`
- `NovelEpubReaderHost`

This keeps the near-term EPUB-only scope clear without making the whole module impossible to extend later.

## Architecture

The first phase adds a parallel vertical slice:

```text
WinUI shell
  -> NovelLibraryPage / NovelLibraryViewModel
  -> INovelLibraryService
  -> IDataService novel methods or a focused novel repository layer
  -> SQLite novel tables

Novel import
  -> file picker
  -> INovelEpubImportService
  -> EPUB metadata and cover extraction
  -> NovelBook persisted to SQLite
```

The existing comic flow remains separate:

```text
Comic pages/viewmodels/services
  -> IComicService
  -> comic tables and comic source plugins
```

The shared app shell may route to both domains, but the domain services should not call each other.

## Data Model

Add separate SQLite tables for novels.

`novel_books` stores imported EPUB records:

```sql
CREATE TABLE novel_books (
  id TEXT PRIMARY KEY,
  title TEXT NOT NULL,
  author TEXT,
  file_path TEXT NOT NULL UNIQUE,
  cover_path TEXT,
  imported_at TEXT NOT NULL,
  last_opened_at TEXT,
  language TEXT,
  unique_identifier TEXT
);
```

`novel_reading_progress` stores the current reader location:

```sql
CREATE TABLE novel_reading_progress (
  book_id TEXT PRIMARY KEY,
  location_json TEXT NOT NULL,
  progression REAL,
  chapter_href TEXT,
  updated_at TEXT NOT NULL
);
```

`novel_reader_settings` stores global and per-book settings:

```sql
CREATE TABLE novel_reader_settings (
  scope TEXT NOT NULL,
  scope_id TEXT NOT NULL,
  settings_json TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  PRIMARY KEY (scope, scope_id)
);
```

The first implementation can add the tables through the existing migration system. Duplicate imports are prevented by normalized `file_path`, because local EPUB files are the only supported source in this phase.

## First UI Slice

Add a Novel navigation entry that opens `NovelLibraryPage`.

The library page should support:

- importing a local `.epub` file
- showing imported title, author, cover when available, and file path fallback when metadata is missing
- opening a selected book into a `NovelReaderPage` placeholder

The initial reader page is a placeholder for this first slice. It should be clearly separated from the existing comic `ReaderPage` and should not attempt to render EPUB body content yet.

## EPUB Handling

Short-term supported format is EPUB only. Import should validate file extension and handle invalid or unreadable files with a user-facing error through the existing notification/dialog pattern.

Metadata extraction should prefer standard EPUB package metadata:

- title
- creator/author
- language
- unique identifier
- cover reference when available

The implementation should avoid loading full book content into memory during import. Full reading/rendering belongs to the later WebView2 + foliate-js slice.

## Reader Direction

The novel reader should eventually use WebView2 and foliate-js as described in `agents.md`. It must not render novel body text with WinUI text controls except for temporary placeholders.

The reader bridge should use typed messages with version and type fields when it is introduced. JavaScript should handle rendering, selection, coordinates, and events only; dictionary and Japanese language logic stay in C# services or native backends.

## Error Handling

Import failures should not crash the app. Expected failure cases:

- selected file is not `.epub`
- file is missing or inaccessible
- EPUB metadata is absent or malformed
- cover extraction fails
- database write fails

Metadata and cover failures may fall back to title-from-file and no-cover UI. File access and database failures should surface through the existing notification/dialog pattern.

## Testing

Add tests around non-UI logic:

- EPUB file validation
- metadata fallback behavior
- novel repository or data-service methods
- migration creates expected novel tables
- duplicate import behavior by normalized file path

WinUI page rendering can remain manually verified for this first slice unless the project already has a stable UI automation pattern.

## Implementation Order

1. Add novel domain models and service interfaces.
2. Add SQLite migration for novel tables.
3. Add novel storage methods or focused repositories.
4. Add EPUB import service with minimal metadata fallback.
5. Add `NovelLibraryViewModel` and tests for import/list behavior.
6. Add `NovelLibraryPage` and navigation entry.
7. Add a separate `NovelReaderPage` placeholder route.
8. Build and run the app; verify comic flows still compile and tests pass.
