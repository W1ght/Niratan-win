# Dictionary Popup First-Frame and Lookup Responsiveness Design

## Problem

The dictionary popup currently has two competing definitions of `contentReady`.
The generated shell posts readiness when the first `.glossary-content` node appears,
while `popup.js` posts readiness again after every requested entry has rendered.
The native host reveals the WebView2 after the first message.

At the same time, result injection hides the document root with
`visibility: hidden`, while dictionary column layout assigns
`visibility: visible` directly to glossary cards. Those cards can therefore
be painted while the entry header and tags remain hidden. The user sees
dictionary bodies first and the primary expression flashes in when the final
render makes the document root visible.

Rendering all results also delays the final frame. Each dictionary section
yields through `requestAnimationFrame`, so a 16-result lookup can take several
hundred milliseconds even when native dictionary lookup is fast. Reader and
Video lookup paths can additionally execute the native P/Invoke lookup on the
WinUI thread when the index and semaphore are already ready.

## Goals

- Never expose a popup frame that contains glossary bodies without the primary
  expression, tags, and header actions.
- Emit exactly one `contentReady` message for each render generation.
- Reveal the popup after the complete first entry has been committed and laid
  out, without waiting for all remaining entries.
- Continue rendering remaining entries incrementally without moving or
  replacing the first entry.
- Preserve generation checks so stale work cannot reveal old results.
- Keep hoshidicts calls serialized while running lookup and deserialization
  away from the WinUI thread.
- Cache dictionary styles until dictionary configuration or active language is
  rebuilt.
- Preserve result ordering, `MaxResults`, popup stack behavior, structured
  content, audio, Anki, history navigation, and theme behavior.

## Chosen Design

Use a first-entry commit boundary as the single popup readiness contract.

`renderPopup()` builds the first entry in document order. It appends the entry
header and tags, renders every dictionary section belonging to that first
entry, completes column layout, makes the document root visible, and posts one
generation-scoped `contentReady`. The remaining entries are then appended in
small animation-frame batches. No other observer or fallback path may post
`contentReady` for populated results.

The native host remains at `Opacity=0` until it receives that message for the
current generation. Descendant layout code may continue using visibility to
hide unmeasured dictionary cards, but it must not be able to reveal anything
before the first-entry commit. The native opacity gate is the authoritative
outer visibility boundary.

## Alternatives Rejected

### Wait for every result before revealing the popup

This removes the flash but preserves the observed 300-700 ms common render
delay and can exceed one second for structured dictionaries. It makes the
popup feel slower even though the useful first entry is already available.

### Reveal after the first glossary node

This is the current observer behavior. It is fast, but it does not prove that
the header, tags, all first-entry dictionaries, and column layout form a stable
first frame. It also creates a second readiness authority alongside the main
renderer.

### Copy Niratan's four-entry bridge batches unchanged

Niratan reduces individual JavaScript long tasks, but its popup has no
content-ready or opacity barrier and intentionally exposes progressive DOM.
Batching alone does not define a coherent first visible frame.

## Component Changes

### Popup readiness bridge

`PopupHtmlGenerator` keeps shell readiness and diagnostic reporting, but no
longer owns a MutationObserver that posts populated-result `contentReady`.
The shell may report empty diagnostic state during prewarm without revealing a
pending generation.

`popup.js` becomes the only source of populated-result readiness. It tracks the
active generation and a local `readyPosted` flag. A canceled generation exits
without posting readiness or continuing background batches.

`DictionaryLookupPopup` continues validating the generation before revealing
the native root. Duplicate or stale readiness messages remain harmless and are
ignored, but tests require the normal render path to emit only one message.

### First-entry rendering

The renderer separates these operations:

1. Create and append an entry header and tags.
2. Create all dictionary sections for that entry.
3. Measure and lay out its dictionary columns.
4. Commit the first entry, expose the document root, and post readiness.
5. Schedule remaining entries in bounded animation-frame batches.

The first entry remains at the top of the existing container. Background
entries append after it and do not replace the container, reset scroll, or
re-run first-entry audio autoplay.

Post-processing that affects the first visible frame, including compact
glossary/pitch CSS and custom CSS, is installed before readiness. Whole-document
post-processing may run again idempotently after background rendering finishes.

### Lookup execution

`DictionaryLookupService` owns native-session serialization. The semaphore is
acquired asynchronously, then the synchronous P/Invoke call, native string
copy, deserialization, and display-title projection execute on a worker thread.
The lock remains held for the full native-session operation so the session is
never accessed concurrently with rebuild, styles, or media calls.

Callers continue awaiting the same `LookupAsync` interface. Existing Reader
request-version checks and popup render generations discard stale results at
their current boundaries.

### Dictionary style cache

The lookup service stores the converted style list after the first successful
native read. `GetStylesAsync` returns an immutable snapshot/copy from that cache.
`RebuildQueryAsync` and active-language changes clear the cache before rebuilding
the native session. Failed style reads do not publish a partial cache.

## Data Flow

```text
selection
  -> await serialized background native lookup
  -> results + cached styles
  -> inject generation into warm WebView2
  -> build complete first entry
  -> apply first-frame styles and column layout
  -> post exactly one contentReady(generation)
  -> native validates generation and reveals popup
  -> append remaining entries in background animation frames
```

## Error and Cancellation Handling

- If a new generation starts, old JavaScript batches stop at the next boundary
  and never post readiness.
- If first-entry rendering throws, the existing popup error diagnostic is
  posted and the native popup remains hidden rather than exposing partial DOM.
- If background rendering fails after readiness, diagnostics record the error;
  the already committed first entry remains usable.
- Native lookup exceptions continue propagating through the existing caller
  error paths, and the native-session semaphore is always released.
- Style-cache invalidation occurs before rebuild so stale styles cannot survive
  a dictionary or language change.

## Testing

### Automated contract tests

- Assert that populated-result readiness is posted only by `popup.js`.
- Assert that readiness occurs after first-entry dictionary rendering, first
  layout, root visibility, and first-frame style installation.
- Assert that remaining entries are scheduled only after readiness and keep
  generation checks.
- Assert that native `ShowReadyContent` still requires a matching generation.
- Assert that lookup work crosses an explicit background boundary while native
  session access remains serialized.
- Assert that repeated `GetStylesAsync` calls reuse cached native styles and
  rebuild invalidates that cache.

### Runtime verification

- Build with `dotnet build -p:Platform=x64`.
- Run dictionary-focused tests and the full Hoshi test project.
- Launch the unpackaged x64 app with the existing build/run workflow.
- Open the supplied video and reproduce lookup for `せっかく`.
- Confirm the first visible frame contains expression, tags, buttons, and all
  first-entry dictionary cards together.
- Confirm logs contain one accepted `contentReady` per generation and that it
  precedes background render completion.
- Exercise repeated lookup, Shift-hover, nested lookup, scroll, light/dark
  theme, and resize. Verify no stale popup appears and UI input remains
  responsive during a slow native lookup.

## Scope

This change is limited to dictionary popup first-frame rendering, readiness,
lookup-thread responsiveness, style caching, and regression coverage. It does
not modify `native/hoshidicts/`, dictionary result semantics, EPUB rendering,
database access, popup placement, or Anki behavior.
