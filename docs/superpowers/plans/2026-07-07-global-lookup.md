# Global Lookup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a reusable global lookup path that can be opened from Hoshi and can grow into Niratan-style cross-application selected-text lookup while using the existing dictionary popup rendering pipeline.

**Architecture:** Build a shared dictionary popup request service that gathers lookup results, dictionary styles, display settings, theme, audio settings, Anki settings, and optional mining context. The global lookup window owns only UI concerns: text input, paste, focus, embedded popup overlay, and window activation. A global selection lookup coordinator owns opt-in enablement, hotkey/trigger status, one-shot selected-text reading, and window activation; it must not monitor the clipboard in the background. Existing reader and popup JavaScript remain unchanged.

**Tech Stack:** WinUI 3, Windows App SDK, C#/.NET, CommunityToolkit.Mvvm, existing hoshidicts-backed `IDictionaryLookupService`, existing `DictionaryPopupOverlay`.

---

### Task 1: Shared Popup Request Service

**Files:**
- Create: `Hoshi/Models/Dictionary/DictionaryPopupRequest.cs`
- Create: `Hoshi/Services/Dictionary/IDictionaryPopupRequestService.cs`
- Create: `Hoshi/Services/Dictionary/DictionaryPopupRequestService.cs`
- Modify: `Hoshi/ViewModels/Pages/VideoPlayerViewModel.cs`
- Test: `Hoshi.Tests/Services/Dictionary/DictionaryPopupRequestServiceTests.cs`

- [ ] Write tests for blank query, no results, lookup settings propagation, style dictionary mapping, settings snapshot, and optional Anki mining context.
- [ ] Verify the new tests fail because the service does not exist yet.
- [ ] Implement `DictionaryPopupRequest` as the canonical request model for popup display.
- [ ] Implement `DictionaryPopupRequestService.CreateAsync(...)` using `IDictionaryLookupService` and `ISettingsService`.
- [ ] Refactor `VideoPlayerViewModel.CreateLookupRequestAsync(...)` to return the shared request type, keeping video-specific mining context construction in the video view model.
- [ ] Run the new service tests and affected video tests.

### Task 2: Global Lookup ViewModel And Window Service

**Files:**
- Create: `Hoshi/ViewModels/Windows/GlobalLookupWindowViewModel.cs`
- Create: `Hoshi/Services/Dictionary/IGlobalLookupWindowService.cs`
- Create: `Hoshi/Services/Dictionary/GlobalLookupWindowService.cs`
- Test: `Hoshi.Tests/ViewModels/Windows/GlobalLookupWindowViewModelTests.cs`

- [ ] Write tests for empty input status, in-progress state, no-results status, successful request emission, initial query lookup, and exception status handling.
- [ ] Verify the new tests fail because the view model and service do not exist yet.
- [ ] Implement `GlobalLookupWindowViewModel` with `Query`, `StatusText`, `IsLookupInProgress`, an async lookup command, and a `LookupReady` event carrying `DictionaryPopupRequest`.
- [ ] Implement `IGlobalLookupWindowService.OpenAsync(string? initialQuery = null, CancellationToken ct = default)` following the single-window reuse pattern from `VideoPlayerWindowService`.
- [ ] Keep all dictionary lookup work in the view model/service layer; the future window code-behind only subscribes to `LookupReady` and shows the popup.
- [ ] Run the new view model tests.

### Task 3: Global Lookup Window UI And DI

**Files:**
- Create: `Hoshi/Views/Dictionary/GlobalLookupWindow.xaml`
- Create: `Hoshi/Views/Dictionary/GlobalLookupWindow.xaml.cs`
- Create: `Hoshi/Models/Settings/GlobalLookupSettings.cs`
- Create: `Hoshi/Services/Dictionary/IGlobalSelectionLookupService.cs`
- Create: `Hoshi/Services/Dictionary/IGlobalSelectedTextReader.cs`
- Create: `Hoshi/Services/Dictionary/GlobalSelectionLookupService.cs`
- Create: `Hoshi/Services/Dictionary/ClipboardSelectedTextReader.cs`
- Modify: `Hoshi/App.xaml.cs`
- Modify: `Hoshi/Models/Settings/AppSettings.cs`
- Modify: `Hoshi/Views/Pages/NavigationPage.xaml`
- Modify: `Hoshi/Views/Pages/NavigationPage.xaml.cs`
- Modify: `Hoshi/ViewModels/Pages/NavigationPageViewModel.cs`
- Modify: `Hoshi/Strings/en-US/Resources.resw`
- Modify: `Hoshi/Strings/zh-CN/Resources.resw`
- Test: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`
- Test: `Hoshi.Tests/Services/Dictionary/GlobalSelectionLookupServiceTests.cs`

- [ ] Add asset tests proving the global lookup services are registered, the window embeds `DictionaryPopupOverlay`, and navigation exposes a global lookup command without adding a custom results list.
- [ ] Add service tests proving global lookup is disabled by default, disabled mode does not read selected text, enabled trigger opens the lookup window only for non-empty text, and empty/no selection reports a visible status without starting a dictionary query.
- [ ] Verify the asset tests fail before implementation.
- [ ] Build `GlobalLookupWindow` with a compact WinUI command surface: query `TextBox`, search button, paste button, status text, and embedded `Canvas` inside a `DictionaryPanelRoot`.
- [ ] In code-behind, handle only UI concerns: key events, paste text from clipboard on explicit button click, focus, `DictionaryPopupOverlay` setup, size changes, dismissal, and `ShowLookupAsync`.
- [ ] Add `GlobalLookupSettings` under `AppSettings` with `Enabled = false` by default and a fixed MVP display hotkey such as `Ctrl+Alt+D` if full shortcut editing is out of scope.
- [ ] Implement a global selection lookup service/coordinator that can be started from app launch and triggered from tests or a future hotkey hook. If a full Win32 `RegisterHotKey` hook is not completed in this task, expose explicit status text such as `Global lookup hotkey registration is not available in this build` rather than pretending global hotkey support is active.
- [ ] Implement selected-text reading as one-shot only. Prefer Windows UI Automation selected text where feasible; if falling back to clipboard copy, save and restore clipboard content and never poll or monitor clipboard in the background.
- [ ] Register `DictionaryPopupRequestService`, `GlobalLookupWindowViewModel`, `GlobalLookupWindowService`, `GlobalSelectionLookupService`, and selected-text reader in `App.xaml.cs`.
- [ ] Add a top-bar lookup button in `NavigationPage` that calls `IGlobalLookupWindowService.OpenAsync(...)`.
- [ ] Localize visible and automation strings in English and Chinese resources.
- [ ] Run the asset test and build.

### Task 4: Verification

**Files:**
- No planned source edits unless verification finds a defect.

- [ ] Run targeted tests that do not depend on the native hoshidicts DLL:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopupRequestServiceTests|FullyQualifiedName~GlobalLookupWindowViewModelTests|FullyQualifiedName~NovelReaderWebAssetTests|FullyQualifiedName~VideoMiningContextFactoryTests|FullyQualifiedName~VideoPlayerViewModel"
```

- [ ] Run full build:

```powershell
dotnet build -p:Platform=x64
```

- [ ] Try the full test suite and record any native DLL baseline failure separately:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
```

- [ ] If the app can launch in the current environment, run `.\build-and-run.ps1` and verify the global lookup window opens, accepts typed/pasted text, renders popup results, and supports nested lookup.
