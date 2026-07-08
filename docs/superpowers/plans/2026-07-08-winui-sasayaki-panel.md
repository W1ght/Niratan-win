# WinUI Sasayaki Panel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a WinUI `ContentDialog` Sasayaki panel in the reader, aligned with Niratan's Audio, Playback, and Settings controls, including text and background colors.

**Architecture:** The reader top Sasayaki button opens a WinUI sheet-like dialog. The panel reuses existing `SasayakiViewModel`, existing playback methods, existing `SasayakiSettings`, and existing sidecar persistence; it does not add Lyrics Mode and does not move reader rendering out of WebView2.

**Tech Stack:** WinUI 3 XAML, C#/.NET, CommunityToolkit.Mvvm state, xUnit asset tests.

## Global Constraints

- Do not modify `native/hoshidicts/`.
- Do not add Lyrics Mode.
- Reader rendering stays WebView2 + CSS multi-column.
- WebView2 JavaScript must not own settings or dictionary logic.
- Keep changes scoped to reader Sasayaki panel XAML, reader page UI glue, resources, and asset tests.
- Do not commit automatically because this worktree already contains unrelated user changes.

---

### Task 1: Add Failing Asset Coverage

**Files:**
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`
- Test: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Consumes: `ReadNovelReaderPageXaml()`
- Produces: assertions for the dialog, section labels, control `AutomationId`s, slider ranges, and Lyrics Mode absence.

- [ ] **Step 1: Add an asset test**

Add a test named `ReaderPage_DefinesNiratanStyleSasayakiPanelWithoutLyricsMode` near the existing Sasayaki menu test. It should read `NovelReaderPage.xaml` and assert these strings exist:

```csharp
"NovelReaderSasayakiPanelDialog"
"NovelReaderSasayakiAudioSection"
"NovelReaderSasayakiPlaybackSection"
"NovelReaderSasayakiSettingsSection"
"NovelReaderSasayakiThemeSection"
"NovelReaderSasayakiPanelSkipBackButton"
"NovelReaderSasayakiPanelPreviousCueButton"
"NovelReaderSasayakiPanelPlayPauseButton"
"NovelReaderSasayakiPanelNextCueButton"
"NovelReaderSasayakiPanelSkipForwardButton"
"NovelReaderSasayakiPanelLoadAudioButton"
"NovelReaderSasayakiDelaySlider"
"Minimum=\"-2\""
"Maximum=\"2\""
"StepFrequency=\"0.05\""
"NovelReaderSasayakiSpeedSlider"
"Minimum=\"0.5\""
"Maximum=\"1.5\""
"NovelReaderSasayakiShowToggleSwitch"
"NovelReaderSasayakiAutoScrollToggleSwitch"
"NovelReaderSasayakiAutoPauseToggleSwitch"
"NovelReaderSasayakiLightTextColorPicker"
"NovelReaderSasayakiLightBackgroundColorPicker"
"NovelReaderSasayakiDarkTextColorPicker"
"NovelReaderSasayakiDarkBackgroundColorPicker"
```

Assert these strings do not exist:

```csharp
"NovelReaderLyricsModeMenuItem"
"Lyrics Mode"
```

- [ ] **Step 2: Run the focused test and confirm failure**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ReaderPage_DefinesNiratanStyleSasayakiPanelWithoutLyricsMode"
```

Expected: one failing test because the dialog does not exist yet.

### Task 2: Add the WinUI Sasayaki Panel

**Files:**
- Modify: `Hoshi/Views/Pages/NovelReaderPage.xaml`
- Modify: `Hoshi/Views/Pages/NovelReaderPage.xaml.cs`

**Interfaces:**
- Consumes: `_sasayakiVM`, `CurrentSasayakiSettings`, `LoadSasayakiFromPickerAsync`, `ToggleSasayakiPlaybackAsync`, `GoToPreviousSasayakiCueAsync`, `GoToNextSasayakiCueAsync`, `SkipSasayakiBackAsync`, `SkipSasayakiForwardAsync`, `ApplySasayakiPlayback`, `SaveSasayakiPlaybackAsync`, `HighlightSasayakiCueAsync`.
- Produces: `SasayakiPanelDialog`, section controls, open handler, setting/color persistence handlers, and panel state refresh.

- [ ] **Step 1: Replace the small menu as the primary click target**

In `NovelReaderPage.xaml`, keep the existing top `SasayakiButton` but change its click behavior so it opens the panel. The previous menu-only commands may remain as secondary controls only if they do not conflict with the panel.

- [ ] **Step 2: Define the `ContentDialog`**

Add `ContentDialog x:Name="SasayakiPanelDialog"` with `AutomationProperties.AutomationId="NovelReaderSasayakiPanelDialog"`. Inside it, add a `ScrollViewer` and four sections:

- Audio: icon buttons for skip back, previous cue, play/pause, next cue, skip forward; time text; cue text; load audio button.
- Playback: delay slider `-2..2 step 0.05`; speed slider `0.5..1.5 step 0.05`.
- Settings: three toggle switches for Show Sasayaki, Auto-Scroll, Auto-Pause.
- Theme: four ColorPickers for light text/background and dark text/background.

- [ ] **Step 3: Add UI event handlers**

In `NovelReaderPage.xaml.cs`, add handlers that:

- open the dialog and refresh control values from `CurrentSasayakiSettings`;
- route Audio buttons to existing playback methods;
- update `_sasayakiDelay` and `_sasayakiPlayer.PlaybackRate`;
- save playback sidecar after delay/speed changes;
- save global Sasayaki settings after toggle/color changes;
- refresh current highlight after color changes.

- [ ] **Step 4: Run the focused test and confirm pass**

Run the same focused test. Expected: PASS.

### Task 3: Resource Copy and Full Verification

**Files:**
- Modify: `Hoshi/Strings/en-US/Resources.resw`
- Modify: `Hoshi/Strings/zh-CN/Resources.resw`

**Interfaces:**
- Consumes: new `x:Uid` values from `NovelReaderPage.xaml`.
- Produces: localized panel title, section labels, control labels, and close button text.

- [ ] **Step 1: Add resource entries**

Add English and Chinese strings for the panel title and controls. Keep labels concise: Sasayaki, Audio, Playback, Settings, Theme, Load Audio, Delay, Speed, Show Sasayaki, Auto-Scroll, Auto-Pause, Light Text, Light Background, Dark Text, Dark Background.

- [ ] **Step 2: Run Sasayaki asset tests**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderWebAssetTests"
```

Expected: PASS.

- [ ] **Step 3: Build**

Run:

```powershell
dotnet build -p:Platform=x64
```

Expected: build succeeds. Existing vulnerability warnings are acceptable if unchanged.

- [ ] **Step 4: Launch**

Run:

```powershell
.\build-and-run.ps1
```

Expected: Hoshi launches and the main window responds.
