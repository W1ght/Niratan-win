# Niratan Video Library Parity Design

## Goal

Bring the Windows video library closer to Niratan's local video library without turning this increment into a full macOS port. The first version should make browsing feel like Niratan: a library sidebar, list/poster layout switching, generated thumbnails, and lightweight manual or smart collections.

## User-Visible Scope

- Add a toolbar segmented control for `List` and `Posters`.
- Keep the existing settings-style secondary menu, but align its sections with Niratan:
  - Library: Continue Watching, Unwatched, Finished, Recent, All Videos, Needs Review.
  - Organization: Favorites, Series, Collections, Folders, Tags.
- Add a dense list layout with thumbnail, title, folder/source metadata, modified date or file size, progress bar, and watched/resume text.
- Keep the poster grid layout, but use real thumbnails when available and a stable 16:9 placeholder when they are not.
- Add smart collection creation with match preview before saving.
- Keep manual collection support as a virtual organization layer.

## Non-Goals

- Do not move, rename, delete, or rewrite user video files during organization.
- Do not add internet metadata lookup, TMDb, TVDb, AniDB, Python, Node, ML models, or bundled anime rules.
- Do not rebuild the video player UI, subtitle mining panel, or Anki media export in this increment.
- Do not port Niratan's full right-side inspector. Windows can use a focused dialog or compact panel for collection creation and metadata actions.

## Architecture

The implementation stays inside the existing WinUI MVVM structure:

```text
VideoLibraryPage.xaml
  -> VideoLibraryPageViewModel
  -> IVideoLibraryService / IDataService / thumbnail service
```

The ViewModel remains the source of UI state: selected library mode, selected layout mode, search text, sort option, rows, sections, and collection commands. Services handle filesystem scan, thumbnail IO, persistence, and collection storage. XAML code-behind remains UI-only.

## Data Model

### Video Items

`VideoItem` keeps the existing file identity and playback fields. It gains lightweight metadata needed for Niratan-style browsing:

- `FileSizeBytes`
- `ModifiedAt`
- `ThumbnailPath`
- `IsFavorite`
- `CollectionIds` or service-backed membership

Existing `PosterPath` remains the highest-priority artwork source. `ThumbnailPath` is for generated frame previews.

### Collections

Collections are stored separately from `VideoItem` rows:

- `VideoCollection`
  - `Id`
  - `Name`
  - `Kind`: `Manual` or `Smart`
  - `RuleJson`
  - `CreatedAt`
  - `UpdatedAt`
  - `ManualSortOrder`

- `VideoCollectionItem`
  - `CollectionId`
  - `VideoId`

Manual collections use `VideoCollectionItem`. Smart collections use rules and are evaluated against already-loaded video rows, playback state, tags, folders, subtitle binding, and path metadata.

## Smart Rules

Smart collection V1 follows Niratan's explainable rule set:

- File name contains text.
- Parent folder contains text.
- Full path contains text.
- Tag contains text.
- Has bound subtitle.
- Playback state is unwatched, in progress, or finished.

Multiple rules use `All` matching for V1. This keeps the UI simple and deterministic. The create dialog shows a preview count plus up to five matching video titles before saving.

## Thumbnail Pipeline

Artwork resolution order:

1. Existing poster image next to the video or folder cover.
2. Cached generated thumbnail.
3. Stable 16:9 placeholder.

Generated thumbnails use the existing mpv-backed screenshot capability or a small service built around the same playback infrastructure. The thumbnail scheduler runs one job at a time, deduplicates concurrent requests for the same file, writes cache files atomically, and skips generation while the player window is open.

Cache identity uses file path, file size, and modified timestamp. If a file changes, the old thumbnail is ignored and a new one can be generated.

## UI Design

### Header

The video page header contains:

- Page title and result count.
- Search box.
- Sort menu.
- Segmented layout switcher: List / Posters.
- Refresh or scan folder action.
- Import video action.
- Create smart collection action when Collections is selected or always available in the command area.

### Sidebar

The secondary sidebar keeps the Settings-like layout already added, but expands to Niratan's modes. Section names and entries are localized in English and Simplified Chinese.

### List Layout

Each row is a single video:

- 16:9 thumbnail at fixed size.
- Primary title with one-line truncation.
- Metadata line: source folder, parent folder, size, modified date.
- Progress line: progress bar plus watched, remaining, or resume text.
- Context actions: play, play from beginning, mark watched, clear progress, reveal file, add to collection.

### Poster Layout

Poster cards keep fixed 16:9 artwork, two-line title, metadata text, bottom progress bar, and context actions. Grid columns use stable min/max widths so thumbnails and text do not reflow unpredictably.

### Collection Creation

Smart collection creation uses a dialog:

- Name field.
- Rule field selector.
- Rule value field or playback-state selector.
- Preview count and sample titles.
- Save and Cancel buttons.

Manual collection creation can be added through the same collection command surface, but the first priority is smart collection creation because it is the missing Niratan feature called out by the user.

## Error Handling

- Thumbnail generation failures are silent for the main UI and leave the placeholder visible.
- Missing files remain visible in Missing or Needs Review views until refresh or removal.
- Invalid smart rules keep Save disabled and show zero preview matches.
- Database migration preserves existing video rows and treats missing collection data as empty.
- Scan, thumbnail, and collection operations never block opening a video.

## Testing

Add focused tests before implementation:

- ViewModel tests for layout switching, new library modes, smart rule matching, preview rows, and collection filtering.
- Storage tests for collection tables, smart rule persistence, manual membership persistence, and migration from the current video schema.
- Thumbnail service tests for cache-key stability, generated path preference, failure fallback, and scheduler deduplication.
- XAML/static asset tests for segmented layout control, list view template, create smart collection command, automation IDs, and localization keys.

Manual verification after tests:

- Build with `dotnet build -p:Platform=x64`.
- Run `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64`.
- Start Hoshi, scan a video folder, switch List/Posters repeatedly, create a smart collection, and confirm thumbnails appear without delaying video open or restore.

## Acceptance Criteria

- The video library visually supports both Niratan-style list and poster browsing.
- Videos without external posters get cached preview thumbnails after background generation.
- Smart collections can be created, previewed, selected, and deleted without changing user files.
- Continue Watching, Unwatched, Finished, Recent, All Videos, Needs Review, Favorites, Series, Collections, Folders, and Tags are represented in the secondary navigation.
- All new visible strings have English and Simplified Chinese resources.
- Existing playback restore behavior remains fast and is not slowed by thumbnail generation.
