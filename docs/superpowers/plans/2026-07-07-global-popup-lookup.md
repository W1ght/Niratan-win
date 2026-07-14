# Global Popup Lookup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the global lookup hotkey show the same dictionary popup experience as reader lookup, while keeping the existing global lookup window as a manual lookup entry point.

**Architecture:** Split global lookup into two UI targets: `IGlobalLookupWindowService` remains the explicit command/window entry, and a new `IGlobalLookupPopupService` owns transient hotkey popup display. `GlobalSelectionLookupService` reads selected text and calls the popup service; the popup service reuses `DictionaryPopupRequestService` plus `DictionaryPopupOverlay` inside a lightweight topmost WinUI window near the cursor.

**Tech Stack:** WinUI 3, Windows App SDK AppWindow, CommunityToolkit.Mvvm, WebView2 dictionary popup, xUnit v3, FluentAssertions.

---

### Task 1: Route Hotkey To Popup Service

**Files:**
- Create: `Niratan/Services/Dictionary/IGlobalLookupPopupService.cs`
- Modify: `Niratan/Services/Dictionary/GlobalSelectionLookupService.cs`
- Modify: `Niratan/App.xaml.cs`
- Test: `Niratan.Tests/Services/Dictionary/GlobalSelectionLookupServiceTests.cs`

- [ ] **Step 1: Write failing tests**

Update `GlobalSelectionLookupServiceTests` so enabled hotkey triggers call a recording popup service instead of `IGlobalLookupWindowService`.

Expected assertions:
- `RegisteredTrigger_WhenEnabled_ReadsSelectedTextOnceAndShowsPopup` records `"星"` in popup queries.
- `RegisteredTrigger_WithEmptySelection_DoesNotOpenPopupOrWindow` records no popup and no window.
- Existing window service fake remains only to prove the hotkey path no longer opens the window.

- [ ] **Step 2: Run red test**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~GlobalSelectionLookupServiceTests"
```

Expected: tests fail because `GlobalSelectionLookupService` still depends on `IGlobalLookupWindowService`.

- [ ] **Step 3: Implement minimal service boundary**

Create `IGlobalLookupPopupService`:

```csharp
namespace Niratan.Services.Dictionary;

public interface IGlobalLookupPopupService
{
    Task ShowAsync(string query, CancellationToken ct = default);
}
```

Change `GlobalSelectionLookupService` constructor to accept `IGlobalLookupPopupService`. In `TriggerLookupAsync`, return early for empty selected text and call `ShowAsync(query, ct)` for non-empty selected text.

- [ ] **Step 4: Register service placeholder**

Register the implementation once Task 2 creates it. During Task 1, tests can use a recording fake.

---

### Task 2: Build Floating Popup Window

**Files:**
- Create: `Niratan/Views/Dictionary/GlobalLookupPopupWindow.xaml`
- Create: `Niratan/Views/Dictionary/GlobalLookupPopupWindow.xaml.cs`
- Create: `Niratan/Services/Dictionary/GlobalLookupPopupService.cs`
- Modify: `Niratan/App.xaml.cs`
- Test: `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

- [ ] **Step 1: Write asset tests**

Add tests that assert:
- `GlobalLookupPopupWindow.xaml` exists.
- The popup window has no `TextBox`, no search button, and no custom result list.
- The code-behind uses `DictionaryPopupOverlay`, `UseCanvas`, and `ShowLookupAsync`.
- `App.xaml.cs` registers `IGlobalLookupPopupService, GlobalLookupPopupService`.

- [ ] **Step 2: Run red test**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~GlobalLookupPopup"
```

Expected: fail because popup window/service do not exist.

- [ ] **Step 3: Implement popup window**

Create a small borderless/topmost-ish WinUI window whose content is a full-size `Canvas` named `DictionaryOverlayCanvas`. Code-behind should:
- set title to `"Niratan Lookup Popup"`;
- initialize `DictionaryPopupOverlay`;
- call `UseCanvas(DictionaryOverlayCanvas)`;
- call `ShowLookupAsync` with the `DictionaryPopupRequest` contents;
- position the popup anchor at `(24, 24, 1, 1)` inside the popup window;
- close on `Esc`;
- dismiss/close when overlay dismisses.

- [ ] **Step 4: Implement popup service**

`GlobalLookupPopupService` should:
- dispatch to `App.MainWindow.DispatcherQueue` when called from the hotkey callback;
- call `DictionaryPopupRequestService.CreateAsync(query, traceId: $"global-popup-{Guid.NewGuid():N}")`;
- do nothing if the request is null;
- reuse one `GlobalLookupPopupWindow` and replace its contents on repeated hotkeys;
- position near the current cursor using Win32 `GetCursorPos`;
- keep the existing `GlobalLookupWindowService` unchanged for manual entry.

---

### Task 3: Verify Interaction And Regression Safety

**Files:**
- Test: `Niratan.Tests/Services/Dictionary/GlobalSelectionLookupServiceTests.cs`
- Test: `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

- [ ] **Step 1: Run targeted tests**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~GlobalSelectionLookupServiceTests|FullyQualifiedName~GlobalLookupPopup|FullyQualifiedName~GlobalLookupWindow"
```

Expected: all targeted tests pass.

- [ ] **Step 2: Build**

Run:

```powershell
dotnet build -p:Platform=x64
```

Expected: build succeeds with only existing package advisory warnings.

- [ ] **Step 3: Launch and manual automation check**

Run:

```powershell
.\build-and-run.ps1
```

Then perform the real hotkey test from Notepad:
- open a temporary text file with `明日 星 辞書`;
- focus the document;
- select all text;
- press `Ctrl+Alt+D`;
- verify a visible Niratan popup window appears without opening `Global Lookup`;
- verify logs show a `global-popup-` lookup trace and rendered popup content.

- [ ] **Step 4: Full test suite**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --no-build
```

Expected: all tests pass.
