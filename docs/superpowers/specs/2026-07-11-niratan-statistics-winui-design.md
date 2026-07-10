# Niratan Statistics and Bookshelf WinUI Design

## Goal

Present Niratan's complete statistics and shelf behavior through native WinUI controls, MVVM bindings, accessible interaction, and explicit responsive layouts.

## Normal Bookshelf

The novel page owns vertical page scrolling and exposes a native `CommandBar` for sort, selection, statistics, sync, shelf management, and import.

Sections appear in this order when applicable:

1. Reading, derived from current progress and controlled by the existing show-reading setting.
2. Custom shelves in `shelves.json` order.
3. Google Drive remote books when enabled.
4. Unshelved books.

Custom shelves use compact horizontal rails where the page remains the vertical scroll owner. Unshelved content uses the main adaptive poster grid. In-section drag reorder switches to manual sort and only changes that section. Cross-shelf movement uses a context menu or multi-select command. Shelf management uses a focused dialog for create, rename, reorder, and delete.

## Statistics Entry and Loading

The bookshelf command surface toggles between the normal bookshelf and statistics dashboard without retaining the old compact Today/Week/By Book border. Dashboard loading is cancellable and generation-aware. Initial loading shows placeholders, then cached data if available, then the refreshed snapshot. A small non-blocking status pill indicates background scanning.

An empty dashboard has a native content-unavailable state. Corrupt statistics show a warning without hiding valid books.

## Dashboard Card Order

`Range & Trend` is full width at every breakpoint. Remaining cards use Niratan's order.

At `>= 1260` effective pixels:

- Left column: Today, Goal, This Week.
- Right two-column cluster: Shelf Comparison spans both columns; Selected Range above Reading Calendar; Speed Summary above Book Ranking.

At `840...1259`:

- Left flow: Today, Goal, This Week, Reading Calendar, Shelf Comparison.
- Right flow: Selected Range, Speed Summary, Book Ranking.

Below `840`:

- Single flow: Today, Goal, This Week, Reading Calendar, Selected Range, Speed Summary, Book Ranking, Shelf Comparison.

## Controls and Interaction

- Trend controls expose Range, Time Grain, Metric, and Style using native selection controls.
- Trend supports bar and line presentations, a y-axis, dense-range horizontal scrolling, and pointer tooltips with metric value and top book contributors.
- Tooltip activation is delayed and throttled; it follows the pointer within plot bounds and clears consistently.
- Reading Calendar uses a horizontally scrollable seven-row heatmap with a visible scroll indicator, selectable dates, and a selected-date details footer.
- Book Ranking switches characters/duration/speed and uses lightweight progress bars rather than nested cards.
- Shelf Comparison is a compact horizontally scrollable table.
- Goal uses native selection and `NumberBox` controls; historical calculations update live.

The page owns vertical scrolling. Trend, calendar, shelf rails, and comparison tables own horizontal scrolling only where needed.

## Settings Placement

Statistics Settings retains only:

- Enable Statistics.
- Autostart: Off, Page Turn, On.
- TTU Sync toggle and Merge/Replace when global sync is enabled.

Daily and weekly goals live in the dashboard Goal card. Reader appearance retains show-statistics-toggle, show-speed, and show-time controls.

## Styling, Localization, and Accessibility

- Use theme resources and native Fluent control states; support light, dark, and high contrast.
- Avoid extra outer borders and double-card compositions.
- Localize all visible strings in English and Simplified Chinese.
- Provide stable AutomationIds, names, help text, keyboard focus order, and equivalent pointer/touch/keyboard actions.
- Trend points and heatmap cells expose the same information to Narrator that pointer tooltips show visually.

## Testing and Acceptance

- ViewModel tests cover mode/range/grain/metric/style selection, date selection, goal updates, loading generations, and error states.
- Static XAML contract tests cover required cards, automation IDs, localization, scroll ownership, and removed By Book UI.
- Runtime verification covers wide, medium, and narrow windows, horizontal scroll surfaces, pointer tooltips, keyboard-only use, touch, text scaling, light/dark, and high contrast.
- Acceptance requires the user-approved responsive layout and shelf layout to remain usable without overlapping, clipped primary controls, or nested vertical-scroll conflicts.
