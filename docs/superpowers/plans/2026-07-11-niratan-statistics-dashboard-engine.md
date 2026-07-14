# Niratan Statistics Dashboard Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Port Niratan's complete typed dashboard repository, calculations, comparison models, and derived cache without coupling calculations to WinUI.

**Architecture:** `NovelStatisticsDashboardRepository` loads visible book metadata, book-info, statistics, and shelf state off the UI thread into an immutable snapshot. `NovelStatisticsDashboardCalculator` contains only pure functions for ranges, goals, speeds, trends, calendar, ranking, and shelves. A versioned cache accelerates startup but never becomes a source of truth.

**Tech Stack:** C# 14 / .NET 10, System.Text.Json, existing Niratan atomic JSON store, xUnit v3, FluentAssertions, Moq.

## Global Constraints

- Port behavior from `docs/reference/Niratan/Features/Bookshelf/StatisticsDashboardModels.swift`.
- Dashboard window is the recent year ending on the current local date.
- Valid speed samples require positive characters and at least 60 seconds; invalid samples remain in totals.
- Percentages use rounding, may exceed 100, and future week cells have no percentage.
- Remove the legacy By Book distribution model; ranking replaces it.
- Corrupt statistics are reported per book and never overwritten by loading or cache recovery.
- Cache schema/key mismatch or corrupt cache deletes only the derived cache and rebuilds asynchronously.
- No WinUI types in repository/calculator APIs; build/test x64 only.

---

### Task 1: Complete Dashboard Snapshot and Repository Models

**Files:**
- Replace: `Niratan/Models/Novel/NovelStatisticsDashboardModels.cs`
- Modify: `Niratan/Services/Novels/INovelStatisticsDashboardService.cs`
- Modify: `Niratan/Services/Novels/NovelStatisticsDashboardService.cs`
- Modify: `Niratan/Services/Novels/NovelStatisticsSidecarService.cs`
- Test: `Niratan.Tests/Services/Novels/NovelStatisticsDashboardRepositoryTests.cs`

**Interfaces:**
- Consumes: visible `NovelBook` list, `INovelBookSidecarService`, `INovelStatisticsSidecarService`.
- Produces: book records, day aggregates, speed-capable contributions, and corrupt book IDs.

- [ ] **Step 1: Write repository failures**

```csharp
[Fact]
public async Task LoadSnapshot_KeepsTotalsButMarksShortBurstsInvalidForSpeed()
{
    var snapshot = await repository.LoadSnapshotAsync([book], ct);
    snapshot.Days.Single().Characters.Should().Be(200);
    snapshot.Days.Single().ReadingTime.Should().Be(80);
    snapshot.Days.Single().BookContributions.Should().Contain(x => !x.IsValidSpeedSample);
}

[Fact]
public async Task LoadSnapshot_ReportsCorruptStatisticsWithoutOverwritingThem()
{
    await File.WriteAllTextAsync(statisticsPath, "{ broken", ct);
    var before = await File.ReadAllBytesAsync(statisticsPath, ct);
    var snapshot = await repository.LoadSnapshotAsync([book], ct);
    snapshot.SkippedCorruptBookIds.Should().Contain(book.Id);
    (await File.ReadAllBytesAsync(statisticsPath, ct)).Should().Equal(before);
}
```

- [ ] **Step 2: Run and verify failures**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardRepositoryTests"
```

- [ ] **Step 3: Add exact snapshot types and status-aware loading**

```csharp
public sealed record NovelStatisticsBookRecord(
    string Id, string Title, string? CoverPath, int TotalCharacterCount);

public sealed record NovelStatisticsBookContribution(
    string BookId, string Title, string? CoverPath,
    int Characters, double ReadingTime, bool IsValidSpeedSample);

public sealed record NovelStatisticsDashboardSnapshot(
    DateOnly WindowStart,
    DateOnly WindowEnd,
    IReadOnlyList<NovelStatisticsDayAggregate> Days,
    IReadOnlyList<NovelStatisticsBookRecord> Books,
    IReadOnlyList<string> SkippedCorruptBookIds);
```

Change sidecar loading to return a status-bearing result so missing and corrupt files are distinct. Parse only exact `yyyy-MM-dd` keys, deduplicate by newest modification time, use current title/cover/book-info totals, and run file IO inside `Task.Run` or async file APIs without blocking the UI thread.

- [ ] **Step 4: Run repository tests and commit**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardRepositoryTests|FullyQualifiedName~NovelStatisticsSidecarServiceTests"
git add Niratan/Models/Novel/NovelStatisticsDashboardModels.cs Niratan/Services/Novels/INovelStatisticsDashboardService.cs Niratan/Services/Novels/NovelStatisticsDashboardService.cs Niratan/Services/Novels/NovelStatisticsSidecarService.cs Niratan.Tests/Services/Novels/NovelStatisticsDashboardRepositoryTests.cs
git commit -m "feat(statistics): load complete dashboard snapshots"
```

### Task 2: Target Snapping, Range Selection, Today, and Week

**Files:**
- Create: `Niratan/Services/Novels/NovelStatisticsDashboardCalculator.cs`
- Modify: `Niratan/Models/Novel/NovelStatisticsDashboardModels.cs`
- Test: `Niratan.Tests/Services/Novels/NovelStatisticsDashboardSummaryTests.cs`

**Interfaces:**
- Produces: `RecentYear`, `SelectedRange`, `TodaySummary`, `WeekSummary`, `RangeSummary`.

- [ ] **Step 1: Port target/range fixtures as failing tests**

Cover character snap `500...20_000` by 500, duration snap `5...240` by 5, weekly days `1...7`, year/month/Monday-week/day selection clipped to recent-year window, complete Monday-Sunday week cells, elapsed-day averages, goal percentages above 100, daily streak, and weekly streak.

```csharp
NovelStatisticsDashboardTargets.SnapCharacterTarget(749).Should().Be(500);
NovelStatisticsDashboardTargets.SnapCharacterTarget(750).Should().Be(1_000);
NovelStatisticsDashboardTargets.SnapDurationTarget(33).Should().Be(35);
```

- [ ] **Step 2: Run and verify failures**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardSummaryTests"
```

- [ ] **Step 3: Implement pure target/range/summary functions**

Use `DateOnly`; Monday offset is `((int)DayOfWeek + 6) % 7`. Today may extend an ongoing streak from yesterday when its target is not met. Week summary always exposes seven cells but excludes future cells from goal percentages and elapsed averages.

- [ ] **Step 4: Run and commit**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardSummaryTests"
git add Niratan/Services/Novels/NovelStatisticsDashboardCalculator.cs Niratan/Models/Novel/NovelStatisticsDashboardModels.cs Niratan.Tests/Services/Novels/NovelStatisticsDashboardSummaryTests.cs
git commit -m "feat(statistics): port dashboard goal summaries"
```

### Task 3: Speed Summary and Active-Day Windows

**Files:**
- Modify: `Niratan/Services/Novels/NovelStatisticsDashboardCalculator.cs`
- Modify: `Niratan/Models/Novel/NovelStatisticsDashboardModels.cs`
- Test: `Niratan.Tests/Services/Novels/NovelStatisticsDashboardSpeedTests.cs`

**Interfaces:**
- Produces: `NovelStatisticsSpeedSummary` with weighted, median, last-seven-active-days, change, fastest, and slowest values.

- [ ] **Step 1: Add Niratan speed fixtures**

Assert short bursts contribute totals but not speed, weighted speed combines only valid samples, median uses active-day speeds, last seven ignores inactive dates, change is absent until two non-overlapping fourteen-active-day windows exist, and equal fastest/slowest speeds choose deterministic dates.

- [ ] **Step 2: Run and verify failures**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardSpeedTests"
```

- [ ] **Step 3: Implement the speed API**

```csharp
public sealed record NovelStatisticsSpeedSummary(
    int? WeightedAveragePerHour,
    int? MedianActiveDayPerHour,
    int? LastSevenActiveDaysPerHour,
    int? ChangePercent,
    NovelStatisticsSpeedDay? FastestDay,
    NovelStatisticsSpeedDay? SlowestDay);
```

Filter contributions with `Characters > 0 && ReadingTime >= 60`; never introduce `0/h` samples. Sort fastest ties by earliest date and slowest ties by earliest date, matching the reference fixture.

- [ ] **Step 4: Run and commit**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardSpeedTests"
git add Niratan/Services/Novels/NovelStatisticsDashboardCalculator.cs Niratan/Models/Novel/NovelStatisticsDashboardModels.cs Niratan.Tests/Services/Novels/NovelStatisticsDashboardSpeedTests.cs
git commit -m "feat(statistics): port dashboard speed windows"
```

### Task 4: Trends and Reading Calendar

**Files:**
- Modify: `Niratan/Services/Novels/NovelStatisticsDashboardCalculator.cs`
- Modify: `Niratan/Models/Novel/NovelStatisticsDashboardModels.cs`
- Test: `Niratan.Tests/Services/Novels/NovelStatisticsDashboardTrendTests.cs`

**Interfaces:**
- Produces: day/week/month `TrendPoints` and recent-year `CalendarDays`.

- [ ] **Step 1: Add failing trend/calendar fixtures**

Cover day/week/month grains independent of range mode; characters/duration/valid-speed metrics; inactive interior daily fill with leading/trailing trim; every weekly/monthly period touched by the active range; top contributors in each point; selected calendar day totals and active-book count.

- [ ] **Step 2: Run and verify failures**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardTrendTests"
```

- [ ] **Step 3: Implement typed trend/calendar models**

```csharp
public enum NovelStatisticsTrendGrain { Day, Week, Month }
public enum NovelStatisticsTrendMetric { Characters, Duration, Speed }
public sealed record NovelStatisticsTrendPoint(
    string Id, string Label, int Characters, double ReadingTime,
    int? AverageSpeedPerHour,
    IReadOnlyList<NovelStatisticsTrendBookBreakdown> TopBooks);
```

Group week points by Monday and month points by first day. Sort tooltip contributors by characters descending then title/id deterministically.

- [ ] **Step 4: Run and commit**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardTrendTests"
git add Niratan/Services/Novels/NovelStatisticsDashboardCalculator.cs Niratan/Models/Novel/NovelStatisticsDashboardModels.cs Niratan.Tests/Services/Novels/NovelStatisticsDashboardTrendTests.cs
git commit -m "feat(statistics): port trends and calendar"
```

### Task 5: Book Ranking and Shelf Comparison

**Files:**
- Modify: `Niratan/Services/Novels/NovelStatisticsDashboardCalculator.cs`
- Modify: `Niratan/Models/Novel/NovelStatisticsDashboardModels.cs`
- Test: `Niratan.Tests/Services/Novels/NovelStatisticsDashboardComparisonTests.cs`

**Interfaces:**
- Consumes: snapshot plus `NovelShelfState`.
- Produces: twelve-row ranking and custom/Unshelved shelf comparison.

- [ ] **Step 1: Add comparison fixtures**

Cover ranking by characters, duration, valid weighted speed, deterministic ties, and limit 12. Cover shelf book count, total book characters, recorded characters, duration, valid speed, unknown-ID exclusion, and Unshelved row.

- [ ] **Step 2: Run and verify failures**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardComparisonTests"
```

- [ ] **Step 3: Implement ranking and shelf rows**

```csharp
public enum NovelStatisticsBookRankingMetric { Characters, Duration, Speed }
public sealed record NovelStatisticsShelfComparisonRow(
    string Id, string Name, int BookCount, int TotalBookCharacters,
    int RecordedCharacters, double ReadingTime, int? AverageSpeedPerHour);
```

Aggregate contributions by book before joining shelf IDs; use only valid samples for speed and all samples for counts/time.

- [ ] **Step 4: Run and commit**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardComparisonTests"
git add Niratan/Services/Novels/NovelStatisticsDashboardCalculator.cs Niratan/Models/Novel/NovelStatisticsDashboardModels.cs Niratan.Tests/Services/Novels/NovelStatisticsDashboardComparisonTests.cs
git commit -m "feat(statistics): rank books and compare shelves"
```

### Task 6: Versioned Derived Cache and Invalidation

**Files:**
- Create: `Niratan/Services/Novels/NovelStatisticsDashboardCache.cs`
- Modify: `Niratan/Services/Novels/NovelStatisticsDashboardService.cs`
- Modify: `Niratan/Messages/NovelLibraryChangedMessage.cs`
- Test: `Niratan.Tests/Services/Novels/NovelStatisticsDashboardCacheTests.cs`

**Interfaces:**
- Produces: schema-versioned cache keyed by visible book identity and invalidated by metadata/statistics/book-info/shelves/removal changes.

- [ ] **Step 1: Add cache failures**

Assert stable keys include ID, folder, title, metadata/book-info/statistics modification projections; reordered input produces the same key; schema/key mismatch and malformed JSON delete only cache; cancellation is observed; each file-storage event invalidates memory and disk entries.

- [ ] **Step 2: Run and verify failures**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardCacheTests"
```

- [ ] **Step 3: Implement cache-as-derivative**

Store `statistics_dashboard_cache_v1.json` through `INiratanJsonFileStore`. Return a matching cached snapshot immediately, then rebuild asynchronously from sidecars. Never write cache data back into book files. On invalid cache, delete the cache and continue with a fresh repository load.

- [ ] **Step 4: Run and commit**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardCacheTests|FullyQualifiedName~NovelStatisticsDashboardRepositoryTests"
git add Niratan/Services/Novels/NovelStatisticsDashboardCache.cs Niratan/Services/Novels/NovelStatisticsDashboardService.cs Niratan/Messages/NovelLibraryChangedMessage.cs Niratan.Tests/Services/Novels/NovelStatisticsDashboardCacheTests.cs
git commit -m "feat(statistics): cache derived dashboard snapshots"
```

### Task 7: Remove Legacy Distribution and Verify Engine Parity

**Files:**
- Modify: `Niratan/ViewModels/Pages/NovelLibraryPageViewModel.cs`
- Modify: `Niratan/Views/Pages/NovelLibraryPage.xaml`
- Modify: `Niratan.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`
- Modify: `docs/ARCHITECTURE.md`
- Modify: `docs/VERIFICATION.md`
- Modify: `docs/CHANGELOG.md`

**Interfaces:**
- Consumes: complete engine.
- Produces: engine-ready ViewModel projections without the removed distribution module; full WinUI dashboard layout remains in its separate approved UI plan.

- [ ] **Step 1: Replace distribution expectations with typed engine projections**

Expose snapshot/loading/corrupt IDs and derived today/week/range/speed/trend/calendar/ranking/shelf properties. Remove `NovelStatisticsDistributionRow`, `DistributionRows`, and the XAML `By Book` list.

- [ ] **Step 2: Run focused engine/ViewModel tests**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboard|FullyQualifiedName~NovelLibraryPageViewModelTests"
```

- [ ] **Step 3: Run full verification**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64
dotnet build -p:Platform=x64
```

Expected: all tests pass and build has zero errors; retained video SQLite may still emit the known advisory.

- [ ] **Step 4: Document and commit**

```powershell
git add Niratan/ViewModels/Pages/NovelLibraryPageViewModel.cs Niratan/Views/Pages/NovelLibraryPage.xaml Niratan.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs docs/ARCHITECTURE.md docs/VERIFICATION.md docs/CHANGELOG.md
git commit -m "feat(statistics): complete niratan dashboard engine"
```
