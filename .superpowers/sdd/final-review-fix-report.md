# Final Review Fix Report

Status: DONE

## Fixes

- Added regression coverage for poster-backed thumbnail paths and changed the video library reload decision to ignore poster artwork. Cached/generated thumbnail paths are persisted before they can trigger a reload.
- Preserved existing `IsFavorite` during `UpsertVideoAsync` when a scan/import item carries the default `false` value.
- Persisted manual collection membership through `CreateManualCollectionAsync` by calling `SetVideoCollectionItemsAsync`.
- Populated `FileSizeBytes` and `ModifiedAt` for both single video imports and folder scans.
- Added a localized smart-rule field selector bound to `SelectedSmartRuleField` through `AvailableSmartRuleFields`.
- Removed the visible duplicate `Watched` navigation item while keeping internal `Watched` compatibility mapped to `Finished`.
- Kept `PlaybackStateSaved` subscribed through the WinUI `Closed` handler so late async close saves can still raise `LibraryChanged`; thumbnail resume on close is preserved.

## Verification

- RED: focused test run failed before implementation because `VideoLibraryPageViewModel.AvailableSmartRuleFields` did not exist.
- GREEN focused: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoLibraryPageViewModelTests|FullyQualifiedName~VideoDataServiceTests|FullyQualifiedName~VideoLibraryServiceTests|FullyQualifiedName~VideoLibraryPageAssetTests"` passed, 37/37.
- Thumbnail focused: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoThumbnailServiceTests"` passed, 3/3.
- Build: `dotnet build -p:Platform=x64` succeeded with existing NU1903 warnings for `SQLitePCLRaw.lib.e_sqlite3`.
- Full tests: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64` passed, 511/511, with the same NU1903 warnings.

## Concerns

- No manual app smoke was run in this pass; verification is build plus automated tests.
- The playback close race is covered by a source-level regression guard because `VideoPlayerWindow` close/event ordering is not cleanly unit-testable through the current WinUI service seam.
