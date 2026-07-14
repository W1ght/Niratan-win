# Dictionary Popup Rounded-Corner Guard Design

## Problem

The dictionary popup must keep its visible rounded silhouette while appearing over video and reader content. The current implementation rounds the web document and makes the pixels outside that rounded document transparent. WinUI 3 WebView2 cannot composite those transparent pixels over sibling XAML content, so they resolve to the host window background. In the video window that background is black, producing four black corner wedges.

Applying a Composition clip to the popup's parent Grid does not change that WebView2 transparency limitation and therefore does not reveal the video below the popup.

## Goal

- Preserve a 12 DIP rounded popup in the normal in-app overlay.
- Preserve an 8 DIP rounded popup in the naked floating-window presentation.
- Preserve the existing square standalone-window presentation.
- Show the underlying video or reader surface outside the native rounded silhouette.
- Eliminate black or white rectangular WebView2 corner pixels during normal display, warm-up, navigation, and theme changes.
- Keep dictionary rendering and lookup behavior unchanged.

## Chosen Design

Use a native WinUI rounded guard band around an opaque rectangular WebView2.

The outer `Border` owns the visible rounded silhouette and paints a theme-matched, opaque popup surface. All rectangular content is inset far enough that its corners lie entirely inside the outer rounded rectangle:

- 12 DIP radius uses a 4 DIP guard inset.
- 8 DIP radius uses a 3 DIP guard inset.
- 0 DIP radius uses no guard inset.

For a quarter-circle corner, an axis-aligned inner rectangle is contained by an inset of at least `radius * (1 - 1 / sqrt(2))`. The selected integer insets round this value upward, so the WebView2 never reaches pixels outside the native rounded silhouette.

The WebView2 host and web document remain opaque and use the same theme surface color as the native guard band. The CSS viewport no longer depends on transparent pixels to express the outer popup shape. This also avoids black flashes while WebView2 is initializing or navigating.

## Component Changes

### `DictionaryLookupPopup`

- Restore a native `Border` background and corner radius as the visual shell.
- Inset the entire rectangular content root according to the active corner radius.
- Set the WebView2 default background to the current opaque popup surface color before and after WebView2 initialization.
- Update the native shell, inset, and web theme together when the presentation mode or theme changes.
- Remove the ineffective parent Composition rounded clip if it no longer performs any distinct work.

These changes remain UI-only code in the View layer. Lookup, database, dictionary, and JavaScript bridge responsibilities do not move into code-behind.

### `DictionaryPopupMaterial`

- Continue to provide the canonical opaque light and dark popup surface colors.
- Use the same color source for the native guard band and WebView2 default background so their boundary is visually seamless.

### Popup web assets

- Make the document/root background opaque and theme-matched.
- Keep the scroll viewport and dictionary content behavior unchanged.
- Do not use transparent document corners as the popup's outer clipping mechanism.

## State Flow

1. The popup selects its presentation radius: 12, 8, or 0 DIP.
2. The native shell applies that radius and the corresponding guard inset.
3. The selected theme supplies one opaque surface color to both the native shell and WebView2.
4. WebView2 renders only inside the guard inset.
5. Pixels outside the native rounded shell contain no WebView2 content, so normal XAML composition reveals the video or reader underneath.

## Failure Handling

- If WebView2 is not initialized yet, native shell state is applied immediately and the background color is applied again after initialization.
- Presentation or theme changes are idempotent and may run during warm-up.
- JavaScript theme updates remain best-effort as today; the native opaque default prevents black corner flashes if a script update is delayed.

## Testing

### Automated regression tests

- First add a failing source-level regression test that requires the radius-to-inset mapping and native opaque guard surface.
- Require WebView2 to use the canonical opaque theme color rather than transparent default background.
- Require the popup web document background to be opaque and disallow transparent outer-corner styling.
- Retain existing tests for popup stack behavior, content-ready generations, warm WebView reuse, and dictionary rendering.

### Runtime verification

- Build with `dotnet build -p:Platform=x64`.
- Run dictionary-focused tests.
- Launch Niratan and open a dictionary popup over a bright video frame.
- Verify all four corners at 100% and high-DPI scaling.
- Verify normal 12 DIP, naked 8 DIP, and square standalone presentations.
- Verify light and dark themes, popup warm-up, nested lookup, scrolling, resize, and Shift-hover responsiveness.

## Alternatives Rejected

### Dedicated rounded HWND

A separate popup window can use a window region for exact clipping, but it adds window ownership, focus, DPI, positioning, and nested-popup lifecycle complexity to an otherwise in-tree overlay.

### Windowless WebView2 composition host

A custom composition controller could support true alpha, but replacing the WinUI WebView2 control would require new input, IME, accessibility, scaling, and lifecycle integration. That risk is disproportionate to this visual defect.

## Scope

This change is limited to dictionary popup presentation and regression coverage. It does not change dictionary lookup, hoshidicts integration, EPUB rendering, popup stack semantics, or Anki behavior.
