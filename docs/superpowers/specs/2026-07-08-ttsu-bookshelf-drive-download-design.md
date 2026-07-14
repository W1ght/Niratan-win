# TTU Bookshelf Drive Download Design

## Goal

Align the Windows novel library with Niratan's bookshelf sync behavior: the home/bookshelf page can refresh Google Drive, show remote TTU books that are not imported locally, and download those books into the local EPUB library.

## Scope

- Add a bookshelf command next to local EPUB import for refreshing Google Drive books.
- Read remote book folders under `ttu-reader-data`.
- Show only remote books that have `bookdata_*.zip` and are not already present locally by TTU-sanitized title.
- Download a selected remote book's `bookdata_*.zip`.
- Convert the TTU book data package into a temporary EPUB-compatible import source, import it through the existing novel library service, then import remote progress, statistics, and Sasayaki playback sidecars when present.
- Keep cloud delete out of this increment.

## Niratan Alignment

Niratan does not store raw EPUBs in Drive. It stores `bookdata_1_6_...zip` inside each book folder. Importing a cloud book downloads that zip, converts the TTU package back into an EPUB/book folder, imports progress/statistics/audio sidecars, removes the remote placeholder from the bookshelf, and refreshes local books.

## Architecture

- `ITtuSyncRemoteStore` remains the Drive transport boundary and grows remote-book listing and bookdata download methods.
- A new `ITtuBookImportService` handles TTU package conversion and local import orchestration so the ViewModel does not know Drive file formats.
- `NovelLibraryPageViewModel` owns UI state: remote book cards, refresh/download commands, progress/status.
- XAML adds a refresh/sync command and renders remote book cards in the existing library grid style.

## Error Handling

- If Google Drive is disconnected, refresh shows a notification and does not open auth UI.
- Remote books without `bookdata_*.zip` are ignored.
- Download failures keep the remote item visible and show an error notification.
- Duplicate local titles are filtered before display and after successful import.

## Verification

- Unit tests cover Drive remote listing/download, TTU import orchestration, and bookshelf ViewModel command behavior.
- Asset tests cover the new homepage sync button and remote book item bindings.
- Final verification runs `dotnet build -p:Platform=x64`, full `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64`, and WinUI launch smoke check.
