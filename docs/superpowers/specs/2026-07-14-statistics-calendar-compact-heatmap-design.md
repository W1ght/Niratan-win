# Statistics Calendar Compact Heatmap Design

## Goal

Make the Dashboard reading calendar match the compact Niratan heatmap: small day cells with consistently small gaps, seven rows, and horizontal scrolling through the recent year.

## Root Cause

The WinUI calendar uses a `ListView` with an `ItemsWrapGrid`, but neither the item container nor the panel slot has a compact fixed size. The default `ListViewItem` preserves a touch-oriented minimum extent, so the 18-pixel day square is centered inside a much larger row and column slot. The result is the widely separated grid shown in the reported screenshot.

The Niratan macOS reference uses 12-by-12 day cells, four effective pixels between cells, seven fixed rows, and ten pixels of heatmap padding.

## Layout

- Each visible day square is exactly 12 by 12 effective pixels.
- Each day template has a two-pixel margin on every side, producing a fixed 16-pixel item slot and a four-pixel visible gap.
- The `ItemsWrapGrid` fixes both `ItemWidth` and `ItemHeight` to 16 and keeps `MaximumRowsOrColumns="7"` with vertical orientation.
- The `ListViewItem` container removes default padding, margin, minimum width, and minimum height so WinUI cannot enlarge the slots.
- The heatmap viewport uses ten pixels of internal padding and a maximum height sized for seven compact rows plus padding.
- The recent-year sequence remains horizontally scrollable; dates, heat opacity, selection, accessibility text, and calendar detail behavior do not change.

## Scope

This is a view-only correction in `NovelStatisticsDashboardView.xaml`. It does not change Dashboard calculations, ViewModel state, sidecars, cache contents, Google Drive synchronization, or selected-range behavior.

## Verification

Automated XAML contract coverage will require:

- 12-by-12 day cells with a two-pixel margin;
- a compact `ListViewItem` container with zero minimum width and height;
- a 16-by-16 `ItemsWrapGrid` slot with seven rows;
- horizontal scrolling retained and vertical scrolling disabled.

Manual verification will open the real Dashboard at wide and narrow widths and confirm that:

- adjacent cells have a consistent four-pixel gap;
- seven rows fit without vertical scrolling or clipping;
- the recent year scrolls horizontally;
- selecting a cell still updates the detail row and range state.

## Out of Scope

- weekday or month labels;
- changing heat intensity thresholds;
- changing the recent-year window;
- increasing the day cell to a touch-sized control.
