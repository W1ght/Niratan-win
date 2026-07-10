# Niratan Reader Statistics Semantics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move Reader statistics ownership into a deterministic session service and make manual, programmatic, Sasayaki, and lifecycle events match Niratan checkpoint semantics.

**Architecture:** `ReaderStatisticsSession` owns clocks, baselines, local-day rollover, TTU projections, and sidecar writes. `NovelReaderPageViewModel` projects that state; `NovelReaderPage.xaml.cs` only classifies UI/WebView events and forwards typed operations. `statistics.json` remains the TTU v1.6-compatible source of truth.

**Tech Stack:** C# 14 / .NET 10 `TimeProvider`, CommunityToolkit.Mvvm, System.Text.Json, WinUI 3, xUnit v3, FluentAssertions, Moq.

## Global Constraints

- Preserve every TTU statistic field and deduplicate by `dateKey` using newest `lastStatisticModified`.
- Use Windows local time for date keys and rollover, never UTC day boundaries.
- JavaScript reports movement/progress only; statistics policy stays native.
- Natural adjacent chapters are reading checkpoints; all other destinations use the programmatic transaction.
- Programmatic navigation orders work as: flush old once → resolve → save bookmark without stats flush → reset baseline.
- Negative movement cannot reduce stored session/day characters below zero.
- Do not modify `native/hoshidicts/`; build/test x64 only.

---

### Task 1: Session Contract and Pure TTU Math

**Files:**
- Create: `Hoshi/Models/Novel/ReaderStatisticsSessionModels.cs`
- Create: `Hoshi/Services/Novels/IReaderStatisticsSession.cs`
- Create: `Hoshi/Services/Novels/ReaderStatisticsMath.cs`
- Test: `Hoshi.Tests/Services/Novels/ReaderStatisticsMathTests.cs`

**Interfaces:**
- Consumes: `NovelReadingStatistic`.
- Produces: `ReaderStatisticsPosition`, `ReaderStatisticsCheckpointReason`, `ReaderStatisticsSessionState`, `IReaderStatisticsSession`, and pure math helpers.

- [ ] **Step 1: Write failing contract/formula tests**

```csharp
[Fact]
public void Update_UsesTtuFormulaAndClampsNegativeMovement()
{
    var source = ReaderStatisticsMath.Empty("Book", new DateOnly(2026, 7, 11));
    var first = ReaderStatisticsMath.Update(source, 120, 60, 42);
    first.LastReadingSpeed.Should().Be(1_800);
    ReaderStatisticsMath.Update(first, 1, -1_000, 43).CharactersRead.Should().Be(0);
}

[Fact]
public void Deduplicate_KeepsNewestModificationForEachDateKey()
{
    var result = ReaderStatisticsMath.Deduplicate([
        Statistic("2026-07-11", 1, modified: 10),
        Statistic("2026-07-11", 2, modified: 20),
    ]);
    result.Should().ContainSingle().Which.CharactersRead.Should().Be(2);
}
```

- [ ] **Step 2: Run tests and verify missing-type failures**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ReaderStatisticsMathTests"
```

- [ ] **Step 3: Add the typed API**

```csharp
public readonly record struct ReaderStatisticsPosition(int RawCharacterCount);

public enum ReaderStatisticsCheckpointReason
{
    ReadingMovement, AdjacentChapter, ProgrammaticDeparture,
    Pause, Stop, Close, Background,
}

public sealed record ReaderStatisticsSessionState(
    bool IsTracking,
    bool IsPaused,
    NovelReadingStatistic Session,
    NovelReadingStatistic Today,
    NovelReadingStatistic AllTime,
    IReadOnlyList<NovelReadingStatistic> History);

public interface IReaderStatisticsSession
{
    ReaderStatisticsSessionState State { get; }
    event EventHandler<ReaderStatisticsSessionState>? StateChanged;
    Task LoadAsync(string bookRoot, string title, ReaderStatisticsPosition position, CancellationToken ct = default);
    void Start(ReaderStatisticsPosition position);
    void Tick(ReaderStatisticsPosition position);
    Task CheckpointAsync(ReaderStatisticsPosition position, ReaderStatisticsCheckpointReason reason, CancellationToken ct = default);
    Task PauseAsync(ReaderStatisticsPosition position, CancellationToken ct = default);
    Task StopAsync(ReaderStatisticsPosition position, CancellationToken ct = default);
    void ResetBaseline(ReaderStatisticsPosition position);
}
```

`ReaderStatisticsMath.Update` adds nonnegative time, clamps total characters, uses integer truncation for `characters / seconds * 3600`, updates min/max fields with the existing Niratan formulas, and stamps Unix milliseconds. `Aggregate` derives all-time speed from summed counts/time. `Deduplicate` uses ordinal date keys.

- [ ] **Step 4: Run tests and commit**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ReaderStatisticsMathTests"
git add Hoshi/Models/Novel/ReaderStatisticsSessionModels.cs Hoshi/Services/Novels/IReaderStatisticsSession.cs Hoshi/Services/Novels/ReaderStatisticsMath.cs Hoshi.Tests/Services/Novels/ReaderStatisticsMathTests.cs
git commit -m "feat(statistics): define reader session math"
```

### Task 2: Deterministic Session, Checkpoints, and Local Rollover

**Files:**
- Create: `Hoshi/Services/Novels/ReaderStatisticsSession.cs`
- Modify: `Hoshi/App.xaml.cs`
- Test: `Hoshi.Tests/Services/Novels/ReaderStatisticsSessionTests.cs`

**Interfaces:**
- Consumes: `TimeProvider`, `INovelStatisticsSidecarService`, Task 1 types.
- Produces: transient `IReaderStatisticsSession` implementation.

- [ ] **Step 1: Add deterministic lifecycle tests**

Use a manual `TimeProvider`. Cover load, start, tick, checkpoint, pause, stop, reload, negative movement, and a `23:59:59 → 00:00:01` local rollover. Assert ticks do not write; checkpoints write once; paused ticks do not accumulate; rollover archives the old date and establishes the new date.

```csharp
session.Start(new ReaderStatisticsPosition(100));
clock.Advance(TimeSpan.FromSeconds(2));
session.Tick(new ReaderStatisticsPosition(120));
await session.CheckpointAsync(new ReaderStatisticsPosition(120),
    ReaderStatisticsCheckpointReason.ReadingMovement, ct);
sidecars.Verify(x => x.SaveAsync(root, It.IsAny<IReadOnlyList<NovelReadingStatistic>>(), ct), Times.Once);
```

- [ ] **Step 2: Run and verify failures**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ReaderStatisticsSessionTests"
```

- [ ] **Step 3: Implement ownership and DI**

Store `_lastTimestamp`, `_baseline`, `_bookRoot`, `_title`, and deduplicated history. Split elapsed time at local midnight before applying the remaining interval. `Tick` advances in-memory state; `CheckpointAsync` applies elapsed state and saves once. `PauseAsync`/`StopAsync` checkpoint then change flags. `ResetBaseline` changes timestamp/position without totals.

```csharp
services.AddTransient<IReaderStatisticsSession>(provider =>
    new ReaderStatisticsSession(
        provider.GetRequiredService<INovelStatisticsSidecarService>(),
        TimeProvider.System));
```

- [ ] **Step 4: Run focused regression and commit**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ReaderStatisticsSessionTests|FullyQualifiedName~NovelStatisticsSidecarServiceTests"
git add Hoshi/Services/Novels/ReaderStatisticsSession.cs Hoshi/App.xaml.cs Hoshi.Tests/Services/Novels/ReaderStatisticsSessionTests.cs
git commit -m "feat(statistics): track deterministic reader sessions"
```

### Task 3: Refactor Reader ViewModel into a Session Projection

**Files:**
- Modify: `Hoshi/ViewModels/Pages/NovelReaderPageViewModel.cs`
- Modify: `Hoshi.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs`

**Interfaces:**
- Consumes: `IReaderStatisticsSession`.
- Produces: session-backed start/checkpoint/pause/stop/reset methods, `Task SaveProgressNowAsync(bool flushStatistics = true, CancellationToken ct = default)`, and existing UI text properties.

- [ ] **Step 1: Change tests to inject and verify a mocked session**

```csharp
session.Verify(s => s.Start(new ReaderStatisticsPosition(sut.CurrentCharacterCount)), Times.Once);
session.Verify(s => s.CheckpointAsync(
    new ReaderStatisticsPosition(sut.CurrentCharacterCount),
    ReaderStatisticsCheckpointReason.ReadingMovement, ct), Times.Once);
```

Assert sidecar writes and timestamps no longer belong to the ViewModel. A `StateChanged` event must refresh session/today/all-time properties and remaining-time estimates.

- [ ] **Step 2: Run and observe constructor/expectation failures**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderPageViewModelTests"
```

- [ ] **Step 3: Delegate all tracking policy**

Remove `_statistics`, `_lastStatisticsTimestamp`, `_lastStatisticsCharacterCount`, direct sidecar calls, `UpdateStatistic`, and `AggregateAllTime`. Retain bookmark persistence in `INovelLibraryService`.

```csharp
public Task CheckpointReadingAsync(ReaderStatisticsCheckpointReason reason, CancellationToken ct = default) =>
    _statisticsSession.CheckpointAsync(new(CurrentCharacterCount), reason, ct);

public void ResetStatisticsBaseline() =>
    _statisticsSession.ResetBaseline(new(CurrentCharacterCount));

public async Task SaveProgressNowAsync(
    bool flushStatistics = true,
    CancellationToken ct = default)
{
    await SaveCanonicalBookmarkAsync(ct);
    if (flushStatistics)
        await CheckpointReadingAsync(ReaderStatisticsCheckpointReason.ReadingMovement, ct);
}
```

- [ ] **Step 4: Run and commit**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderPageViewModelTests"
git add Hoshi/ViewModels/Pages/NovelReaderPageViewModel.cs Hoshi.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs
git commit -m "refactor(statistics): project reader session state"
```

### Task 4: Actual-Movement Autostart and Reading Checkpoints

**Files:**
- Modify: `Hoshi/Views/Pages/NovelReaderPage.xaml.cs`
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`
- Create: `Hoshi.Tests/Views/Pages/NovelReaderStatisticsEventMatrixTests.cs`

**Interfaces:**
- Consumes: Task 3 ViewModel methods and typed bridge results.
- Produces: correct Off/PageTurn/On behavior and one checkpoint per real movement.

- [ ] **Step 1: Add an event matrix**

Require PageTurn start only after bridge `moved`, changed continuous-scroll progress, natural adjacent chapter, Sasayaki changed progress, or Sasayaki chapter movement. Require no start/checkpoint for boundary `limit`, same-progress scroll, or current-cue highlighting. Require On start only after restore.

- [ ] **Step 2: Run and verify current failures**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderStatisticsEventMatrixTests|FullyQualifiedName~NovelReaderWebAssetTests"
```

- [ ] **Step 3: Route verified movement**

Move PageTurn autostart inside successful-result branches. Ordinary moves call `SaveProgressNowAsync(flushStatistics: false)` and then `CheckpointReadingAsync(ReadingMovement)` exactly once. Natural previous/next chapter changes use the same bookmark call followed by `AdjacentChapter`.

- [ ] **Step 4: Run and commit**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderStatisticsEventMatrixTests|FullyQualifiedName~NovelReaderWebAssetTests"
git add Hoshi/Views/Pages/NovelReaderPage.xaml.cs Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs Hoshi.Tests/Views/Pages/NovelReaderStatisticsEventMatrixTests.cs
git commit -m "fix(statistics): count only actual reader movement"
```

### Task 5: Programmatic Navigation Transaction

**Files:**
- Modify: `Hoshi/Views/Pages/NovelReaderPage.xaml.cs`
- Modify: `Hoshi/ViewModels/Pages/NovelReaderPageViewModel.cs`
- Modify: `Hoshi.Tests/Views/Pages/NovelReaderStatisticsEventMatrixTests.cs`

**Interfaces:**
- Consumes: `ProgrammaticDeparture`, bookmark save without a statistics flush, baseline reset.
- Produces: exactly-once navigation ordering.

- [ ] **Step 1: Parameterize chapter list, character jump, highlight, history, internal link, and non-natural Sasayaki/lyrics tests**

Assert flush old → resolve → save bookmark → reset. Assert no second statistics flush. Same-chapter links must not reload; cross-chapter links wait for resolved bridge progress.

- [ ] **Step 2: Run and verify failures**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderStatisticsEventMatrixTests.Programmatic"
```

- [ ] **Step 3: Add transaction helpers**

```csharp
private Task BeginProgrammaticNavigationAsync() =>
    ViewModel.CheckpointReadingAsync(ReaderStatisticsCheckpointReason.ProgrammaticDeparture);

private async Task CompleteProgrammaticNavigationAsync()
{
    await ViewModel.SaveProgressNowAsync(flushStatistics: false);
    ViewModel.ResetStatisticsBaseline();
}
```

Use one pending destination generation for cross-chapter resolution so stale bridge callbacks cannot complete a newer navigation.

- [ ] **Step 4: Run and commit**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderStatisticsEventMatrixTests|FullyQualifiedName~NovelReaderPageViewModelTests"
git add Hoshi/Views/Pages/NovelReaderPage.xaml.cs Hoshi/ViewModels/Pages/NovelReaderPageViewModel.cs Hoshi.Tests/Views/Pages/NovelReaderStatisticsEventMatrixTests.cs
git commit -m "fix(statistics): isolate programmatic navigation"
```

### Task 6: One-Second Projection and Lifecycle Flushes

**Files:**
- Modify: `Hoshi/Views/Pages/NovelReaderPage.xaml.cs`
- Modify: `Hoshi/App.xaml.cs`
- Create: `Hoshi/Messages/AppBackgroundingMessage.cs`
- Create: `Hoshi.Tests/Views/Pages/NovelReaderStatisticsLifecycleTests.cs`

**Interfaces:**
- Consumes: session tick/close/background operations.
- Produces: live projections and exactly-once lifecycle checkpoints.

- [ ] **Step 1: Add timer and lifecycle tests**

Assert a one-second tick only while tracking/unpaused. Assert Reader close and app background each checkpoint once even if lifecycle callbacks race. Assert unloading stops the timer.

- [ ] **Step 2: Run and verify failures**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderStatisticsLifecycleTests"
```

- [ ] **Step 3: Implement native lifecycle routing**

Use `DispatcherQueueTimer` for ticks. Publish `AppBackgroundingMessage` from the app lifecycle callback. Serialize lifecycle checkpoint requests with one `SemaphoreSlim` and a transition generation.

- [ ] **Step 4: Run and commit**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderStatisticsLifecycleTests|FullyQualifiedName~NovelReaderPageViewModelTests"
git add Hoshi/Views/Pages/NovelReaderPage.xaml.cs Hoshi/App.xaml.cs Hoshi/Messages/AppBackgroundingMessage.cs Hoshi.Tests/Views/Pages/NovelReaderStatisticsLifecycleTests.cs
git commit -m "feat(statistics): flush reader lifecycle checkpoints"
```

### Task 7: Full Verification and Documentation

**Files:**
- Modify: `docs/ARCHITECTURE.md`
- Modify: `docs/VERIFICATION.md`
- Modify: `docs/CHANGELOG.md`

**Interfaces:**
- Consumes: Tasks 1-6.
- Produces: trustworthy Reader statistics for the dashboard engine.

- [ ] **Step 1: Document session ownership, TTU formulas, event matrix, navigation transaction, and local rollover**

- [ ] **Step 2: Run focused and full verification**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Statistics|FullyQualifiedName~NovelReaderPageViewModelTests"
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
dotnet build -p:Platform=x64
```

Expected: zero failed tests and zero build errors; the retained video SQLite advisory may remain.

- [ ] **Step 3: Launch and verify**

Run `./build-and-run.ps1`. Verify Off/PageTurn/On, actual/no-op movements, adjacent chapters, every programmatic destination, Sasayaki movement/no-op, pause, close, background, and reopen. Confirm one `statistics.json` entry per local date and no jump inflation.

- [ ] **Step 4: Commit docs**

```powershell
git add docs/ARCHITECTURE.md docs/VERIFICATION.md docs/CHANGELOG.md
git commit -m "docs: record niratan reader statistics semantics"
```
