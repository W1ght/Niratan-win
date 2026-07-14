# Dictionary Popup Desktop Style Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Match the supplied Hoshi Reader Desktop popup appearance with an 8 DIP outlined black/white shell and compact theme-aware dictionary cards while preserving the existing scrollbar and popup behavior.

**Architecture:** Keep the native WinUI `Border` responsible for the popup silhouette and add a canonical theme-aware outline color beside the existing opaque surface color. Keep WebView2 opaque inside the existing corner guard, and express glossary-card colors through CSS theme variables without touching any scrollbar selector or declaration.

**Tech Stack:** C#/.NET, WinUI 3, Windows App SDK, WebView2, CSS, xUnit v3, FluentAssertions

## Global Constraints

- Use an 8 DIP outer corner radius and a 1 DIP outer outline.
- Use `#FFFFFF` / `#D1D1D6` in light mode and `#000000` / `#3A3A3C` in dark mode for the shell surface / outline.
- Apply the same shell and card treatment to root and nested dictionary popup hosts.
- Do not change scrollbar appearance, selectors, declarations, or behavior.
- Do not change popup dimensions, placement, lookup, WebView messaging, `contentReady`, nested lookup, Anki, audio, or structured dictionary rendering.
- Do not modify `native/hoshidicts/`.
- Build and test x64 only.

---

## File Structure

- `Niratan/Views/Dictionary/DictionaryPopupMaterial.cs`: canonical theme-aware popup surface and outline colors.
- `Niratan/Views/Dictionary/DictionaryLookupPopup.cs`: native shell radius, border brush, border thickness, guard geometry, and theme synchronization.
- `Niratan/Web/DictionaryPopup/popup.css`: theme tokens and visual treatment for glossary cards; scrollbar blocks remain byte-for-byte unchanged.
- `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs`: source-level regression coverage for native shell, exact theme colors, card CSS, and preserved scrollbar chrome.

### Task 1: Native 8 DIP Outlined Popup Shell

**Files:**
- Modify: `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs:1054`
- Modify: `Niratan/Views/Dictionary/DictionaryPopupMaterial.cs:43`
- Modify: `Niratan/Views/Dictionary/DictionaryLookupPopup.cs:48-170, 315-334`

**Interfaces:**
- Consumes: existing `DictionaryPopupMaterial.GetOpaqueSurfaceColor(ThemeMode)` and `DictionaryPopupCornerGuard.CalculateInset(double)`.
- Produces: `DictionaryPopupMaterial.GetOutlineColor(ThemeMode) -> Windows.UI.Color`; an 8 DIP default shell with a 1 DIP outline synchronized by `ApplySurfaceTheme(ThemeMode)`.

- [ ] **Step 1: Replace the shell regression assertions with the desired contract**

Update `DictionaryLookupPopup_UsesFluentFloatingCardShell` in `NovelReaderWebAssetTests.cs` so its shell-specific assertions include:

```csharp
popupCode.Should().Contain("private readonly SolidColorBrush _surfaceBrush;");
popupCode.Should().Contain("private readonly SolidColorBrush _outlineBrush;");
popupCode.Should().Contain("private double _popupCornerRadius = 8;");
popupCode.Should().Contain("DictionaryPopupMaterial.GetOpaqueSurfaceColor(ThemeMode.System)");
popupCode.Should().Contain("DictionaryPopupMaterial.GetOutlineColor(ThemeMode.System)");
popupCode.Should().Contain("Background = _surfaceBrush");
popupCode.Should().Contain("BorderBrush = _outlineBrush");
popupCode.Should().Contain("BorderThickness = new Thickness(1)");
popupCode.Should().Contain("DictionaryPopupCornerGuard.CalculateInset(_popupCornerRadius)");
popupCode.Should().Contain("_surfaceRoot.Margin = new Thickness(guardInset);");
popupCode.Should().Contain("VisualRoot.CornerRadius = new CornerRadius(_popupCornerRadius);");
popupCode.Should().Contain("_outlineBrush.Color = DictionaryPopupMaterial.GetOutlineColor(themeMode);");
materialCode.Should().Contain("Windows.UI.Color.FromArgb(0xFF, 0xD1, 0xD1, 0xD6)");
materialCode.Should().Contain("Windows.UI.Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3C)");
popupCode.Should().Contain("_contentWebView.DefaultBackgroundColor = surfaceColor;");
popupCode.Should().NotContain("DefaultBackgroundColor = Colors.Transparent");
```

Remove the obsolete assertions that forbid `_strokeBrush` / `BorderBrush`; keep the existing opacity, first-frame, corner-guard, non-acrylic, and non-composition assertions.

- [ ] **Step 2: Run the focused test and confirm the red state**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryLookupPopup_UsesFluentFloatingCardShell"
```

Expected: FAIL because `_outlineBrush`, `GetOutlineColor`, the 8 DIP default radius, and `BorderThickness` do not exist yet.

- [ ] **Step 3: Add the canonical outline color API**

Add this method beside `GetOpaqueSurfaceColor` in `DictionaryPopupMaterial.cs`:

```csharp
public static Windows.UI.Color GetOutlineColor(ThemeMode themeMode)
{
    return IsThemeDark(themeMode)
        ? Windows.UI.Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3C)
        : Windows.UI.Color.FromArgb(0xFF, 0xD1, 0xD1, 0xD6);
}
```

- [ ] **Step 4: Apply the native shell outline and 8 DIP default radius**

Make these focused edits in `DictionaryLookupPopup.cs`:

```csharp
private readonly SolidColorBrush _surfaceBrush;
private readonly SolidColorBrush _outlineBrush;
// ...
private double _popupCornerRadius = 8;
```

Initialize both brushes from the same initial theme:

```csharp
var initialSurfaceColor = DictionaryPopupMaterial.GetOpaqueSurfaceColor(ThemeMode.System);
var initialOutlineColor = DictionaryPopupMaterial.GetOutlineColor(ThemeMode.System);
_surfaceBrush = new SolidColorBrush(initialSurfaceColor);
_outlineBrush = new SolidColorBrush(initialOutlineColor);
```

Add the outline to `VisualRoot`:

```csharp
VisualRoot = new Border
{
    Background = _surfaceBrush,
    BorderBrush = _outlineBrush,
    BorderThickness = new Thickness(1),
    CornerRadius = new CornerRadius(_popupCornerRadius),
    Child = _surfaceRoot,
    Visibility = Visibility.Visible,
    Opacity = 0,
    IsHitTestVisible = false,
};
```

Synchronize the outline in `ApplySurfaceTheme` while leaving the WebView2 background opaque:

```csharp
private void ApplySurfaceTheme(ThemeMode themeMode)
{
    var surfaceColor = DictionaryPopupMaterial.GetOpaqueSurfaceColor(themeMode);
    _surfaceBrush.Color = surfaceColor;
    _outlineBrush.Color = DictionaryPopupMaterial.GetOutlineColor(themeMode);
    _contentWebView.DefaultBackgroundColor = surfaceColor;
}
```

Do not alter `UseStandaloneWindowVisuals`, `UseNakedFloatingWindowVisuals`, `SetPopupCornerRadius`, or `DictionaryPopupCornerGuard`; the default and naked presentations now both resolve to 8 DIP, while standalone remains square.

- [ ] **Step 5: Run focused shell and corner-guard tests**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryLookupPopup_UsesFluentFloatingCardShell|FullyQualifiedName~DictionaryPopupCornerGuardTests|FullyQualifiedName~DictionaryPopup_WebDocumentUsesOpaqueHostSurface"
```

Expected: PASS with no failed tests.

- [ ] **Step 6: Commit the native shell change**

```powershell
git add -- Niratan/Views/Dictionary/DictionaryPopupMaterial.cs Niratan/Views/Dictionary/DictionaryLookupPopup.cs Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "style: match dictionary popup desktop shell"
```

### Task 2: Theme-aware Dictionary Cards Without Scrollbar Changes

**Files:**
- Modify: `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs:229-245, 276-310`
- Modify: `Niratan/Web/DictionaryPopup/popup.css:12-24, 348-360, 563-607`

**Interfaces:**
- Consumes: the existing `data-niratan-color-scheme` theme attribute emitted by `PopupHtmlGenerator`.
- Produces: CSS variables `--popup-card-border-color`, `--popup-card-inner-highlight`, and `--popup-card-shadow-color`, consumed only by `.glossary-group`.

- [ ] **Step 1: Add the failing card-style regression assertions**

Add a focused test beside `DictionaryPopup_UsesNiratanStyleTwoColumnDictionaryCards`:

```csharp
[Fact]
public void DictionaryPopup_UsesDesktopReferenceDictionaryCards()
{
    var popupCss = File.ReadAllText(
        Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.css")
    );

    popupCss.Should().Contain("--popup-card-border-color: rgba(0, 0, 0, 0.14);");
    popupCss.Should().Contain("--popup-card-inner-highlight: rgba(255, 255, 255, 0.34);");
    popupCss.Should().Contain("--popup-card-shadow-color: rgba(0, 0, 0, 0.10);");
    popupCss.Should().MatchRegex(
        @"(?s)\.glossary-group\s*\{[^}]*border:\s*1px solid var\(--popup-card-border-color\);[^}]*border-radius:\s*8px;");
    popupCss.Should().Contain("--popup-card-border-color: rgba(255, 255, 255, 0.18);");
    popupCss.Should().Contain("inset 0 0 0 1px var(--popup-card-inner-highlight)");
    popupCss.Should().Contain("0 1px 2px var(--popup-card-shadow-color)");
}
```

Keep `DictionaryPopup_UsesPanningIndicatorScrollbarChrome` unchanged; it remains the regression contract for the original scrollbar.

- [ ] **Step 2: Run card and scrollbar tests and confirm only the card test fails**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopup_UsesDesktopReferenceDictionaryCards|FullyQualifiedName~DictionaryPopup_UsesPanningIndicatorScrollbarChrome"
```

Expected: one FAIL for missing card variables and one PASS for unchanged scrollbar chrome.

- [ ] **Step 3: Add light-theme card variables and consume them**

Add these default variables to the existing `html, body` variable block in `popup.css` without editing the scrollbar variables beneath them:

```css
--popup-card-border-color: rgba(0, 0, 0, 0.14);
--popup-card-inner-highlight: rgba(255, 255, 255, 0.34);
--popup-card-shadow-color: rgba(0, 0, 0, 0.10);
```

Change only the visual declarations in `.glossary-group`:

```css
.glossary-group {
    position: relative;
    margin-top: 5px;
    min-width: 0;
    padding: 6px 8px;
    border: 1px solid var(--popup-card-border-color);
    border-radius: 8px;
    box-shadow:
        inset 0 0 0 1px var(--popup-card-inner-highlight),
        0 1px 2px var(--popup-card-shadow-color);
}
```

- [ ] **Step 4: Add explicit dark-theme card variables**

Add the following three declarations to both existing dark-theme variable blocks: the `@media (prefers-color-scheme: dark)` `html, body` block and the `html[data-niratan-color-scheme="dark"]` block:

```css
--popup-card-border-color: rgba(255, 255, 255, 0.18);
--popup-card-inner-highlight: rgba(255, 255, 255, 0);
--popup-card-shadow-color: rgba(0, 0, 0, 0.36);
```

Delete the two now-redundant dark `.glossary-group` rules that override `border-color` and `box-shadow`. Do not edit any selector or declaration from `::-webkit-scrollbar` through `::-webkit-scrollbar-thumb:active`, or the dark scrollbar variables.

- [ ] **Step 5: Run card, scrollbar, and popup web-asset tests**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopup_UsesDesktopReferenceDictionaryCards|FullyQualifiedName~DictionaryPopup_UsesPanningIndicatorScrollbarChrome|FullyQualifiedName~DictionaryPopup_WebDocumentUsesOpaqueHostSurface|FullyQualifiedName~DictionaryPopup_UsesNiratanStyleTwoColumnDictionaryCards"
```

Expected: PASS with no failed tests.

- [ ] **Step 6: Commit the dictionary card change**

```powershell
git add -- Niratan/Web/DictionaryPopup/popup.css Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "style: match dictionary popup desktop cards"
```

### Task 3: Full Verification and Runtime Visual Check

**Files:**
- Verify only; no production files are added in this task.

**Interfaces:**
- Consumes: the completed native shell and WebView card styling from Tasks 1 and 2.
- Produces: build, automated-test, startup, and visual evidence that the style works without behavioral regressions.

- [ ] **Step 1: Run JavaScript syntax validation**

Run:

```powershell
node --check Niratan/Web/DictionaryPopup/popup.js
```

Expected: exit code 0 and no output.

- [ ] **Step 2: Run the dictionary-focused automated suite**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Dictionary"
```

Expected: PASS with no failed tests.

- [ ] **Step 3: Run the full x64 build and test commands**

Run:

```powershell
dotnet build -p:Platform=x64
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64
```

Expected: both commands exit 0 with no failed tests.

- [ ] **Step 4: Launch Niratan and verify objective startup**

Run:

```powershell
.\build-and-run.ps1
```

Expected: a responsive Niratan top-level window opens. If an existing instance locks build output, close that instance, rebuild, and relaunch.

- [ ] **Step 5: Verify popup appearance and preserved behavior**

Open `C:\Users\Wight\Downloads\哈利波特1魔法石.epub`, show a root lookup popup, then a nested lookup popup, and verify:

- both shells have an 8 DIP radius and a crisp 1 DIP outline;
- light mode is `#FFFFFF` / `#D1D1D6`, dark mode is `#000000` / `#3A3A3C`;
- glossary cards have an 8px radius and visible low-elevation outline in both themes;
- the scrollbar is visually and behaviorally unchanged;
- first-frame reveal has no black/white flash or corner wedge;
- scrolling, resize, nested lookup, and Shift-hover remain responsive.

Repeat at 100% scaling and one available high-DPI scaling configuration. Leave the final verified Niratan instance running.

- [ ] **Step 6: Inspect the final diff and repository state**

Run:

```powershell
git diff HEAD~2 --check
git diff HEAD~2 -- Niratan/Views/Dictionary/DictionaryPopupMaterial.cs Niratan/Views/Dictionary/DictionaryLookupPopup.cs Niratan/Web/DictionaryPopup/popup.css Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git status --short
```

Expected: no whitespace errors; only the intended popup style/test commits plus the pre-existing untracked `.codex/` directory are present.
