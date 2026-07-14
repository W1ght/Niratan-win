# Novel Library Command Bar and Sort Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Novel library actions visible and labelled, and reliably reflect the persisted sort selection while defaulting to Recent.

**Architecture:** Keep page actions in the WinUI `CommandBar` and source labels from `.resw` files. Before each page load, reconcile the singleton ViewModel's selected sort with the current persisted setting without writing settings during restoration.

**Tech Stack:** WinUI 3 XAML, CommunityToolkit.Mvvm, .NET, xUnit v3, FluentAssertions.

## Global Constraints

- Preserve View → ViewModel → Service layering; code-behind remains UI-only.
- Use built-in `CommandBar`, `AppBarButton`, and `SymbolIcon`; add no dependencies.
- The first-run and invalid sort fallback is `NovelLibrarySortOption.Recent`.
- Google Drive sync is a primary command at ordinary desktop widths; native overflow remains active at narrow widths.
- Update both `en-US` and `zh-CN` resource files for new labels.
- Do not modify the unrelated change in `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs`.

---

### Task 1: Label and prioritize Novel library actions

**Files:**
- Modify: `Niratan/Views/Pages/NovelLibraryPage.xaml:184-224`
- Modify: `Niratan/Strings/en-US/Resources.resw`
- Modify: `Niratan/Strings/zh-CN/Resources.resw`
- Modify: `Niratan.Tests/Views/Pages/NovelLibraryPageAssetTests.cs`
- Modify: `Niratan.Tests/Services/Sync/NovelLibraryTtuSyncAssetTests.cs`

**Interfaces:**
- Consumes: `EnterStatisticsCommand`, `RefreshRemoteBooksCommand`, `ImportNovelCommand`, and `ReturnToBookshelfCommand`.
- Produces: Labelled primary commands for Statistics, Manage shelves, Sync Google Drive, and Import.

- [ ] **Step 1: Write failing asset coverage**

```csharp
[Fact]
public void NovelLibraryCommandBar_UsesVisiblePrimaryActions()
{
    var xaml = File.ReadAllText(
        Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml"));

    xaml.Should().Contain("<SymbolIcon Symbol=\"BarChart\" />");
    xaml.Should().Contain("x:Uid=\"NovelLibraryStatisticsButton\"");
    xaml.Should().Contain("x:Uid=\"NovelLibraryRefreshGoogleDriveButton\"");
    xaml.Should().Contain("x:Uid=\"ImportNovelButton\"");
    xaml.Should().NotContain("<CommandBar.SecondaryCommands>");
}
```

In `NovelLibraryTtuSyncAssetTests`, assert that the Google Drive button text occurs between `<CommandBar.PrimaryCommands>` and `</CommandBar.PrimaryCommands>`.

- [ ] **Step 2: Verify the tests fail**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageAssetTests|FullyQualifiedName~NovelLibraryTtuSyncAssetTests"
```

Expected: FAIL because Statistics uses `Document` and Google Drive is a secondary command.

- [ ] **Step 3: Implement the command bar and labels**

Replace the Statistics icon with:

```xml
<AppBarButton.Icon>
    <SymbolIcon Symbol="BarChart" />
</AppBarButton.Icon>
```

Move this existing Google Drive action into `PrimaryCommands`, after Manage shelves and before Import:

```xml
<AppBarButton x:Name="NovelLibraryRefreshGoogleDriveButton"
              x:Uid="NovelLibraryRefreshGoogleDriveButton"
              AutomationProperties.AutomationId="NovelLibraryRefreshGoogleDriveButton"
              Icon="Refresh"
              Command="{x:Bind ViewModel.RefreshRemoteBooksCommand}" />
```

Remove the empty `SecondaryCommands` element. Add `Label` and `AutomationProperties.Name` values to both resource files:

| Resource prefix | en-US label | zh-CN label |
| --- | --- | --- |
| `NovelLibraryStatisticsButton` | `Statistics` | `统计` |
| `NovelLibraryRefreshGoogleDriveButton` | `Sync Google Drive` | `同步 Google Drive` |
| `ImportNovelButton` | `Import` | `导入` |

Retain existing resources for Manage shelves and Back to bookshelf.

- [ ] **Step 4: Verify the asset tests pass**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageAssetTests|FullyQualifiedName~NovelLibraryTtuSyncAssetTests"
```

Expected: PASS.

- [ ] **Step 5: Commit Task 1**

```powershell
git add Niratan/Views/Pages/NovelLibraryPage.xaml Niratan/Strings/en-US/Resources.resw Niratan/Strings/zh-CN/Resources.resw Niratan.Tests/Views/Pages/NovelLibraryPageAssetTests.cs Niratan.Tests/Services/Sync/NovelLibraryTtuSyncAssetTests.cs
git commit -m "fix(bookshelf): label primary library commands"
```

### Task 2: Restore the persisted sort selection

**Files:**
- Modify: `Niratan/ViewModels/Pages/NovelLibraryPageViewModel.cs:104-108`
- Modify: `Niratan.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`

**Interfaces:**
- Consumes: `ISettingsService.Current.NovelLibrarySortOption`.
- Produces: `SelectedSortOption` is Recent for fresh settings and reflects a valid current setting on every `InitializeAsync` call.

- [ ] **Step 1: Write failing ViewModel coverage**

```csharp
[Fact]
public async Task InitializeAsync_UsesRecentWhenNoSortPreferenceHasBeenStored()
{
    var settings = new AppSettings();
    var service = Mock.Of<ISettingsService>(value => value.Current == settings);
    var sut = CreateSut(settingsService: service);

    await sut.InitializeAsync();

    sut.SelectedSortOption.Should().Be(NovelLibrarySortOption.Recent);
}

[Fact]
public async Task InitializeAsync_ReflectsTheCurrentPersistedSortPreference()
{
    var settings = new AppSettings { NovelLibrarySortOption = NovelLibrarySortOption.Recent };
    var service = Mock.Of<ISettingsService>(value => value.Current == settings);
    var sut = CreateSut(settingsService: service);
    settings.NovelLibrarySortOption = NovelLibrarySortOption.Manual;

    await sut.InitializeAsync();

    sut.SelectedSortOption.Should().Be(NovelLibrarySortOption.Manual);
}
```

- [ ] **Step 2: Verify the restored-value test fails**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageViewModelTests"
```

Expected: `InitializeAsync_ReflectsTheCurrentPersistedSortPreference` fails because the constructor captured Recent.

- [ ] **Step 3: Restore before catalog loading**

Add this helper to `NovelLibraryPageViewModel`:

```csharp
private void RestoreSelectedSortOption()
{
    var restored = _settingsService.Current.NovelLibrarySortOption;
    if (!Enum.IsDefined(restored))
        restored = NovelLibrarySortOption.Recent;

    _suppressSortApplication = true;
    SelectedSortOption = restored;
    _suppressSortApplication = false;
}
```

Call it before `LoadNovelsAsync`:

```csharp
public async Task InitializeAsync()
{
    RestoreSelectedSortOption();
    await LoadNovelsAsync();
}
```

- [ ] **Step 4: Verify the ViewModel tests pass**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageViewModelTests"
```

Expected: PASS with both Recent and Manual assertions green.

- [ ] **Step 5: Commit Task 2**

```powershell
git add Niratan/ViewModels/Pages/NovelLibraryPageViewModel.cs Niratan.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs
git commit -m "fix(bookshelf): restore persisted sort selection"
```

### Task 3: Verify the integrated WinUI change

**Files:**
- Verify: `Niratan/Views/Pages/NovelLibraryPage.xaml`
- Verify: `Niratan/ViewModels/Pages/NovelLibraryPageViewModel.cs`

**Interfaces:**
- Consumes: command-bar labels from Task 1 and sort restoration from Task 2.
- Produces: a buildable x64 application with visible actions and correct sort selection.

- [ ] **Step 1: Run focused regression tests**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageAssetTests|FullyQualifiedName~NovelLibraryTtuSyncAssetTests|FullyQualifiedName~NovelLibraryPageViewModelTests"
```

Expected: PASS with zero failures.

- [ ] **Step 2: Build x64**

Run:

```powershell
dotnet build Niratan/Niratan.csproj -c Debug -p:Platform=x64
```

Expected: `0 Error(s)`.

- [ ] **Step 3: Launch and inspect**

Run:

```powershell
Start-Process "D:\CODE\Yukari\Niratan\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Niratan.exe"
```

Verify the visible Statistics, Manage shelves, Sync Google Drive, and Import labels; verify the BarChart icon; then select Manual, navigate away and back, and confirm Manual remains selected. Use fresh settings to confirm Recent is selected initially.

- [ ] **Step 4: Confirm no unintended files are staged**

Run:

```powershell
git status --short
```

Expected: no verification artifact is staged and `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs` remains untouched.
