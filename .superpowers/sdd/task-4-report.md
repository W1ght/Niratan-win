# Task 4 Report: ViewModel Layout, Navigation, and Smart Collection Commands

## Scope

Implemented Task 4 in the ViewModel/test layer for the Niratan-style video library worktree:

- `Hoshi/ViewModels/Pages/VideoLibraryPageViewModel.cs`
- `Hoshi.Tests/ViewModels/Pages/VideoLibraryPageViewModelTests.cs`
- `Hoshi.Tests/ViewModels/Components/VideoItemViewModelTests.cs`

`Hoshi/ViewModels/Components/VideoItemViewModel.cs` already matched the required Task 4 metadata surface in this worktree, so no additional functional edit was needed there.

## What Changed

### VideoLibraryPageViewModel

- Added bindable layout state:
  - `SelectedLayoutMode`
  - `IsListLayout`
  - `IsPosterLayout`
  - `SelectLayoutCommand`
- Added smart-collection draft state:
  - `SmartCollectionNameDraft`
  - `SelectedSmartRuleField`
  - `SmartRuleValueDraft`
  - `SmartCollectionPreviewRows`
- Added `CreateSmartCollectionCommand`
  - creates a smart collection through `IVideoLibraryService`
  - clears draft state
  - reloads collections/videos
  - switches to `VideoLibraryView.Collections`
- Expanded view filtering to cover:
  - `Unwatched`
  - `Finished`
  - `Recent`
  - `Favorites`
  - `NeedsReview`
  - `Series`
  - collection-id based collection filtering
- Loaded collections alongside videos and rebuilt collection filter rows from service-backed collections when available.
- Added background thumbnail generation for the first 24 visible rows via `IVideoThumbnailService`, with a guarded reload when a thumbnail path changes.
- Preserved legacy `"Watched"` navigation tag compatibility by mapping it to `Finished` if needed.

### Tests

- Added the required Task 4 red/green coverage:
  - layout mode toggles list/poster flags
  - smart collection preview filters correctly
  - smart collection command creates collection and reload path
  - artwork path preference component test
- Extended the page-viewmodel recording doubles with collection and thumbnail support required by the new tests.

## TDD Notes

1. Added the new Task 4 tests first.
2. Ran the focused test command and confirmed a red pass caused by missing Task 4 surface.
3. Implemented the minimum ViewModel changes to satisfy those tests.
4. Re-ran the same focused test command to green.

## Verification

Command run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoLibraryPageViewModelTests|FullyQualifiedName~VideoItemViewModelTests"
```

Result:

- Passed: 17
- Failed: 0
- Skipped: 0

## Integration Overlap / Concerns

- This worktree already contained parallel Task 3/adjacent video-library work, including richer smart-rule models, thumbnail service work, and in-flight `VideoLibraryPage` XAML/code-behind changes.
- I aligned Task 4 to the existing service-side smart matcher and thumbnail service contract rather than introducing a second competing implementation.
- `VideoLibraryPage.xaml` had been deleted in the worktree during parallel work. I restored the tracked file locally to unblock XAML compilation during verification; it is back to a non-diff state and is not part of the intended Task 4 delta.
- I did not implement storage or thumbnail internals beyond the ViewModel-facing contract use required by this task.
- I did not perform a full app launch/UI verification loop; verification for this task stayed focused on the requested ViewModel/component tests.

## Commit

Commit created after verification:

- `feat: add video library layout and smart collection state`
