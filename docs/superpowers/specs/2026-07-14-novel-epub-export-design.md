# Novel EPUB Export Design

## Goal

Add an **Export EPUB…** action to each local novel's bookshelf context menu. The action exports the original EPUB retained in Niratan's private book storage without adding reading progress, bookmarks, statistics, highlights, or other sidecar data.

## User Experience

- Place **Export EPUB…** in the local novel context menu before **Move to shelf…**.
- Open the native Windows save-file picker with the novel title as the suggested file name and `.epub` as the only file type.
- Sanitize characters that Windows does not allow in file names. If sanitization leaves no usable name, fall back to `book.epub`.
- Treat picker cancellation as a normal no-op and show no notification.
- Show a success notification after the file is copied.
- Show a clear error notification when the stored EPUB is missing, the target is invalid, or the copy fails.
- Do not add the action to remote Google Drive books; those must be downloaded/imported before local export.

## Architecture

The feature follows the existing View → ViewModel → Service layering.

### View

`NovelLibraryPage.xaml` adds a localized `MenuFlyoutItem` with a stable AutomationId. It binds directly to an export command on `NovelLibraryPageViewModel` and passes the selected `NovelBookItemViewModel` as its command parameter. No export business logic is added to code-behind.

### ViewModel

`NovelLibraryPageViewModel` coordinates the operation:

1. Ask `IDialogService` for an EPUB destination using a sanitized suggested file name.
2. Return without notification when the user cancels.
3. Ask `INovelLibraryService` to export the selected book to that destination.
4. Translate the result into the existing success or error notifications.

The ViewModel does not read or copy files directly.

### UI Service

`IDialogService` gains a save-file picker method that accepts a suggested file name and extension. `DialogService` implements it with the existing `Microsoft.Windows.Storage.Pickers` APIs and the page's `AppWindowId`.

The picker is responsible only for selecting the destination. It does not copy the EPUB.

### Novel Service

`INovelLibraryService` gains an async export operation accepting a book ID and destination path. `NovelLibraryService` loads the book from storage, verifies that its private EPUB exists, validates the destination, and asynchronously copies the original file bytes to the selected destination.

Export is a read-only library operation and remains available even if novel metadata storage is in recovery/read-only mode. It must not alter metadata, sidecars, timestamps stored in Niratan, shelf membership, or the private source EPUB.

## File Naming

- Start from the displayed novel title.
- Replace invalid Windows file-name characters with a safe separator and trim trailing spaces or periods.
- Avoid producing an empty or dot-only base name.
- Ensure exactly one `.epub` extension in the suggestion.
- The user may edit the suggested name in the native picker.

## Error Handling

- Missing catalog entry: return a titled export failure.
- Missing private EPUB: return a titled “file not found” export failure.
- Empty, malformed, or source-equal destination: return an export failure without modifying the source.
- Cancellation: return no error notification.
- I/O and access errors: return the underlying useful message under a stable export-failure title and log the exception without exposing unrelated application state.

## Localization and Accessibility

- Add English and Simplified Chinese resources for the menu label and automation name.
- Use `NovelBookExportMenuItem` as the stable AutomationId.
- Keep the native picker keyboard- and screen-reader-accessible.

## Tests

Use test-first development and cover:

- XAML contains the localized local-book export item, AutomationId, command, and command parameter.
- ViewModel cancellation does not call the export service or show notifications.
- ViewModel success calls the service with the chosen path and shows success.
- ViewModel failure shows the service error.
- Suggested names sanitize invalid characters and fall back for unusable titles.
- Service export copies bytes exactly from the private EPUB to the selected destination.
- Service reports missing books, missing source files, invalid destinations, and source-equal destinations without damaging the private EPUB.
- Existing novel library tests, the full x64 test suite, build, and application launch remain successful.

## Out of Scope

- Embedding Niratan sidecars inside the EPUB.
- Exporting a bundle containing progress, bookmarks, statistics, or highlights.
- Repacking extracted chapter content into a new EPUB.
- Exporting books that exist only in Google Drive.
- Batch export.
