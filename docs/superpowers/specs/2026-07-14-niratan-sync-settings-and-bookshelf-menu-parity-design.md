# Niratan Sync Settings and Bookshelf Menu Parity Design

## Goal

Align Hoshi Windows with Niratan's user-visible sync settings and per-book bookshelf sync commands while retaining the Windows desktop OAuth client secret securely on the local machine.

## Reference Behavior

Niratan is the behavior reference for this feature:

- Global sync controls whether the rest of the sync configuration is visible.
- Connected and disconnected Google Drive states expose different actions.
- Automatic direction presents one per-book Sync command.
- Manual direction presents Import and Export choices under Sync.
- Book data, statistics, and audiobook progress remain independently selectable payloads.

Windows keeps one deliberate platform deviation: desktop Google OAuth requires a Client Secret as well as a Client ID. The Client Secret remains a masked field and is never written to `AppSettings` or another plain-text settings file.

## Sync Settings Page

The page uses the existing WinUI and CommunityToolkit settings controls and follows this order:

1. **Syncing** — an Enable toggle and localized explanatory text describing bookmark/statistics synchronization through Google Drive and the per-book context-menu workflow.
2. **Client credentials** — Client ID and masked Client Secret fields. These controls are disabled while the account is connected so the displayed credentials cannot diverge from the active token credentials.
3. **Connection** — localized Connected, Not connected, or Connecting status. A disconnected account shows Connect Google Drive. A connected account shows Clear Cache and Sign out.
4. **Behaviour** — Direction and Auto Sync controls.
5. **Data** — Upload Books, Sync Stats, and Sync Audiobook Progress.

Sections 2–5 are hidden while global sync is disabled. Hiding a section never resets a stored preference. Sync Stats is shown only when statistics are enabled. Sync Audiobook Progress is shown only when Sasayaki is enabled.

Direction uses the existing `TtuSettingsSyncMode.Auto` and `TtuSettingsSyncMode.Manual` values. A native WinUI selection control may be used instead of copying Niratan's custom segmented control, provided the two choices and behavior remain identical.

`TtuSyncSettings.UploadBooks` defaults to `true` for a newly created settings object, matching Niratan. An existing explicit `false` value remains `false`.

## Client Secret Persistence

The existing `WindowsCredentialGoogleDriveCredentialStore` remains the only persistent Client Secret store. It already stores `GoogleDriveCredentials.ClientSecret` in Windows Credential Manager after successful authentication.

When the settings page is entered, its ViewModel asynchronously loads the saved Google Drive credentials. If credentials exist, it restores both Client ID and Client Secret into the page fields and marks the connection as connected. The PasswordBox continues to mask the secret. Successful connection no longer clears the ViewModel's Client Secret.

Failed authentication leaves the entered Client Secret in the page so the user can correct other input and retry. Signing out deletes the credential record and clears the displayed Client Secret. Clearing the Drive cache does not delete or clear client credentials.

No log, notification, exception text, settings JSON, test output, or telemetry may contain the Client Secret, access token, or refresh token.

## Connection Actions

Connect, Clear Cache, and Sign out remain ViewModel commands. Busy state disables connection actions and prevents duplicate operations.

Clear Cache and Sign out require confirmation, matching Niratan. Sign out clears authorization credentials and both Drive caches covered by the current cache services. Success and failure status is localized and does not expose OAuth secrets.

The XAML code-behind remains UI-only: navigation and control-level presentation are allowed, but credential, settings, confirmation, and sync decisions stay in the ViewModel and services.

## Data Preferences

The sync settings ViewModel projects these existing settings into one Niratan-aligned page:

- Upload Books → `TtuSyncSettings.UploadBooks`
- Sync Stats → `NovelStatisticsSettings.EnableSync`
- Sync Audiobook Progress → `SasayakiSettings.EnableSync`

Updating a projected toggle preserves every unrelated property in its owning settings object. Statistics and Sasayaki settings pages continue to expose their existing controls and observe the same persisted values.

## Bookshelf Context Menu

Each local novel card gains a localized Sync entry with a sync icon and stable AutomationId. The entry is absent when global sync is disabled.

- In Auto mode, Sync is a direct command and passes `TtuSyncDirection.Auto`.
- In Manual mode, Sync is a submenu containing Import and Export. Import passes `TtuSyncDirection.ImportFromTtu`; Export passes `TtuSyncDirection.ExportToTtu`.

The context menu remains reachable by mouse, touch/pen long press, Shift+F10, and the keyboard menu key through the standard WinUI flyout behavior.

## Per-Book Sync Flow

`NovelLibraryPageViewModel` receives `ITtuSyncService` and owns the per-book command. It rejects a request with a localized notification when global sync is disabled or Google Drive is disconnected.

For an accepted request, the ViewModel takes one settings snapshot and builds `TtuSyncOptions`:

- `Direction` comes from the invoked menu command.
- `SyncBookData` uses global sync and Upload Books.
- `SyncStatistics` uses the stored statistics sync preference.
- `StatisticsSyncMode` uses the stored Merge/Replace preference.
- `SyncAudioBook` requires Sasayaki to be enabled and its sync preference to be enabled.

The command awaits `ITtuSyncService.SyncBookAsync` without blocking the UI thread. A per-book in-flight guard prevents accidental duplicate syncs for the same book while allowing unrelated books to sync independently.

Results match Niratan semantics:

- Synced: report that the book is already synchronized.
- Imported: reload the local catalog/progress and report the imported character count.
- Exported: report the exported character count.
- Skipped: do not show a success notification.
- Failure: keep local data intact and show a localized sync error.
- Cancellation: exit without an error notification.

## Localization and Accessibility

All new and changed visible strings use `x:Uid` or localized resource lookup and are present in both `en-US` and `zh-CN` resources. This includes connection status, confirmation dialogs, sync results, errors, and context-menu labels.

Settings controls and menu commands receive stable AutomationIds and meaningful accessible names. The page must tolerate localized text growth, text scaling, light/dark themes, and high contrast without clipping.

## Automated Verification

Tests are added before production changes and must cover:

- Upload Books defaults to true without overwriting an explicit false value.
- Entering the settings page restores Client ID and Client Secret from the credential store.
- Successful authentication retains the Client Secret in the ViewModel.
- Failed authentication retains the entered Client Secret.
- Sign out clears stored/displayed credentials; cache clearing does not.
- Statistics and Sasayaki projected toggles preserve unrelated settings.
- Settings XAML contains the Niratan-aligned sections, conditional visibility, connection-state actions, localization keys, and AutomationIds.
- Context-menu assets contain Auto Sync and Manual Import/Export command surfaces with localization and AutomationIds.
- Per-book commands map directions and payload preferences to the expected `TtuSyncOptions`.
- Disconnected/disabled, success, skipped, failure, cancellation, duplicate-command, and imported-catalog-refresh behavior.

The focused sync and bookshelf tests run first, followed by the complete x64 test suite and x64 build.

## Runtime Verification

After automated verification, launch the WinUI app and confirm a responsive top-level window. Verify the sync page in disabled, disconnected, connecting, and connected states; confirm that the secret stays masked and survives leaving/re-entering the page and an app restart after successful authentication.

Verify the local book context menu with global sync disabled, Auto mode, and Manual mode using pointer and keyboard. Real Google Drive import/export is not executed unless the user explicitly authorizes a test account and test book; otherwise service and ViewModel tests provide remote-call evidence.

## Out of Scope

- Changing TTU file formats, merge rules, or Google Drive folder layout.
- Adding a second credential backend or storing the Client Secret in plain settings.
- Changing reader auto-sync scheduling.
- Modifying `native/hoshidicts/`.
