# Video Popup Continuous Lookup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Allow click and Shift-hover subtitle lookup while the video dictionary popup remains visible, replacing only the latest successful root result.

**Architecture:** Video uses a host-only hit-test mode so transparent popup space passes input to the subtitle Canvas. Popup replacement is a committed/pending generation transaction: JavaScript stages a first frame off-DOM, native keeps committed content visible, and generation-scoped ready events commit layout and subtitle highlight without awaiting contentReady.

**Tech Stack:** WinUI 3, Windows App SDK, C#/.NET 10, WebView2, JavaScript, xUnit v3, FluentAssertions.

## Global Constraints

- Target Windows 10+ x64; build with dotnet build -p:Platform=x64 and do not build ARM64 by default.
- Do not modify native/hoshidicts/.
- JavaScript must not perform dictionary lookup or deinflection.
- Never await contentReady in the subtitle lookup path.
- Pointer passthrough is video-only; novel reader and global popup defaults stay unchanged.
- Latest request wins. Stale lookup, ready, cancellation, and error paths cannot replace or dismiss committed content.
- Popup scrolling, audio, Anki, nested lookup, and explicit point-outside dismissal keep existing behavior.
- Add no dependency or setting.
- Use RED → GREEN and a focused commit for every task.

---

## File Structure

- Create Hoshi/Services/Dictionary/DictionaryPopupDisplayTransaction.cs for UI-independent generation state.
- Create Hoshi.Tests/Services/Dictionary/DictionaryPopupDisplayTransactionTests.cs for transaction behavior.
- Modify Hoshi/Web/DictionaryPopup/popup.js for detached first-frame staging.
- Modify Hoshi/Views/Dictionary/DictionaryLookupPopup.cs for native committed/pending visibility.
- Modify Hoshi/Views/Dictionary/DictionaryPopupOverlay.cs for video input mode and pending root layout.
- Modify Hoshi/Views/Video/VideoPlayerWindow.xaml, VideoPlayerWindow.xaml.cs, and VideoPlayerWindow.SubtitleOverlay.cs for continuous lookup.
- Modify Hoshi/Services/Video/VideoSubtitleLookupRequestCoordinator.cs for pending highlight ownership.
- Modify the matching dictionary/video test files and docs/CHANGELOG.md.

---

### Task 1: Model committed and pending popup generations

**Files:**
- Create: Hoshi/Services/Dictionary/DictionaryPopupDisplayTransaction.cs
- Create: Hoshi.Tests/Services/Dictionary/DictionaryPopupDisplayTransactionTests.cs

**Interfaces:**
- Consumes: generation long and trace id string?.
- Produces: DictionaryPopupContentCommit, BeginPending, TryCommit, CancelPending, Dismiss, HasCommittedContent, PendingGeneration.

- [ ] **Step 1: Write the failing tests**

~~~csharp
using FluentAssertions;
using Hoshi.Services.Dictionary;

namespace Hoshi.Tests.Services.Dictionary;

public class DictionaryPopupDisplayTransactionTests
{
    [Fact]
    public void Replacement_PreservesCommittedContentUntilCurrentGenerationCommits()
    {
        var state = new DictionaryPopupDisplayTransaction();
        state.BeginPending(1, "first").Should().BeFalse();
        state.TryCommit(1, out _).Should().BeTrue();

        state.BeginPending(2, "second").Should().BeTrue();
        state.TryCommit(1, out _).Should().BeFalse();
        state.TryCommit(2, out var commit).Should().BeTrue();

        commit.Should().Be(new DictionaryPopupContentCommit(2, "second"));
        state.HasCommittedContent.Should().BeTrue();
    }

    [Fact]
    public void CancelPending_PreservesCommit_AndDismissClearsIt()
    {
        var state = new DictionaryPopupDisplayTransaction();
        state.BeginPending(1, "shown");
        state.TryCommit(1, out _);
        state.BeginPending(2, "cancelled");

        state.CancelPending("cancelled").Should().BeTrue();
        state.HasCommittedContent.Should().BeTrue();

        state.Dismiss();
        state.HasCommittedContent.Should().BeFalse();
        state.PendingGeneration.Should().BeNull();
    }
}
~~~

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopupDisplayTransactionTests" --no-restore
~~~

Expected: build fails because the transaction types do not exist.

- [ ] **Step 3: Implement the state**

~~~csharp
using System;

namespace Hoshi.Services.Dictionary;

internal readonly record struct DictionaryPopupContentCommit(long Generation, string? TraceId);

internal sealed class DictionaryPopupDisplayTransaction
{
    private long? _pendingGeneration;
    private string? _pendingTraceId;

    public bool HasCommittedContent { get; private set; }
    public long? PendingGeneration => _pendingGeneration;

    public bool BeginPending(long generation, string? traceId)
    {
        _pendingGeneration = generation;
        _pendingTraceId = traceId;
        return HasCommittedContent;
    }

    public bool TryCommit(long generation, out DictionaryPopupContentCommit commit)
    {
        commit = default;
        if (_pendingGeneration != generation)
            return false;

        commit = new DictionaryPopupContentCommit(generation, _pendingTraceId);
        _pendingGeneration = null;
        _pendingTraceId = null;
        HasCommittedContent = true;
        return true;
    }

    public bool CancelPending(string? traceId)
    {
        if (_pendingGeneration is null)
            return false;
        if (traceId is not null
            && !string.Equals(traceId, _pendingTraceId, StringComparison.Ordinal))
            return false;

        _pendingGeneration = null;
        _pendingTraceId = null;
        return true;
    }

    public void Dismiss()
    {
        _pendingGeneration = null;
        _pendingTraceId = null;
        HasCommittedContent = false;
    }
}
~~~

- [ ] **Step 4: Run GREEN**

Run the Step 2 command. Expected: 2 passed, 0 failed.

- [ ] **Step 5: Commit**

~~~powershell
git add Hoshi/Services/Dictionary/DictionaryPopupDisplayTransaction.cs Hoshi.Tests/Services/Dictionary/DictionaryPopupDisplayTransactionTests.cs
git commit -m "test(dictionary): model popup display transactions"
~~~

---

### Task 2: Stage popup DOM before replacing committed content

**Files:**
- Modify: Hoshi/Web/DictionaryPopup/popup.js:1484-1504,1650-1855
- Modify: Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs

**Interfaces:**
- Consumes: window.popupRenderGeneration and injected entries.
- Produces: detached first-frame commit and window.hoshiCancelPopupRender(expectedGeneration).

- [ ] **Step 1: Add a failing asset test**

~~~csharp
[Fact]
public void DictionaryPopup_StagesReplacementUntilFirstFrameReady()
{
    var js = File.ReadAllText(
        Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.js"));

    js.Should().Contain("var liveContainer = document.getElementById('entries-container');");
    js.Should().Contain("var stagingContainer = document.createElement('div');");
    js.Should().Contain("liveContainer.replaceChildren.apply(liveContainer");
    js.Should().Contain("window.hoshiCancelPopupRender = function (expectedGeneration)");

    var start = js.IndexOf("window.hoshiInjectResults = function", StringComparison.Ordinal);
    var end = js.IndexOf("function snapshot()", start, StringComparison.Ordinal);
    js[start..end].Should().NotContain("container.innerHTML = ''");
}
~~~

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopup_StagesReplacementUntilFirstFrameReady" --no-restore
~~~

Expected: FAIL because staging and cancellation are absent.

- [ ] **Step 3: Stop clearing the live container during injection**

Replace hoshiInjectResults and add cancellation:

~~~javascript
window.hoshiInjectResults = function (entriesJson, count) {
  postPopupTrace('inject-results-start', { count: count });
  closeOverlay();
  flushPendingHistoryRestore();
  backStack.length = 0;
  forwardStack.length = 0;
  window.lookupEntries = entriesJson;
  window.entryCount = count;
  selectedDictionaries = {};
  audioUrls = {};
  window.renderPopup();
  requestAnimationFrame(function () {
    getPopupScrollElement().scrollTop = 0;
  });
};

window.hoshiCancelPopupRender = function (expectedGeneration) {
  if (expectedGeneration !== (window.popupRenderGeneration || 0)) return false;
  window.popupRenderGeneration = expectedGeneration + 1;
  return true;
};
~~~

- [ ] **Step 4: Render first frame off-DOM and swap once**

At renderPopup start:

~~~javascript
var liveContainer = document.getElementById('entries-container');
if (!liveContainer || !window.entryCount) return;
var stagingContainer = document.createElement('div');
var container = stagingContainer;
~~~

Use this commit function:

~~~javascript
function commitFirstFrame(generation, entryDiv) {
  if (firstFrameCommitted
    || !entryDiv
    || generation !== (window.popupRenderGeneration || 0)) return false;

  wrapRubyTextNodes(entryDiv);
  applyConfiguredStyles();
  disconnectDictionaryColumns();
  liveContainer.replaceChildren.apply(
    liveContainer,
    Array.from(stagingContainer.childNodes));
  container = liveContainer;
  observeAllDictionarySections();
  layoutDictionaryColumns();
  document.documentElement.style.visibility = 'visible';
  firstFrameCommitted = true;
  postPopupTrace('first-frame-ready', {
    generation: generation,
    renderedEntries: 1,
    renderedGlossaries: entryDiv.querySelectorAll('.glossary-content').length,
    textLength: entryDiv.innerText.length,
    elapsedMs: performance.now() - renderStart
  });
  postPopupMessage('contentReady', { generation: generation });
  return true;
}
~~~

Keep the deferred pump unchanged so it appends through the reassigned container.

- [ ] **Step 5: Run GREEN**

~~~powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopup_StagesReplacementUntilFirstFrameReady|FullyQualifiedName~NovelReaderWebAssetTests" --no-restore
~~~

Expected: all matching tests pass.

- [ ] **Step 6: Commit**

~~~powershell
git add Hoshi/Web/DictionaryPopup/popup.js Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "fix(dictionary): stage popup replacements atomically"
~~~

---

### Task 3: Preserve the native committed popup during replacement

**Files:**
- Modify: Hoshi/Views/Dictionary/DictionaryLookupPopup.cs:43-60,343-418,512-522,845-864,1027-1079
- Modify: Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs

**Interfaces:**
- Consumes: DictionaryPopupDisplayTransaction and hoshiCancelPopupRender.
- Produces: DictionaryPopupContentCommittedEventArgs, ContentCommitted, HasCommittedContent, CancelPendingContent, and generationStarted callback.

- [ ] **Step 1: Add a failing lifecycle test**

~~~csharp
[Fact]
public void DictionaryLookupPopup_PreservesCommittedVisualDuringReplacement()
{
    var code = File.ReadAllText(
        Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs"));

    code.Should().Contain("public bool HasCommittedContent => _displayTransaction.HasCommittedContent;");
    code.Should().Contain("EventHandler<DictionaryPopupContentCommittedEventArgs>? ContentCommitted");
    code.Should().Contain("var preserveCommittedContent = _displayTransaction.BeginPending(");
    code.Should().Contain("if (!preserveCommittedContent)");
    code.Should().Contain("public void CancelPendingContent(string? traceId)");
    code.Should().Contain("window.hoshiCancelPopupRender");
}
~~~

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryLookupPopup_PreservesCommittedVisualDuringReplacement" --no-restore
~~~

Expected: FAIL because the native transaction API is absent.

- [ ] **Step 3: Add event and transaction interfaces**

~~~csharp
public sealed class DictionaryPopupContentCommittedEventArgs(
    long generation,
    string? traceId) : EventArgs
{
    public long Generation { get; } = generation;
    public string? TraceId { get; } = traceId;
}

public event EventHandler<DictionaryPopupContentCommittedEventArgs>? ContentCommitted;
private readonly DictionaryPopupDisplayTransaction _displayTransaction = new();
public bool HasCommittedContent => _displayTransaction.HasCommittedContent;
~~~

Add Action<long>? generationStarted = null as the final ShowResultsWarmAsync parameter. Immediately after PrepareForPendingContent returns, call generationStarted?.Invoke(generation).

- [ ] **Step 4: Preserve visibility only when committed content exists**

Replace PrepareForPendingContent with:

~~~csharp
private long PrepareForPendingContent(CancellationToken cancellationToken)
{
    var generation = ++_displayGeneration;
    _pendingContentGeneration = generation;
    _pendingContentCancellationToken = cancellationToken;
    _pendingContentStopwatch = null;
    var preserveCommittedContent = _displayTransaction.BeginPending(
        generation,
        _currentTraceId);

    if (!preserveCommittedContent)
    {
        VisualRoot.Visibility = Visibility.Visible;
        VisualRoot.Opacity = 0;
        VisualRoot.IsHitTestVisible = false;
    }

    return generation;
}
~~~

- [ ] **Step 5: Commit only a current ready generation**

In the contentReady dispatcher callback:

~~~csharp
if (!CanShowReadyContent(readyGeneration)
    || !_displayTransaction.TryCommit(readyGeneration, out var commit))
    return;

ShowReadyContent();
ContentReady?.Invoke(this, EventArgs.Empty);
ContentCommitted?.Invoke(
    this,
    new DictionaryPopupContentCommittedEventArgs(
        commit.Generation,
        commit.TraceId));
~~~

- [ ] **Step 6: Cancel pending content without hiding committed content**

~~~csharp
public void CancelPendingContent(string? traceId)
{
    var generation = _pendingContentGeneration;
    if (!_displayTransaction.CancelPending(traceId))
        return;

    _pendingContentGeneration = null;
    _pendingContentCancellationToken = default;
    _pendingContentStopwatch = null;

    if (generation is long value && _contentWebView.CoreWebView2 is not null)
    {
        _ = _contentWebView.CoreWebView2.ExecuteScriptAsync(
            $"window.hoshiCancelPopupRender?.({value});");
    }

    if (!_displayTransaction.HasCommittedContent)
        Hide();
}
~~~

Make explicit Hide call _displayTransaction.Dismiss(). Cancellation paths call CancelPendingContent instead of Hide.

- [ ] **Step 7: Run GREEN**

~~~powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopupDisplayTransactionTests|FullyQualifiedName~DictionaryLookupPopup_PreservesCommittedVisualDuringReplacement|FullyQualifiedName~NovelReaderWebAssetTests" --no-restore
~~~

Expected: all matching tests pass.

- [ ] **Step 8: Commit**

~~~powershell
git add Hoshi/Views/Dictionary/DictionaryLookupPopup.cs Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "fix(dictionary): preserve committed popup generations"
~~~

---

### Task 4: Add video-only pointer passthrough and pending layout

**Files:**
- Modify: Hoshi/Views/Dictionary/DictionaryPopupOverlay.cs:22-102,150-174,197-298,529-654,855-916
- Modify: Hoshi/Views/Video/VideoPlayerWindow.xaml:84-97
- Modify: Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs
- Modify: Hoshi.Tests/Services/Video/VideoSubtitleLookupAssetTests.cs

**Interfaces:**
- Consumes: DictionaryLookupPopup.ContentCommitted, HasCommittedContent, CancelPendingContent.
- Produces: DictionaryPopupCanvasInputMode and RootContentCommitted.

- [ ] **Step 1: Add failing default and video-mode tests**

~~~csharp
[Fact]
public void DictionaryPopupOverlay_DefaultInputModeRemainsModal()
{
    var code = File.ReadAllText(
        Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryPopupOverlay.cs"));

    code.Should().Contain("DictionaryPopupCanvasInputMode.ModalSurface");
    code.Should().Contain("DictionaryPopupCanvasInputMode.VisibleHostsOnly");
    code.Should().Contain("new SolidColorBrush(Colors.Transparent)");
}

[Fact]
public void VideoSubtitleLookup_PopupEmptySpacePassesThrough()
{
    var xaml = File.ReadAllText(
        Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
    var code = ReadVideoPlayerWindowCode();

    code.Should().Contain("DictionaryPopupCanvasInputMode.VisibleHostsOnly");
    code.Should().Contain("RootContentCommitted");
    xaml.Should().NotContain(
        "x:Name=\"VideoDictionaryPanelChrome\"\r\n" +
        "                        Canvas.ZIndex=\"100\"\r\n" +
        "                        HorizontalAlignment=\"Stretch\"\r\n" +
        "                        VerticalAlignment=\"Stretch\"\r\n" +
        "                        Margin=\"16,16,16,120\"\r\n" +
        "                        Background=\"Transparent\"");
}
~~~

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopupOverlay_DefaultInputModeRemainsModal|FullyQualifiedName~VideoSubtitleLookup_PopupEmptySpacePassesThrough" --no-restore
~~~

Expected: both tests fail.

- [ ] **Step 3: Add explicit input mode**

~~~csharp
public enum DictionaryPopupCanvasInputMode
{
    ModalSurface,
    VisibleHostsOnly,
}

public void UseCanvas(
    Canvas overlayCanvas,
    DictionaryPopupCanvasInputMode inputMode =
        DictionaryPopupCanvasInputMode.ModalSurface)
{
    if (!ReferenceEquals(_canvas, overlayCanvas))
    {
        _canvas.PointerPressed -= OnOverlayPointerPressed;
        _canvas = overlayCanvas;
        _canvas.PointerPressed += OnOverlayPointerPressed;
    }

    _canvas.Background = inputMode == DictionaryPopupCanvasInputMode.ModalSurface
        ? new SolidColorBrush(Colors.Transparent)
        : null;
    _canvas.IsHitTestVisible = false;
    _canvas.Visibility = Visibility.Visible;
}
~~~

Remove Background="Transparent" from VideoDictionaryPanelChrome. In EnsurePopupOverlay:

~~~csharp
_popupOverlay.UseCanvas(
    PopupOverlayCanvas,
    DictionaryPopupCanvasInputMode.VisibleHostsOnly);
~~~

- [ ] **Step 4: Stage root layout by generation**

Add:

~~~csharp
private readonly record struct PendingRootCommit(
    long Generation,
    string? TraceId,
    DictionaryPopupLayoutResult Layout);

private PendingRootCommit? _pendingRootCommit;
public event EventHandler<DictionaryPopupContentCommittedEventArgs>?
    RootContentCommitted;
~~~

Extract layout application:

~~~csharp
private static void ApplyHostLayout(
    DictionaryLookupPopup host,
    DictionaryPopupLayoutResult layout)
{
    host.SetSize(layout.Width, layout.Height);
    Canvas.SetLeft(host.VisualRoot, layout.Left);
    Canvas.SetTop(host.VisualRoot, layout.Top);
}
~~~

Subscribe root ContentCommitted during prewarm. Resolve the target layout before injection and pass:

~~~csharp
generationStarted: generation =>
{
    _pendingRootCommit = new PendingRootCommit(
        generation,
        traceId,
        targetLayout);
    if (!_rootHost.HasCommittedContent)
        ApplyHostLayout(_rootHost, targetLayout);
}
~~~

Commit it:

~~~csharp
private void OnRootContentCommitted(
    object? sender,
    DictionaryPopupContentCommittedEventArgs e)
{
    if (_pendingRootCommit is not PendingRootCommit pending
        || pending.Generation != e.Generation
        || !string.Equals(
            pending.TraceId,
            e.TraceId,
            StringComparison.Ordinal))
        return;

    ApplyHostLayout(_rootHost, pending.Layout);
    _pendingRootCommit = null;
    _rootVisible = true;
    RootContentCommitted?.Invoke(this, e);
}
~~~

CancelShow must call _rootHost.CancelPendingContent(traceId) and retain committed content. Dismiss only if no committed root exists.

- [ ] **Step 5: Run GREEN**

~~~powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderWebAssetTests|FullyQualifiedName~VideoSubtitleLookupAssetTests" --no-restore
~~~

Expected: all matching tests pass and default overlay contracts remain green.

- [ ] **Step 6: Commit**

~~~powershell
git add Hoshi/Views/Dictionary/DictionaryPopupOverlay.cs Hoshi/Views/Video/VideoPlayerWindow.xaml Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs Hoshi.Tests/Services/Video/VideoSubtitleLookupAssetTests.cs
git commit -m "fix(video): pass popup empty space to subtitles"
~~~

---

### Task 5: Commit only the current video lookup and preserve misses

**Files:**
- Modify: Hoshi/Services/Video/VideoSubtitleLookupRequestCoordinator.cs
- Modify: Hoshi.Tests/Services/Video/VideoSubtitleLookupRequestCoordinatorTests.cs
- Modify: Hoshi/Views/Video/VideoPlayerWindow.xaml.cs:611-735,887-935
- Modify: Hoshi/Views/Video/VideoPlayerWindow.SubtitleOverlay.cs:361-429,547-598
- Modify: Hoshi.Tests/Services/Video/VideoSubtitleLookupAssetTests.cs

**Interfaces:**
- Consumes: RootContentCommitted and current VideoSubtitleLookupRequest.
- Produces: VideoSubtitlePopupCommit, StagePopupCommit, TryTakePopupCommit.

- [ ] **Step 1: Add failing coordinator tests**

~~~csharp
[Fact]
public void PopupCommit_IsTakenOnlyForCurrentRequestAndTrace()
{
    using var coordinator = new VideoSubtitleLookupRequestCoordinator();
    var request = coordinator.BeginRequest();
    coordinator.StagePopupCommit(request, "trace-1", 3, "師匠");

    coordinator.TryTakePopupCommit("other", out _).Should().BeFalse();
    coordinator.TryTakePopupCommit("trace-1", out var commit).Should().BeTrue();
    commit.Should().Be(new VideoSubtitlePopupCommit(3, "師匠"));
}

[Fact]
public void NewRequest_InvalidatesPreviousPendingPopupCommit()
{
    using var coordinator = new VideoSubtitleLookupRequestCoordinator();
    var first = coordinator.BeginRequest();
    coordinator.StagePopupCommit(first, "old", 0, "古い");

    coordinator.BeginRequest();

    coordinator.TryTakePopupCommit("old", out _).Should().BeFalse();
}
~~~

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSubtitleLookupRequestCoordinatorTests" --no-restore
~~~

Expected: build fails because popup commit APIs are absent.

- [ ] **Step 3: Implement pending commit ownership**

Add the following records and field, clear the field in BeginRequest and CancelCurrent, then add the methods:

~~~csharp
internal readonly record struct VideoSubtitlePopupCommit(
    int SelectionStart,
    string MatchedText);

private readonly record struct PendingPopupCommit(
    long RequestVersion,
    string TraceId,
    VideoSubtitlePopupCommit Commit);

private PendingPopupCommit? _pendingPopupCommit;

public void StagePopupCommit(
    VideoSubtitleLookupRequest request,
    string traceId,
    int selectionStart,
    string matchedText)
{
    lock (_gate)
    {
        if (request.Version != _version
            || request.CancellationToken.IsCancellationRequested)
            return;

        _pendingPopupCommit = new PendingPopupCommit(
            request.Version,
            traceId,
            new VideoSubtitlePopupCommit(selectionStart, matchedText));
    }
}

public bool TryTakePopupCommit(
    string? traceId,
    out VideoSubtitlePopupCommit commit)
{
    lock (_gate)
    {
        commit = default;
        if (_pendingPopupCommit is not PendingPopupCommit pending
            || pending.RequestVersion != _version
            || !string.Equals(
                pending.TraceId,
                traceId,
                StringComparison.Ordinal))
            return false;

        commit = pending.Commit;
        _pendingPopupCommit = null;
        return true;
    }
}
~~~

- [ ] **Step 4: Add a failing video behavior test**

~~~csharp
[Fact]
public void VideoSubtitleLookup_ReplacesVisiblePopupWithoutPredismiss()
{
    var code = ReadVideoPlayerWindowCode();

    code.Should().Contain(
        "RootContentCommitted += PopupOverlay_RootContentCommitted");
    code.Should().Contain("StagePopupCommit(");
    code.Should().Contain("TryTakePopupCommit(");
    code.Should().Contain("if (!_isLookupPopupVisible)");
    code.Should().Contain("ViewModel.StatusText = \"No dictionary results\"");
}
~~~

- [ ] **Step 5: Run RED**

~~~powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSubtitleLookup_ReplacesVisiblePopupWithoutPredismiss" --no-restore
~~~

Expected: FAIL because committed-event ownership is absent.

- [ ] **Step 6: Stage highlight before showing new results**

Before ShowLookupAsync:

~~~csharp
_subtitleLookupCoordinator.StagePopupCommit(
    lookupRequest,
    popupRequest.TraceId,
    sentenceOffset,
    popupRequest.Results[0].Matched);
~~~

Remove immediate HighlightSubtitleCanvasSelectionAsync, _isLookupPopupVisible = true, and "Lookup opened" assignments from the request path.

- [ ] **Step 7: Apply highlight only from current committed event**

Subscribe in EnsurePopupOverlay and unsubscribe in OnClosed:

~~~csharp
_popupOverlay.RootContentCommitted += PopupOverlay_RootContentCommitted;
~~~

Add:

~~~csharp
private async void PopupOverlay_RootContentCommitted(
    object? sender,
    DictionaryPopupContentCommittedEventArgs e)
{
    if (!_subtitleLookupCoordinator.TryTakePopupCommit(
            e.TraceId,
            out var commit))
        return;

    await HighlightSubtitleCanvasSelectionAsync(
        commit.SelectionStart,
        commit.MatchedText);
    _isLookupPopupVisible = true;
    VideoDictionaryPanelChrome.Visibility = Visibility.Visible;
    ViewModel.StatusText = "Lookup opened";
    ApplySubtitleAppearance();
}
~~~

- [ ] **Step 8: Preserve committed popup on miss/error and cancel on explicit dismiss**

For popupRequest == null:

~~~csharp
if (!_isLookupPopupVisible)
{
    ClearSubtitleCanvasSelection();
    VideoDictionaryPanelChrome.Visibility = Visibility.Collapsed;
}
ViewModel.StatusText = "No dictionary results";
return;
~~~

Stale/cancellation paths call CancelShow(traceId), which Task 4 makes non-destructive when committed content exists. The general exception handler dismisses only when !_isLookupPopupVisible. PopupOverlay_Dismissed calls _subtitleLookupCoordinator.CancelCurrent() before clearing highlight.

- [ ] **Step 9: Run GREEN**

~~~powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSubtitleLookupRequestCoordinatorTests|FullyQualifiedName~VideoSubtitleLookupAssetTests" --no-restore
~~~

Expected: all matching tests pass.

- [ ] **Step 10: Commit**

~~~powershell
git add Hoshi/Services/Video/VideoSubtitleLookupRequestCoordinator.cs Hoshi.Tests/Services/Video/VideoSubtitleLookupRequestCoordinatorTests.cs Hoshi/Views/Video/VideoPlayerWindow.xaml.cs Hoshi/Views/Video/VideoPlayerWindow.SubtitleOverlay.cs Hoshi.Tests/Services/Video/VideoSubtitleLookupAssetTests.cs
git commit -m "fix(video): replace visible popup with current lookup"
~~~

---

### Task 6: Document and verify the complete flow

**Files:**
- Modify: docs/CHANGELOG.md
- Verify: all files changed by Tasks 1-5

**Interfaces:**
- Consumes: complete passthrough and generation transaction.
- Produces: documentation, automated evidence, and a launched x64 build.

- [ ] **Step 1: Add the changelog entry**

~~~markdown
## 视频 popup 显示后无法继续查字幕

**原因**：
- 视频 popup 的透明外层和 Canvas 覆盖字幕并参与命中，popup 显示后空白区域也会截获单击和 Shift hover。
- popup 替换曾在新首屏 ready 前隐藏并清空根内容；无结果和取消还会关闭最后一次成功结果。

**解决**：
- 视频宿主启用仅可见 popup host 命中的穿透模式，实际 popup 保持交互，透明空白把输入交给字幕 Canvas。
- 根 popup 使用 committed/pending generation；新首屏在 detached DOM 中完成后原子替换，过期 generation 不能提交。
- latest-request-wins 在 ready 后提交新锚点和高亮；无结果、取消或失败保留最后一次成功 popup。

---
~~~

- [ ] **Step 2: Run source checks**

~~~powershell
git diff --check
~~~

Expected: diff check exits 0.

- [ ] **Step 3: Run focused tests**

~~~powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopup|FullyQualifiedName~NovelReaderWebAssetTests|FullyQualifiedName~VideoSubtitle" --no-restore
~~~

Expected: all matching tests pass.

- [ ] **Step 4: Run full verification**

~~~powershell
dotnet build -p:Platform=x64
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --no-restore
~~~

Expected: build has 0 errors and all 555+ tests pass. Report the existing SQLitePCLRaw.lib.e_sqlite3 NU1903 warning separately.

- [ ] **Step 5: Launch and manually verify**

~~~powershell
.\build-and-run.ps1
~~~

Verify:

1. Open a subtitle popup, then click a second uncovered word without dismissing.
2. Hold Shift and move across at least three uncovered words.
3. Move rapidly; stale results never flash after the latest result.
4. Query a no-result word; committed popup and highlight remain.
5. Scroll and use audio/Anki inside the popup.
6. Shift-hover inside popup; nested lookup still opens a child.
7. Click non-subtitle video space; popup closes and pending lookup cannot reopen it.

- [ ] **Step 6: Commit documentation**

~~~powershell
git add docs/CHANGELOG.md
git commit -m "docs: record continuous video popup lookup"
git status --short
~~~

Expected: commit succeeds and status is clean.
