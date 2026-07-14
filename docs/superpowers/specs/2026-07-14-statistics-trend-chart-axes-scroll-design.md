# Statistics Trend Chart Axes and Range Scrolling Design

## Goal

Make the statistics trend chart readable and stable at a glance:

- show numeric values on both chart axes;
- keep the chart at a fixed height;
- remove the anchor-date picker;
- let the user move the selected date window with an always-visible horizontal range slider;
- keep every Dashboard card synchronized with the visible date window.

This is a Windows extension to the current Niratan macOS Dashboard reference. The Mac implementation provides range modes and a fixed minimum chart height, but does not expose numeric axes or a draggable historical window. The extension preserves Niratan's date-range and aggregation semantics.

## Interaction Model

The existing `Year`, `Month`, `Week`, and `Day` range modes remain. They define the width and calendar alignment of the selected window:

- `Year`: the complete available recent-year window;
- `Month`: one calendar month;
- `Week`: one locale-aligned calendar week;
- `Day`: one local calendar day.

The anchor-date picker and the public `AnchorDate` ViewModel state are removed. An always-visible horizontal WinUI `Slider` below the chart acts as the date-range drag bar and selects one discrete, calendar-aligned window from the available Dashboard data window. A standalone WinUI `ScrollBar` is not used because its overlay template auto-hides the enabled track in the target Windows configuration.

- The newest available window is selected initially.
- Dragging toward the left selects older periods; dragging toward the right selects newer periods.
- Keyboard arrows move one period; page movement skips several periods.
- The slider value is always an integer period index.
- `Year` has one period, so the slider remains visible for layout stability but is disabled.
- Changing the range mode selects the newest valid period for that mode.
- A snapshot refresh preserves the selected period when it still exists; otherwise it clamps to the nearest valid period.
- Selecting a day in the reading calendar moves the slider to the period containing that day, then updates the day-detail card.

The selected period continues to filter the entire Dashboard, including the trend, selected-range summary, speed summary, book ranking, shelf comparison, and calendar selection styling. The slider is not a chart-only visual pan and never mutates source statistics.

## Chart Layout

`NovelStatisticsTrendChart` uses a fixed total height of 260 effective pixels. The control reserves stable gutters for labels so data does not move when values change:

- left gutter: Y-axis tick labels;
- center: grid and bar/line plot;
- bottom gutter: X-axis labels;
- range slider: placed below the chart by the Dashboard view and not counted in the 260-pixel chart height.

The X axis displays the first, middle, and last visible point labels. Duplicate labels are suppressed for very short ranges. The Y axis displays five aligned ticks: zero, three intermediate values, and the current maximum. Grid lines align with the Y ticks.

Y-axis formatting follows the active metric:

- characters: localized compact counts with grouping or `k`/`M` where needed;
- duration: minutes or hours;
- speed: localized characters per hour.

The maximum scale is never zero. Empty and all-zero ranges use a stable zero-to-one fallback scale while retaining the existing empty/zero visual behavior. Bars, line markers, tooltips, and accessibility names remain available.

## Components and Data Flow

### Date-window calculation

A pure statistics helper produces the ordered list of selectable `NovelStatisticsDateRange` values for a range mode and the available snapshot window. It owns calendar alignment, clipping at the data-window edges, and offset clamping. The ViewModel stores only the selected integer offset and the generated windows.

### ViewModel

`NovelStatisticsDashboardViewModel` exposes:

- the current range-window offset;
- slider minimum, maximum, step frequency, small change, and large change;
- whether scrolling is enabled;
- chart axis tick display models.

When the mode, offset, or snapshot changes, the ViewModel resolves `SelectedDateRange` first and then runs the existing Dashboard calculations against that range. No service, SQLite, sidecar, or Google Drive contract changes are required.

### Chart control

`NovelStatisticsTrendChart` remains a UI-only custom WinUI control. It receives display-ready trend points and Y-axis ticks, measures the plot rectangle after subtracting the gutters, and draws axes, grid, bars or line, markers, and labels. It does not calculate statistics or access storage.

### Dashboard view

The range mode, metric, grain, and style controls remain. The anchor-date label and `CalendarDatePicker` are removed. The new slider appears immediately under the fixed-height chart with an automation identifier and localized accessible name.

## Accessibility and Localization

- The slider exposes its selected range through its accessible name/help text.
- Axis labels are rendered as real `TextBlock` elements rather than pixels.
- Existing point tooltip and automation text remain unchanged.
- New labels and accessibility strings are added to both `en-US` and `zh-CN` resources.
- Axis text uses the current culture and does not assume Latin-only glyph widths.

## Error and Boundary Handling

- Empty snapshots produce no selectable periods and a disabled slider.
- A one-period snapshot has minimum and maximum zero.
- Partial first or last weeks/months are clipped to the available snapshot window.
- Offset values from XAML binding are rounded and clamped before use.
- Resize and theme changes rerender the control without changing the selected date range.
- Existing source sidecars, Dashboard cache contents, and Google Drive synchronization behavior are unchanged.

## Verification

Automated coverage will verify:

- calendar-aligned day, week, month, and year windows;
- ordering, newest-period default, and offset clamping;
- mode and snapshot changes preserve or clamp the current period correctly;
- all Dashboard summaries use the scrolled range;
- Y-axis tick values and metric-specific formatting;
- the XAML contract has a fixed 260-pixel chart, a horizontal integer-snapping slider, and no anchor-date picker;
- both bar and line styles continue to render from the same visible points.

Manual verification will open the real Dashboard and check:

- numeric X/Y axes at wide, medium, and narrow window widths;
- stable 260-pixel chart height while switching metric, grain, and style;
- drag, arrow-key, and page-key movement through historical periods;
- disabled year-slider behavior;
- synchronized range title, summaries, calendar, ranking, and shelf comparison;
- no regression in the recovered Google Drive statistics for `かがみの孤城`.

## Out of Scope

- free-form two-ended date selection;
- zooming to arbitrary day counts;
- changing the one-year Dashboard source window;
- persisting the slider position across app restarts;
- changes to statistics collection, Niratan sidecars, Google Drive import, or Dashboard cache schemas.
