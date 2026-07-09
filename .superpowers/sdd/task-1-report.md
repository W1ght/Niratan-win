# Task 1 Report: Domain Models And Storage

## Scope Completed

Implemented the Task 1 storage/domain surface in the task worktree:

- Added `VideoLibraryLayoutMode`.
- Updated `VideoLibraryView` to the Task 1 enum shape.
- Extended `VideoItem` storage fields for file metadata, favorites, and thumbnails.
- Added `Migration_012` and registered it in `DatabaseMigrator`.
- Extended `IDataService` with video collection, thumbnail, and favorite APIs.
- Extended `DataService` with:
  - isolated connection-string constructor for tests
  - video metadata persistence for `FileSizeBytes`, `ModifiedAt`, `ThumbnailPath`, `IsFavorite`
  - video collection CRUD and membership persistence
  - thumbnail/favorite update methods
- Added the required migration and CRUD tests in `Hoshi.Tests/Services/Storage/VideoDataServiceTests.cs`.

## TDD Trail

### Red

Added the required tests from the brief, then ran:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoDataServiceTests"
```

Initial failure was expected and showed the missing Task 1 surface, including:

- `VideoCollection`
- `VideoCollectionKind`
- `DatabaseMigrator`
- `DataService`

### Green Work

Implemented the required models, migration, and storage methods listed above.

## Verification

Re-ran the focused storage test command multiple times after implementation.

Latest run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoDataServiceTests"
```

Result: blocked before test execution by unrelated in-progress video-library UI/ViewModel work already present in the task worktree.

Latest failing diagnostics:

- `Hoshi/Views/Pages/VideoLibraryPage.xaml.cs`: missing `InitializeComponent`
- `Hoshi/Views/Pages/VideoLibraryPage.xaml.cs`: missing `VideoLibrarySecondaryNavigationView`
- `Hoshi/Views/Pages/VideoLibraryPage.xaml.cs`: missing `VideoLibraryAllNavItem`
- `Hoshi/Views/Pages/VideoLibraryPage.xaml.cs`: `VideoLibraryPageViewModel` missing `SelectLibraryViewCommand`
- WinUI XAML compiler exits with code 1

Additional observed workspace state affecting buildability:

- `Hoshi/ViewModels/Pages/VideoLibraryPageViewModel.cs` is currently deleted in the worktree (`git status` shows `D`), which is consistent with the XAML/code-behind breakage above.

## Minimal Non-Task Adjustments Made To Reach Verification

These were outside the Task 1 ownership list but were compiler-forced and intentionally minimal:

- `Hoshi/Services/Video/VideoThumbnailService.cs`
  - aligned the concrete service signature with the already-present `IVideoThumbnailService` contract in the worktree
- `Hoshi/Models/Video/VideoSmartRule.cs`
  - fixed missing `using System;`

I did not repair the broader video-library XAML/ViewModel slice because that exceeds Task 1 ownership and appears to be parallel work in progress.

## Self-Review Notes

- Kept storage changes scoped to the files named in the brief, plus the two minimal compiler-forced fixes above.
- Preserved existing dirty video-library/playback changes instead of reverting or refactoring around them.
- `DataService` collection membership writes are transactional as required.
- `DataService` smart-rule persistence uses `System.Text.Json` and hydrates `SmartRules` plus `ItemIds` on read.
- `CollectionName` remains in video read/write SQL during this increment, per brief.

## Remaining Risk / Follow-Up

Once the video-library XAML/ViewModel slice is restored to a buildable state, re-run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoDataServiceTests"
```

That is the remaining verification gap for this task.
