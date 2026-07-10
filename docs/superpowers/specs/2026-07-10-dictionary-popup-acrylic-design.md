# Dictionary Popup Acrylic Design

## Context

The dictionary popup now matches the supplied Hoshi Reader Desktop shell and
card geometry: an 8 DIP native radius, a 1 DIP theme-aware outline, opaque
black/white surfaces, and transparent outlined glossary cards. The next visual
step is to make the popup use a frosted-glass surface by default.

The intended effect is true backdrop sampling: reader text, selections, and
color should remain contextually visible through the popup as diffused shapes,
not as sharp readable content. The user does not want a material setting; the
Acrylic presentation is the default.

## Platform Basis

WinUI 3 in-app Acrylic is the primary implementation because the popup is a
transient surface over content inside the same app window. An `AcrylicBrush`
provides blur, tint, luminosity control, noise, and automatic fallback when
transparency is unavailable.

WebView2 can expose its hosting app content only when its default background is
fully transparent. WebView2 does not support partially transparent default
background colors, so tint and fallback belong to the native Acrylic surface,
not to `DefaultBackgroundColor`.

References:

- <https://learn.microsoft.com/en-us/windows/apps/develop/ui/in-app-acrylic>
- <https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic>
- <https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2controller.defaultbackgroundcolor>

## Goals

- Use in-app Acrylic as the default root and nested popup surface.
- Show reader content through the popup as blurred, tinted context.
- Preserve the current 8 DIP outer radius and 1 DIP theme outline.
- Preserve the rounded-corner guard so WebView2 never reaches pixels outside
  the native silhouette.
- Preserve the current card geometry, scrollbar, sizing, placement, lookup,
  nested lookup, and first-frame behavior.
- Fall back to the current opaque white/black appearance when Windows disables
  or cannot render Acrylic.
- Keep light and dark popup text readable over the material.

## Non-goals

- Do not add a user-facing material setting.
- Do not implement screenshot capture, bitmap cropping, or Win2D Gaussian blur.
- Do not replace the WinUI WebView2 control with a composition controller.
- Do not change dictionary content rendering, Anki, audio, IPC, popup stack,
  dimensions, placement, or scrollbar behavior.
- Do not modify `native/hoshidicts/`.

## Chosen Design

### Native Acrylic shell

`DictionaryLookupPopup` continues using one native `Border` as the popup
silhouette. Its background changes from `SolidColorBrush` to the existing
custom in-app `AcrylicBrush` produced by `DictionaryPopupMaterial`.

The Acrylic recipe is theme-aware:

| Property | Light | Dark |
| --- | --- | --- |
| Tint color | `#FFF8F8F8` | `#FF242424` |
| Tint opacity | `0.78` | `0.12` |
| Tint luminosity opacity | `0.62` | `0.18` |
| Fallback color | `#FFFFFFFF` | `#FF000000` |

`AlwaysUseFallback` remains `false`. Windows therefore handles transparency,
power-saving, hardware, and high-contrast fallback, while the explicit opaque
fallback colors preserve the current visual behavior.

The existing outline remains independent from Acrylic:

- light outline: `#D1D1D6`;
- dark outline: `#3A3A3C`;
- thickness: 1 DIP;
- radius: 8 DIP.

Root and child hosts receive the same material because both are
`DictionaryLookupPopup` instances.

### Transparent WebView2 host

`DictionaryLookupPopup` sets `WebView2.DefaultBackgroundColor` to fully
transparent before WebView2 initialization and reapplies the same value after
`EnsureCoreWebView2Async`. No partially transparent native WebView2 color is
used.

The existing 8 DIP radius produces a 3 DIP guard inset through
`DictionaryPopupCornerGuard.CalculateInset`. The rectangular WebView2 remains
entirely inside the rounded native silhouette. Pixels in the outer corner band
are native Acrylic and cannot become black or white WebView2 wedges.

The presentation modes remain:

- in-reader and nested popup: 8 DIP Acrylic shell;
- naked floating popup: 8 DIP Acrylic shell;
- standalone window presentation: square host, with its existing window-level
  material behavior unchanged.

### Transparent web document surface

The generated popup document keeps its black/white `--background-color`
variables because dictionary structured content and custom CSS may depend on
them. Only the actual popup surface becomes transparent.

`popup.css` introduces `--popup-surface-color: transparent` and uses it for:

- `html` and `body` background;
- `#popup-viewport` background;
- the full-popup overlay background where appropriate.

`PopupHtmlGenerator` continues injecting theme text and semantic background
variables, but its inline shell rule paints the document background with
`var(--popup-surface-color)` instead of `var(--background-color)`.

Glossary cards remain unfilled, so the Acrylic material is visible through
their content area. Existing borders, shadows, scrollbars, and dictionary
styles remain unchanged.

## State Flow

1. The popup resolves the requested light or dark theme.
2. `DictionaryPopupMaterial` updates the Acrylic tint, luminosity, opaque
   fallback, and outline color.
3. The native shell applies the active radius and the matching guard inset.
4. WebView2 initializes with a fully transparent default background.
5. The generated HTML keeps the semantic theme variables but paints its shell
   transparently.
6. WebView2 content is composited over the native Acrylic surface, which
   samples and blurs the reader content behind it.
7. The current generation-scoped `contentReady` gate reveals the completed
   popup without changing lookup latency or awaiting visual readiness.

## Capability Gate

The first implementation checkpoint is a runtime sampling test over high-
contrast reader content and an active selection.

The native design passes only when:

- underlying reader shapes and selection color are visibly diffused through
  the popup;
- underlying text is not sharply readable through the surface;
- no black or white corner wedges appear;
- the popup does not flash opaque white or black during initialization.

If the popup shows only a flat tint, displays sharp unblurred reader content,
or regresses the corner guard, the native approach is considered unsupported
for the current Reader WebView2 composition path. Implementation stops and the
branch is not merged. A screenshot-plus-Gaussian-blur pipeline requires a new
design and explicit approval.

## Failure and Fallback Behavior

- When Acrylic is unavailable, `FallbackColor` produces the existing opaque
  white or black popup.
- High contrast and disabled transparency are handled by the platform fallback;
  text remains driven by the explicit popup theme.
- The transparent WebView2 value is applied both before and after controller
  initialization to avoid a stale white default.
- Existing opacity gating continues hiding warm or stale DOM generations.
- Theme updates modify the brush and outline in place; they do not recreate the
  WebView2 or popup host.

## Testing

### Automated regression tests

- Add a failing test requiring `AcrylicBrush` as the in-app shell surface.
- Require the exact light/dark tint, luminosity, and opaque fallback values.
- Require fully transparent WebView2 background before and after initialization.
- Require transparent HTML/body/viewport surface rules while preserving the
  semantic black/white theme variables.
- Retain the 8 DIP radius, 1 DIP outline, and corner-guard assertions.
- Assert that scrollbar selectors, variables, JavaScript, and regression test
  bodies remain unchanged.
- Retain first-frame, nested popup, placement, lookup, and rendering tests.

### Runtime verification

- Build and test x64.
- Verify the capability gate over high-contrast vertical reader text and a
  colored selection.
- Verify root and nested popups in light and dark themes.
- Verify all four corners at the active DPI and at one additional available
  scaling configuration.
- Verify first display, warm reuse, scrolling, resize, and Shift-hover.
- Verify platform fallback with transparency effects unavailable when this can
  be tested without changing user system settings; otherwise validate the
  explicit fallback values through automated tests.
- Leave the final verified Hoshi instance running.

## Alternatives Considered

### `SystemBackdropElement`

This can host a system material on a specific element, but it still requires a
transparent WebView2 and adds a new visual/lifecycle layer. It does not solve a
sampling failure that also affects the simpler existing Acrylic brush.

### Reader capture plus Win2D Gaussian blur

Capturing the Reader WebView2, cropping to popup bounds, blurring a bitmap, and
updating it after movement or resize would provide deterministic pixels. It
also adds capture latency, stale-frame handling, DPI conversion, memory churn,
and synchronization pressure to the non-blocking lookup path. It is excluded
from this design.

### Composition-controller WebView2

A windowless composition controller could provide deeper alpha integration,
but would require replacing the WinUI control and rebuilding input, focus,
IME, accessibility, scaling, and lifecycle integration. The risk is not
proportionate to this visual feature.

## Scope

The implementation is limited to the dictionary popup material helper, native
popup shell, generated popup surface CSS/HTML, and focused regression coverage.
No dictionary, reader layout, database, native interop, or popup-stack behavior
changes are included.
