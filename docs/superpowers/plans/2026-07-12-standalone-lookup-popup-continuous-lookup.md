# Standalone Lookup Popup Continuous Lookup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep the standalone lookup page's root dictionary result visible while mouse click and Shift-hover open reusable nested lookup popups.

**Architecture:** Remove the page-wide WinUI pointer-dismiss path that races WebView2 input. Preserve the existing `popup.js` text hit-testing, typed `lookupRedirect` message, `DictionaryPopupRedirectRouter`, and `DictionaryPopupOverlay` child stack as the sole continuous-lookup path.

**Tech Stack:** WinUI 3, Windows App SDK, C#/.NET, WebView2 JavaScript bridge, xUnit v3, FluentAssertions

## Global Constraints

- Target Windows 10+ x64; build with `dotnet build -p:Platform=x64`.
- Run tests with `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64`.
- Do not modify `native/hoshidicts/`.
- Do not move dictionary lookup logic into JavaScript or broaden the WebView2 native API.
- Preserve the existing `DictionaryPopupOverlay` nested stack and `tapOutside ≠ dismiss` behavior.
- Preserve the current uncommitted embedded-root popup sizing changes in `Niratan/Views/Dictionary/DictionaryPopupOverlay.cs` and `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs`.
- Do not add dependencies or settings.

---

## File Structure

- Modify `Niratan.Tests/Views/Pages/LookupPageShellContractTests.cs`: add the regression contract proving the standalone page does not register a page-wide pointer dismissal path.
- Modify `Niratan/Views/Pages/NovelLookupPage.xaml.cs`: remove only the page-wide `PointerPressed` registration, handler, and now-unused visual-tree import.
- Verify without modifying `Niratan/Web/DictionaryPopup/popup.js`: its existing click and Shift handlers must continue to send `lookupRedirect`.
- Verify without modifying `Niratan/Views/Dictionary/DictionaryPopupRedirectRouter.cs`: requests with selection coordinates/source must continue to resolve as `Nested`.
- Verify without modifying `Niratan/Views/Dictionary/DictionaryPopupOverlay.cs`: native lookup and child popup stacking remain the implementation boundary.

### Task 1: Stop the standalone page from dismissing embedded results before WebView2 lookup

**Files:**
- Modify: `Niratan.Tests/Views/Pages/LookupPageShellContractTests.cs:60`
- Modify: `Niratan/Views/Pages/NovelLookupPage.xaml.cs:10`
- Modify: `Niratan/Views/Pages/NovelLookupPage.xaml.cs:46`
- Delete: `Niratan/Views/Pages/NovelLookupPage.xaml.cs:72`
- Modify: `Niratan/Views/Pages/NovelLookupPage.xaml.cs:176`
- Verify: `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs`
- Verify: `Niratan.Tests/Views/Dictionary/DictionaryPopupRedirectRouterTests.cs`

**Interfaces:**
- Consumes: existing `DictionaryPopupOverlay.ShowLookupAsync(...)`, `DictionaryPopupRedirectRequest`, and WebView2 `lookupRedirect` messages.
- Produces: `NovelLookupPage` lifecycle with no page-wide `PointerPressed` dismissal registration; existing nested popup APIs remain unchanged.

- [ ] **Step 1: Write the failing standalone-page input regression test**

Append this test to `LookupPageShellContractTests`:

```csharp
[Fact]
public void NovelLookupPage_PreservesEmbeddedResultsAcrossPagePointerPresses()
{
    var lookupPageCode = ReadProjectFile("Views", "Pages", "NovelLookupPage.xaml.cs");

    lookupPageCode.Should().NotContain("AddHandler(PointerPressedEvent");
    lookupPageCode.Should().NotContain("RemoveHandler(PointerPressedEvent");
    lookupPageCode.Should().NotContain("OnPagePointerPressed");
}
```

- [ ] **Step 2: Run the new test and verify RED**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~LookupPageShellContractTests.NovelLookupPage_PreservesEmbeddedResultsAcrossPagePointerPresses"
```

Expected: FAIL because `NovelLookupPage.xaml.cs` currently contains `AddHandler(PointerPressedEvent`, `RemoveHandler(PointerPressedEvent`, and `OnPagePointerPressed`.

- [ ] **Step 3: Implement the minimal WinUI fix**

In `NovelLookupPage.xaml.cs`, remove the now-unused media import:

```diff
-using Microsoft.UI.Xaml.Media;
```

Keep the load lifecycle as:

```csharp
private void NovelLookupPage_Loaded(object sender, RoutedEventArgs e)
{
    var popupOverlay = EnsurePopupOverlay();
    DictionaryPanelRoot.SizeChanged += OnDictionaryPanelSizeChanged;
    _ = popupOverlay.PrewarmAsync(XamlRoot);
}
```

Delete the complete page-wide handler:

```csharp
private void OnPagePointerPressed(object sender, PointerRoutedEventArgs e)
{
    if (_popupOverlay is null) return;
    if (DictionaryPanelRoot.Visibility != Visibility.Visible) return;

    var source = e.OriginalSource as DependencyObject;
    while (source != null)
    {
        if (ReferenceEquals(source, DictionaryPanelRoot))
            return;
        source = VisualTreeHelper.GetParent(source);
    }

    _popupOverlay.Dismiss();
}
```

Keep disposal as:

```csharp
public void Dispose()
{
    Loaded -= NovelLookupPage_Loaded;
    Unloaded -= NovelLookupPage_Unloaded;
    DictionaryPanelRoot.SizeChanged -= OnDictionaryPanelSizeChanged;
    if (_popupOverlay != null)
    {
        _popupOverlay.Dismissed -= OnPopupOverlayDismissed;
        _popupOverlay.Dispose();
        _popupOverlay = null;
    }
}
```

Do not change `LookupAsync`: a main search with zero results must retain its explicit `_popupOverlay?.Dismiss()` behavior.

- [ ] **Step 4: Re-run the focused test and verify GREEN**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~LookupPageShellContractTests.NovelLookupPage_PreservesEmbeddedResultsAcrossPagePointerPresses"
```

Expected: PASS.

- [ ] **Step 5: Verify the existing click, Shift, and nested-routing contracts**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderWebAssetTests.PopupScript_SupportsClickAndShiftNestedLookupInsideLookupWindow|FullyQualifiedName~DictionaryPopupRedirectRouterTests"
```

Expected: PASS. This proves the retained WebView2 path sends click/Shift redirects and native routing still selects `Nested`.

- [ ] **Step 6: Run dictionary-focused regression tests**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Dictionary"
```

Expected: PASS with no new failures.

- [ ] **Step 7: Run the full x64 test suite**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64
```

Expected: PASS. If an unrelated failure comes from the pre-existing dirty files, record the exact test and keep this task's files unchanged while diagnosing ownership.

- [ ] **Step 8: Build the WinUI application**

Run:

```powershell
dotnet build -p:Platform=x64
```

Expected: build succeeds with zero errors.

- [ ] **Step 9: Launch and verify the real interaction**

Run:

```powershell
.\build-and-run.ps1
```

Verify objective launch first: the Niratan main window is visible and responsive. Then open `查词`, search a term with definition text, and check:

1. Clicking definition text leaves the root result visible and opens a child popup.
2. Clicking definition text inside the child opens the next child without hiding its parents.
3. Holding Shift and moving over definition text at both levels starts lookup immediately and leaves the root visible.
4. Clicking the search box, page whitespace, audio/Anki buttons, summary headers, and scrollable content does not dismiss the root result.
5. A new successful main search replaces the root and clears children; a new main search with no result closes the root according to existing behavior.

Expected: all five behaviors pass. Leave the final verified app instance running for the user.

- [ ] **Step 10: Review and commit only this task's clean files**

Run:

```powershell
git diff --check -- Niratan.Tests/Views/Pages/LookupPageShellContractTests.cs Niratan/Views/Pages/NovelLookupPage.xaml.cs
git diff -- Niratan.Tests/Views/Pages/LookupPageShellContractTests.cs Niratan/Views/Pages/NovelLookupPage.xaml.cs
git add -- Niratan.Tests/Views/Pages/LookupPageShellContractTests.cs Niratan/Views/Pages/NovelLookupPage.xaml.cs
git diff --cached --name-only
git commit -m "fix(dictionary): keep standalone lookup results interactive"
```

Expected: the cached name list contains exactly the two task files; the commit succeeds without including the pre-existing popup sizing changes or unrelated plan files.
