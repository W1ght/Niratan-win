# Dictionary Popup First-Frame Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reveal a complete dictionary first entry without flashing partial content, continue remaining rendering in the background, and keep native dictionary lookup work off the WinUI thread.

**Architecture:** `popup.js` becomes the single owner of generation-scoped readiness and commits one complete first entry before revealing the native opacity-gated popup. A small dictionary-native executor serializes P/Invoke work on the thread pool, while `DictionaryLookupService` caches converted styles until rebuild invalidation.

**Tech Stack:** WinUI 3, C#/.NET, WebView2 JavaScript, xUnit v3, FluentAssertions, Serilog.

## Global Constraints

- Do not modify `native/hoshidicts/`.
- Keep dictionary lookup async and do not block the WinUI thread.
- Keep popup WebView2 hidden until the current generation's content is ready.
- Do not await `contentReady` in the lookup call path.
- Preserve result order, `MaxResults`, structured content, nested lookup, audio, Anki, and popup stack behavior.
- Build and test x64 only.

---

### Task 1: Make first-entry commit the only readiness authority

**Files:**
- Modify: `Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs`
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`
- Modify: `Hoshi/Services/Dictionary/PopupHtmlGenerator.cs`
- Modify: `Hoshi/Web/DictionaryPopup/popup.js`

**Interfaces:**
- Consumes: existing `window.popupRenderGeneration`, `postPopupMessage`, entry creation helpers, and native generation validation.
- Produces: `commitFirstFrame(generation, entryDiv)` and `renderRemainingEntries(startIndex, generation, onFinished)` JavaScript contracts.

- [ ] **Step 1: Add failing readiness contract tests**

Add assertions that generated shell HTML no longer contains `hoshiPopupObserveContentReady`, that `popup.js` contains exactly one populated-result `postPopupMessage('contentReady', { generation: generation })`, and that the first-frame function performs these operations in order:

```csharp
var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "DictionaryPopup", "popup.js"));
script.Should().Contain("function commitFirstFrame(generation, entryDiv)");
script.Should().Contain("function renderRemainingEntries(startIndex, generation, onFinished)");
script.Split("postPopupMessage('contentReady', { generation: generation })").Length.Should().Be(2);

var commitStart = script.IndexOf("function commitFirstFrame(generation, entryDiv)", StringComparison.Ordinal);
var commitEnd = script.IndexOf("function renderRemainingEntries", commitStart, StringComparison.Ordinal);
var commit = script[commitStart..commitEnd];
commit.IndexOf("applyConfiguredStyles()", StringComparison.Ordinal).Should().BeLessThan(
    commit.IndexOf("layoutDictionaryColumns()", StringComparison.Ordinal));
commit.IndexOf("layoutDictionaryColumns()", StringComparison.Ordinal).Should().BeLessThan(
    commit.IndexOf("document.documentElement.style.visibility = 'visible'", StringComparison.Ordinal));
commit.IndexOf("document.documentElement.style.visibility = 'visible'", StringComparison.Ordinal).Should().BeLessThan(
    commit.IndexOf("postPopupMessage('contentReady', { generation: generation })", StringComparison.Ordinal));
```

Require the main render path to call `renderEntry(0, generation, ...)`, then `commitFirstFrame`, then `renderRemainingEntries(1, ...)`, with a generation guard in each asynchronous boundary.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryLookupServiceTests|FullyQualifiedName~NovelReaderWebAssetTests"
```

Expected: FAIL because the MutationObserver readiness bridge still exists and the first-frame functions do not exist.

- [ ] **Step 3: Remove observer-owned readiness from the generated shell**

Keep shell diagnostics and `shellReady`, but reduce the generated post-load script to:

```javascript
try {
  postDiagnostic('shellReady', { entryCount: window.entryCount || 0 });
  if (typeof window.renderPopup === 'function') {
    window.renderPopup();
  } else {
    // existing popup error handling
  }
} catch (e) {
  // existing popup error handling and diagnostic
}
```

Remove calls to `window.hoshiPopupObserveContentReady` from result injection and replacement paths.

- [ ] **Step 4: Refactor `renderPopup` around a first-entry boundary**

Extract idempotent `applyConfiguredStyles()` and `wrapRubyTextNodes(root)`. Implement entry rendering so the first entry appends its header, tags, and all dictionary sections before its completion callback. Then implement:

```javascript
function commitFirstFrame(generation, entryDiv) {
  if (generation !== (window.popupRenderGeneration || 0)) return false;
  wrapRubyTextNodes(entryDiv);
  applyConfiguredStyles();
  layoutDictionaryColumns();
  document.documentElement.style.visibility = 'visible';
  postPopupTrace('first-frame-ready', { generation: generation });
  postPopupMessage('contentReady', { generation: generation });
  return true;
}

function renderRemainingEntries(startIndex, generation, onFinished) {
  var idx = startIndex;
  function next() {
    if (generation !== (window.popupRenderGeneration || 0)) return;
    if (idx >= window.entryCount) { onFinished(); return; }
    renderEntry(idx, generation, function () {
      idx++;
      requestAnimationFrame(next);
    });
  }
  requestAnimationFrame(next);
}
```

The final callback performs whole-document ruby post-processing, final column layout, and `render-finished` diagnostics, but does not post readiness again.

- [ ] **Step 5: Run focused tests and verify GREEN**

Run the Step 2 command.

Expected: PASS.

- [ ] **Step 6: Commit Task 1**

```powershell
git add Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs Hoshi/Services/Dictionary/PopupHtmlGenerator.cs Hoshi/Web/DictionaryPopup/popup.js
git commit -m "fix: commit dictionary popup first frame atomically"
```

---

### Task 2: Serialize native lookup on a worker and cache styles

**Files:**
- Create: `Hoshi/Services/Dictionary/DictionaryNativeExecutor.cs`
- Create: `Hoshi.Tests/Services/Dictionary/DictionaryNativeExecutorTests.cs`
- Modify: `Hoshi/Services/Dictionary/DictionaryLookupService.cs`
- Modify: `Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs`

**Interfaces:**
- Produces: `DictionaryNativeExecutor.RunAsync<T>(SemaphoreSlim gate, Func<T> operation)`.
- Consumes: the existing `_rebuildLock` as the single native-session gate.

- [ ] **Step 1: Add failing executor tests**

Create tests proving worker-thread execution and serialization:

```csharp
[Fact]
public async Task RunAsync_ExecutesOperationAwayFromCallingThread()
{
    using var gate = new SemaphoreSlim(1, 1);
    var callerThread = Environment.CurrentManagedThreadId;
    var workerThread = await DictionaryNativeExecutor.RunAsync(
        gate,
        () => Environment.CurrentManagedThreadId);
    workerThread.Should().NotBe(callerThread);
}

[Fact]
public async Task RunAsync_SerializesOperationsSharingGate()
{
    using var gate = new SemaphoreSlim(1, 1);
    var concurrent = 0;
    var maximum = 0;
    async Task RunOne() => await DictionaryNativeExecutor.RunAsync(gate, () =>
    {
        var current = Interlocked.Increment(ref concurrent);
        maximum = Math.Max(maximum, current);
        Thread.Sleep(20);
        Interlocked.Decrement(ref concurrent);
        return true;
    });

    await Task.WhenAll(RunOne(), RunOne(), RunOne());
    maximum.Should().Be(1);
}
```

- [ ] **Step 2: Add a failing style-cache integration test**

Use a recording `ILogger<DictionaryLookupService>` with the existing temporary native dictionary. Call `GetStylesAsync()` twice and assert that the log message `styles native+deserialize completed` occurs once. Call `RebuildQueryAsync()`, then `GetStylesAsync()` and assert that the count becomes two.

- [ ] **Step 3: Run the dictionary service tests and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryNativeExecutorTests|FullyQualifiedName~DictionaryLookupServiceTests"
```

Expected: FAIL because `DictionaryNativeExecutor` and style caching do not exist.

- [ ] **Step 4: Implement the native executor**

```csharp
internal static class DictionaryNativeExecutor
{
    public static async Task<T> RunAsync<T>(SemaphoreSlim gate, Func<T> operation)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(operation).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
```

- [ ] **Step 5: Route lookup and style conversion through the executor**

Move `hoshi_lookup`, native string ownership, deserialization, and display-title projection into the executor operation. Preserve existing trace logging inside that operation.

Store styles as a published array:

```csharp
private DictionaryStyle[]? _cachedStyles;
```

Return a list copy on cache hits. On a miss, fetch and convert styles inside `DictionaryNativeExecutor.RunAsync`, publish only the completed array, and return a copy. Clear `_cachedStyles` before every native rebuild.

- [ ] **Step 6: Run focused tests and verify GREEN**

Run the Step 3 command.

Expected: PASS.

- [ ] **Step 7: Commit Task 2**

```powershell
git add Hoshi/Services/Dictionary/DictionaryNativeExecutor.cs Hoshi/Services/Dictionary/DictionaryLookupService.cs Hoshi.Tests/Services/Dictionary/DictionaryNativeExecutorTests.cs Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs
git commit -m "perf: move dictionary lookup off ui thread"
```

---

### Task 3: Record the fix and verify the complete application

**Files:**
- Modify: `docs/CHANGELOG.md`

**Interfaces:**
- Consumes: Task 1 readiness logs and Task 2 lookup tracing.
- Produces: documented root cause and verification evidence.

- [ ] **Step 1: Update the changelog**

Add one concise entry recording the duplicate readiness authority, descendant
visibility escape, first-entry commit fix, and worker-thread/style-cache
performance change. Do not add a development diary.

- [ ] **Step 2: Run formatting and diff checks**

```powershell
git diff --check
git status --short
```

Expected: no whitespace errors; only intended files are modified.

- [ ] **Step 3: Run dictionary-focused tests**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Dictionary"
```

Expected: PASS.

- [ ] **Step 4: Run full build and test suite**

```powershell
dotnet build -p:Platform=x64
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
```

Expected: PASS with zero build errors and zero failed tests.

- [ ] **Step 5: Launch and verify the rendered flow**

Run `./build-and-run.ps1`, confirm the Hoshi top-level window is responsive,
open the supplied test video, and look up `せっかく`. Confirm the first visible
popup frame includes the expression, tags, actions, and complete first-entry
dictionary cards. Repeat with Shift-hover and nested lookup.

Inspect `LookupTrace` logs and require one accepted `contentReady` per
generation, a `first-frame-ready` stage before `render-finished`, and no early
diagnostic readiness carrying only one glossary node.

- [ ] **Step 6: Commit verification documentation**

```powershell
git add docs/CHANGELOG.md
git commit -m "docs: record dictionary popup first-frame fix"
```
