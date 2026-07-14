# Popup Appearance Settings Design

## Problem

Niratan currently exposes only dictionary-popup maximum width and height, and
places those controls on the Dictionary settings page. The settings do not
match Niratan: Niratan defaults to `560 x 420`, limits width to `900`, limits
height to `820`, and gives child popups separate `600 x 520` caps. Niratan has no
popup scale, action-bar, or full-width setting.

The popup host already passes one `DictionaryDisplaySettings` snapshot to the
root and child WebView2 hosts. That is the correct ownership boundary, but the
layout layer discards part of the shared configuration by applying different
root and child limits.

## Goals

- Move popup presentation controls from Dictionary settings to Appearance.
- Match Niratan's width, height, scale, action-bar, full-width ranges and
  defaults.
- Use the same popup appearance snapshot for reader, video, global lookup, and
  every nested lookup popup.
- Keep existing saved values when they are valid under the new ranges.
- Preserve the popup render-generation gate, stack behavior, theme behavior,
  structured content, audio, Anki, and lookup performance.

## Settings Contract

`DictionaryDisplaySettings` remains the single persisted configuration model.
The two existing size fields stay in place so existing JSON settings require no
property migration. Three fields are added for the missing Niratan controls.

| Setting | Range | Step | Default |
| --- | ---: | ---: | ---: |
| `PopupMaxWidth` | `100...1400` | `10` | `320` |
| `PopupMaxHeight` | `100...800` | `10` | `250` |
| `PopupScale` | `0.8...1.5` | `0.05` | `1.0` |
| `PopupActionBar` | Boolean | n/a | `false` |
| `PopupFullWidth` | Boolean | n/a | `false` |

Missing JSON fields receive these defaults. Existing saved width and height
remain unchanged when they fall inside the new ranges. Values outside the
ranges are clamped when loaded and before layout; an existing saved height of
`820` therefore resolves to `800`.

The fields remain part of the profile-cloned `DictionaryDisplaySettings`
snapshot. Changing profile continues to change the complete dictionary and
popup presentation configuration together.

## Appearance UI

Remove the current Popup Size header and width/height cards from
`DictionarySettingsPage`. Remove their properties and commands from
`DictionarySettingsPageViewModel`.

Add a Popup section to the shared `ReaderAppearanceSettingsContent`. The same
control is used by the dedicated Appearance page and the in-reader Appearance
surface, so both entry points expose identical values.

The section contains native WinUI settings cards:

- Width slider with its current numeric value.
- Height slider with its current numeric value.
- Scale slider formatted to two decimal places.
- Show Action Bar toggle.
- Full-width toggle.

`SettingsPageViewModel` loads these fields from
`DictionaryDisplaySettings`, clamps them through one shared popup-appearance
constraints helper, and saves changes through `ISettingsService`. The view
contains bindings only; no persistence or popup business logic moves into
code-behind.

Chinese and English resources name the section and every card, value, and
accessibility label. The existing width and height resource keys may be renamed
from “Max Width/Height” to “Width/Height” because Niratan presents these as
popup dimensions even though the viewport can constrain the final extent.

## Popup Layout

The root and child hosts resolve their dimensions from the same normalized
settings. Remove the hard `900 x 820` root caps and `600 x 520` child caps.
Available screen space still wins over configured dimensions, so a configured
`1400 x 800` popup remains contained inside a smaller window.

The layout calculator accepts a full-width flag. When disabled, horizontal and
vertical placement retains the current anchored behavior with the new
configuration ranges. When enabled:

- Width is the available overlay width minus the existing six-DIP border on
  each side.
- Height is the configured height clamped to available overlay height.
- The popup is centered horizontally and anchored to the bottom border.
- The configured width is intentionally ignored, matching Niratan.

Every nested popup consumes the same settings, including full-width. A
full-width child therefore appears above its parent in z-order at the same
bottom placement; dismissing it reveals the parent. This deliberately differs
from Niratan's current reader child-selection call, which forces child
`isFullWidth` to `false`. The deviation is required by the explicit Niratan
Windows requirement that nested popups consume all popup appearance settings.

Embedded standalone dictionary surfaces continue filling their owning panel;
floating-popup size and full-width placement do not override that host mode.

## Popup Scale

Implement scale with Niratan-style CSS variables, not WebView2 browser zoom.
The generated shell defines `--popup-scale` and scaled variables for root text,
body text, expression/reading text, dictionary labels, tags, buttons, icons,
spacing, and radii. Popup CSS consumes those variables for its fixed pixel
dimensions. As in Niratan, numeric `px` lengths in custom popup CSS are
rewritten to `calc(<value>px * var(--popup-scale))` before injection so custom
styling scales with built-in popup content.

Every result injection applies the current scale before rendering and before
the generation-scoped `contentReady` message. The native popup outer width and
height do not change when scale changes. Root and prewarmed child WebViews use
the same scale snapshot, so no child briefly renders at `1.0` before adopting
the configured value.

## Action Bar and Navigation

`DictionaryLookupPopup` owns a native WinUI `CommandBar` above the existing
Sasayaki controls and WebView2 content. It contains compact Back, Forward, and
Close commands with localized automation names and tooltips.

- Close requests the existing overlay dismissal path.
- Back and Forward call the popup document's existing `navigateBack()` and
  `navigateForward()` functions.
- The JavaScript history bridge reports whether each direction is available so
  the native buttons expose the correct enabled state.

Structured-content dictionary links redirect within their current popup and
push the existing JavaScript snapshot history, matching Niratan's action-bar
navigation. Selecting text inside popup content remains a nested lookup and
creates a child popup. Each child owns its own action bar and history state.
Opening new in-place content clears forward history; starting a new render
generation clears both directions.

The action bar is collapsed when `PopupActionBar` is false. Sasayaki controls
remain independently visible when their context is available. The native grid
allocates automatic rows for both bars so either, both, or neither can be
present without covering WebView2 content.

## Data Flow

```text
Appearance control
  -> SettingsPageViewModel
  -> ISettingsService.DictionaryDisplaySettings
  -> lookup request snapshot
  -> DictionaryPopupOverlay
       -> normalize layout settings once
       -> root DictionaryLookupPopup
       -> child DictionaryLookupPopup(s)
  -> native layout + action bar + generated popup CSS variables
```

No ViewModel accesses SQLite. No popup lookup logic moves into WebView
JavaScript; JavaScript remains responsible for rendering, its local history,
selection coordinates, and narrow typed messages.

## Error and State Handling

- Non-finite or out-of-range width, height, and scale values resolve through
  the shared constraints helper before persistence or layout.
- Layout always clamps to actual overlay dimensions and never produces a
  negative popup extent.
- Navigation-state messages are validated and scoped to the active WebView2
  instance. Stale render generations cannot re-enable commands for new
  content.
- A failed in-place redirect leaves the current popup content and history
  usable and does not create an empty child.
- A child receives an immutable settings snapshot from the active root lookup;
  settings changed while a stack is open apply on the next lookup rather than
  partially restyling the visible stack.

## Testing

### Automated

- Assert Niratan-aligned defaults, ranges, steps, and clamping.
- Assert Popup controls exist only in Appearance and use two-way ViewModel
  bindings with localized automation identifiers.
- Assert Dictionary settings no longer exposes or owns popup dimensions.
- Assert root and child layout use the same configured width and height without
  the old child caps.
- Unit-test full-width width, bottom placement, viewport clamping, and nested
  layout behavior.
- Assert generated shell and injection scripts apply `PopupScale` before
  rendering, including child injection.
- Assert action-bar visibility, Back/Forward enabled state, Close routing, and
  generation-scoped navigation messages.
- Assert structured links use in-place history while popup text selection still
  opens a child.
- Assert profile cloning and popup request snapshots preserve all five popup
  appearance fields.

### Runtime

- Run dictionary-focused tests, then the complete x64 test project.
- Build with `dotnet build -p:Platform=x64`.
- Launch with `build-and-run.ps1` and confirm a real responsive Niratan window.
- In both reader and video lookup, verify `320 x 250`, `1400 x 800`, scale
  `0.8`, scale `1.5`, action bar on/off, and full-width on/off.
- Open nested lookups at each scale and with full-width enabled; verify each
  child uses the same settings and closing it reveals the parent.
- Exercise structured-content links and verify Back, Forward, and Close.
- Resize the window with a popup open and repeat lookup to confirm containment.
- Verify light and dark themes and keyboard access to every new setting and
  action-bar command.

## Scope

This change is limited to popup appearance persistence, settings placement,
floating-popup layout, content scale, action-bar navigation, nested-popup
configuration propagation, resources, and regression coverage. It does not add
transparency or swipe-to-dismiss controls from Niratan, change dictionary
lookup limits, modify `native/hoshidicts/`, replace WebView2 rendering, or
alter EPUB pagination.
