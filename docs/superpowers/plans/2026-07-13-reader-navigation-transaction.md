# Reader Navigation Transaction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace split Reader navigation flags with one generation-owned transaction so adjacent and ordinary programmatic navigation persist, publish, recover, and settle lifecycle exactly once.

**Architecture:** A pure `ReaderNavigationTransactionCoordinator` owns immutable source/destination data and the `Rendering -> Committing -> Completed` state machine. `NovelReaderPageViewModel` owns persistence and atomically publishes only a successfully saved destination; `NovelReaderPage` only loads hidden render requests and applies typed terminal instructions. Once commit begins it is a point of no return, so lifecycle and new navigation await/reject instead of cancelling an in-flight write.

**Tech Stack:** C#/.NET 10, WinUI 3, CommunityToolkit.Mvvm, WebView2 bridge JSON v1, xUnit v3, FluentAssertions, Moq.

## Global Constraints

- Do not modify `native/hoshidicts/` or reference submodules.
- Keep WebView2 + CSS multi-column pagination; do not introduce another renderer.
- Page code-behind remains UI-only; persistence/statistics/sync stay in ViewModel/services.
- Bridge messages remain narrow, typed, versioned, finite-value validated, and generation-scoped.
- Backward adjacent navigation resolves `contentLastPageScroll` and never publishes a temporary `1.0`.
- Do not perform a real Google Drive mutation.
- Use `dotnet build -p:Platform=x64`; do not build ARM64 by default.

---

## File Structure

- Create `Niratan/Models/Novel/ReaderNavigationTransactionModels.cs`: immutable positions, destinations, render requests, commit leases, and settlements.
- Create `Niratan/Services/Novels/ReaderNavigationTransactionCoordinator.cs`: pure generation/state owner with no WebView or persistence dependency.
- Create `Niratan.Tests/Services/Novels/ReaderNavigationTransactionCoordinatorTests.cs`: executable state-machine and race coverage.
- Modify `Niratan/ViewModels/Pages/NovelReaderPageViewModel.cs`: transaction-facing APIs, writer-tail destination commit, lifecycle settlement, atomic position publication.
- Modify `Niratan/Views/Pages/NovelReaderPage.xaml.cs`: apply typed render/settlement instructions and remove business-state flags.
- Modify `Niratan/Web/NovelReader/reader-bridge.js`: include navigation generation and chapter identity in terminal messages.
- Modify `Niratan/Services/Novels/NovelReaderBridgeMessageFactory.cs`: keep typed destination serialization centralized.
- Modify `Niratan.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs`: persistence, lifecycle, mutation gate, and exact-position integration tests.
- Modify `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs`: bridge and UI-only Page contracts.
- Modify `Niratan.Tests/Views/Pages/NovelReaderStatisticsLifecycleTests.cs`: lifecycle settles navigation before final checkpoint.
- Delete `Niratan/Services/Novels/ReaderProgrammaticNavigationTracker.cs` and `Niratan/Services/Novels/ReaderAdjacentNavigationCommitCoordinator.cs` after all callers migrate.
- Delete their superseded test files after equivalent behavior exists in the new coordinator suite.
- Update `docs/VERIFICATION.md`, `docs/CHANGELOG.md`, and `.superpowers/sdd/progress.md` with the final transaction behavior and evidence.

---

### Task 1: Build the pure navigation transaction state machine

**Files:**
- Create: `Niratan/Models/Novel/ReaderNavigationTransactionModels.cs`
- Create: `Niratan/Services/Novels/ReaderNavigationTransactionCoordinator.cs`
- Create: `Niratan.Tests/Services/Novels/ReaderNavigationTransactionCoordinatorTests.cs`

**Interfaces:**
- Produces: `ReaderNavigationPositionSnapshot`, `ReaderNavigationDestination`, `ReaderNavigationRenderRequest`, `ReaderNavigationCommitLease`, `ReaderNavigationSettlement`, and `ReaderNavigationTransactionCoordinator`.
- Consumes: existing `ReaderChapterRestoreTarget`.

- [ ] **Step 1: Write failing state-machine tests**

Add tests that use this public surface:

```csharp
var source = new ReaderNavigationPositionSnapshot("book-1", 1, 0, 100, 200, 7);
var destination = ReaderNavigationDestination.AtChapterEnd(0);
var sut = new ReaderNavigationTransactionCoordinator();

var render = sut.TryBegin(source, destination);
render.Should().NotBeNull();
sut.BlocksPositionMutation.Should().BeTrue();

var resolved = new ReaderNavigationPositionSnapshot("book-1", 0, 0.82, 82, 200, 8);
var lease = sut.TryBeginCommit(render!.Generation, resolved);
lease.Should().NotBeNull();
sut.TryCancelRendering(render.Generation).Should().BeNull();

var settled = sut.CompleteCommit(lease!, committed: true);
settled.Position.Should().Be(lease.ResolvedDestination);
settled.ShouldRevealDestination.Should().BeTrue();
await sut.WaitForSettlementAsync().Should().Be(settled);
sut.BlocksPositionMutation.Should().BeTrue();
sut.AcknowledgeTerminalRender(settled.Generation).Should().BeTrue();
sut.BlocksPositionMutation.Should().BeFalse();
```

Cover separately:

- stale generation and wrong chapter return `null` without mutation;
- duplicate completion returns `null`;
- `Rendering` cancellation settles to immutable source;
- cancellation during `Committing` is rejected and does not complete the transaction;
- bridge error during `Rendering` settles to source;
- bridge error during `Committing` returns the existing settlement task;
- new `TryBegin` is rejected until Page acknowledges the active transaction's terminal render;
- settlement keeps `BlocksPositionMutation` active until Page acknowledges the terminal render;
- `AcknowledgeTerminalRender` releases the gate exactly once and rejects the wrong generation.

- [ ] **Step 2: Run the tests and verify RED**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ReaderNavigationTransactionCoordinatorTests"
```

Expected: compile failure because the transaction models and coordinator do not exist.

- [ ] **Step 3: Implement the immutable models**

Create these exact public records:

```csharp
public readonly record struct ReaderNavigationPositionSnapshot(
    string BookId,
    int ChapterIndex,
    double Progress,
    int CharacterCount,
    int TotalCharacterCount,
    long Revision);

public readonly record struct ReaderNavigationDestination(
    int ChapterIndex,
    ReaderChapterRestoreTarget? RestoreTarget,
    double? ExactProgress)
{
    public static ReaderNavigationDestination AtChapterStart(int chapterIndex) =>
        new(chapterIndex, ReaderChapterRestoreTarget.Start, null);

    public static ReaderNavigationDestination AtChapterEnd(int chapterIndex) =>
        new(chapterIndex, ReaderChapterRestoreTarget.End, null);

    public static ReaderNavigationDestination AtProgress(int chapterIndex, double progress) =>
        new(chapterIndex, null, Math.Clamp(progress, 0, 1));
}

public sealed record ReaderNavigationRenderRequest(
    long Generation,
    ReaderNavigationPositionSnapshot Source,
    ReaderNavigationDestination Destination);

public sealed record ReaderNavigationCommitLease(
    long Generation,
    ReaderNavigationPositionSnapshot Source,
    ReaderNavigationPositionSnapshot ResolvedDestination);

public sealed record ReaderNavigationSettlement(
    long Generation,
    ReaderNavigationPositionSnapshot Position,
    bool ShouldRevealDestination);
```

- [ ] **Step 4: Implement the coordinator under one lock**

Implement these exact members:

```csharp
public bool BlocksPositionMutation { get; }
public ReaderNavigationRenderRequest? ActiveRenderRequest { get; }
public ReaderNavigationRenderRequest? TryBegin(
    ReaderNavigationPositionSnapshot source,
    ReaderNavigationDestination destination);
public ReaderNavigationCommitLease? TryBeginCommit(
    long generation,
    ReaderNavigationPositionSnapshot resolvedDestination);
public ReaderNavigationSettlement? TryCancelRendering(long generation);
public Task<ReaderNavigationSettlement?> HandleBridgeErrorAsync();
public ReaderNavigationSettlement? CompleteCommit(
    ReaderNavigationCommitLease lease,
    bool committed);
public Task<ReaderNavigationSettlement?> WaitForSettlementAsync();
public bool AcknowledgeTerminalRender(long generation);
```

Use `TaskCompletionSource<ReaderNavigationSettlement>` with `RunContinuationsAsynchronously`. `TryBeginCommit` validates book identity, destination chapter, finite progress, character bounds, and a revision newer than the source without publishing it. `CompleteCommit(false)` settles to source; `CompleteCommit(true)` settles to resolved destination. Do not accept cancellation once phase is `Committing`. Keep the transaction active after settlement until `AcknowledgeTerminalRender` confirms that Page established a visible terminal render.

- [ ] **Step 5: Run focused tests and verify GREEN**

Run the Task 1 test command. Expected: all coordinator tests pass.

- [ ] **Step 6: Commit Task 1**

```powershell
git add -- Niratan/Models/Novel/ReaderNavigationTransactionModels.cs Niratan/Services/Novels/ReaderNavigationTransactionCoordinator.cs Niratan.Tests/Services/Novels/ReaderNavigationTransactionCoordinatorTests.cs
git commit -m "refactor(reader): add navigation transaction state machine"
```

---

### Task 2: Move persistence and lifecycle settlement into the ViewModel transaction

**Files:**
- Modify: `Niratan/ViewModels/Pages/NovelReaderPageViewModel.cs`
- Modify: `Niratan/Views/Pages/NovelReaderPage.xaml.cs`
- Modify: `Niratan/App.xaml.cs`
- Modify: `Niratan.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs`
- Modify: `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs`
- Modify: `Niratan.Tests/Views/Pages/NovelReaderStatisticsLifecycleTests.cs`

**Interfaces:**
- Consumes: Task 1 coordinator and models.
- Produces: `TryBeginNavigation`, `ResolveNavigationAsync`, `HandleNavigationBridgeErrorAsync`, `SettleNavigationForLifecycleAsync`, and `CanAcceptReaderPositionMutation`.

- [ ] **Step 1: Write failing ViewModel transaction tests**

Add deterministic tests with controlled `SaveProgressAsync` tasks for:

```csharp
var render = sut.TryBeginNavigation(0, ReaderChapterRestoreTarget.End, exactProgress: null);
render.Should().NotBeNull();
sut.CurrentChapterIndex.Should().Be(1);
sut.Progress.Should().Be(0);

var resolve = sut.ResolveNavigationAsync(
    render!.Generation,
    destinationChapterIndex: 0,
    resolvedProgress: 0.82,
    TestContext.Current.CancellationToken);

await saveStarted.Task;
sut.CurrentChapterIndex.Should().Be(1);
sut.Progress.Should().Be(0);
releaseSave.SetResult();

var settlement = await resolve;
settlement!.ShouldRevealDestination.Should().BeTrue();
sut.CurrentChapterIndex.Should().Be(0);
sut.Progress.Should().Be(0.82);
```

Add separate tests proving:

- save returns failure: no baseline reset, no publication, no auto-sync, settlement is source;
- save throws: coordinator settles source and later Reader input is accepted;
- lifecycle during `Rendering` cancels to source before the lifecycle bookmark/checkpoint;
- lifecycle during `Committing` waits, then checkpoints only destination;
- close/background never enqueue a second source baseline reset behind destination commit;
- stale/double completion never calls save or changes ViewModel;
- success schedules one export; failure schedules none;
- `CanAcceptReaderPositionMutation` is false in `Rendering` and `Committing`, true after settlement.

- [ ] **Step 2: Run ViewModel tests and verify RED**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderPageViewModelTests"
```

Expected: compile failures for the new ViewModel transaction API.

- [ ] **Step 3: Inject the coordinator and expose typed APIs**

Register `ReaderNavigationTransactionCoordinator` as transient in `App.xaml.cs` and inject it into `NovelReaderPageViewModel`.

Add these members:

```csharp
public bool CanAcceptReaderPositionMutation =>
    !_navigationTransactions.BlocksPositionMutation;

public ReaderNavigationRenderRequest? TryBeginNavigation(
    int destinationChapterIndex,
    ReaderChapterRestoreTarget? restoreTarget,
    double? exactProgress);

public Task<ReaderNavigationSettlement?> ResolveNavigationAsync(
    long generation,
    int destinationChapterIndex,
    double resolvedProgress,
    CancellationToken ct = default);

public Task<ReaderNavigationSettlement?> HandleNavigationBridgeErrorAsync();
public Task<ReaderNavigationSettlement?> SettleNavigationForLifecycleAsync();
public bool AcknowledgeNavigationRendered(long generation);
```

`TryBeginNavigation` captures the current immutable source and creates a `ReaderNavigationDestination`. It must not call `SetChapter` or `UpdateProgress`.

- [ ] **Step 4: Implement point-of-no-return persistence**

`ResolveNavigationAsync` must:

1. construct the exact destination `ReaderNavigationPositionSnapshot` from chapter character counts and obtain a matching `ReaderNavigationCommitLease`;
2. admit one writer-tail operation with `CancellationToken.None` after the lease is granted;
3. call a persistence-only helper around `INovelLibraryService.SaveProgressAsync` with the lease destination;
4. on returned persistence failure or persistence exception call `CompleteCommit(lease, false)` exactly once;
5. once persistence succeeds, treat the destination as irrevocably durable: reset the destination baseline, atomically publish the destination tuple, schedule export, and broadcast in that order;
6. contain and report any post-persistence baseline, property-notification, export-scheduling, or broadcast exception without settling to source;
7. call `CompleteCommit(lease, true)` exactly once after the durable destination path;
8. return the resulting settlement.

Do not pass Page navigation cancellation into the admitted destination write. A persistence failure returns a source settlement. After persistence succeeds, every later fault returns a destination settlement because source recovery would contradict the durable bookmark.

Add explicit tests whose fake statistics baseline, property subscriber, auto-sync scheduler, and messenger each throw after a successful bookmark save. Every case must settle to destination, keep the destination tuple authoritative, and never instruct source recovery.

- [ ] **Step 5: Settle navigation before lifecycle boundaries**

Modify `NovelReaderPage.HandleAppLifecycleCheckpointAsync` so Page invokes `SettleNavigationForLifecycleAsync`, applies the returned source/destination terminal instruction, and acknowledges it before calling either `CheckpointAppBackgroundingAsync` or `PrepareForReaderLifecycleCloseAsync`:

- if phase is `Rendering`, cancel to source;
- if phase is `Committing`, await its settlement;
- after settlement and terminal-render acknowledgement, capture exactly one current progress request and run the existing lifecycle writer boundary;
- remove lifecycle use of `_latestAdmittedProgressRequest`, `LifecycleBoundarySnapshot`, and the independent Page-side baseline reset.

When the Reader is closing and its WebView has already been detached, Page explicitly acknowledges the terminal settlement as abandoned before awaiting `PrepareForReaderLifecycleCloseAsync`; no Reader callback remains able to mutate position. Backgrounding keeps the Page attached and acknowledges only after the source/destination render is established.

- [ ] **Step 6: Guard ViewModel position-changing entry points**

Before changing progress in manual Reader events, programmatic commands, or Sasayaki application APIs, require `CanAcceptReaderPositionMutation`. Transaction-internal atomic publication bypasses this public guard through a private method.

- [ ] **Step 7: Run focused and lifecycle tests**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderPageViewModelTests|FullyQualifiedName~NovelReaderStatisticsLifecycleTests"
```

Expected: all selected tests pass.

- [ ] **Step 8: Commit Task 2**

```powershell
git add -- Niratan/App.xaml.cs Niratan/ViewModels/Pages/NovelReaderPageViewModel.cs Niratan/Views/Pages/NovelReaderPage.xaml.cs Niratan.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs Niratan.Tests/Views/Pages/NovelReaderStatisticsLifecycleTests.cs
git commit -m "refactor(reader): commit navigation through viewmodel transaction"
```

---

### Task 3: Make Page and bridge consume typed render and settlement instructions

**Files:**
- Modify: `Niratan/Views/Pages/NovelReaderPage.xaml.cs`
- Modify: `Niratan/Web/NovelReader/reader-bridge.js`
- Modify: `Niratan/Services/Novels/NovelReaderBridgeMessageFactory.cs`
- Modify: `Niratan.Tests/Services/Novels/NovelReaderBridgeMessageFactoryTests.cs`
- Modify: `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Consumes: Task 2 ViewModel transaction APIs.
- Produces: generation-scoped hidden render, terminal reveal, and source recovery.

- [ ] **Step 1: Write failing bridge/Page contract tests**

Require the bridge to emit:

```javascript
postToHost("restoreCompleted", {
  progress: window.niratanReader.calculateProgress(),
  chapterIndex: currentChapter.index,
  navigationGeneration: navigationGeneration ?? null,
});

postToHost("chapterReady", {
  ...window.__niratanReaderState,
  chapterIndex: currentChapter.index,
  navigationGeneration: navigationGeneration ?? null,
});
```

Add Page contract assertions that:

- `LoadChapter` receives a `ReaderNavigationRenderRequest` and does not mutate ViewModel position;
- `restoreCompleted` forwards generation, chapter, and resolved progress to `ResolveNavigationAsync`;
- source settlement reloads the immutable source;
- destination settlement refreshes/reveals once;
- wrong-generation `chapterReady` cannot reveal;
- bridge `error` calls `HandleNavigationBridgeErrorAsync` and always applies a terminal settlement.

- [ ] **Step 2: Run bridge/Page tests and verify RED**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderBridgeMessageFactoryTests|FullyQualifiedName~NovelReaderWebAssetTests"
```

Expected: failures because terminal bridge payloads and Page transaction calls are absent.

- [ ] **Step 3: Carry generation and chapter identity through the bridge**

Modify `notifyRestoreComplete` and `chapterReady` as required by Step 1. Keep explicit `Start`/`End` checks before progress fallbacks and keep exactly one `notifyRestoreComplete` call in `restoreProgress`.

- [ ] **Step 4: Replace Page pending business flags with one UI render token**

Remove `_pendingAdjacentChapterNavigation`, `_pendingAdjacentChapterIndex`, `_pendingAdjacentChapterRestoreTarget`, and the Page-owned commit coordinator. Retain only immutable render instructions and UI readiness facts:

```csharp
private ReaderNavigationRenderRequest? _hiddenRenderRequest;
private bool _hiddenRenderChapterReady;
private ReaderNavigationSettlement? _pendingTerminalSettlement;
private TaskCompletionSource? _terminalRenderReady;
```

Use `ReaderNavigationRenderRequest` to build chapter metadata, typed restore target, exact progress, and navigation generation. Do not call ViewModel `SetChapter`/`UpdateProgress` during hidden render.

- [ ] **Step 5: Apply terminal settlements in one helper**

Add one UI-only helper:

```csharp
private void ApplyNavigationSettlement(ReaderNavigationSettlement settlement)
{
    _currentProgress = settlement.Position.Progress;
    RefreshReaderDisplayChrome();
    if (settlement.ShouldRevealDestination &&
        _hiddenRenderChapterReady &&
        _hiddenRenderGeneration == settlement.Generation)
    {
        NovelWebView.Opacity = 1;
        ViewModel.AcknowledgeNavigationRendered(settlement.Generation);
    }
    else if (!settlement.ShouldRevealDestination)
    {
        LoadChapter(settlement.Position.ChapterIndex,
            progressOverride: settlement.Position.Progress);
    }
}
```

Clear the UI render request only after terminal reveal/recovery navigation is established. Make recovery `chapterReady` reveal the source, call `AcknowledgeNavigationRendered`, complete `_terminalRenderReady`, and release the token. Until acknowledgement, ViewModel continues rejecting all position mutation. Attached background lifecycle awaits `_terminalRenderReady` before entering its writer boundary; detached close acknowledges the settlement as abandoned because no further WebView callback can arrive.

- [ ] **Step 6: Route bridge errors through ViewModel settlement**

The `error` message handler must await `HandleNavigationBridgeErrorAsync`; it must not directly clear transaction state. Apply the returned source/destination settlement so `Opacity=0` cannot be terminal.

- [ ] **Step 7: Run focused tests and verify GREEN**

Run the Task 3 test command. Expected: all selected tests pass.

- [ ] **Step 8: Commit Task 3**

```powershell
git add -- Niratan/Views/Pages/NovelReaderPage.xaml.cs Niratan/Web/NovelReader/reader-bridge.js Niratan/Services/Novels/NovelReaderBridgeMessageFactory.cs Niratan.Tests/Services/Novels/NovelReaderBridgeMessageFactoryTests.cs Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "refactor(reader): render navigation settlements atomically"
```

---

### Task 4: Gate every live position mutation, including Sasayaki

**Files:**
- Modify: `Niratan/Views/Pages/NovelReaderPage.xaml.cs`
- Modify: `Niratan/ViewModels/Pages/NovelReaderPageViewModel.cs`
- Modify: `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs`
- Modify: `Niratan.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs`

**Interfaces:**
- Consumes: `CanAcceptReaderPositionMutation` and terminal settlement from Tasks 2-3.
- Produces: defense-in-depth mutation rejection for all Reader entry points.

- [ ] **Step 1: Write failing mutation-gate tests**

Start a transaction in each test and prove the following do not change chapter/progress or call save:

- manual `pageChanged`;
- internal link and search/TOC navigation;
- Reader history navigation;
- same-chapter Sasayaki progress application;
- cross-chapter `LoadChapterForSasayakiAutoScroll`;
- live playback callback that would normally auto-scroll;
- statistics projection tick that would checkpoint a position.

Also prove playback UI state and non-positional cue highlighting may still update.

- [ ] **Step 2: Run Reader tests and verify RED**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderPageViewModelTests|FullyQualifiedName~NovelReaderWebAssetTests"
```

Expected: at least the live Sasayaki callback and programmatic-entry contracts fail.

- [ ] **Step 3: Add one Page guard helper**

```csharp
private bool CanMutateReaderPosition() =>
    ViewModel.CanAcceptReaderPositionMutation;
```

Use it before every Page call that can load another chapter, update progress, enqueue bookmark save, or start another navigation. A committing navigation rejects new commands; it does not queue them.

- [ ] **Step 4: Enforce the same gate in ViewModel**

Reader-originated public methods return `false`, `NoMovement`, or completed no-op tasks when a transaction blocks mutation. Private transaction commit publication is the only bypass.

- [ ] **Step 5: Make all Sasayaki paths navigation-safe**

While gated:

- call `HighlightSasayakiCueAsync(match, allowAutoScroll: false)`;
- do not call `TryApplySasayakiAutoScrollProgress`;
- do not call `LoadChapterForSasayakiAutoScroll`;
- do not call `SaveProgressDebounced` from a cue position result.

- [ ] **Step 6: Run focused tests and verify GREEN**

Run the Task 4 test command. Expected: all selected tests pass.

- [ ] **Step 7: Commit Task 4**

```powershell
git add -- Niratan/Views/Pages/NovelReaderPage.xaml.cs Niratan/ViewModels/Pages/NovelReaderPageViewModel.cs Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs Niratan.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs
git commit -m "fix(reader): gate position mutation during navigation"
```

---

### Task 5: Remove legacy navigation state and perform final verification

**Files:**
- Delete: `Niratan/Services/Novels/ReaderProgrammaticNavigationTracker.cs`
- Delete: `Niratan/Services/Novels/ReaderAdjacentNavigationCommitCoordinator.cs`
- Delete: `Niratan.Tests/Services/Novels/ReaderProgrammaticNavigationTrackerTests.cs`
- Delete: `Niratan.Tests/Services/Novels/ReaderAdjacentNavigationCommitCoordinatorTests.cs`
- Modify: `docs/VERIFICATION.md`
- Modify: `docs/CHANGELOG.md`
- Modify: `.superpowers/sdd/progress.md`

**Interfaces:**
- Consumes: completed transaction flow from Tasks 1-4.
- Produces: one navigation owner with no duplicate tracker/coordinator path.

- [ ] **Step 1: Write the legacy-removal contract test**

Update `NovelReaderWebAssetTests` to assert the Page references `ReaderNavigationTransactionCoordinator` only through ViewModel APIs and contains none of:

```text
ReaderProgrammaticNavigationTracker
ReaderAdjacentNavigationCommitCoordinator
_pendingAdjacentChapterNavigation
_latestAdmittedProgressRequest
```

- [ ] **Step 2: Run the contract test and verify RED**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderWebAssetTests"
```

Expected: failure while legacy types or fields remain.

- [ ] **Step 3: Delete legacy files and remove remaining references**

Delete the four superseded production/test files listed above. Remove revision/candidate workarounds that exist only for the split navigation path. Preserve ordinary writer-tail serialization for bookmarks/statistics.

- [ ] **Step 4: Update verification and changelog**

Document:

- direct A-last -> B-first -> A-last behavior with no temporary `1.0`;
- lifecycle during `Rendering` versus `Committing`;
- bridge-error source/destination recovery;
- Sasayaki mutation gate;
- no real Drive mutation in automated verification.

- [ ] **Step 5: Run focused transaction suites**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ReaderNavigationTransactionCoordinatorTests|FullyQualifiedName~NovelReaderPageViewModelTests|FullyQualifiedName~NovelReaderWebAssetTests|FullyQualifiedName~NovelReaderStatisticsLifecycleTests"
```

Expected: all selected tests pass.

- [ ] **Step 6: Run statistics/sync targeted suites**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Statistics|FullyQualifiedName~TtuSync|FullyQualifiedName~GoogleDrive|FullyQualifiedName~ReaderAutoSyncCoordinator"
```

Expected: all selected tests pass without a real Google Drive request.

- [ ] **Step 7: Run full x64 tests and build**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64
dotnet build -p:Platform=x64
```

Expected: zero failed tests and zero build errors. Existing `NU1903` for `SQLitePCLRaw.lib.e_sqlite3` and the unrelated `xUnit1051` warning may remain.

- [ ] **Step 8: Verify format and diff**

```powershell
$base = git merge-base HEAD main
$files = @(git diff --name-only "$base..HEAD" -- '*.cs')
dotnet format Niratan.slnx --verify-no-changes --include $files
git diff --check "$base..HEAD"
```

Expected: changed-file format passes and `git diff --check` exits zero. Do not alter the two dirty reference submodule markers.

- [ ] **Step 9: Run exact-worktree smoke verification**

Build and start only:

```text
D:\CODE\Yukari\.worktrees\niratan-reader-statistics-parity\Niratan\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Niratan.exe
```

Verify its process path and non-zero main-window handle. If safe UI automation can identify that exact process, check a backward adjacent boundary and confirm progress chrome has no temporary 100%/`1.0` update. Do not stop or interact with another Niratan instance.

- [ ] **Step 10: Commit Task 5**

```powershell
git add -- Niratan Niratan.Tests docs/VERIFICATION.md docs/CHANGELOG.md .superpowers/sdd/progress.md
git reset -- docs/reference/Niratan
git commit -m "test(reader): verify atomic navigation transactions"
```

- [ ] **Step 11: Request final whole-branch review**

Review `90ffeab592a964997a99ad79d80a6bd884e169ba..HEAD`, with explicit attention to transaction cancellation, lifecycle settlement, bridge errors, Sasayaki callbacks, TTU empty Replace/rollback, and exact-once statistics behavior. Fix every Critical and Important issue before delivery.
