# Statistics Summary Card Density Design

## Goal

Align the Windows statistics summary cards with the supplied Hoshi reference:

- make the Today goal ring visually prominent instead of appearing as a small status badge;
- keep the This Week card only as tall as its own content instead of stretching to match a taller card in the same dashboard row.

## Current behavior and root cause

The Today goal ring was hosted in an `88×88` grid. More importantly, WinUI's default `ProgressRing` style sets the control itself to `32×32` and centers it. Enlarging only the host therefore leaves the rendered ring at its default size. The Hoshi Mac statistics dashboard uses a `118×118` frame for the equivalent ring.

The wide dashboard uses a shared three-column `Grid`. `WeekCard` and the taller ranking card occupy the same auto-sized row. Because a `Border` stretches vertically by default, the week card expands to the full row height and displays a large empty area below its content.

## Design

1. Change both the Today ring host and the determinate `ProgressRing` itself to `118×118` effective pixels, matching the Hoshi Mac reference implementation. Keep the percentage binding, metrics, and accessibility behavior.
2. Set `WeekCard` to `VerticalAlignment="Top"`. Its width continues to stretch to the dashboard column, but its height is determined by the header, four metrics, and seven weekday tiles.
3. Do not alter the dashboard breakpoints, card padding, metric data, weekday tile sizes, or card ordering.

## Alternatives considered

- **Redesign Today as a side-by-side Mac layout.** This would align more of the card but changes information hierarchy and narrow-window wrapping beyond the requested size adjustment.
- **Reduce all dashboard card padding and spacing.** This would make the entire dashboard denser but does not address the shared-row stretch root cause and would affect unrelated cards.

The selected approach is the smallest behavior-preserving alignment with the supplied references.

## Verification

- Add a XAML contract regression test that isolates the Today `ProgressRing` element and requires the control itself to declare `118×118`, plus a top-aligned `WeekCard`.
- Observe that the test fails before the production XAML change and passes afterward.
- Run the dashboard-focused tests, the full x64 test suite, and the x64 build.
- Launch Hoshi and use screenshot-only UI verification at narrow and wide widths. Confirm the ring is visibly larger, the week card has no stretched empty interior, and the dashboard remains responsive.
