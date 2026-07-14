# Niratan Manual Book Sync Menu Fix Design

## Context

Hoshi exposes per-book ッツ sync actions from the local novel card context menu. The current implementation declares those actions inside a `DataTemplate` and binds their commands and visibility back to `NovelLibraryPage` with `ElementName=ThisPage`.

At runtime, a `MenuFlyout` uses a separate XAML namescope. Those page-level bindings do not resolve from the flyout. The failure has two visible consequences:

- with sync enabled and `SyncMode` set to `Manual`, both the automatic **Sync** item and the manual **Sync** submenu are visible;
- the flyout command bindings do not reliably reach `NovelLibraryPageViewModel`, so selecting a sync action can produce no service call or result notification.

The observed local settings were `EnableSync=true`, `SyncMode=Manual`, and `EnableAutoSync=false`, while UI Automation still reported two top-level items with the label **Sync**. The application log contained no matching sync-service activity. Niratan's `BookCell` renders exactly one branch: a manual submenu containing **Import** and **Export**, or a single automatic **Sync** action.

## Goals

- Match Niratan's per-book sync menu behavior.
- Ensure every visible sync action reaches the existing ViewModel command with the selected book.
- Preserve the current sync service, direction resolution, deduplication, notifications, and cancellation behavior.
- Keep business logic out of code-behind.

## Non-goals

- Do not change Google Drive file formats, folder discovery, authentication, or credential storage.
- Do not change automatic reader sync or remote-book download behavior.
- Do not perform a live Google Drive write as part of automated verification.
- Do not refactor unrelated novel-card or video-card flyouts.

## Design

### Flyout presentation

The novel-card `MenuFlyout` will handle its `Opening` event. The page will treat this handler as UI-only presentation logic and read the existing ViewModel properties:

- `ShowAutomaticBookSyncAction`
- `ShowManualBookSyncAction`

The handler will set the two sync containers so they are mutually exclusive:

| Sync state | Automatic item | Manual submenu |
| --- | --- | --- |
| Disabled | Hidden | Hidden |
| Enabled + Auto | Visible | Hidden |
| Enabled + Manual | Hidden | Visible |

The manual branch remains one top-level **Sync** submenu containing **Import** and **Export**. It will not expose a second sibling **Sync** action.

### Command dispatch

The three sync actions will store the templated `NovelBookItemViewModel` in `Tag` using the existing compiled item binding. Their `Click` handlers will perform UI-only forwarding:

- automatic **Sync** → `SyncNovelCommand`;
- manual **Import** → `ImportNovelFromTtuCommand`;
- manual **Export** → `ExportNovelCommand`.

Each handler validates the tagged item and invokes the existing asynchronous command. Direction selection, authentication checks, payload preferences, concurrency control, service calls, catalog refresh, and notifications remain in `NovelLibraryPageViewModel`.

This avoids cross-namescope command bindings without moving sync decisions into code-behind.

### Result and error behavior

The existing ViewModel behavior remains authoritative:

- imported and exported results show success notifications;
- already-synced results show the existing informational success notification;
- skipped results do not claim success;
- unavailable sync and service failures show existing error notifications;
- cancellation remains silent;
- duplicate sync attempts for the same book remain deduplicated.

No new remote retry or fallback behavior is introduced by this fix.

## Alternatives considered

### Custom binding proxy

A proxy could expose the page ViewModel inside the flyout namescope. This keeps declarative commands but adds a custom XAML mechanism whose lifetime and resource resolution would need additional coverage. It is more complex than the narrow UI bridge required here.

### Commands on every item ViewModel

Each `NovelBookItemViewModel` could receive page commands and sync-mode presentation state. This would remove code-behind forwarding, but it would couple a lightweight book presentation model to page-level command lifetime and sync settings. It would also require updating every item construction and shelf projection path.

## Testing and verification

Implementation will follow test-driven development:

1. Add a failing XAML contract test proving the flyout no longer depends on page `ElementName` bindings for sync actions and declares explicit opening/click bridges.
2. Retain and run the existing ViewModel tests that verify command-to-direction mapping, payload preferences, unavailable sync, result notifications, failures, cancellation, and per-book deduplication.
3. Run the focused novel-library and sync test suites.
4. Run the full x64 build and test commands required by the repository.
5. Launch the `niratan-sync-parity` worktree executable and use UI Automation to verify that Manual mode exposes exactly one top-level **Sync** submenu with **Import** and **Export**.

Automated and UI verification will not select **Export** or otherwise write to Google Drive. A live remote sync can be performed separately only with explicit user direction.
