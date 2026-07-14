# Reader Transition Responsiveness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Reader entry render from local state without waiting for Google Drive import and make Reader exit return to the bookshelf without waiting for remote export, while preserving imported position and durable close-time persistence.

**Architecture:** Split local Reader initialization from open-time auto-sync in `NovelReaderPageViewModel`. `NovelReaderPage` renders the local EPUB first, observes a cancellable background import, and reapplies imported sidecars only while the page is current; the back command navigates immediately and reuses the existing detached lifecycle close for bookmark/statistics persistence and remote export.

**Tech Stack:** C#/.NET 10, WinUI 3, Windows App SDK, CommunityToolkit.Mvvm, WebView2, xUnit v3, FluentAssertions, Moq, Serilog.

## Global Constraints

- Build only x64 with `dotnet build -p:Platform=x64`; do not build ARM64 by default.
- Run tests with `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64`.
- Do not modify any file under `native/hoshidicts/`.
- Preserve the View → ViewModel → Service layering; page code-behind only coordinates UI controls and WebView2.
- Do not change TTU conflict resolution, Google Drive file formats, or remote filenames.
- Do not discard auto-sync with a fixed timeout.
- Preserve the existing navigation settlement and close-time writer barriers.
- Leave a verified worktree executable running after final validation.

---

## File Structure

- `Hoshi/ViewModels/Pages/NovelReaderPageViewModel.cs` — separate local initialization from open import and make the back command navigation-only.
- `Hoshi/Views/Pages/NovelReaderPage.xaml.cs` — schedule and cancel the page-owned open-sync task, then reapply imported sidecars to the active Reader.
- `Hoshi.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs` — prove local initialization is independent of the import wait, imported state is reloaded, and back navigation does not await close flush.
- `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs` — constrain lifecycle orchestration order and imported-state refresh in WinUI code-behind.
- `docs/CHANGELOG.md` — record the blocking network waits and the local-first/detached-close fix.

---

### Task 1: Split Local Initialization From Open-Time Import

**Files:**
- Modify: `Hoshi/ViewModels/Pages/NovelReaderPageViewModel.cs`
- Modify: `Hoshi.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs`

**Interfaces:**
- Consumes: `IReaderAutoSyncCoordinator.ImportOnOpenAsync(NovelBook, CancellationToken)` and `INovelLibraryService.GetNovelBookAsync(string, CancellationToken)`.
- Produces: `Task<bool> NovelReaderPageViewModel.SyncOnOpenAsync(CancellationToken ct = default)`; `InitializeAsync` becomes local-only.

- [ ] **Step 1: Replace the existing combined initialization test with two failing lifecycle tests**

Replace `InitializeAsync_WhenOpenSyncImports_ReloadsBookBeforeRestore` with a local initialization test whose auto-sync task never completes:

```csharp
[Fact]
public async Task InitializeAsync_CompletesLocalOpenWithoutWaitingForAutoSync()
{
    var ct = TestContext.Current.CancellationToken;
    var local = new NovelBook { Id = "book-1", Title = "Local" };
    var library = new Mock<INovelLibraryService>();
    library.Setup(service => service.GetNovelBookAsync(
            "book-1", It.IsAny<CancellationToken>()))
        .ReturnsAsync(Result<NovelBook?>.Success(local));
    library.Setup(service => service.MarkOpenedAsync(
            "book-1", It.IsAny<CancellationToken>()))
        .ReturnsAsync(Result.Success());
    var autoSync = CreateAutoSyncCoordinator();
    autoSync.Setup(service => service.ImportOnOpenAsync(
            It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()))
        .Returns(new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously).Task);
    var sut = CreateSut(
        library.Object,
        Mock.Of<INotificationService>(),
        new FakeMessenger(),
        new FakeReaderStatisticsSession(),
        autoSync.Object);

    await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);

    sut.CurrentBook.Should().BeSameAs(local);
    autoSync.Verify(service => service.ImportOnOpenAsync(
        It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()), Times.Never);
}
```

Add a separate import test:

```csharp
[Fact]
public async Task SyncOnOpenAsync_WhenImportSucceeds_ReloadsImportedBook()
{
    var ct = TestContext.Current.CancellationToken;
    var local = new NovelBook { Id = "book-1", Title = "Local" };
    var imported = new NovelBook
    {
        Id = "book-1",
        Title = "Imported",
        CurrentChapterIndex = 2,
        Progress = 0.65,
    };
    var library = new Mock<INovelLibraryService>();
    var loadCount = 0;
    library.Setup(service => service.GetNovelBookAsync(
            "book-1", It.IsAny<CancellationToken>()))
        .ReturnsAsync(() => Result<NovelBook?>.Success(
            ++loadCount == 1 ? local : imported));
    library.Setup(service => service.MarkOpenedAsync(
            "book-1", It.IsAny<CancellationToken>()))
        .ReturnsAsync(Result.Success());
    var autoSync = CreateAutoSyncCoordinator(imported: true);
    var sut = CreateSut(
        library.Object,
        Mock.Of<INotificationService>(),
        new FakeMessenger(),
        new FakeReaderStatisticsSession(),
        autoSync.Object);
    await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);

    var changed = await sut.SyncOnOpenAsync(ct);

    changed.Should().BeTrue();
    sut.CurrentBook.Should().BeSameAs(imported);
    sut.ReaderTitle.Should().Be("Imported");
    library.Verify(service => service.GetNovelBookAsync(
        "book-1", It.IsAny<CancellationToken>()), Times.Exactly(2));
}
```

- [ ] **Step 2: Run the two tests and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~InitializeAsync_CompletesLocalOpenWithoutWaitingForAutoSync|FullyQualifiedName~SyncOnOpenAsync_WhenImportSucceeds_ReloadsImportedBook"
```

Expected: the first test fails because `InitializeAsync` invokes `ImportOnOpenAsync`; the second does not compile because `SyncOnOpenAsync` is absent.

- [ ] **Step 3: Move import and reload into `SyncOnOpenAsync`**

Keep local book loading, profile activation and `MarkOpenedAsync` in `InitializeAsync`, but remove the `ImportOnOpenAsync` block. Add:

```csharp
public async Task<bool> SyncOnOpenAsync(CancellationToken ct = default)
{
    var book = CurrentBook;
    if (book == null || !await _readerAutoSyncCoordinator.ImportOnOpenAsync(book, ct))
        return false;

    var imported = await _novelLibraryService.GetNovelBookAsync(book.Id, ct);
    if (!imported.IsSuccess || imported.Value == null)
        return false;

    CurrentBook = imported.Value;
    OnPropertyChanged(nameof(ReaderTitle));
    return true;
}
```

Do not call `MarkOpenedAsync` a second time after import.

- [ ] **Step 4: Run the two tests and verify GREEN**

Run the command from Step 2.

Expected: both tests pass with zero failures.

- [ ] **Step 5: Commit the local/open-sync separation**

```powershell
git add Hoshi/ViewModels/Pages/NovelReaderPageViewModel.cs Hoshi.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs
git commit -m "refactor(reader): separate local open from auto sync"
```

---

### Task 2: Start Import After Local Render and Reapply Imported State

**Files:**
- Modify: `Hoshi/Views/Pages/NovelReaderPage.xaml.cs`
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Consumes: `NovelReaderPageViewModel.SyncOnOpenAsync`, `LoadChapter(int, double?)`, `LoadStatisticsAsync`, `SasayakiSidecarService.LoadPlaybackAsync` and `ApplySasayakiPlayback`.
- Produces: page-owned `CancellationTokenSource? _openSyncCts`, `RunOpenSyncAsync(CancellationToken)` and `ApplyImportedReaderStateAsync(CancellationToken)`.

- [ ] **Step 1: Add a failing page lifecycle contract**

Add to `NovelReaderWebAssetTests`:

```csharp
[Fact]
public void ReaderOpenSync_StartsAfterLocalReaderAndIsCancelledOnDetach()
{
    var code = File.ReadAllText(
        Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs"));
    var navigatedTo = code.IndexOf("protected override async void OnNavigatedTo", StringComparison.Ordinal);
    var navigatedFrom = code.IndexOf("protected override void OnNavigatedFrom", navigatedTo, StringComparison.Ordinal);
    var openBody = code[navigatedTo..navigatedFrom];

    openBody.IndexOf("await InitializeReaderAsync();", StringComparison.Ordinal)
        .Should().BeLessThan(openBody.IndexOf("StartOpenSync();", StringComparison.Ordinal));
    code.Should().Contain("private CancellationTokenSource? _openSyncCts;");
    code.Should().Contain("private async Task RunOpenSyncAsync(CancellationToken ct)");
    code.Should().Contain("await ViewModel.SyncOnOpenAsync(ct)");
    code.Should().Contain("await ApplyImportedReaderStateAsync(ct)");
    code.Should().Contain("_openSyncCts?.Cancel();");
}
```

Add a second contract:

```csharp
[Fact]
public void ImportedReaderState_ReappliesBookmarkStatisticsAndSasayakiPlayback()
{
    var code = File.ReadAllText(
        Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs"));
    var start = code.IndexOf("private async Task ApplyImportedReaderStateAsync", StringComparison.Ordinal);
    var end = code.IndexOf("\n    private ", start + 10, StringComparison.Ordinal);
    var body = code[start..end];

    body.Should().Contain("ViewModel.CurrentBook.CurrentChapterIndex");
    body.Should().Contain("ViewModel.CurrentBook.Progress");
    body.Should().Contain("await ViewModel.LoadStatisticsAsync(ct)");
    body.Should().Contain("SasayakiSidecarService.LoadPlaybackAsync");
    body.Should().Contain("ApplySasayakiPlayback(playback)");
    body.Should().Contain("LoadChapter(");
}
```

- [ ] **Step 2: Run the page contracts and verify RED**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ReaderOpenSync_StartsAfterLocalReaderAndIsCancelledOnDetach|FullyQualifiedName~ImportedReaderState_ReappliesBookmarkStatisticsAndSasayakiPlayback"
```

Expected: both tests fail because the page-owned background import lifecycle is absent.

- [ ] **Step 3: Add page-owned open-sync orchestration**

After `await InitializeReaderAsync()` in `OnNavigatedTo`, call `StartOpenSync()`. Implement it so it replaces and disposes any previous CTS, stores the new CTS, and observes `RunOpenSyncAsync` without awaiting it from `OnNavigatedTo`.

`RunOpenSyncAsync` must:

```csharp
private async Task RunOpenSyncAsync(CancellationToken ct)
{
    try
    {
        if (!await ViewModel.SyncOnOpenAsync(ct) || ct.IsCancellationRequested)
            return;

        await ApplyImportedReaderStateAsync(ct);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "[NovelReader] Failed to apply open sync import");
    }
}
```

At the beginning of `OnNavigatedFrom`, cancel and dispose `_openSyncCts` and set it to null before detaching WebView2 handlers.

- [ ] **Step 4: Reapply imported sidecars only to the active page**

Implement `ApplyImportedReaderStateAsync` with cancellation checks before every UI mutation. Clamp `CurrentChapterIndex` to the parsed spine, set ViewModel chapter/progress, reload statistics, apply the latest Sasayaki playback to an existing player, refresh statistics chrome, and call `LoadChapter(importedChapter, importedProgress)`.

Do not rematch subtitles, recreate the player, save a zero playback position, or replace `_epubBook`.

- [ ] **Step 5: Run the page contracts and the existing Reader asset suite**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderWebAssetTests"
```

Expected: all `NovelReaderWebAssetTests` pass with zero failures.

- [ ] **Step 6: Commit the local-first page lifecycle**

```powershell
git add Hoshi/Views/Pages/NovelReaderPage.xaml.cs Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "fix(reader): apply open sync after local render"
```

---

### Task 3: Navigate Away Before Detached Close Flush

**Files:**
- Modify: `Hoshi/ViewModels/Pages/NovelReaderPageViewModel.cs`
- Modify: `Hoshi.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs`
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`
- Modify: `docs/CHANGELOG.md`

**Interfaces:**
- Consumes: `SwitchAppModeMessage`, `OnNavigatedFrom`, and `CompleteReaderLifecycleCloseAfterDetachAsync`.
- Produces: a navigation-only `BackToLibrary` relay command; durable close remains owned by the detached page lifecycle.

- [ ] **Step 1: Add a failing non-blocking back test**

Add to `NovelReaderPageViewModelTests`:

```csharp
[Fact]
public async Task BackToLibraryCommand_SendsNavigationWithoutStartingCloseFlush()
{
    var ct = TestContext.Current.CancellationToken;
    using var temp = new TempBookDirectory();
    var messenger = new FakeMessenger();
    var autoSync = CreateAutoSyncCoordinator();
    autoSync.Setup(service => service.FlushAsync(
            It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()))
        .Returns(new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously).Task);
    var novelService = CreateNovelService(temp.Path);
    var sut = CreateSut(
        novelService.Object,
        Mock.Of<INotificationService>(),
        messenger,
        new FakeReaderStatisticsSession(),
        autoSync.Object);
    await sut.InitializeAsync(new NovelReaderNavigationArgs("book-1"), ct);

    sut.BackToLibraryCommand.Execute(null);
    await Task.Yield();

    messenger.GetSingleSentMessage<SwitchAppModeMessage>().appMode
        .Should().Be(AppMode.Navigation);
    autoSync.Verify(service => service.FlushAsync(
        It.IsAny<NovelBook>(), It.IsAny<CancellationToken>()), Times.Never);
}
```

- [ ] **Step 2: Run the test and verify RED**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~BackToLibraryCommand_SendsNavigationWithoutStartingCloseFlush"
```

Expected: the command does not complete because it waits for `PrepareForReaderLifecycleCloseAsync`/`FlushAsync`.

- [ ] **Step 3: Make the command navigation-only**

Replace the async command with:

```csharp
[RelayCommand]
private void BackToLibrary() =>
    _messenger.Send(new SwitchAppModeMessage(AppMode.Navigation, null));
```

Do not remove `CompleteReaderLifecycleCloseAfterDetachAsync` from `OnNavigatedFrom`; it remains the only close path for ordinary Reader back navigation.

- [ ] **Step 4: Strengthen the detached-close asset contract**

Extend the existing lifecycle asset test to assert that `OnNavigatedFrom` still calls `_ = CompleteReaderLifecycleCloseAfterDetachAsync();`, while the `BackToLibrary` method body contains `SwitchAppModeMessage` and does not contain `PrepareForReaderLifecycleCloseAsync`.

- [ ] **Step 5: Run ViewModel and Reader lifecycle tests**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderPageViewModelTests|FullyQualifiedName~NovelReaderWebAssetTests"
```

Expected: all selected tests pass with zero failures, including existing close-boundary ordering tests.

- [ ] **Step 6: Record the fix**

Add a concise entry to `docs/CHANGELOG.md`: open-time Google Drive import was awaited before local EPUB initialization and close-time export was awaited before navigation; local-first render plus detached close removes both visible network waits while retaining sidecar persistence.

- [ ] **Step 7: Commit the non-blocking exit**

```powershell
git add Hoshi/ViewModels/Pages/NovelReaderPageViewModel.cs Hoshi.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs docs/CHANGELOG.md
git commit -m "fix(reader): detach close sync from navigation"
```

---

### Task 4: Full Verification and Runtime Transition Check

**Files:**
- Verify only; modify production files only if a failing test or reproduced delay identifies a remaining root cause.

**Interfaces:**
- Consumes: worktree build scripts, Hoshi logs, UI Automation IDs and the imported test library.
- Produces: fresh build/test evidence and a running exact worktree instance.

- [ ] **Step 1: Run formatting/diff checks**

```powershell
git diff --check
git status --short
```

Expected: no whitespace errors and only intended files are modified.

- [ ] **Step 2: Run the complete x64 test suite**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
```

Expected: zero failed tests.

- [ ] **Step 3: Build the x64 app**

```powershell
dotnet build -p:Platform=x64
```

Expected: exit code 0 with zero build errors.

- [ ] **Step 4: Launch the exact worktree output**

Use `build-and-run.ps1` from this worktree, verify the launched process path resolves under this worktree, verify a non-zero main window handle, and keep the final instance running.

- [ ] **Step 5: Reproduce entry and exit with auto-sync enabled**

Open `無職転生 ～異世界行ったら本気だす～ 08`, record the time from card click to the first local Reader chapter, then click Back and record the time to the bookshelf. Confirm logs show local chapter initialization before open-sync completion and detached close completion after the bookshelf is visible.

- [ ] **Step 6: Verify imported state and fast re-entry**

With a newer remote bookmark/Sasayaki position, confirm the background import updates the active Reader. Return and immediately open another book; confirm the old task does not navigate or update the new Reader.

- [ ] **Step 7: Final review and commit any verification-only documentation adjustment**

Re-read `docs/superpowers/specs/2026-07-14-reader-transition-responsiveness-design.md`, inspect `git diff HEAD~3..HEAD`, and ensure every acceptance criterion has direct test or runtime evidence.
