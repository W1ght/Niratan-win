# Statistics Dashboard Cache Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the statistics dashboard load Google Drive-imported statistics by deserializing book contributions correctly and rebuilding safely when its derived cache is incompatible.

**Architecture:** Keep the source `statistics.json` sidecars unchanged. Fix the dashboard snapshot model's JSON constructor contract, then contain model-compatibility recovery inside `NovelStatisticsDashboardCache`, where deleting the derived cache is safe and naturally triggers the existing sidecar rebuild path.

**Tech Stack:** C#/.NET 10, System.Text.Json, WinUI 3, xUnit v3, FluentAssertions.

## Global Constraints

- Target Windows 10+ x64; build with `dotnet build -p:Platform=x64`.
- Do not modify `native/hoshidicts/` or any reference submodule.
- Do not change Google Drive download or statistics-sync settings semantics.
- Do not change the per-book `statistics.json` format.
- Cache recovery may delete only `statistics_dashboard_cache_v1.json`.
- Preserve the user's existing uncommitted changes in `NovelReaderWebAssetTests.cs`, `DictionaryPopupOverlay.cs`, and `2026-07-11-novel-library-commandbar-and-sort.md`.

## File map

- `Hoshi/Models/Novel/NovelStatisticsDashboardModels.cs`: declares the explicit JSON constructor for persisted book contributions.
- `Hoshi/Services/Novels/NovelStatisticsDashboardCache.cs`: converts unsupported derived-cache models into a cache miss and clears only the derived cache.
- `Hoshi.Tests/Services/Novels/NovelStatisticsDashboardCacheTests.cs`: covers real contribution round trips and unsupported-cache recovery.
- `docs/CHANGELOG.md`: records the root cause and durable fix.
- `docs/VERIFICATION.md`: records the incompatible-cache regression scenario.

---

### Task 1: Persist and reload book contributions

**Files:**
- Modify: `Hoshi.Tests/Services/Novels/NovelStatisticsDashboardCacheTests.cs:42-70`
- Modify: `Hoshi/Models/Novel/NovelStatisticsDashboardModels.cs:20-40`

**Interfaces:**
- Consumes: `NovelStatisticsDashboardCache.StoreAsync(string, NovelStatisticsDashboardSnapshot, CancellationToken)` and `TryLoadAsync(string, CancellationToken)`.
- Produces: an explicit `[method: JsonConstructor]` contract on the six-parameter `NovelStatisticsBookContribution` primary constructor.

- [ ] **Step 1: Extend the disk round-trip test with a real contribution**

Replace the snapshot construction and final assertion in `StoredSnapshot_ReloadsFromDiskInNewCacheInstance` with:

```csharp
var contribution = new NovelStatisticsBookContribution(
    "book-1",
    "かがみの孤城",
    "cover.jpg",
    153371,
    44387.1125397682,
    true);
var snapshot = new NovelStatisticsDashboardSnapshot(
    today,
    today,
    [new NovelStatisticsDayAggregate(today, 153371, 44387.1125397682, [contribution])],
    [new NovelStatisticsBookRecord("book-1", "かがみの孤城", "cover.jpg", 248250)],
    []);
```

Keep the existing `StoreAsync` and new-cache construction, then assert:

```csharp
var loaded = await reloaded.TryLoadAsync("key", ct);

loaded.Should().BeEquivalentTo(snapshot);
loaded!.Days.Should().ContainSingle()
    .Which.BookContributions.Should().ContainSingle()
    .Which.Should().BeEquivalentTo(contribution);
```

- [ ] **Step 2: Run the focused test and verify the regression is red**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardCacheTests.StoredSnapshot_ReloadsFromDiskInNewCacheInstance"
```

Expected: FAIL with `NotSupportedException` stating that `NovelStatisticsBookContribution` does not have a supported JSON constructor.

- [ ] **Step 3: Mark the primary contribution constructor for JSON**

Change the declaration to:

```csharp
[method: JsonConstructor]
public sealed record NovelStatisticsBookContribution(
    string BookId,
    string Title,
    string? CoverPath,
    int Characters,
    double ReadingTime,
    bool IsValidSpeedSample)
```

Leave the existing five-parameter convenience constructor unchanged.

- [ ] **Step 4: Run the focused test and verify it is green**

Run the command from Step 2.

Expected: PASS, 1 test passed and 0 failed.

- [ ] **Step 5: Commit the serialization fix**

```powershell
git add -- Hoshi/Models/Novel/NovelStatisticsDashboardModels.cs Hoshi.Tests/Services/Novels/NovelStatisticsDashboardCacheTests.cs
git commit -m "fix(statistics): deserialize cached book contributions"
```

---

### Task 2: Recover from incompatible derived caches

**Files:**
- Modify: `Hoshi.Tests/Services/Novels/NovelStatisticsDashboardCacheTests.cs:72-100,130-160`
- Modify: `Hoshi/Services/Novels/NovelStatisticsDashboardCache.cs:45-88`

**Interfaces:**
- Consumes: `INiratanJsonFileStore.ReadAsync<T>(string, CancellationToken)` which may throw `NotSupportedException` for an incompatible model contract.
- Produces: `NovelStatisticsDashboardCache.TryLoadAsync` returns `null` after invalidating only its derived cache when that exception occurs.

- [ ] **Step 1: Add a failing incompatible-cache recovery test**

Add this test after `CorruptCache_DeletesOnlyDerivedCache`:

```csharp
[Fact]
public async Task UnsupportedCacheModel_DeletesOnlyDerivedCache()
{
    var ct = TestContext.Current.CancellationToken;
    using var temp = new TempDirectory();
    var bookRoot = Path.Combine(temp.Path, "book");
    Directory.CreateDirectory(bookRoot);
    var statisticsPath = Path.Combine(bookRoot, "statistics.json");
    await File.WriteAllTextAsync(statisticsPath, "[]", ct);
    var cachePath = Path.Combine(temp.Path, NovelStatisticsDashboardCache.FileName);
    await File.WriteAllTextAsync(cachePath, "{}", ct);
    var cache = new NovelStatisticsDashboardCache(
        new UnsupportedReadStore(),
        new WeakReferenceMessenger(),
        cachePath);

    (await cache.TryLoadAsync("key", ct)).Should().BeNull();

    File.Exists(cachePath).Should().BeFalse();
    File.Exists(statisticsPath).Should().BeTrue();
}
```

Add this nested fake before `TempDirectory`:

```csharp
private sealed class UnsupportedReadStore : INiratanJsonFileStore
{
    public Task<NovelJsonReadResult<T>> ReadAsync<T>(
        string path,
        CancellationToken ct = default) =>
        throw new NotSupportedException("Unsupported cached model.");

    public Task WriteAsync<T>(
        string path,
        T value,
        CancellationToken ct = default) =>
        Task.CompletedTask;
}
```

- [ ] **Step 2: Run the recovery test and verify it is red**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardCacheTests.UnsupportedCacheModel_DeletesOnlyDerivedCache"
```

Expected: FAIL because `NotSupportedException: Unsupported cached model.` escapes `TryLoadAsync`.

- [ ] **Step 3: Contain unsupported-model recovery in the cache**

In `TryLoadAsync`, replace the direct read with:

```csharp
NovelJsonReadResult<NovelStatisticsDashboardCachePayload> result;
try
{
    result = await _store.ReadAsync<NovelStatisticsDashboardCachePayload>(_path, ct);
}
catch (NotSupportedException)
{
    Invalidate();
    return null;
}
```

Replace the invalid-result delete block with:

```csharp
Invalidate();
return null;
```

Add the helper and reuse it from `Receive`:

```csharp
private void Invalidate()
{
    _memoryKey = null;
    _memorySnapshot = null;
    if (File.Exists(_path))
        File.Delete(_path);
}

public void Receive(NovelLibraryChangedMessage message) => Invalidate();
```

Do not add `NotSupportedException` handling to `NiratanJsonFileStore`.

- [ ] **Step 4: Run all dashboard cache tests**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboardCacheTests"
```

Expected: PASS, all `NovelStatisticsDashboardCacheTests` pass with 0 failures.

- [ ] **Step 5: Commit cache recovery**

```powershell
git add -- Hoshi/Services/Novels/NovelStatisticsDashboardCache.cs Hoshi.Tests/Services/Novels/NovelStatisticsDashboardCacheTests.cs
git commit -m "fix(statistics): rebuild unsupported dashboard cache"
```

---

### Task 3: Document and verify the user-visible recovery

**Files:**
- Modify: `docs/CHANGELOG.md:3`
- Modify: `docs/VERIFICATION.md:213-230`

**Interfaces:**
- Consumes: the fixed dashboard cache and the existing imported `かがみの孤城/statistics.json`.
- Produces: documented regression coverage and verified x64 application behavior.

- [ ] **Step 1: Add the changelog entry**

Insert this section after `# Changelog`:

```markdown
## Google Drive 统计已下载但 Dashboard 无数据并崩溃

**原因**：
- Dashboard 派生缓存中的 `NovelStatisticsBookContribution` 同时有主构造函数和便利构造函数，却没有明确 JSON 构造函数；包含真实书籍贡献的缓存重载会抛出 `NotSupportedException`。
- 缓存读取没有把模型不兼容视为可重建的派生缓存失效，异常直接到达 WinUI 未处理异常边界。

**解决**：
- 明确统计贡献模型的 JSON 主构造函数，并用非空 `bookContributions` 覆盖磁盘往返。
- Dashboard 缓存遇到不支持的模型时只删除 `statistics_dashboard_cache_v1.json`，随后从各书 `statistics.json` 重建；原始统计、书签和 EPUB 不受影响。

---
```

- [ ] **Step 2: Extend Dashboard verification**

Replace item 10 in section `1.11 Niratan Dashboard 验证` with:

```markdown
10. 使用包含非空 `bookContributions` 的 `statistics_dashboard_cache_v1.json` 重启并进入 Dashboard；缓存必须正常反序列化。再放入结构有效但模型不兼容的派生缓存，确认只删除该缓存并从各书 `statistics.json` 重建，应用不得退出，原始 sidecar、EPUB 和视频 SQLite 均不得改变。
```

- [ ] **Step 3: Run focused statistics tests**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboard"
```

Expected: all dashboard tests pass with 0 failures.

- [ ] **Step 4: Run the full x64 test suite**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
```

Expected: all tests pass with 0 failures. Existing NU1903 and xUnit analyzer warnings may remain.

- [ ] **Step 5: Build x64**

```powershell
dotnet build -p:Platform=x64
```

Expected: build succeeds with 0 errors. Existing NU1903 warnings may remain.

- [ ] **Step 6: Verify the real imported statistics in Hoshi**

Run:

```powershell
.\build-and-run.ps1
```

Open Statistics from the bookshelf. Confirm the app stays running and the existing `かがみの孤城` sidecar contributes its 10 rows, 153371 characters, and 44387.1125397682 seconds across 2026-03-16 through 2026-07-06. Confirm the latest Hoshi log has no new `NovelStatisticsBookContribution` deserialization exception.

- [ ] **Step 7: Check the patch and commit documentation**

```powershell
git diff --check
git add -- docs/CHANGELOG.md docs/VERIFICATION.md
git commit -m "docs: record dashboard cache recovery"
```

Expected: `git diff --check` prints no errors; only the intended documentation is included in this commit.
