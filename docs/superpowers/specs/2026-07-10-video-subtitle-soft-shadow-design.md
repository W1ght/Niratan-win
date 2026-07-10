# Video Subtitle Soft Shadow Design

## Goal

Make the Windows video subtitle shadow match Niratan at the full `0...10`
range. A value of `10` must remain a soft shadow instead of producing eight
hard displaced copies of each glyph.

## Approved visual behavior

- Interpret `SubtitleShadowRadius` as a Gaussian blur radius clamped to
  `0...10`.
- Render one black shadow at 90% of the effective subtitle opacity.
- Offset that shadow by `0` DIPs horizontally and `1` DIP vertically.
- Draw the configured subtitle foreground once, sharply, above the shadow.
- When the subtitle mask blur is active, blur the completed shadow-plus-text
  composite by the configured mask radius.
- Preserve existing text alignment, wrapping, vertical positioning, mask
  opacity, and invisible WebView2 hit-testing behavior.

## Architecture

Replace the native eight-`TextBlock` shadow stack and asynchronous PNG mask
renderer with one Win2D `CanvasControl`. A focused video subtitle renderer
draws a black text source through `GaussianBlurEffect`, then draws the crisp
foreground. For mask mode it draws both into a command list and applies the
existing second Gaussian blur to the composite.

This keeps rendering GPU-backed and synchronous with the current subtitle
state, avoiding stale asynchronous bitmap generations. The WebView2 remains
transparent and continues to own character hit testing and selection.

## Error and edge behavior

- Radius or opacity at zero produces no shadow.
- Radius, opacity, font size, and font weight remain clamped at their existing
  bounds.
- Empty subtitle text clears the canvas naturally.
- Canvas resource creation and drawing stay on the WinUI render path; no IO or
  ViewModel access moves into services.

## Verification

- Add unit coverage for Niratan-compatible shadow parameters at radius `0`,
  default radius `3`, and maximum radius `10`.
- Update asset contracts to require a single `CanvasControl`,
  `GaussianBlurEffect`, and no eight named shadow `TextBlock`s.
- Run focused subtitle tests, the full x64 test suite, and the x64 build.
- Launch the app and verify a real top-level window. Visually check shadow `10`
  against the supplied reproduction when the test video is available.

