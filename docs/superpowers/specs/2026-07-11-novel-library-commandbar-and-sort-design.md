# Novel library command bar and sort selection

## Goal

Make the Novel library command bar self-explanatory and keep its sort picker
consistent with the persisted library setting.

## Command bar

- The Statistics command uses the native `BarChart` symbol and the visible label
  `Statistics`.
- Manage shelves, Sync Google Drive, Import, and Return to bookshelf all expose
  visible labels through their localized `AppBarButton` resources.
- Sync Google Drive moves from `SecondaryCommands` to `PrimaryCommands`, so it
  stays visible at ordinary desktop widths rather than starting in the overflow
  menu.
- The command bar retains native dynamic overflow at very narrow widths to avoid
  clipped controls.

## Sort picker

- The first-run sort value is `Recent`.
- When the user selects Title or Manual, the existing settings persistence stores
  that value.
- On subsequent navigation to the Novel library and on app restart, the picker
  displays the stored value instead of resetting to `Recent`.

## Verification

- Asset tests assert that the Google Drive command is primary, the statistics
  command uses `BarChart`, and the relevant command labels are present.
- ViewModel tests verify that the initial sort value is Recent and a persisted
  sort value is reflected when the library ViewModel is created.
- Build the x64 application and confirm the main window opens.
