# Video Popup Continuous Lookup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Allow click and Shift-hover subtitle lookup while the video dictionary popup remains visible, replacing only the latest successful root result.

**Architecture:** Video uses a host-only hit-test mode so transparent popup space passes input to the subtitle Canvas. Popup replacement is a two-phase committed/pending transaction: JavaScript stages DOM plus all interaction data and posts contentPrepared; native linearizes that generation as commit-in-flight before acknowledging commit; JavaScript atomically promotes pending state and posts contentReady. Warm initialization is single-flight, commit acknowledgement is observed/reconciled asynchronously, and a single latest queued replacement resumes after completion or exact abort, so the lookup path awaits neither message.

**Tech Stack:** WinUI 3, Windows App SDK, C#/.NET 10, WebView2, JavaScript, xUnit v3, FluentAssertions.

## Global Constraints

- Target Windows 10+ x64; build with dotnet build -p:Platform=x64 and do not build ARM64 by default.
- Do not modify native/hoshidicts/.
- JavaScript must not perform dictionary lookup or deinflection.
- Never await contentReady in the subtitle lookup path.
- Never replace committed DOM or JavaScript interaction state before native acknowledges the exact pending generation.
- Native trace/audio/Anki/mining/Sasayaki context must remain committed until matching contentReady; commit-in-flight cannot be cancelled or overwritten.
- Cold popup initialization must be single-flight and reset after failure/process loss.
- Commit command false/exception/timeout must resolve through exact abort or JavaScript committed-generation reconciliation; native commit-in-flight may never remain stuck.
- Pointer passthrough is video-only; novel reader and global popup defaults stay unchanged.
- Latest request wins. Stale lookup, ready, cancellation, and error paths cannot replace or dismiss committed content.
- Popup scrolling, audio, Anki, nested lookup, and explicit point-outside dismissal keep existing behavior.
- Add no dependency or setting.
- Use RED → GREEN and a focused commit for every task.

---

## File Structure

- Create Niratan/Services/Dictionary/DictionaryPopupDisplayTransaction.cs for UI-independent generation state.
- Create Niratan.Tests/Services/Dictionary/DictionaryPopupDisplayTransactionTests.cs for transaction behavior.
- Modify Niratan/Services/Dictionary/PopupHtmlGenerator.cs to inject a complete pending render payload without overwriting committed globals.
- Modify Niratan/Web/DictionaryPopup/popup.js for detached first-frame staging and native-acknowledged commit.
- Modify Niratan/Views/Dictionary/DictionaryLookupPopup.cs for native committed/pending visibility.
- Modify Niratan/Views/Dictionary/DictionaryPopupOverlay.cs for video input mode and pending root layout.
- Modify Niratan/Views/Video/VideoPlayerWindow.xaml, VideoPlayerWindow.xaml.cs, and VideoPlayerWindow.SubtitleOverlay.cs for continuous lookup.
- Modify Niratan/Services/Video/VideoSubtitleLookupRequestCoordinator.cs for pending highlight ownership.
- Modify the matching dictionary/video test files and docs/CHANGELOG.md.

---

### Task 1: Model committed and pending popup generations

**Files:**
- Create: Niratan/Services/Dictionary/DictionaryPopupDisplayTransaction.cs
- Create: Niratan.Tests/Services/Dictionary/DictionaryPopupDisplayTransactionTests.cs

**Interfaces:**
- Consumes: generation long and trace id string?.
- Produces: DictionaryPopupContentCommit, BeginPending, TryAcceptCommit, TryCompleteCommit, CancelPending(generation, traceId), Dismiss, HasCommittedContent, PendingGeneration, CommitInFlightGeneration, and CommittedGeneration.

- [ ] **Step 1: Write the failing tests**

~~~csharp
using FluentAssertions;
using Niratan.Services.Dictionary;

namespace Niratan.Tests.Services.Dictionary;

public class DictionaryPopupDisplayTransactionTests
{
    [Fact]
    public void Replacement_PreservesCommittedContentUntilCurrentGenerationCommits()
    {
        var state = new DictionaryPopupDisplayTransaction();
        state.BeginPending(1, "first").Should().BeFalse();
        state.TryAcceptCommit(1).Should().BeTrue();
        state.TryCompleteCommit(1, out _).Should().BeTrue();

        state.BeginPending(2, "second").Should().BeTrue();
        state.TryAcceptCommit(1).Should().BeFalse();
        state.TryAcceptCommit(2).Should().BeTrue();
        state.TryCompleteCommit(2, out var commit).Should().BeTrue();

        commit.Should().Be(new DictionaryPopupContentCommit(2, "second"));
        state.HasCommittedContent.Should().BeTrue();
    }

    [Fact]
    public void CancelPending_PreservesCommit_AndDismissClearsIt()
    {
        var state = new DictionaryPopupDisplayTransaction();
        state.BeginPending(1, "shown");
        state.TryAcceptCommit(1);
        state.TryCompleteCommit(1, out _);
        state.BeginPending(2, "cancelled");

        state.CancelPending(2, "cancelled").Should().BeTrue();
        state.HasCommittedContent.Should().BeTrue();

        state.BeginPending(3, "newer");
        state.CancelPending(2, "cancelled").Should().BeFalse();
        state.PendingGeneration.Should().Be(3);

        state.Dismiss();
        state.HasCommittedContent.Should().BeFalse();
        state.PendingGeneration.Should().BeNull();
    }
}
~~~

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopupDisplayTransactionTests" --no-restore
~~~

Expected: build fails because the transaction types do not exist.

- [ ] **Step 3: Implement the state**

~~~csharp
using System;

namespace Niratan.Services.Dictionary;

internal readonly record struct DictionaryPopupContentCommit(long Generation, string? TraceId);

internal sealed class DictionaryPopupDisplayTransaction
{
    private long? _pendingGeneration;
    private string? _pendingTraceId;

    public bool HasCommittedContent { get; private set; }
    public long? PendingGeneration => _pendingGeneration;
    public long? CommitInFlightGeneration { get; private set; }
    public long? CommittedGeneration { get; private set; }

    public bool BeginPending(long generation, string? traceId)
    {
        _pendingGeneration = generation;
        _pendingTraceId = traceId;
        return HasCommittedContent;
    }

    public bool TryAcceptCommit(long generation)
    {
        if (_pendingGeneration != generation || CommitInFlightGeneration is not null)
            return false;

        CommitInFlightGeneration = generation;
        _pendingGeneration = null;
        return true;
    }

    public bool TryCompleteCommit(long generation, out DictionaryPopupContentCommit commit)
    {
        commit = default;
        if (CommitInFlightGeneration != generation)
            return false;

        commit = new DictionaryPopupContentCommit(generation, _pendingTraceId);
        CommitInFlightGeneration = null;
        _pendingTraceId = null;
        HasCommittedContent = true;
        CommittedGeneration = generation;
        return true;
    }

    public bool CancelPending(long generation, string? traceId)
    {
        if (_pendingGeneration != generation)
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
        CommitInFlightGeneration = null;
        HasCommittedContent = false;
        CommittedGeneration = null;
    }
}
~~~

- [ ] **Step 4: Run GREEN**

Run the Step 2 command. Expected: 2 passed, 0 failed.

- [ ] **Step 5: Commit**

~~~powershell
git add Niratan/Services/Dictionary/DictionaryPopupDisplayTransaction.cs Niratan.Tests/Services/Dictionary/DictionaryPopupDisplayTransactionTests.cs
git commit -m "test(dictionary): model popup display transactions"
~~~

---

### Task 2: Stage popup DOM and runtime data until native commit

**Files:**
- Modify: Niratan/Services/Dictionary/PopupHtmlGenerator.cs:286-354
- Modify: Niratan/Web/DictionaryPopup/popup.js:1484-1504,1650-1855
- Modify: Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs

**Interfaces:**
- Consumes: a complete pending payload containing generation, entries, styles, display settings, trace, audio, and Anki state.
- Produces: epoch-tagged contentPrepared, window.niratanCommitPopupRender(epoch, generation), window.niratanCancelPopupRender(epoch, generation), and epoch-tagged contentReady after native acknowledgement.

- [ ] **Step 1: Add a failing asset test**

~~~csharp
[Fact]
public void DictionaryPopup_StagesReplacementUntilFirstFrameReady()
{
    var js = File.ReadAllText(
        Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.js"));

    js.Should().Contain("var liveContainer = document.getElementById('entries-container');");
    js.Should().Contain("var stagingContainer = document.createElement('div');");
    js.Should().Contain("postPopupMessage('contentPrepared', { generation: generation })");
    js.Should().Contain("window.niratanCommitPopupRender = function (expectedGeneration)");
    js.Should().Contain("liveContainer.replaceChildren.apply(liveContainer");
    js.Should().Contain("window.niratanCancelPopupRender = function (expectedGeneration)");

    var start = js.IndexOf("window.niratanInjectResults = function", StringComparison.Ordinal);
    var end = js.IndexOf("function snapshot()", start, StringComparison.Ordinal);
    js[start..end].Should().NotContain("window.lookupEntries = entriesJson");
    js[start..end].Should().NotContain("container.innerHTML = ''");

    var generator = File.ReadAllText(
        Path.Combine(ProjectRoot, "Services", "Dictionary", "PopupHtmlGenerator.cs"));
    generator.Should().Contain("window.niratanStagePopupRender({");
    generator.Should().NotContain("window.lookupEntries = {entriesJson};");
}
~~~

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopup_StagesReplacementUntilFirstFrameReady" --no-restore
~~~

Expected: FAIL because the two-phase stage/commit protocol and pending payload are absent.

- [ ] **Step 3: Inject one pending payload instead of committed globals**

Change `GenerateInjectionScript` so it calls `window.niratanStagePopupRender` with one object containing `generation`, `entries`, `entryCount`, `dictionaryStyles`, all display booleans/numbers/CSS, `traceId`, `audioSources`, audio playback/autoplay, and all Anki flags. Remove the preceding assignments to `window.lookupEntries`, `window.dictionaryStyles`, `window.audioSources`, `window.useAnkiConnect`, and the other committed runtime globals.

~~~javascript
window.niratanStagePopupRender({
  generation: renderGeneration,
  entries: entriesJson,
  entryCount: finalResultCount,
  runtime: {
    dictionaryStyles: stylesJson,
    compactGlossaries: compactGlossaries,
    compactPitchAccents: compactPitchAccents,
    harmonicFrequency: harmonicFrequency,
    deduplicatePitchAccents: deduplicatePitchAccents,
    expandFirstDictionary: expandFirstDictionary,
    collapseMode: collapseMode,
    collapsedDictionaries: collapsedDictionaries,
    showExpressionTags: showExpressionTags,
    scanNonJapaneseText: scanNonJapaneseText,
    maxResults: maxResults,
    scanLength: scanLength,
    customCSS: customCSS,
    lookupTraceId: traceId,
    audioSources: audioSources,
    audioPlaybackMode: audioPlaybackMode,
    audioEnableAutoplay: audioEnableAutoplay,
    useAnkiConnect: useAnkiConnect,
    embedMedia: embedMedia,
    allowDupes: allowDupes,
    needsAudio: needsAudio,
    compactGlossariesAnki: compactGlossariesAnki
  }
});
~~~

- [ ] **Step 4: Stage first frame and expose native commit/cancel**

Add `capturePopupRuntime` and `applyPopupRuntime` listing every runtime key emitted above. `niratanStagePopupRender(payload)` temporarily applies the pending runtime only while synchronously building the detached first entry, then restores the committed runtime before returning and posts `contentPrepared`.

At `renderPopup` start, capture local entry data and use it throughout the render closure instead of reading mutable globals:

~~~javascript
var liveContainer = document.getElementById('entries-container');
if (!liveContainer || !window.entryCount) return;
var stagingContainer = document.createElement('div');
var container = stagingContainer;
var lookupEntries = window.lookupEntries;
var entryCount = window.entryCount;
var generation = window.popupRenderGeneration || 0;
~~~

When the detached first entry is complete, do not swap. Store a pending object with a commit closure and restore the committed runtime:

~~~javascript
window.niratanPendingPopupRender = {
  generation: generation,
  payload: pendingPayload,
  commit: function () {
    applyPopupRuntime(pendingPayload.runtime);
    window.popupRenderGeneration = generation;
    window.lookupEntries = lookupEntries;
    window.entryCount = entryCount;
    disconnectDictionaryColumns();
    liveContainer.replaceChildren.apply(
      liveContainer,
      Array.from(stagingContainer.childNodes));
    container = liveContainer;
    applyConfiguredStyles();
    observeAllDictionarySections();
    layoutDictionaryColumns();
    document.documentElement.style.visibility = 'visible';
    postPopupMessage('contentReady', { generation: generation });
    renderAvailableEntries();
  }
};
postPopupMessage('contentPrepared', { generation: generation });
~~~

Expose exact-generation operations:

~~~javascript
window.niratanCommitPopupRender = function (expectedGeneration) {
  var pending = window.niratanPendingPopupRender;
  if (!pending || pending.generation !== expectedGeneration) return false;
  window.niratanPendingPopupRender = null;
  pending.commit();
  return true;
};

window.niratanCancelPopupRender = function (expectedGeneration) {
  var pending = window.niratanPendingPopupRender;
  if (!pending || pending.generation !== expectedGeneration) return false;
  window.niratanPendingPopupRender = null;
  return true;
};
~~~

The old committed globals and DOM remain unchanged between `contentPrepared` and native commit. Deferred rendering uses the closure-local `lookupEntries` and the reassigned live `container`.

- [ ] **Step 5: Run GREEN**

~~~powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopup_StagesReplacementUntilFirstFrameReady|FullyQualifiedName~NovelReaderWebAssetTests" --no-restore
~~~

Expected: all matching tests pass.

- [ ] **Step 6: Commit**

~~~powershell
git add Niratan/Services/Dictionary/PopupHtmlGenerator.cs Niratan/Web/DictionaryPopup/popup.js Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "fix(dictionary): stage popup replacements atomically"
~~~

---

### Task 3: Preserve the native committed popup during replacement

**Files:**
- Modify: Niratan/Views/Dictionary/DictionaryLookupPopup.cs:43-60,343-418,512-522,845-864,1027-1079
- Modify: Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs

**Interfaces:**
- Consumes: DictionaryPopupDisplayTransaction plus niratanCommitPopupRender/niratanCancelPopupRender from Task 2.
- Produces: contentPrepared commit linearization, one latest queued replacement, generation-scoped native interaction context, DictionaryPopupContentCommittedEventArgs, ContentCommitted, HasCommittedContent, CancelPendingContent(generation, traceId), and generationStarted callback.

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
    code.Should().Contain("case \"contentPrepared\":");
    code.Should().Contain("window.niratanCommitPopupRender");
    code.Should().Contain("public void CancelPendingContent(long generation, string? traceId)");
    code.Should().Contain("window.niratanCancelPopupRender");
}
~~~

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryLookupPopup_PreservesCommittedVisualDuringReplacement" --no-restore
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
private long PrepareForPendingContent(
    CancellationToken cancellationToken,
    string? traceId)
{
    var generation = ++_displayGeneration;
    _pendingContentGeneration = generation;
    _pendingContentCancellationToken = cancellationToken;
    _pendingContentStopwatch = null;
    var preserveCommittedContent = _displayTransaction.BeginPending(
        generation,
        traceId);

    if (!preserveCommittedContent)
    {
        VisualRoot.Visibility = Visibility.Visible;
        VisualRoot.Opacity = 0;
        VisualRoot.IsHitTestVisible = false;
    }

    return generation;
}
~~~

- [ ] **Step 5: Acknowledge only a current prepared generation**

Handle `contentPrepared` by parsing its generation, checking `_pendingContentGeneration`, the pending cancellation token, and transaction identity, then enqueueing this non-blocking commit command:

~~~csharp
if (!CanShowReadyContent(preparedGeneration)
    || _displayTransaction.PendingGeneration != preparedGeneration
    || !_displayTransaction.TryAcceptCommit(preparedGeneration)
    || _contentWebView.CoreWebView2 is null)
    return;

_ = _contentWebView.CoreWebView2.ExecuteScriptAsync(
    $"window.niratanCommitPopupRender?.({preparedGeneration});");
~~~

Do not await `contentPrepared` or `contentReady` from `ShowResultsWarmAsync`.

- [ ] **Step 6: Commit only a current ready generation**

In the contentReady dispatcher callback:

~~~csharp
if (_displayTransaction.CommitInFlightGeneration != readyGeneration
    || !_displayTransaction.TryCompleteCommit(readyGeneration, out var commit))
    return;

ShowReadyContent();
ContentReady?.Invoke(this, EventArgs.Empty);
if (_displayTransaction.CommittedGeneration != readyGeneration)
    return;
if (_displayTransaction.PendingGeneration is not null
    || _displayGeneration != readyGeneration)
    return;
ContentCommitted?.Invoke(
    this,
    new DictionaryPopupContentCommittedEventArgs(
        commit.Generation,
        commit.TraceId));
~~~

- [ ] **Step 7: Cancel the exact pending generation without hiding committed content**

~~~csharp
public void CancelPendingContent(long generation, string? traceId)
{
    if (_pendingContentGeneration != generation
        || !_displayTransaction.CancelPending(generation, traceId))
        return;

    _pendingContentGeneration = null;
    _pendingContentCancellationToken = default;
    _pendingContentStopwatch = null;

    if (_contentWebView.CoreWebView2 is not null)
    {
        _ = _contentWebView.CoreWebView2.ExecuteScriptAsync(
            $"window.niratanCancelPopupRender?.({generation});");
    }

    if (!_displayTransaction.HasCommittedContent)
        Hide();
}
~~~

Make explicit Hide call _displayTransaction.Dismiss(). Cancellation paths call CancelPendingContent instead of Hide.

Add a generation-scoped native context record containing local trace id, normalized audio settings, normalized Anki settings, and the next mining context. `ShowResultsWarmAsync` must use request-local normalized values through any `WarmAsync` await and store them as pending without assigning `_currentTraceId`, `_audioSettings`, `_ankiSettings`, `_miningContext`, or Sasayaki controls. Promote all fields and update Sasayaki controls only when `TryCompleteCommit` succeeds.

When `CommitInFlightGeneration` is non-null, `ShowResultsWarmAsync` must store/replace one latest queued show request and return immediately. After matching contentReady completes native context and events, dequeue the latest non-cancelled request with fire-and-forget `ProcessQueuedShowAsync`; do not await ready in the caller. Explicit Hide/Dismiss cancels and clears the queued request.

Third-review hardening protocol:

1. Extract a testable single-flight warm coordinator. Concurrent cold callers share one warm task; success remains cached, failure resets it, and WebView process failure resets readiness for a later retry.
2. Add exact-generation `TryAbortCommit`. Aborting clears only the matching accepted generation and preserves previous committed generation/context.
3. Route `niratanCommitPopupRender(generation)` through a non-blocking async helper with a bounded timeout. `ShowResultsWarmAsync`, `contentPrepared`, and the lookup caller never await ready or commit acknowledgement.
4. Treat matching `contentReady` and a parsed script result of `true` as idempotent completion signals. A `false` result aborts immediately.
5. On script exception/timeout, call `niratanGetCommittedPopupGeneration()`. Complete when it equals the accepted generation; otherwise call narrow `niratanDiscardPopupRender(generation)` and reconcile. The unavailable-renderer abort portion of this third-review step is superseded by the fourth-review forced-shell protocol below.
6. Extract executable coordination tests for concurrent warm success/failure retry and commit true/false/exception/timeout/ready races plus queue recovery. Keep source assertions only for WebView bridge wiring.

Fourth-review renderer epoch and recovery protocol:

1. Allocate a non-reusable document epoch for every popup shell navigation. Native stage/commit/query/discard/cancel/append commands carry epoch, and JavaScript rejects commands whose epoch differs from the current document. Shell/prepared/ready messages carry the same epoch.
2. Replace the reusable shell-ready completion with per-epoch waiters. A stale shell message cannot warm a newer operation.
3. Give every single-flight warm operation a cancellation/version lease. `Reset` immediately cancels its public task, and the operation validates the lease before event subscription, navigation, shell-ready completion, success publication, and return. Event subscriptions remain single-install.
4. When commit reconciliation cannot reach the renderer, preserve accepted ownership and the latest queue. Reset warm state, navigate a fresh shell, and wait for its strictly newer epoch `shellReady`; only then exact-abort the old generation and start the latest request in the fresh epoch.
5. If recovery fails, retain the old native committed context, accepted ownership, staged context, and latest queue. A future request retries recovery. Guard ProcessFailed, Hide, recovery failure, and recovery completion with generation + failed epoch + attempt tickets.
6. Add executable tests proving Reset immediately faults an old warm caller and ignores its late success, stale epoch commands do not match a fresh document, recovery does not release the queue before a newer epoch is ready, failed attempts retain ownership, and the fresh epoch consumes only the latest request.
7. At the unified accepted-completion boundary, reject the exact generation + failed epoch while its recovery ownership exists. This gate applies equally to late `contentReady` and successful commit/reconciliation callbacks from the old document, and remains active across failed/new attempts until fresh-shell completion or explicit Hide/cancel clears that recovery ownership.

- [ ] **Step 8: Run GREEN**

~~~powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopupDisplayTransactionTests|FullyQualifiedName~DictionaryLookupPopup_PreservesCommittedVisualDuringReplacement|FullyQualifiedName~NovelReaderWebAssetTests" --no-restore
~~~

Expected: all matching tests pass.

- [ ] **Step 9: Commit**

~~~powershell
git add Niratan/Views/Dictionary/DictionaryLookupPopup.cs Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "fix(dictionary): preserve committed popup generations"
~~~

---

### Task 4: Add video-only pointer passthrough and pending layout

**Files:**
- Modify: Niratan/Views/Dictionary/DictionaryPopupOverlay.cs:22-102,150-174,197-298,529-654,855-916
- Modify: Niratan/Views/Video/VideoPlayerWindow.xaml:84-97
- Modify: Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs
- Modify: Niratan.Tests/Services/Video/VideoSubtitleLookupAssetTests.cs

**Interfaces:**
- Consumes: DictionaryLookupPopup.ContentCommitted, HasCommittedContent, and exact-generation CancelPendingContent.
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
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopupOverlay_DefaultInputModeRemainsModal|FullyQualifiedName~VideoSubtitleLookup_PopupEmptySpacePassesThrough" --no-restore
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

CancelShow must require the matching PendingRootCommit and call `_rootHost.CancelPendingContent(pending.Generation, traceId)`, then clear only that pending layout and retain committed content. Dismiss only if no committed root exists.

- [ ] **Step 5: Run GREEN**

~~~powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderWebAssetTests|FullyQualifiedName~VideoSubtitleLookupAssetTests" --no-restore
~~~

Expected: all matching tests pass and default overlay contracts remain green.

- [ ] **Step 6: Commit**

~~~powershell
git add Niratan/Views/Dictionary/DictionaryPopupOverlay.cs Niratan/Views/Video/VideoPlayerWindow.xaml Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs Niratan.Tests/Services/Video/VideoSubtitleLookupAssetTests.cs
git commit -m "fix(video): pass popup empty space to subtitles"
~~~

---

### Task 5: Commit only the current video lookup and preserve misses

**Files:**
- Modify: Niratan/Services/Video/VideoSubtitleLookupRequestCoordinator.cs
- Modify: Niratan.Tests/Services/Video/VideoSubtitleLookupRequestCoordinatorTests.cs
- Modify: Niratan/Views/Video/VideoPlayerWindow.xaml.cs:611-735,887-935
- Modify: Niratan/Views/Video/VideoPlayerWindow.SubtitleOverlay.cs:361-429,547-598
- Modify: Niratan.Tests/Services/Video/VideoSubtitleLookupAssetTests.cs

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
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSubtitleLookupRequestCoordinatorTests" --no-restore
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
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSubtitleLookup_ReplacesVisiblePopupWithoutPredismiss" --no-restore
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
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSubtitleLookupRequestCoordinatorTests|FullyQualifiedName~VideoSubtitleLookupAssetTests" --no-restore
~~~

Expected: all matching tests pass.

- [ ] **Step 10: Commit**

~~~powershell
git add Niratan/Services/Video/VideoSubtitleLookupRequestCoordinator.cs Niratan.Tests/Services/Video/VideoSubtitleLookupRequestCoordinatorTests.cs Niratan/Views/Video/VideoPlayerWindow.xaml.cs Niratan/Views/Video/VideoPlayerWindow.SubtitleOverlay.cs Niratan.Tests/Services/Video/VideoSubtitleLookupAssetTests.cs
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
- 根 popup 使用两阶段 committed/pending generation；JavaScript 暂存 DOM 与全部交互数据并发送 prepared，native 校验精确 generation 后才允许原子替换，过期 generation 不能提交。
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
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopup|FullyQualifiedName~NovelReaderWebAssetTests|FullyQualifiedName~VideoSubtitle" --no-restore
~~~

Expected: all matching tests pass.

- [ ] **Step 4: Run full verification**

~~~powershell
dotnet build -p:Platform=x64
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --no-restore
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
