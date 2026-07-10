# Dictionary Popup Acrylic Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make native in-app Acrylic the default dictionary popup surface while preserving the 8 DIP outline shell, rounded-corner guard, dictionary rendering, and scrollbar behavior.

**Architecture:** Replace the in-reader popup's solid native surface brush with the existing theme-aware `AcrylicBrush`, make WebView2 and the generated document fully transparent, and keep semantic black/white CSS variables available to structured content. The existing 3 DIP guard inset keeps the rectangular WebView2 inside the 8 DIP native silhouette. A runtime capability gate decides whether the native path actually blurs Reader WebView2 content; failure stops the work without introducing screenshot blur.

**Tech Stack:** C#/.NET, WinUI 3, Windows App SDK 2.0, WebView2, HTML/CSS, xUnit v3, FluentAssertions

## Global Constraints

- Acrylic is the default popup material; do not add a user-facing material setting.
- Light Acrylic uses tint `#FFF8F8F8`, tint opacity `0.78`, luminosity opacity `0.62`, and fallback `#FFFFFFFF`.
- Dark Acrylic uses tint `#FF242424`, tint opacity `0.12`, luminosity opacity `0.18`, and fallback `#FF000000`.
- Keep the 8 DIP radius, 1 DIP outline, exact outline colors, and `DictionaryPopupCornerGuard` inset.
- WebView2 background must be fully transparent before and after controller initialization; do not use partial alpha.
- Keep semantic `--background-color` black/white variables for dictionary content while painting only the popup shell transparently.
- Do not change card geometry, scrollbar selectors/declarations/behavior, sizing, placement, lookup, WebView messaging, `contentReady`, nested lookup, Anki, audio, or structured content.
- Do not implement screenshot capture, Win2D Gaussian blur, or a composition-controller WebView2.
- Do not modify `native/hoshidicts/`.
- Build and test x64 only.
- If native Acrylic does not visibly blur the Reader WebView2, shows sharp background text, flashes opaque colors, or regresses corner wedges, stop and report the capability-gate failure; do not merge or substitute another architecture.

---

## File Structure

- `Hoshi/Views/Dictionary/DictionaryPopupMaterial.cs`: canonical Acrylic tint, luminosity, and opaque fallback values.
- `Hoshi/Views/Dictionary/DictionaryLookupPopup.cs`: Acrylic native shell, transparent WebView2 initialization, outline synchronization, and existing guard geometry.
- `Hoshi/Services/Dictionary/PopupHtmlGenerator.cs`: generated shell remains semantically themed while its rendered document background is transparent.
- `Hoshi/Web/DictionaryPopup/popup.css`: dedicated transparent popup-surface variable; cards and scrollbars remain unchanged.
- `Hoshi/Web/DictionaryPopup/popup-host.html`: static host mirrors the transparent generated shell.
- `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`: native shell, transparent host, corner-guard, scrollbar, and asset regressions.
- `Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs`: generated HTML keeps semantic theme colors while painting a transparent shell.

### Task 1: Native Acrylic Shell and Transparent WebView2

**Files:**
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs:1060-1130`
- Modify: `Hoshi/Views/Dictionary/DictionaryPopupMaterial.cs:15-75`
- Modify: `Hoshi/Views/Dictionary/DictionaryLookupPopup.cs:45-110, 330-340, 425-435`

**Interfaces:**
- Consumes: `DictionaryPopupMaterial.CreateInAppAcrylicThinBrush(ThemeMode)` and `DictionaryPopupMaterial.ApplyTheme(AcrylicBrush, ThemeMode)`.
- Produces: an `AcrylicBrush`-backed `VisualRoot` and `WebView2.DefaultBackgroundColor = Colors.Transparent` before and after initialization.

- [ ] **Step 1: Change the native-shell regression to the Acrylic contract**

Update the shell-specific assertions in `DictionaryLookupPopup_UsesFluentFloatingCardShell`:

```csharp
popupCode.Should().Contain("private readonly AcrylicBrush _surfaceBrush;");
popupCode.Should().Contain("private readonly SolidColorBrush _outlineBrush;");
popupCode.Should().Contain("private double _popupCornerRadius = 8;");
popupCode.Should().Contain("DictionaryPopupMaterial.CreateInAppAcrylicThinBrush(ThemeMode.System)");
popupCode.Should().Contain("DictionaryPopupMaterial.GetOutlineColor(ThemeMode.System)");
popupCode.Should().Contain("DefaultBackgroundColor = Colors.Transparent");
popupCode.Should().Contain("Background = _surfaceBrush");
popupCode.Should().Contain("BorderBrush = _outlineBrush");
popupCode.Should().Contain("BorderThickness = new Thickness(1)");
popupCode.Should().Contain("DictionaryPopupCornerGuard.CalculateInset(_popupCornerRadius)");
popupCode.Should().Contain("_surfaceRoot.Margin = new Thickness(guardInset);");
popupCode.Should().Contain("DictionaryPopupMaterial.ApplyTheme(_surfaceBrush, themeMode);");
popupCode.Should().Contain("_outlineBrush.Color = DictionaryPopupMaterial.GetOutlineColor(themeMode);");
popupCode.Should().MatchRegex(
    @"(?s)await _contentWebView\.EnsureCoreWebView2Async\(environment\);\s*_contentWebView\.DefaultBackgroundColor = Colors\.Transparent;");
popupCode.Should().NotContain("_surfaceBrush.Color");
materialCode.Should().Contain("TintOpacity = isDark ? 0.12 : 0.78");
materialCode.Should().Contain("TintLuminosityOpacity = isDark ? 0.18 : 0.62");
materialCode.Should().Contain("Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00)");
materialCode.Should().Contain("Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)");
materialCode.Should().NotContain("Windows.UI.Color.FromArgb(0x58, 0x24, 0x24, 0x24)");
materialCode.Should().NotContain("Windows.UI.Color.FromArgb(0xDC, 0xF8, 0xF8, 0xF8)");
```

Keep the existing assertions for exact outline colors, 8 DIP geometry,
opacity gating, no composition clip, no snapshot blur, and no `ThemeShadow`.
Remove only assertions that require a solid surface or forbid Acrylic and
transparent WebView2.

- [ ] **Step 2: Run the focused shell test and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryLookupPopup_UsesFluentFloatingCardShell"
```

Expected: FAIL because the popup still declares `SolidColorBrush`, uses the
opaque theme surface, and reapplies `_surfaceBrush.Color` after WebView2
initialization.

- [ ] **Step 3: Make Acrylic fallback colors fully opaque**

Keep the existing tint recipe in `DictionaryPopupMaterial.ApplyTheme`, but
replace its fallback assignment with:

```csharp
brush.FallbackColor = isDark
    ? Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00)
    : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
```

Do not change `TryApplyDesktopAcrylicThin`, `DictionaryDesktopAcrylicThinBackdrop`,
or window-level material behavior.

- [ ] **Step 4: Use Acrylic and transparent WebView2 in `DictionaryLookupPopup`**

Change the field and constructor initialization:

```csharp
private readonly AcrylicBrush _surfaceBrush;
private readonly SolidColorBrush _outlineBrush;
```

```csharp
_surfaceBrush = DictionaryPopupMaterial.CreateInAppAcrylicThinBrush(ThemeMode.System);
var initialOutlineColor = DictionaryPopupMaterial.GetOutlineColor(ThemeMode.System);
_outlineBrush = new SolidColorBrush(initialOutlineColor);

_contentWebView = new WebView2
{
    DefaultBackgroundColor = Colors.Transparent,
    IsTabStop = false,
    UseSystemFocusVisuals = false,
};
```

Synchronize material and outline without assigning an opaque WebView color:

```csharp
private void ApplySurfaceTheme(ThemeMode themeMode)
{
    DictionaryPopupMaterial.ApplyTheme(_surfaceBrush, themeMode);
    _outlineBrush.Color = DictionaryPopupMaterial.GetOutlineColor(themeMode);
    _contentWebView.DefaultBackgroundColor = Colors.Transparent;
}
```

Immediately after controller initialization, reapply transparency:

```csharp
await _contentWebView.EnsureCoreWebView2Async(environment);
_contentWebView.DefaultBackgroundColor = Colors.Transparent;
```

Do not change `UpdatePopupShellGeometry`, `SetPopupCornerRadius`, presentation
modes, `WarmAsync`, or `contentReady` handling.

- [ ] **Step 5: Run shell, guard, and first-frame tests**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryLookupPopup_UsesFluentFloatingCardShell|FullyQualifiedName~DictionaryPopupCornerGuardTests|FullyQualifiedName~DictionaryPopup_WebDocumentUsesOpaqueHostSurface|FullyQualifiedName~PopupHtmlGenerator_AutoRendersLookupEntriesAfterShellLoads"
```

Expected at this checkpoint: native shell/guard tests PASS; the old opaque web
document test may still PASS because Task 2 has not changed CSS. Record this as
an intentional cross-task ⚠️, not as completion of the Acrylic surface.

- [ ] **Step 6: Run the full suite and commit Task 1**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
```

Expected: all tests pass, with only the repository's pre-existing NU1903
advisory warning.

Commit:

```powershell
git add -- Hoshi/Views/Dictionary/DictionaryPopupMaterial.cs Hoshi/Views/Dictionary/DictionaryLookupPopup.cs Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "style: use acrylic dictionary popup shell"
```

### Task 2: Transparent Generated Popup Surface

**Files:**
- Modify: `Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs:100-120`
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs:250-270`
- Modify: `Hoshi/Services/Dictionary/PopupHtmlGenerator.cs:45-70`
- Modify: `Hoshi/Web/DictionaryPopup/popup.css:10-60, 475-495`
- Modify: `Hoshi/Web/DictionaryPopup/popup-host.html:7-23`

**Interfaces:**
- Consumes: semantic `--background-color` and `--text-color` values from `PopupHtmlGenerator`.
- Produces: `--popup-surface-color: transparent`, consumed by the actual document shell without changing semantic theme variables.

- [ ] **Step 1: Change generator and asset tests to require a transparent shell**

Rename `PopupHtmlGenerator_UsesOpaqueThemeBackgroundColors` to
`PopupHtmlGenerator_PreservesSemanticThemeColorsWithTransparentSurface` and
use these assertions:

```csharp
darkHtml.Should().Contain("--background-color: #000000;");
darkHtml.Should().Contain("--popup-surface-color: transparent;");
darkHtml.Should().Contain("background-color: var(--popup-surface-color);");
darkHtml.Should().NotContain("background-color: var(--background-color);");
darkInjection.Should().Contain(
    "document.documentElement.style.setProperty('--background-color', '#000000');");
lightHtml.Should().Contain("--background-color: #FFFFFF;");
lightHtml.Should().Contain("--popup-surface-color: transparent;");
```

Rename `DictionaryPopup_WebDocumentUsesOpaqueHostSurface` to
`DictionaryPopup_WebDocumentUsesTransparentAcrylicSurface` and require:

```csharp
popupCss.Should().Contain("--popup-surface-color: transparent;");
popupCss.Should().MatchRegex(
    @"(?s)html,\s*body\s*\{[^}]*background-color:\s*var\(--popup-surface-color\)\s*!important;");
popupCss.Should().MatchRegex(
    @"(?s)#popup-viewport\s*\{[^}]*overflow-y:\s*auto;[^}]*background-color:\s*var\(--popup-surface-color\);");
popupCss.Should().MatchRegex(
    @"(?s)\.overlay\s*\{[^}]*background-color:\s*var\(--popup-surface-color\);");
popupCss.Should().Contain("--background-color: #FFFFFF;");
popupCss.Should().Contain("--background-color: #000000;");
```

Also load `popup-host.html` in the asset test and require its inline shell to
declare and use `--popup-surface-color: transparent`.

- [ ] **Step 2: Run the focused generator and asset tests and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~PopupHtmlGenerator_PreservesSemanticThemeColorsWithTransparentSurface|FullyQualifiedName~DictionaryPopup_WebDocumentUsesTransparentAcrylicSurface"
```

Expected: FAIL because generated HTML, CSS, viewport, overlay, and the static
host still paint opaque theme surfaces.

- [ ] **Step 3: Add the dedicated transparent surface variable to CSS**

Add to the root variable block in `popup.css`:

```css
--popup-surface-color: transparent;
```

Change only shell-paint declarations:

```css
html,
body {
    /* existing variables and properties */
    background-color: var(--popup-surface-color) !important;
}

#popup-viewport {
    /* existing geometry, scrolling, and padding */
    background-color: var(--popup-surface-color);
}

.overlay {
    /* existing positioning and scrolling */
    background-color: var(--popup-surface-color);
}
```

Do not change `.glossary-group`, any `--popup-card-*` variable, or any
`::-webkit-scrollbar*` selector/declaration.

- [ ] **Step 4: Make generated and static HTML shell backgrounds transparent**

In `PopupHtmlGenerator.GenerateHtml`, preserve `--background-color` and add:

```csharp
html, body {{
    --background-color: {bgColor};
    --popup-surface-color: transparent;
    --text-color: {textColor};
    background-color: var(--popup-surface-color);
    color: var(--text-color);
}}
```

Do not change `GetThemeColors` or the injection script assignment to
`--background-color`.

Mirror the same variable and `background-color` declaration in
`popup-host.html`. Keep its semantic light/dark theme values.

- [ ] **Step 5: Run transparent-surface, card, scrollbar, and first-frame tests**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~PopupHtmlGenerator_PreservesSemanticThemeColorsWithTransparentSurface|FullyQualifiedName~DictionaryPopup_WebDocumentUsesTransparentAcrylicSurface|FullyQualifiedName~DictionaryPopup_UsesDesktopReferenceDictionaryCards|FullyQualifiedName~DictionaryPopup_UsesPanningIndicatorScrollbarChrome|FullyQualifiedName~PopupHtmlGenerator_AutoRendersLookupEntriesAfterShellLoads"
```

Expected: all selected tests pass.

- [ ] **Step 6: Prove scrollbar and card blocks did not change**

Compare Task 2's diff against its base and require no changed line matching:

```powershell
git diff --unified=0 HEAD -- Hoshi/Web/DictionaryPopup/popup.css | Select-String -Pattern 'popup-scrollbar|webkit-scrollbar|popup-card|glossary-group'
```

Expected: no output for scrollbar/card selectors or variables. Inspect the
complete CSS diff to confirm only root/viewport/overlay surface declarations
changed.

- [ ] **Step 7: Run the full suite and commit Task 2**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
```

Expected: all tests pass.

Commit:

```powershell
git add -- Hoshi/Services/Dictionary/PopupHtmlGenerator.cs Hoshi/Web/DictionaryPopup/popup.css Hoshi/Web/DictionaryPopup/popup-host.html Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "style: make dictionary popup surface transparent"
```

### Task 3: Acrylic Capability Gate and Final Verification

**Files:**
- Verify only; do not add screenshot-blur or alternate material code.

**Interfaces:**
- Consumes: the native Acrylic shell and transparent WebView/document from Tasks 1 and 2.
- Produces: a pass/fail capability report for native sampling plus complete automated and startup evidence.

- [ ] **Step 1: Run syntax, dictionary, build, and full tests fresh**

Run:

```powershell
node --check Hoshi/Web/DictionaryPopup/popup.js
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Dictionary"
dotnet build -p:Platform=x64
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
```

Expected: JavaScript exit 0, dictionary tests pass, build has 0 errors, and
the full test suite has 0 failures.

- [ ] **Step 2: Launch the exact worktree build**

Run:

```powershell
.\build-and-run.ps1
```

Use objective process/window evidence to confirm the running `Hoshi.exe` path
belongs to the isolated worktree, has a nonzero top-level window, and is
responsive.

- [ ] **Step 3: Execute the native Acrylic capability gate**

Open or restore a Japanese book with high-contrast vertical text. Create a
visible selection or lookup highlight beneath the popup. Show a root popup and
a nested popup.

Pass only when all of these are visually true:

- reader glyph shapes and selection color are visible as blurred/diffused
  context through the popup;
- reader text is not sharply readable through the popup;
- the effect is more than a flat translucent tint;
- all four native corners are free of black/white wedges;
- popup initialization and nested creation show no opaque flash.

If any condition fails, record screenshots and exact behavior, mark the task
`BLOCKED` by the platform capability gate, leave the branch unmerged, and stop.
Do not implement capture blur or composition-controller fallback.

- [ ] **Step 4: Verify themes and existing behavior if the gate passes**

Verify root and nested popups in light and dark themes. Check scrolling,
resize/reflow, warm reuse, Shift-hover, card readability, outline visibility,
and the unchanged scrollbar. Repeat at the active DPI and one additional
available scaling configuration without changing system settings solely for
this test.

- [ ] **Step 5: Verify fallback contract without changing user settings**

Inspect automated assertions for exact opaque fallback colors. If transparency
effects are already unavailable in the environment, confirm the popup falls
back to white/black. Do not change Windows transparency, security, power, or
accessibility settings as part of verification.

- [ ] **Step 6: Inspect the full diff and final state**

Run:

```powershell
git diff --check
git diff --stat $(git merge-base main HEAD)..HEAD
git diff $(git merge-base main HEAD)..HEAD -- Hoshi/Views/Dictionary/DictionaryPopupMaterial.cs Hoshi/Views/Dictionary/DictionaryLookupPopup.cs Hoshi/Services/Dictionary/PopupHtmlGenerator.cs Hoshi/Web/DictionaryPopup/popup.css Hoshi/Web/DictionaryPopup/popup-host.html Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git status --short
```

Expected: no whitespace errors, only the planned files changed, and no
tracked/untracked implementation residue. Leave the final verified Hoshi
instance running only when the capability gate passes.
