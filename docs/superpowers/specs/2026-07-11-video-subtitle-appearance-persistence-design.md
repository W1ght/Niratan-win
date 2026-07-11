# Video subtitle appearance persistence

## Goal

Video subtitle appearance changes made in the player inspector must persist as the global video subtitle preference and be restored when a new video player window is created.

## Defaults

For a new profile and for the player’s reset action, use the values from the approved reference image:

- Font family: `Noto Serif CJK JP`
- Font size: `52 px`
- Font weight: `700`
- Shadow radius: `10`
- Vertical position: `-51`
- Color: opaque white (`#FFFFFFFF`)

Other subtitle defaults remain unchanged.

## Design

`VideoSettings` remains the single persisted source for video preferences. It owns the default subtitle values and validates their ranges. `JapaneseFontCatalog` exposes the new default subtitle font so all font-picker and view-model defaults agree.

`VideoPlayerViewModel` receives `ISettingsService`, loads `VideoSettings` at construction, and persists subtitle appearance changes through the settings service. A guard suppresses writes while settings are initially applied, so constructing a player does not overwrite an existing profile. Persistence is limited to subtitle appearance fields; it retains all unrelated video preferences currently stored in `VideoSettings`.

The player view remains responsible only for applying view-model state to the subtitle renderer. It does not implement settings persistence.

## Data flow

1. Player opens and the view model applies the persisted `VideoSettings` subtitle appearance.
2. A user changes an appearance control in the player inspector.
3. The view model normalizes the value, updates `VideoSettings` through `ISettingsService`, and requests asynchronous save.
4. A later player instance reads the stored values.

## Error handling

Settings serialization failures retain the existing `SettingsService` logging behavior. In-memory settings remain updated, and invalid values are normalized by `VideoSettings`.

## Tests

- Update model and player default tests for the approved defaults.
- Add a player view-model regression test that mutates appearance, verifies the settings service receives the normalized values, and verifies a new view model loads them.
- Run the targeted subtitle-settings and player-appearance tests, then the full test suite and x64 build.
