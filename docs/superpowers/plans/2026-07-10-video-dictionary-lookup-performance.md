# Video Dictionary Lookup Performance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove popup cold-start and whole-result WebView2 transfer from the first visible video dictionary result while preserving all configured results and making Shift-hover latest-request-wins.

**Architecture:** Prewarm the existing popup overlay when the subtitle WebView becomes ready. Split lookup results into one initial result plus deferred batches of at most three, transport each generation-scoped batch through the existing narrow popup bridge, and coordinate video requests with a small cancellation/version service so stale results cannot update UI.

**Tech Stack:** C#/.NET 10, WinUI 3, WebView2, CommunityToolkit.Mvvm, xUnit v3, FluentAssertions, JavaScript.

## Global Constraints

- Build only x64 with `dotnet build -p:Platform=x64`; do not build ARM64 by default.
- Do not modify `native/hoshidicts/`.
- Keep dictionary lookup in C#; JavaScript only renders, selects text, extracts coordinates, and sends narrow versioned messages.
- Do not await `contentReady` in the lookup path.
- Keep ViewModel free of SQLite and preserve the existing View → ViewModel → Service layering.
- Preserve `DictionaryDisplaySettings.MaxResults`, nested lookup, audio, Anki mining, popup placement, and generation invalidation.
- Treat all WebView2 messages and EPUB/web content as untrusted input.

---

### Task 1: Define deterministic popup result batches

**Files:**
- Create: `Hoshi/Services/Dictionary/DictionaryPopupBatchPlanner.cs`
- Create: `Hoshi.Tests/Services/Dictionary/DictionaryPopupBatchPlannerTests.cs`

**Interfaces:**
- Produces: `DictionaryPopupBatchPlanner.Create(int resultCount) -> IReadOnlyList<DictionaryPopupBatchRange>`.
- Produces: `DictionaryPopupBatchRange(int Offset, int Count)`.
- Contract: range zero contains one result; later ranges contain at most three; ranges are ordered and cover every result exactly once.

- [ ] **Step 1: Write the failing planner tests**

```csharp
using FluentAssertions;
using Hoshi.Services.Dictionary;

namespace Hoshi.Tests.Services.Dictionary;

public class DictionaryPopupBatchPlannerTests
{
    [Theory]
    [InlineData(0, new int[0])]
    [InlineData(1, new[] { 1 })]
    [InlineData(2, new[] { 1, 1 })]
    [InlineData(4, new[] { 1, 3 })]
    [InlineData(9, new[] { 1, 3, 3, 2 })]
    public void Create_PreservesAllResultsWithOneInitialResult(int resultCount, int[] counts)
    {
        var ranges = DictionaryPopupBatchPlanner.Create(resultCount);

        ranges.Select(range => range.Count).Should().Equal(counts);
        ranges.Sum(range => range.Count).Should().Be(resultCount);
        var expectedOffsets = new List<int>();
        var offset = 0;
        foreach (var count in counts)
        {
            expectedOffsets.Add(offset);
            offset += count;
        }
        ranges.Select(range => range.Offset).Should().Equal(expectedOffsets);
    }
}
```

- [ ] **Step 2: Run the planner test and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter FullyQualifiedName~DictionaryPopupBatchPlannerTests
```

Expected: compilation fails because `DictionaryPopupBatchPlanner` and `DictionaryPopupBatchRange` do not exist.

- [ ] **Step 3: Implement the minimal planner**

```csharp
namespace Hoshi.Services.Dictionary;

internal readonly record struct DictionaryPopupBatchRange(int Offset, int Count);

internal static class DictionaryPopupBatchPlanner
{
    internal const int InitialBatchSize = 1;
    internal const int DeferredBatchSize = 3;

    public static IReadOnlyList<DictionaryPopupBatchRange> Create(int resultCount)
    {
        if (resultCount <= 0)
            return [];

        var ranges = new List<DictionaryPopupBatchRange>
        {
            new(0, Math.Min(InitialBatchSize, resultCount)),
        };

        for (var offset = InitialBatchSize; offset < resultCount; offset += DeferredBatchSize)
        {
            ranges.Add(new DictionaryPopupBatchRange(
                offset,
                Math.Min(DeferredBatchSize, resultCount - offset)));
        }

        return ranges;
    }
}
```

- [ ] **Step 4: Run the planner test and verify GREEN**

Run the Step 2 command. Expected: all `DictionaryPopupBatchPlannerTests` pass.

- [ ] **Step 5: Commit the planner cycle**

```powershell
git add -- Hoshi/Services/Dictionary/DictionaryPopupBatchPlanner.cs Hoshi.Tests/Services/Dictionary/DictionaryPopupBatchPlannerTests.cs
git commit -m "test: define dictionary popup result batches"
```

---

### Task 2: Add a generation-scoped deferred append bridge

**Files:**
- Modify: `Hoshi/Services/Dictionary/PopupHtmlGenerator.cs`
- Modify: `Hoshi/Web/DictionaryPopup/popup.js`
- Modify: `Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs`

**Interfaces:**
- Extends: `PopupHtmlGenerator.GenerateInjectionScript(..., int? totalResultCount = null)` so the initial payload may contain one result while advertising the final result count.
- Produces: `PopupHtmlGenerator.GenerateAppendResultsScript(List<DictionaryLookupResult> results, int totalResultCount, long renderGeneration)`.
- Produces JavaScript bridge: `window.hoshiAppendResults(entries, finalCount, generation) -> boolean`.

- [ ] **Step 1: Add failing generator and asset tests**

Add this `CreateLookupResult(string expression)` helper and the following tests:

```csharp
private static DictionaryLookupResult CreateLookupResult(string expression) =>
    new(
        Matched: expression,
        Deinflected: expression,
        Trace: [],
        Term: new TermResult(
            Expression: expression,
            Reading: expression,
            Rules: "",
            Glossaries: [new GlossaryEntry("TestDict", $"{expression} definition", "", "")],
            Frequencies: [],
            Pitches: []),
        PreprocessorSteps: 0);

[Fact]
public void PopupHtmlGenerator_SeparatesInitialAndDeferredResultScripts()
{
    var generator = new PopupHtmlGenerator();
    var first = CreateLookupResult("first");
    var deferred = CreateLookupResult("deferred");

    var initial = generator.GenerateInjectionScript(
        [first], [], renderGeneration: 7, totalResultCount: 2);
    var append = generator.GenerateAppendResultsScript([deferred], 2, 7);

    initial.Should().Contain("window.entryCount = 2;");
    initial.Should().Contain("first");
    initial.Should().NotContain("deferred");
    append.Should().Contain("window.hoshiAppendResults");
    append.Should().Contain("deferred");
    append.Should().Contain(", 2, 7)");
}

[Fact]
public void PopupScript_AppendsOnlyToTheCurrentGeneration()
{
    var script = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Web", "DictionaryPopup", "popup.js"));

    script.Should().Contain("window.hoshiAppendResults = function");
    script.Should().Contain("expectedGeneration !== generation");
    script.Should().Contain("Array.prototype.push.apply(window.lookupEntries, entries)");
    script.Should().Contain("renderAvailableEntries()");
}
```

- [ ] **Step 2: Run the new generator tests and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~PopupHtmlGenerator_SeparatesInitialAndDeferredResultScripts|FullyQualifiedName~PopupScript_AppendsOnlyToTheCurrentGeneration"
```

Expected: compilation/assertion failures because the total-count parameter, append generator, and JavaScript bridge are absent.

- [ ] **Step 3: Extend the HTML generator**

Keep the existing serialization model and add the final optional parameter:

```csharp
public string GenerateInjectionScript(
    List<DictionaryLookupResult> results,
    List<DictionaryStyle> styles,
    DictionaryDisplaySettings? displaySettings = null,
    ThemeMode themeMode = ThemeMode.System,
    long renderGeneration = 0,
    AudioSettings? audioSettings = null,
    AnkiSettings? ankiSettings = null,
    string? traceId = null,
    int? totalResultCount = null)
```

Inside it, use `var finalResultCount = totalResultCount ?? results.Count;` and pass that value to `window.hoshiInjectResults` and `window.entryCount`.

Add:

```csharp
public string GenerateAppendResultsScript(
    List<DictionaryLookupResult> results,
    int totalResultCount,
    long renderGeneration)
{
    var entriesJson = SerializeLookupEntries(results);
    return $"window.hoshiAppendResults?.({entriesJson}, {totalResultCount}, {renderGeneration});";
}
```

- [ ] **Step 4: Replace the one-shot remaining-entry renderer with a restartable pump**

Within `window.renderPopup`, keep the synchronous first-entry commit and replace `renderRemainingEntries` with this closure state:

```javascript
var nextRenderIndex = 1;
var renderPumpActive = false;

function renderAvailableEntries() {
  if (renderPumpActive || generation !== (window.popupRenderGeneration || 0)) return;
  renderPumpActive = true;

  function next() {
    if (generation !== (window.popupRenderGeneration || 0)) {
      renderPumpActive = false;
      return;
    }
    if (nextRenderIndex >= window.lookupEntries.length) {
      renderPumpActive = false;
      if (nextRenderIndex >= window.entryCount) finishRender();
      return;
    }
    renderEntry(nextRenderIndex, generation, function () {
      nextRenderIndex++;
      requestAnimationFrame(next);
    });
  }

  requestAnimationFrame(next);
}

window.hoshiAppendResults = function (entries, finalCount, expectedGeneration) {
  if (expectedGeneration !== generation
      || expectedGeneration !== (window.popupRenderGeneration || 0)
      || !Array.isArray(entries)) return false;
  Array.prototype.push.apply(window.lookupEntries, entries);
  window.entryCount = finalCount;
  renderAvailableEntries();
  return true;
};
```

After `commitFirstFrame`, call `renderAvailableEntries()`. Do not post a second `contentReady` message.

- [ ] **Step 5: Run focused tests and verify GREEN**

Run the Step 2 command, then:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryLookupServiceTests"
```

Expected: all focused tests pass, including the existing single-`contentReady` assertion.

- [ ] **Step 6: Commit the bridge cycle**

```powershell
git add -- Hoshi/Services/Dictionary/PopupHtmlGenerator.cs Hoshi/Web/DictionaryPopup/popup.js Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs
git commit -m "perf: add deferred dictionary popup result bridge"
```

---

### Task 3: Send only one result in the first popup injection

**Files:**
- Modify: `Hoshi/Views/Dictionary/DictionaryLookupPopup.cs`
- Create: `Hoshi.Tests/Views/Dictionary/DictionaryPopupBatchIntegrationTests.cs`

**Interfaces:**
- Consumes: `DictionaryPopupBatchPlanner.Create` and `PopupHtmlGenerator.GenerateAppendResultsScript`.
- Produces: `AppendDeferredResultsAsync(...)`, scoped to the current popup generation and cancellation token.
- Produces: `CancelDeferredResults()` called by show, hide, and dispose paths.

- [ ] **Step 1: Add a failing integration contract test**

```csharp
using FluentAssertions;

namespace Hoshi.Tests.Views.Dictionary;

public class DictionaryPopupBatchIntegrationTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "Hoshi"));

    [Fact]
    public void PopupHost_UsesOneInitialBatchAndCancelsDeferredWorkOnLifecycleChanges()
    {
        var path = Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs");
        var code = File.ReadAllText(path);

        code.Should().Contain("DictionaryPopupBatchPlanner.Create(results.Count)");
        code.Should().Contain("GenerateInjectionScript(initialResults");
        code.Should().Contain("totalResultCount: results.Count");
        code.Should().Contain("AppendDeferredResultsAsync");
        code.Should().Contain("GenerateAppendResultsScript");
        code.Split("CancelDeferredResults();", StringSplitOptions.None).Should().HaveCountGreaterThan(3);
        code.Should().Contain("generation != _displayGeneration");
    }
}
```

- [ ] **Step 2: Run the integration test and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter FullyQualifiedName~DictionaryPopupBatchIntegrationTests
```

Expected: assertions fail because the popup still injects all results at once.

- [ ] **Step 3: Add deferred-render lifecycle state**

Add a `CancellationTokenSource? _deferredResultsCts` field and:

```csharp
private void CancelDeferredResults()
{
    _deferredResultsCts?.Cancel();
    _deferredResultsCts?.Dispose();
    _deferredResultsCts = null;
}
```

Call it before a new `PrepareForPendingContent`, in `Hide`, and in `Dispose`.

- [ ] **Step 4: Inject only the initial range**

In `ShowResultsWarmAsync`:

```csharp
var ranges = DictionaryPopupBatchPlanner.Create(results.Count);
var initialRange = ranges[0];
var initialResults = results.GetRange(initialRange.Offset, initialRange.Count);
var generation = PrepareForPendingContent();
_pendingContentStopwatch = Stopwatch.StartNew();
var serializeSw = Stopwatch.StartNew();
var injectionScript = _htmlGenerator.GenerateInjectionScript(
    initialResults,
    styles,
    displaySettings,
    themeMode,
    generation,
    _audioSettings,
    _ankiSettings,
    traceId,
    totalResultCount: results.Count);
var payloadBytes = Encoding.UTF8.GetByteCount(injectionScript);
Log.Information(
    "[LookupTrace] trace={TraceId} popup initial serialized in {Ms}ms bytes={Bytes} entries={EntryCount} total={TotalCount}",
    traceId ?? "-", serializeSw.ElapsedMilliseconds, payloadBytes, initialResults.Count, results.Count);
await _contentWebView.CoreWebView2.ExecuteScriptAsync(injectionScript);
```

Preserve `PrefetchAudioUrls(results)` for the first result.

- [ ] **Step 5: Append deferred batches without blocking lookup completion**

Create a new CTS, retain its token, and fire an exception-observing method after the initial injection:

```csharp
_deferredResultsCts = new CancellationTokenSource();
_ = AppendDeferredResultsAsync(
    results,
    ranges.Skip(1).ToArray(),
    results.Count,
    generation,
    traceId,
    _deferredResultsCts.Token);
```

`AppendDeferredResultsAsync` must `await Task.Yield()`, check cancellation and `generation != _displayGeneration` before every batch, create `GetRange(range.Offset, range.Count)`, log serialization byte count separately, await `ExecuteScriptAsync`, and catch `OperationCanceledException` as expected control flow. Any other exception logs once and stops later batches.

- [ ] **Step 6: Run focused popup tests and verify GREEN**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopupBatchIntegrationTests|FullyQualifiedName~DictionaryLookupServiceTests|FullyQualifiedName~DictionaryPopup"
```

Expected: all selected tests pass.

- [ ] **Step 7: Commit the popup integration cycle**

```powershell
git add -- Hoshi/Views/Dictionary/DictionaryLookupPopup.cs Hoshi.Tests/Views/Dictionary/DictionaryPopupBatchIntegrationTests.cs
git commit -m "perf: stream deferred dictionary popup results"
```

---

### Task 4: Add latest-request-wins subtitle lookup coordination

**Files:**
- Create: `Hoshi/Services/Video/VideoSubtitleLookupRequestCoordinator.cs`
- Create: `Hoshi.Tests/Services/Video/VideoSubtitleLookupRequestCoordinatorTests.cs`
- Modify: `Hoshi/Views/Video/VideoPlayerWindow.xaml.cs`
- Modify: `Hoshi/Views/Video/VideoPlayerWindow.SubtitleOverlay.cs`
- Modify: `Hoshi/Views/Video/VideoPlayerWindow.Playback.cs`
- Modify: `Hoshi.Tests/Services/Video/VideoSubtitleLookupAssetTests.cs`

**Interfaces:**
- Produces: `VideoSubtitleLookupRequestCoordinator.BeginRequest()` returning `VideoSubtitleLookupRequest(long Version, CancellationToken CancellationToken)`.
- Produces: `IsCurrent(VideoSubtitleLookupRequest request)` and `CancelCurrent()`.
- Replaces: `_isSubtitlePointerLookupRunning` with one versioned pipeline used by click, Shift-hover, inspector button, and keyboard shortcut.

- [ ] **Step 1: Write failing coordinator tests**

```csharp
[Fact]
public void BeginRequest_CancelsThePreviousRequestAndAdvancesVersion()
{
    using var coordinator = new VideoSubtitleLookupRequestCoordinator();
    var first = coordinator.BeginRequest();
    var second = coordinator.BeginRequest();

    first.CancellationToken.IsCancellationRequested.Should().BeTrue();
    second.Version.Should().Be(first.Version + 1);
    coordinator.IsCurrent(first).Should().BeFalse();
    coordinator.IsCurrent(second).Should().BeTrue();
}

[Fact]
public void CancelCurrent_InvalidatesTheCurrentRequest()
{
    using var coordinator = new VideoSubtitleLookupRequestCoordinator();
    var request = coordinator.BeginRequest();

    coordinator.CancelCurrent();

    request.CancellationToken.IsCancellationRequested.Should().BeTrue();
    coordinator.IsCurrent(request).Should().BeFalse();
}
```

- [ ] **Step 2: Run coordinator tests and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter FullyQualifiedName~VideoSubtitleLookupRequestCoordinatorTests
```

Expected: compilation fails because the coordinator types do not exist.

- [ ] **Step 3: Implement the coordinator service**

Use a private lock, monotonic `long _version`, and one `CancellationTokenSource`:

```csharp
internal readonly record struct VideoSubtitleLookupRequest(
    long Version,
    CancellationToken CancellationToken);

internal sealed class VideoSubtitleLookupRequestCoordinator : IDisposable
{
    private readonly object _gate = new();
    private long _version;
    private CancellationTokenSource? _currentCts;

    public VideoSubtitleLookupRequest BeginRequest()
    {
        lock (_gate)
        {
            _currentCts?.Cancel();
            _currentCts?.Dispose();
            _currentCts = new CancellationTokenSource();
            return new VideoSubtitleLookupRequest(++_version, _currentCts.Token);
        }
    }

    public bool IsCurrent(VideoSubtitleLookupRequest request)
    {
        lock (_gate)
            return request.Version == _version && !request.CancellationToken.IsCancellationRequested;
    }

    public void CancelCurrent()
    {
        lock (_gate)
        {
            _version++;
            _currentCts?.Cancel();
            _currentCts?.Dispose();
            _currentCts = null;
        }
    }

    public void Dispose() => CancelCurrent();
}
```

- [ ] **Step 4: Run coordinator tests and verify GREEN**

Run the Step 2 command. Expected: all coordinator tests pass.

- [ ] **Step 5: Add a failing video integration contract**

Extend `VideoSubtitleLookupAssetTests` to require:

```csharp
code.Should().Contain("_subtitleLookupCoordinator.BeginRequest()");
code.Should().Contain("_subtitleLookupCoordinator.IsCurrent(request)");
code.Should().Contain("request.CancellationToken");
code.Should().Contain("request.TraceId");
code.Should().NotContain("_isSubtitlePointerLookupRunning");
```

Run the test and confirm it fails because the window still uses the boolean guard.

- [ ] **Step 6: Route every video lookup through the coordinator**

Add one coordinator field to the window. Introduce a wrapper that calls
`BeginRequest()` and passes the request into the existing lookup method. Pass
`request.CancellationToken` to `ViewModel.CreateLookupRequestAsync`.

```csharp
private readonly VideoSubtitleLookupRequestCoordinator _subtitleLookupCoordinator = new();

private Task StartSubtitleLookupAsync(
    string? queryOverride = null,
    int sentenceOffset = 0,
    Windows.Foundation.Point? anchorPoint = null,
    double? anchorWidth = null,
    double? anchorHeight = null)
{
    var request = _subtitleLookupCoordinator.BeginRequest();
    return LookupCurrentSubtitleAsync(
        request,
        queryOverride,
        sentenceOffset,
        anchorPoint,
        anchorWidth,
        anchorHeight);
}

private bool IsCurrentSubtitleLookup(VideoSubtitleLookupRequest request) =>
    _subtitleLookupCoordinator.IsCurrent(request);
```

Change the lookup method signature to start with
`VideoSubtitleLookupRequest request`, and call the ViewModel with:

```csharp
var popupRequest = await ViewModel.CreateLookupRequestAsync(
    query,
    sentenceOffset,
    RequestVideoMiningMediaAsync,
    request.CancellationToken);
```

After pausing, after request creation, before/after highlighting, and before/
after `ShowLookupAsync`, use one helper:

```csharp
private bool IsCurrentSubtitleLookup(VideoSubtitleLookupRequest request) =>
    _subtitleLookupCoordinator.IsCurrent(request);
```

Return silently for stale/cancelled work. Catch `OperationCanceledException`
when the request token is cancelled without changing `StatusText`. Pass
`traceId: popupRequest.TraceId` into `ShowLookupAsync`.

Remove `_isSubtitlePointerLookupRunning`. Change the inspector button and
keyboard shortcut to call the versioned wrapper. On window close, call
`_subtitleLookupCoordinator.Dispose()` before disposing the popup/WebView2.

- [ ] **Step 7: Run focused video tests and verify GREEN**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSubtitleLookupRequestCoordinatorTests|FullyQualifiedName~VideoSubtitleLookupAssetTests|FullyQualifiedName~VideoSubtitleLookupTextExtractorTests"
```

Expected: all selected tests pass.

- [ ] **Step 8: Commit latest-request-wins coordination**

```powershell
git add -- Hoshi/Services/Video/VideoSubtitleLookupRequestCoordinator.cs Hoshi.Tests/Services/Video/VideoSubtitleLookupRequestCoordinatorTests.cs Hoshi/Views/Video/VideoPlayerWindow.xaml.cs Hoshi/Views/Video/VideoPlayerWindow.SubtitleOverlay.cs Hoshi/Views/Video/VideoPlayerWindow.Playback.cs Hoshi.Tests/Services/Video/VideoSubtitleLookupAssetTests.cs
git commit -m "perf: make video subtitle lookup latest wins"
```

---

### Task 5: Prewarm the popup from subtitle WebView readiness

**Files:**
- Modify: `Hoshi/Views/Video/VideoPlayerWindow.SubtitleOverlay.cs`
- Modify: `Hoshi/Views/Video/VideoPlayerWindow.xaml.cs`
- Modify: `Hoshi.Tests/Services/Video/VideoSubtitleLookupAssetTests.cs`

**Interfaces:**
- Produces: `PrewarmVideoDictionaryPopupAsync()` that observes and logs all exceptions.
- Trigger: version-1 subtitle WebView `ready` message after state synchronization.
- Fallback: existing `ShowLookupAsync -> EnsureWarmAsync` remains unchanged.

- [ ] **Step 1: Add a failing prewarm contract test**

```csharp
[Fact]
public void VideoSubtitleLookup_PrewarmsPopupWhenSubtitleWebViewIsReady()
{
    var code = ReadVideoPlayerWindowCode();
    var readyIndex = code.IndexOf("case \"ready\":", StringComparison.Ordinal);
    var prewarmIndex = code.IndexOf("PrewarmVideoDictionaryPopupAsync", readyIndex, StringComparison.Ordinal);

    readyIndex.Should().BeGreaterThanOrEqualTo(0);
    prewarmIndex.Should().BeGreaterThan(readyIndex);
    code.Should().Contain("await overlay.PrewarmAsync(RootGrid.XamlRoot");
    code.Should().Contain("[VideoLookup] Popup prewarm");
}
```

- [ ] **Step 2: Run the prewarm test and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter FullyQualifiedName~VideoSubtitleLookup_PrewarmsPopupWhenSubtitleWebViewIsReady
```

Expected: assertion failure because video prewarm currently starts only after results return.

- [ ] **Step 3: Implement exception-observing early prewarm**

After `await UpdateSubtitleWebViewAsync()` in the `ready` case, fire:

```csharp
_ = PrewarmVideoDictionaryPopupAsync();
```

Add:

```csharp
private async Task PrewarmVideoDictionaryPopupAsync()
{
    var sw = Stopwatch.StartNew();
    try
    {
        var overlay = EnsurePopupOverlay();
        await overlay.PrewarmAsync(
            RootGrid.XamlRoot,
            App.GetService<ISettingsService>().Current.Theme);
        Log.Information("[VideoLookup] Popup prewarm completed in {Ms}ms", sw.ElapsedMilliseconds);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "[VideoLookup] Popup prewarm failed in {Ms}ms", sw.ElapsedMilliseconds);
    }
}
```

Remove the redundant fire-and-forget `PrewarmAsync` call after lookup results;
`ShowLookupAsync` still performs its own warm fallback.

- [ ] **Step 4: Run video lookup tests and verify GREEN**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSubtitleLookupAssetTests|FullyQualifiedName~VideoSubtitleLookupRequestCoordinatorTests"
```

Expected: all selected tests pass.

- [ ] **Step 5: Commit early prewarming**

```powershell
git add -- Hoshi/Views/Video/VideoPlayerWindow.SubtitleOverlay.cs Hoshi/Views/Video/VideoPlayerWindow.xaml.cs Hoshi.Tests/Services/Video/VideoSubtitleLookupAssetTests.cs
git commit -m "perf: prewarm video dictionary popup"
```

---

### Task 6: Record the fix and perform full verification

**Files:**
- Modify: `docs/CHANGELOG.md`

**Interfaces:**
- Documents: root causes, batching/prewarm/latest-wins solution, and trace fields.

- [ ] **Step 1: Add the root-cause and solution entry**

Add a concise `docs/CHANGELOG.md` entry stating:

```markdown
## 视频查词首次打开和大词条结果卡顿

- 视频窗口在 native lookup 完成后才预热根/子 WebView2，首次查询承担完整冷启动成本。
- 全部 `maxResults` 结果曾被序列化进单个 `ExecuteScriptAsync`；大型 structured content 可产生 1 MB 以上 payload，使 WebView2 传输远慢于 native lookup。
- 字幕 Shift hover 只有 in-flight 布尔锁，没有 latest-request-wins，旧结果可能继续占用热路径。
- 字幕 WebView ready 后后台预热 popup；首条结果独立注入，剩余结果以 generation-scoped 小批次追加；视频请求使用版本和取消令牌拒绝旧结果。
```

- [ ] **Step 2: Run formatting and focused regression verification**

Run:

```powershell
git diff --check
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Dictionary|FullyQualifiedName~VideoSubtitleLookup"
```

Expected: `git diff --check` prints nothing and all selected tests pass.

- [ ] **Step 3: Run the required full build and test suite**

Run:

```powershell
dotnet build -p:Platform=x64
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
```

Expected: both commands exit 0 with zero test failures. Record any pre-existing package vulnerability warning separately; do not describe warning-bearing output as pristine.

- [ ] **Step 4: Launch and verify the real video lookup flow**

Run `./build-and-run.ps1`, open the existing video/subtitle test workflow, and verify:

1. `[VideoLookup] Popup prewarm completed` appears before the first lookup trace.
2. The first popup appears without a post-lookup `popup warm` phase.
3. Initial serialization logs `entries=1` and a small payload.
4. Deferred batches eventually show the configured result count in stable order.
5. A rapid Shift sweep ends on the final hovered word without an older popup replacing it.
6. Nested lookup, autoplay/manual audio, Anki mining, dismissal, resize, and light/dark themes remain functional.

Leave the final verified Hoshi instance running.

- [ ] **Step 5: Commit documentation and any verification-only adjustments**

```powershell
git add -- docs/CHANGELOG.md
git commit -m "docs: record video lookup performance fix"
```

---

## Final Review Checklist

- [ ] Every production behavior was preceded by a focused failing test.
- [ ] The initial popup payload contains exactly one lookup result.
- [ ] Deferred batches contain at most three results and preserve order/count.
- [ ] Stale popup generations reject deferred appends.
- [ ] New video lookup requests invalidate old visible work.
- [ ] Popup prewarm begins from subtitle WebView readiness and retains on-demand fallback.
- [ ] Video passes `DictionaryPopupRequest.TraceId` into the overlay.
- [ ] `native/hoshidicts/` is unchanged.
- [ ] x64 build succeeds and full tests report zero failures.
- [ ] Runtime traces demonstrate that cold warm-up and deferred results are outside first-visible latency.
