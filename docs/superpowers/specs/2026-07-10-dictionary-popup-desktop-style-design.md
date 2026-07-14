# Dictionary Popup Desktop Style Design

## Context

The Windows dictionary popup should match the supplied Hoshi Reader Desktop
screenshots in light and dark themes. The screenshots are a visual reference;
the referenced desktop implementation is not open source.

The current popup already has the required lookup, nested-popup, first-frame,
theme, and scrolling behavior. This change is presentation-only.

## Goals

- Use an 8 DIP outer corner radius.
- Use a 1 DIP theme-aware outer outline.
- Use an opaque white surface in light mode and an opaque black surface in dark
  mode.
- Match the reference's compact, outlined dictionary cards.
- Apply the same visual treatment to root and nested dictionary popups.
- Preserve existing popup sizing, placement, interaction, rendering, and
  scrolling behavior.

## Non-goals

- Do not change scrollbar appearance or behavior.
- Do not change popup dimensions or placement rules.
- Do not change lookup, WebView messaging, `contentReady`, nested lookup, Anki,
  audio, or dictionary rendering behavior.
- Do not copy code from the unavailable Hoshi Reader Desktop implementation.

## Chosen Design

### Native popup shell

The WinUI `Border` remains the owner of the popup silhouette. It uses:

- `CornerRadius = 8` for the in-content popup presentation;
- `BorderThickness = 1`;
- light surface `#FFFFFF` with outline `#D1D1D6`;
- dark surface `#000000` with outline `#3A3A3C`.

The existing opaque WebView2 corner guard is retained and recalculated for the
8 DIP radius. The native surface, WebView2 default background, and generated web
document continue to use the same opaque theme surface, preventing corner
wedges or initialization flashes.

Root and child popup hosts receive the same shell treatment because both use
`DictionaryLookupPopup`.

### Dictionary cards

The WebView2 glossary card remains a single card layer with:

- 8 CSS pixel corner radius;
- a subtle 1 CSS pixel theme-aware border;
- no additional opaque card fill over the popup surface;
- restrained shadow/highlight values that preserve the reference's outlined,
  low-elevation appearance in both themes.

Existing typography, entry layout, structured Yomitan content, and card
grouping remain unchanged.

### Scrollbars

All existing scrollbar selectors, dimensions, colors, visibility rules, hover
states, and scroll-activity behavior remain unchanged.

## State Flow

1. The popup resolves its current theme.
2. `DictionaryPopupMaterial` supplies the opaque surface and outline colors.
3. `DictionaryLookupPopup` applies the 8 DIP shell radius, 1 DIP outline, and
   matching corner guard geometry.
4. The generated HTML receives the existing theme mode and renders matching
   black or white surfaces plus theme-aware glossary cards.
5. Theme changes update the native shell and WebView content without changing
   popup geometry, placement, scrolling, or lookup state.

## Failure Handling

- Before WebView2 initialization, the native shell already presents the correct
  opaque surface and outline.
- After initialization, the WebView2 default background is reapplied from the
  same canonical surface color.
- Delayed JavaScript theme updates cannot expose transparent or mismatched
  corner pixels because the native guard and WebView2 background remain opaque.

## Verification

### Automated checks

- Add a failing regression test first for the 8 DIP radius and 1 DIP outline.
- Assert the exact light and dark outline colors.
- Assert the glossary card's theme-aware 8px outlined style.
- Assert that scrollbar declarations are unchanged by the implementation.
- Retain existing popup first-frame, nested lookup, placement, and rendering
  tests.

### Runtime checks

- Build with `dotnet build -p:Platform=x64`.
- Run dictionary-focused tests.
- Launch Niratan and open root and nested popups in light and dark themes.
- Verify all four outer corners and the outline at 100% and high-DPI scaling.
- Verify card outlines, theme switching, scrolling, resizing, Shift-hover, and
  first-frame reveal behavior.

## Alternatives Considered

### CSS-only outer outline

This is simpler, but it makes the rectangular WebView2 responsible for the
outer silhouette and can regress the existing rounded-corner guard.

### Additional XAML decoration layer

This separates outline and background into multiple elements, but adds another
surface without improving behavior over the existing native `Border`.

## Scope

The implementation is limited to popup presentation resources, the native
popup shell, popup web card styling, and focused regression coverage. Existing
dictionary and reader behavior remains unchanged.
