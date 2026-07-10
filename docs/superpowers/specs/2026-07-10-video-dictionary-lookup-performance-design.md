# Video Dictionary Lookup Performance Design

## Context

Video subtitle lookup is functionally correct, but current traces show that its
interactive latency is dominated by popup startup and WebView2 result transfer,
not by native dictionary lookup.

The latest cold video lookup spent 87 ms in hoshidicts, 509 ms warming the root
and child popup WebView2 controls, and 375 ms preparing and injecting the first
visible result. A separate lookup returned 1.16 MB of native JSON in 63 ms but
spent 1,150 ms before the popup reported `contentReady`. The video player starts
popup prewarming only after lookup results arrive, and the popup serializes the
complete result list into one `ExecuteScriptAsync` call before showing the first
entry.

Shift-hover adds pressure because each newly hit subtitle character may start a
full lookup. The current boolean in-flight guard drops events only while one
lookup is running; it does not provide latest-request-wins semantics for results
that complete after the pointer has moved.

## Goals

- Remove popup WebView2 initialization from the first video lookup hot path.
- Show the first dictionary result without serializing or transferring every
  result first.
- Preserve the configured `DictionaryDisplaySettings.MaxResults` behavior.
- Make video Shift-hover latest-request-wins so stale requests cannot highlight
  text, replace the popup, or append result batches.
- Preserve popup generation safety, nested lookup, Anki mining, audio, theme,
  sizing, and placement behavior.
- Add trace data that separates prewarm, serialization, first-batch transfer,
  deferred batches, and visible-first-frame latency.

## Non-goals

- Do not change hoshidicts or any file under `native/hoshidicts/`.
- Do not reduce the user's configured maximum result count.
- Do not add a second dictionary query implementation or a video-specific
  dictionary service.
- Do not move dictionary logic into JavaScript.
- Do not redesign dictionary cards or popup placement.
- Do not make lookup wait for all deferred entries or media to render.

## Chosen Design

### Lifecycle prewarming

When the video subtitle WebView sends its versioned `ready` message, the video
window creates its existing `DictionaryPopupOverlay`, attaches it to
`PopupOverlayCanvas`, and starts `PrewarmAsync` as fire-and-observe background
work on the UI dispatcher. This point is late enough for a valid `XamlRoot` and
early enough to remove root and child WebView2 creation from the first lookup.

Prewarm failure is logged and does not fail video initialization. The existing
`ShowLookupAsync` path retains its on-demand `EnsureWarmAsync` fallback, so a
failed or incomplete prewarm remains recoverable.

Only one root host and the existing one-child pool are warmed. No additional
WebView2 controls are introduced.

### First-batch and deferred-result transport

`DictionaryLookupPopup.ShowResultsWarmAsync` keeps receiving the complete
native result list. It splits that list into:

- an initial batch containing exactly the first result;
- deferred batches containing up to three results each.

The initial injection contains only the first result and is the only batch in
the latency-critical await path. The existing JavaScript renderer builds that
entry and posts generation-scoped `contentReady`, making the popup visible.

After the initial injection returns, C# schedules deferred batches without
blocking `ShowLookupAsync`. Each deferred batch is serialized independently and
sent through a narrow `window.hoshiAppendResults(entries, finalCount,
generation)` function. The JavaScript side appends the entries to
`window.lookupEntries`, updates `window.entryCount`, and continues rendering in
animation-frame-sized work.

Every append carries the popup render generation. C# and JavaScript both reject
an append when its generation is no longer current. Hiding the popup, starting
a new lookup, or disposing the host cancels the deferred producer. A failed
deferred batch is logged; the already visible first result remains usable.

The full result list stays in C# for Anki, audio prefetch, and mining context.
Autoplay continues to use the first result. Deferred result availability must
not change the configured final result count.

### Latest-request-wins video lookup

The video window assigns a monotonically increasing request version whenever a
valid subtitle lookup request arrives. A new request cancels the previous
managed cancellation source and becomes the only current request.

Native hoshidicts execution remains serialized and is not forcibly interrupted.
After each awaited boundary, the video window checks both cancellation and the
request version before performing visible work:

- setting the selected subtitle highlight;
- dismissing or replacing the current popup;
- showing a no-results status;
- passing results to the overlay;
- marking the lookup popup visible.

This replaces the boolean drop-only guard. Pointer movement can therefore
record the newest request while native work is finishing, and stale results can
never overwrite the newest lookup.

Click lookup and the explicit inspector lookup button use the same versioned
pipeline. Playback pause remains associated with the accepted lookup request;
stale completion must not toggle playback state again.

### Trace propagation and measurements

`DictionaryPopupRequest.TraceId` is passed from the video lookup call into
`DictionaryPopupOverlay.ShowLookupAsync` and then into the popup host.

The trace records these distinct phases:

- video lookup request received and request version;
- prewarm started, completed, or failed;
- initial result serialization time and UTF-8 byte count;
- initial `ExecuteScriptAsync` time;
- `contentReady` time;
- each deferred batch serialization and transfer time;
- stale or cancelled request/batch rejection.

Per-entry diagnostic logging remains available for targeted investigation, but
the optimization does not add more per-entry information-level logging.

## Data Flow

1. Subtitle WebView reports `ready`; the video window starts popup prewarming.
2. Click or Shift-hover sends a validated version-1 lookup request.
3. The video window increments the request version and cancels the previous
   managed request.
4. `DictionaryPopupRequestService` performs the existing native lookup and
   returns up to the configured maximum results with a trace ID.
5. The video window rejects stale completion or highlights the matched text.
6. The overlay uses the already-warmed root popup when available.
7. The popup serializes and injects one initial result, then becomes visible.
8. Deferred batches append only while both request and popup generations remain
   current.
9. Nested popup lookup continues using the existing child-host pipeline.

## Failure Handling

- Prewarm exceptions are observed and logged; lookup falls back to on-demand
  warming.
- Cancellation is treated as expected control flow and does not surface in the
  video status text.
- Empty current results dismiss the current popup only when the request is still
  current.
- Deferred injection failure leaves the first entry visible and cancels later
  batches for that generation.
- Disposing the video window cancels the current lookup and any deferred popup
  producer before WebView2 teardown.
- All incoming WebView messages retain version, type, and payload validation.

## Testing

### Automated tests

- A video asset contract test requires popup prewarming from the subtitle
  WebView `ready` path and forbids first-use-only prewarming after lookup.
- A focused batch planner test verifies an initial batch of one, deferred
  batches of at most three, stable ordering, and preservation of the final
  result count.
- Popup asset tests require a generation-bearing append bridge and rejection of
  stale generations.
- Video lookup coordination tests verify that a newer request invalidates an
  older request and that stale completion cannot perform visible work.
- Existing video subtitle lookup, dictionary popup, nested lookup, audio, and
  Anki mining tests remain green.

### Runtime verification

- Build x64 and run the dictionary and video lookup test filters.
- Run the full x64 test suite.
- Launch Hoshi and open the configured test video/subtitle workflow.
- Verify the popup is already warm before the first lookup.
- Verify click lookup and Shift-hover across several characters.
- Verify all configured results eventually appear in stable order.
- Verify a fast Shift sweep never shows an older word after the pointer stops.
- Compare `LookupTrace` timings against the captured baselines:
  - cold popup warming must no longer occur after native lookup;
  - first-batch payload must contain one result;
  - large-result first-visible latency must not include serialization and
    transfer of deferred results.
- Verify nested lookup, autoplay, manual audio, Anki mining, dismissal, window
  resize, and light/dark popup readability.

## Alternatives Considered

### Prewarm only

This removes roughly 500 ms from the observed cold lookup, but the 1.16 MB
result still spends more than one second crossing the WebView2 boundary. It
does not address Shift-hover stale work.

### Limit video lookup to one or three results

This produces small payloads but changes the meaning of the user's global
`MaxResults` setting and hides valid dictionary entries. The chosen design
keeps all results and changes only when they are transported.

### Replace `ExecuteScriptAsync` with a broad native bridge

A general-purpose bridge would increase the WebView attack surface and conflict
with the narrow, strongly typed IPC requirement. The chosen append function is
specific to generation-scoped dictionary result batches.

## Scope

Implementation is limited to the video lookup coordinator, dictionary popup
batch transport/render bridge, focused models or helpers needed to make batching
testable, performance tracing, and regression tests. It does not modify native
dictionary code, database behavior, EPUB rendering, dictionary presentation,
or user settings.
