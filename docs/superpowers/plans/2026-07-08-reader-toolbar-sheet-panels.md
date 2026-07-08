# Reader Toolbar Sheet Panels Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert reader toolbar Chapter, Search, Highlights, Statistics, and Appearance panels from custom left-side `Popup`s to WinUI `ContentDialog` sheet panels.

**Architecture:** Keep all current panel content, bindings, and event handlers, but host them in `ContentDialog` controls instead of `Popup` controls. Replace popup positioning and open/close helpers with a single dialog open helper that ensures only one reader tool sheet is active.

**Tech Stack:** WinUI 3 XAML, C#/.NET, xUnit asset tests, existing reader ViewModel and controls.

## Global Constraints

- Do not change the Back button into a panel.
- Do not rework Sasayaki; it already uses a `ContentDialog`.
- Do not rewrite reader rendering; WebView2 remains the reader host.
- Do not move business logic into WebView JavaScript.
- Keep content controls and existing ViewModel/data paths intact.
- Do not commit automatically because the worktree contains unrelated user changes.

---

### Task 1: Add Failing Asset Coverage

**Files:**
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Consumes: reader XAML and code-behind text loaded by existing tests.
- Produces: `ReaderPage_UsesSheetDialogsForReaderToolbarPanels` asset test.

- [ ] **Step 1: Write the failing test**

Add a test that asserts `NovelReaderPage.xaml` contains:

```csharp
"ReaderChapterPanelDialog"
"ReaderSearchPanelDialog"
"ReaderHighlightsPanelDialog"
"ReaderStatisticsPanelDialog"
"ReaderAppearancePanelDialog"
"AutomationProperties.AutomationId=\"NovelReaderChapterPanelDialog\""
"AutomationProperties.AutomationId=\"NovelReaderSearchPanelDialog\""
"AutomationProperties.AutomationId=\"NovelReaderHighlightsPanelDialog\""
"AutomationProperties.AutomationId=\"NovelReaderStatisticsPanelDialog\""
"AutomationProperties.AutomationId=\"NovelReaderAppearancePanelDialog\""
```

Assert it does not contain:

```csharp
"ReaderChapterPanelPopup"
"ReaderSearchPanelPopup"
"ReaderHighlightsPanelPopup"
"ReaderStatisticsPanelPopup"
"ReaderAppearancePanelPopup"
```

Assert code-behind contains `ShowReaderPanelDialogAsync` and does not contain `OpenReaderPanel(` or `PositionReaderPanel`.

- [ ] **Step 2: Run the focused test and confirm failure**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ReaderPage_UsesSheetDialogsForReaderToolbarPanels"
```

Expected: FAIL because the old Popup panels still exist.

### Task 2: Convert XAML Panels

**Files:**
- Modify: `Hoshi/Views/Pages/NovelReaderPage.xaml`
- Modify: `Hoshi/Strings/en-US/Resources.resw`
- Modify: `Hoshi/Strings/zh-CN/Resources.resw`

**Interfaces:**
- Consumes: current panel inner content.
- Produces: five `ContentDialog` controls with existing inner controls and localized title/close text.

- [ ] **Step 1: Replace each Popup with a ContentDialog**

Create:

```xml
<ContentDialog x:Name="ReaderChapterPanelDialog" x:Uid="ReaderChapterPanelDialog" AutomationProperties.AutomationId="NovelReaderChapterPanelDialog" CloseButtonText="Close" DefaultButton="Close">...</ContentDialog>
```

Repeat for Search, Highlights, Statistics, and Appearance. Preserve existing inner controls and their `AutomationId`s.

- [ ] **Step 2: Add resources**

Add English and Chinese `.Title` and `.CloseButtonText` entries for each new dialog.

### Task 3: Replace Popup Open/Close Code

**Files:**
- Modify: `Hoshi/Views/Pages/NovelReaderPage.xaml.cs`

**Interfaces:**
- Consumes: existing click handlers and panel refresh methods.
- Produces: async click handlers that call `ShowReaderPanelDialogAsync(ContentDialog dialog)`.

- [ ] **Step 1: Add dialog helper**

Add:

```csharp
private ContentDialog? _activeReaderPanelDialog;

private async Task ShowReaderPanelDialogAsync(ContentDialog dialog)
{
    if (_activeReaderPanelDialog == dialog)
        return;

    CloseReaderPanels();
    dialog.XamlRoot = XamlRoot;
    _activeReaderPanelDialog = dialog;
    try
    {
        await dialog.ShowAsync();
    }
    finally
    {
        if (_activeReaderPanelDialog == dialog)
            _activeReaderPanelDialog = null;
    }
}

private void CloseReaderPanels()
{
    _activeReaderPanelDialog?.Hide();
    _activeReaderPanelDialog = null;
}
```

- [ ] **Step 2: Update click handlers**

Make Chapter, Search, Highlights, Statistics, and Appearance click handlers async where needed and replace popup calls with `await ShowReaderPanelDialogAsync(...)`.

- [ ] **Step 3: Update internal close paths**

Replace all `Reader*PanelPopup.IsOpen = false` references with `CloseReaderPanels()` or the appropriate dialog hide path.

### Task 4: Verify

**Files:**
- Test: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

- [ ] **Step 1: Run focused test**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ReaderPage_UsesSheetDialogsForReaderToolbarPanels"
```

Expected: PASS.

- [ ] **Step 2: Run reader asset tests**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderWebAssetTests"
```

Expected: PASS.

- [ ] **Step 3: Build and launch**

Run:

```powershell
dotnet build -p:Platform=x64
.\build-and-run.ps1
```

Expected: build succeeds and Hoshi launches with a responsive main window.
