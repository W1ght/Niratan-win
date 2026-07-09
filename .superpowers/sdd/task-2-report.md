# Task 2 Report: Smart Collection Matching And Library Service

## Status

Implemented Task 2 service and matcher changes, plus the smallest compile-shim overlap required to express the feature against in-flight Task 1 and thumbnail work.

## Files Changed

Task-owned files:

- `Hoshi/Services/Video/VideoSmartCollectionMatcher.cs`
- `Hoshi/Services/Video/IVideoLibraryService.cs`
- `Hoshi/Services/Video/VideoLibraryService.cs`
- `Hoshi.Tests/Services/Video/VideoSmartCollectionMatcherTests.cs`
- `Hoshi.Tests/Services/Video/VideoLibraryServiceTests.cs`

Minimal overlap / compile shims:

- `Hoshi/Models/Video/VideoCollection.cs`
- `Hoshi/Models/Video/VideoSmartRule.cs`
- `Hoshi/Models/Video/VideoSmartRuleField.cs`
- `Hoshi/Models/Video/VideoSmartRuleMatch.cs`
- `Hoshi/Models/Video/VideoLibraryView.cs`
- `Hoshi/Services/Storage/IDataService.cs`
- `Hoshi/Services/Video/IVideoThumbnailService.cs`
- `Hoshi/Services/Video/VideoThumbnailService.cs`

## What Task 2 Adds

- Smart collection rule evaluation for:
  - `FileName`
  - `ParentFolder`
  - `Path`
  - `Tag`
  - `HasBoundSubtitle`
  - `PlaybackState`
- Library-service APIs for:
  - `GetCollectionsAsync`
  - `CreateManualCollectionAsync`
  - `CreateSmartCollectionAsync`
  - `DeleteCollectionAsync`
  - `SetFavoriteAsync`
- Collection normalization rules from the brief:
  - blank names become `"Untitled Collection"`
  - smart-rule values are trimmed
  - blank value rules are dropped unless the match mode is `IsTrue`

## TDD Notes

1. Added matcher and service tests first.
2. Ran the focused command:

   `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSmartCollectionMatcherTests|FullyQualifiedName~VideoLibraryServiceTests"`

3. First red run exposed unrelated workspace compile drift before reaching Task 2 code:
   - `VideoLibraryView.Watched` mismatch
   - `IVideoThumbnailService` / `VideoThumbnailService` return-type mismatch
   - collection-model drift between existing Task 1 storage work and Task 2 brief
4. Patched those minimal seams, then reran the same focused command.

## Verification Result

The focused test command is still blocked by unrelated parallel UI/XAML work outside Task 2 ownership:

- `Hoshi/Views/Pages/VideoLibraryPage.xaml` is deleted in the current worktree.
- `Hoshi/Views/Pages/VideoLibraryPage.xaml.cs` still compiles as part of the app project.
- There is also an in-flight `Hoshi.Models.Video.VideoSmartCollectionMatcher`, so `VideoLibraryPageViewModel.cs` now sees an ambiguous `VideoSmartCollectionMatcher` reference between model and service namespaces.
- The WinUI/XAML build therefore fails before the focused service tests can complete.

Latest failing command:

`dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSmartCollectionMatcherTests|FullyQualifiedName~VideoLibraryServiceTests"`

Latest blockers:

- `Hoshi/ViewModels/Pages/VideoLibraryPageViewModel.cs(104,37): error CS0104: VideoSmartCollectionMatcher is ambiguous between Hoshi.Services.Video.VideoSmartCollectionMatcher and Hoshi.Models.Video.VideoSmartCollectionMatcher`
- `Hoshi/ViewModels/Pages/VideoLibraryPageViewModel.cs(594,20): error CS0104: VideoSmartCollectionMatcher is ambiguous between Hoshi.Services.Video.VideoSmartCollectionMatcher and Hoshi.Models.Video.VideoSmartCollectionMatcher`
- followed by WinUI XAML compiler failure (`XamlCompiler.exe` exit code 1)

## Self-Review

- `VideoSmartCollectionMatcher` follows the task brief’s exact field and playback-token rules.
- `VideoLibraryService` keeps collection/favorite work in the service layer and delegates persistence to `IDataService`.
- The compile-shim changes are intentionally narrow and documented here because Task 1 / thumbnail work is already in motion in this worktree.
- I did **not** restore or overwrite the deleted `VideoLibraryPage.xaml`, and I did **not** retarget `VideoLibraryPageViewModel` to one matcher implementation, because both would mean editing other agents’ in-flight UI/ViewModel work outside Task 2 ownership.

## Follow-Up Needed

1. Resolve the parallel UI/XAML break in this worktree so the app project builds again.
2. Rerun the focused Task 2 test command above.
3. If Task 1 storage lands with different collection contracts, recheck the minimal shim files listed above and collapse them into the canonical implementation.
