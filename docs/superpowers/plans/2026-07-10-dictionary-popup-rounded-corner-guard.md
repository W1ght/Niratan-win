# Dictionary Popup Rounded-Corner Guard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve the dictionary popup's visible rounded silhouette while preventing WinUI 3 WebView2 from painting black corner pixels over video or reader content.

**Architecture:** A native WinUI `Border` owns the outer rounded shape. A pure geometry helper computes the minimum integer guard inset for each radius, and the rectangular WebView2/content root stays inside that inset with an opaque background matching the native shell. The web document remains opaque, so WebView2 never relies on unsupported transparency.

**Tech Stack:** C#/.NET 10, WinUI 3, Windows App SDK 2.0.1, WebView2, xUnit v3, FluentAssertions, CSS.

## Global Constraints

- Target Windows 10+ x64; do not build ARM64 by default.
- Build with `dotnet build -p:Platform=x64`.
- Test with `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64`.
- Do not modify `native/hoshidicts/`.
- Keep WebView2 as the dictionary-body renderer; do not replace Yomitan structured content with WinUI text controls.
- Keep lookup, database, dictionary, IPC, popup-stack, and Anki behavior unchanged.
- Keep the normal in-app popup radius at 12 DIP, the naked floating radius at 8 DIP, and the standalone content radius at 0 DIP.
- Use a 4 DIP guard inset for 12 DIP, a 3 DIP guard inset for 8 DIP, and no inset for 0 DIP.
- Preserve existing uncommitted work and stage only files explicitly listed by each task.

---

## File Map

- Create `Niratan/Views/Dictionary/DictionaryPopupCornerGuard.cs`: pure radius-to-inset geometry; no XAML or service dependencies.
- Create `Niratan.Tests/Views/Dictionary/DictionaryPopupCornerGuardTests.cs`: real unit coverage for the three supported presentation radii and defensive negative input.
- Modify `Niratan/Views/Dictionary/DictionaryLookupPopup.cs`: native rounded shell, guard inset, opaque synchronized WebView2 background, and presentation-mode updates.
- Modify `Niratan/Views/Dictionary/DictionaryPopupMaterial.cs`: canonical popup colors shared by native and web surfaces.
- Modify `Niratan/Web/DictionaryPopup/popup.css`: opaque root document background; no transparent outer corners.
- Modify `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs`: source-level integration assertions tying the WinUI shell and web asset together.
- Modify `docs/CHANGELOG.md`: record the root cause and solution after verification.

---

### Task 1: Add Tested Corner-Guard Geometry

**Files:**
- Create: `Niratan/Views/Dictionary/DictionaryPopupCornerGuard.cs`
- Create: `Niratan.Tests/Views/Dictionary/DictionaryPopupCornerGuardTests.cs`

**Interfaces:**
- Consumes: a popup corner radius in DIPs as `double`.
- Produces: `DictionaryPopupCornerGuard.CalculateInset(double radius) -> double`, the smallest whole-DIP inset that contains a rectangular WebView2 inside the rounded silhouette.

- [ ] **Step 1: Write the failing geometry test**

Create `Niratan.Tests/Views/Dictionary/DictionaryPopupCornerGuardTests.cs`:

```csharp
using FluentAssertions;
using Niratan.Views.Dictionary;

namespace Niratan.Tests.Views.Dictionary;

public sealed class DictionaryPopupCornerGuardTests
{
    [Theory]
    [InlineData(12, 4)]
    [InlineData(8, 3)]
    [InlineData(0, 0)]
    [InlineData(-4, 0)]
    public void CalculateInset_ContainsRectangularContentInsideRoundedShell(
        double radius,
        double expectedInset)
    {
        DictionaryPopupCornerGuard.CalculateInset(radius).Should().Be(expectedInset);
    }
}
```

- [ ] **Step 2: Run the test and verify RED**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopupCornerGuardTests"
```

Expected: FAIL to compile because `DictionaryPopupCornerGuard` does not exist. This is the missing production behavior, not a test typo.

- [ ] **Step 3: Implement the minimum pure geometry helper**

Create `Niratan/Views/Dictionary/DictionaryPopupCornerGuard.cs`:

```csharp
using System;

namespace Niratan.Views.Dictionary;

internal static class DictionaryPopupCornerGuard
{
    public static double CalculateInset(double radius)
    {
        if (radius <= 0)
            return 0;

        return Math.Ceiling(radius * (1 - 1 / Math.Sqrt(2)));
    }
}
```

- [ ] **Step 4: Run the focused test and verify GREEN**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopupCornerGuardTests"
```

Expected: PASS, four cases.

- [ ] **Step 5: Commit only the helper and its test**

```powershell
git add -- Niratan/Views/Dictionary/DictionaryPopupCornerGuard.cs Niratan.Tests/Views/Dictionary/DictionaryPopupCornerGuardTests.cs
git diff --cached --check
git commit -m "test: define dictionary popup corner guard geometry"
```

Expected: the commit contains exactly the two new files.

---

### Task 2: Replace Unsupported Transparent Corners with the Native Guard Shell

**Files:**
- Modify: `Niratan/Views/Dictionary/DictionaryLookupPopup.cs`
- Modify: `Niratan/Views/Dictionary/DictionaryPopupMaterial.cs`
- Modify: `Niratan/Web/DictionaryPopup/popup.css`
- Modify: `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Consumes: `DictionaryPopupCornerGuard.CalculateInset(double radius)` from Task 1 and `DictionaryPopupMaterial.GetOpaqueSurfaceColor(ThemeMode)`.
- Produces: `ApplySurfaceTheme(ThemeMode themeMode)` and `UpdatePopupShellGeometry()` inside `DictionaryLookupPopup`; the native `Border`, WebView2 default background, and HTML theme share one opaque color.

- [ ] **Step 1: Change the web-surface test to require an opaque host**

Replace `DictionaryPopup_WebDocumentUsesOpaqueClippedThemeSurface` with:

```csharp
[Fact]
public void DictionaryPopup_WebDocumentUsesOpaqueHostSurface()
{
    var popupCss = File.ReadAllText(
        Path.Combine(ProjectRoot, "Web", "DictionaryPopup", "popup.css")
    );

    popupCss.Should().MatchRegex(
        @"(?s)html,\s*body\s*\{[^}]*background-color:\s*var\(--background-color\)\s*!important;");
    popupCss.Should().NotContain("background-color: transparent !important");
    popupCss.Should().MatchRegex(
        @"(?s)#popup-viewport\s*\{[^}]*overflow-y:\s*auto;[^}]*background-color:\s*var\(--background-color\);");
    popupCss.Should().MatchRegex(
        @"(?s)::-webkit-scrollbar\s*\{[^}]*background:\s*transparent;");
    popupCss.Should().MatchRegex(
        @"(?s)::-webkit-scrollbar-track,[^{]*::-webkit-scrollbar-corner\s*\{[^}]*background:\s*transparent;");
}
```

In `DictionaryLookupPopup_UsesFluentFloatingCardShell`, replace the assertions about transparent WebView2 and Composition clipping with:

```csharp
popupCode.Should().Contain("private readonly SolidColorBrush _surfaceBrush;");
popupCode.Should().Contain("DictionaryPopupMaterial.GetOpaqueSurfaceColor(ThemeMode.System)");
popupCode.Should().Contain("DefaultBackgroundColor = initialSurfaceColor");
popupCode.Should().Contain("Background = _surfaceBrush");
popupCode.Should().Contain("DictionaryPopupCornerGuard.CalculateInset(_popupCornerRadius)");
popupCode.Should().Contain("_surfaceRoot.Margin = new Thickness(guardInset);");
popupCode.Should().Contain("VisualRoot.CornerRadius = new CornerRadius(_popupCornerRadius);");
popupCode.Should().Contain("ApplySurfaceTheme(themeMode);");
popupCode.Should().Contain("_contentWebView.DefaultBackgroundColor = _surfaceBrush.Color;");
popupCode.Should().NotContain("CompositionGeometricClip");
popupCode.Should().NotContain("CompositionRoundedRectangleGeometry");
popupCode.Should().NotContain("_surfaceVisual.Clip");
popupCode.Should().NotContain("DefaultBackgroundColor = Colors.Transparent");
popupCode.Should().NotContain("_contentWebView.Margin = new Thickness(-1);\n        SetPopupCornerRadius(8);");

materialCode.Should().Contain(
    "Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00)");
materialCode.Should().Contain(
    "Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)");
```

Also update the later global-popup shell assertions to require `VisualRoot.CornerRadius`, `VisualRoot.Background`, the radius-to-inset helper, and an opaque default background; remove its assertions that require those members to be absent or transparent.

- [ ] **Step 2: Run the focused integration tests and verify RED**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopup_WebDocumentUsesOpaqueHostSurface|FullyQualifiedName~DictionaryLookupPopup_UsesFluentFloatingCardShell|FullyQualifiedName~GlobalLookupPopup"
```

Expected: FAIL because CSS still forces transparent root pixels, WebView2 still defaults to transparent, and the native guard shell does not yet exist.

- [ ] **Step 3: Make native and web popup surface colors identical**

Change `DictionaryPopupMaterial.GetOpaqueSurfaceColor` to:

```csharp
public static Windows.UI.Color GetOpaqueSurfaceColor(ThemeMode themeMode)
{
    return IsThemeDark(themeMode)
        ? Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00)
        : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
}
```

This exactly matches `PopupHtmlGenerator`'s existing `#000000` and `#FFFFFF` values.

- [ ] **Step 4: Replace the parent Composition clip with the native guard shell**

In `DictionaryLookupPopup.cs`:

1. Remove `System.Numerics`, `Microsoft.UI.Composition`, and `Microsoft.UI.Xaml.Hosting` imports.
2. Remove `_surfaceVisual`, `_contentClip`, `_contentClipGeometry`, the `SizeChanged` handler, and `UpdateContentClip()`.
3. Add the brush field and initialize the host with the canonical color:

```csharp
private readonly SolidColorBrush _surfaceBrush;
```

```csharp
var initialSurfaceColor =
    DictionaryPopupMaterial.GetOpaqueSurfaceColor(ThemeMode.System);
_surfaceBrush = new SolidColorBrush(initialSurfaceColor);

_contentWebView = new WebView2
{
    DefaultBackgroundColor = initialSurfaceColor,
    IsTabStop = false,
    UseSystemFocusVisuals = false,
};
```

4. Construct `VisualRoot` as the native shell and update its geometry:

```csharp
VisualRoot = new Border
{
    Background = _surfaceBrush,
    CornerRadius = new CornerRadius(_popupCornerRadius),
    Child = _surfaceRoot,
    Visibility = Visibility.Visible,
    Opacity = 0,
    IsHitTestVisible = false,
};
UpdatePopupShellGeometry();
```

5. Replace the geometry methods with:

```csharp
private void SetPopupCornerRadius(double radius)
{
    _popupCornerRadius = Math.Max(0, radius);
    UpdatePopupShellGeometry();
    _ = ApplyPopupCornerRadiusToWebViewAsync();
}

private void UpdatePopupShellGeometry()
{
    var guardInset = DictionaryPopupCornerGuard.CalculateInset(_popupCornerRadius);
    VisualRoot.CornerRadius = new CornerRadius(_popupCornerRadius);
    _surfaceRoot.Margin = new Thickness(guardInset);
}

private void ApplySurfaceTheme(ThemeMode themeMode)
{
    var surfaceColor = DictionaryPopupMaterial.GetOpaqueSurfaceColor(themeMode);
    _surfaceBrush.Color = surfaceColor;
    _contentWebView.DefaultBackgroundColor = surfaceColor;
}
```

6. Apply theme state before initialization and every content injection:

```csharp
public async Task WarmAsync(
    ThemeMode themeMode = ThemeMode.System,
    AudioSettings? audioSettings = null,
    AnkiSettings? ankiSettings = null)
{
    ApplySurfaceTheme(themeMode);
    if (_isWarmed) return;
    // Existing warm-up body follows unchanged.
}
```

```csharp
public async Task ShowResultsWarmAsync(
    List<DictionaryLookupResult> results,
    Dictionary<string, string> styles,
    DictionaryDisplaySettings displaySettings,
    ThemeMode themeMode,
    AudioSettings? audioSettings = null,
    AnkiSettings? ankiSettings = null,
    string? traceId = null)
{
    ApplySurfaceTheme(themeMode);
    // Existing injection body follows unchanged.
}
```

7. Keep the standalone window's existing `-1` WebView margin because its HWND region performs the outer clipping. Do not use that negative margin for the 8 DIP naked presentation:

```csharp
public void UseStandaloneWindowVisuals()
{
    _contentWebView.Margin = new Thickness(-1);
    SetPopupCornerRadius(0);
}

public void UseNakedFloatingWindowVisuals()
{
    _contentWebView.Margin = new Thickness(0);
    SetPopupCornerRadius(8);
}
```

8. After `EnsureCoreWebView2Async`, reapply the brush color instead of transparency:

```csharp
await _contentWebView.EnsureCoreWebView2Async(environment);
_contentWebView.DefaultBackgroundColor = _surfaceBrush.Color;
```

9. Remove the obsolete `UpdateContentClip()` call from `SetSize`.

- [ ] **Step 5: Make the web document root opaque**

In `Niratan/Web/DictionaryPopup/popup.css`, change only the root background declaration:

```css
html,
body {
    /* Existing variables and declarations stay unchanged. */
    background-color: var(--background-color) !important;
}
```

Keep scrollbar tracks transparent; they are contained inside the already opaque WebView2 surface and do not create outer-corner transparency.

- [ ] **Step 6: Run focused tests and verify GREEN**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopupCornerGuardTests|FullyQualifiedName~DictionaryPopup_WebDocumentUsesOpaqueHostSurface|FullyQualifiedName~DictionaryLookupPopup_UsesFluentFloatingCardShell|FullyQualifiedName~GlobalLookupPopup"
```

Expected: PASS with no build errors or failed assertions.

- [ ] **Step 7: Commit only the guard-shell implementation**

```powershell
git add -- Niratan/Views/Dictionary/DictionaryLookupPopup.cs Niratan/Views/Dictionary/DictionaryPopupMaterial.cs Niratan/Web/DictionaryPopup/popup.css Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git diff --cached --check
git commit -m "fix: guard dictionary popup rounded corners"
```

Expected: no unrelated files are staged. If a listed file contains pre-existing task-related hunks, inspect the complete staged diff before committing and retain all behavior required by the confirmed spec.

---

### Task 3: Verify the Popup and Record the Fix

**Files:**
- Modify: `docs/CHANGELOG.md`

**Interfaces:**
- Consumes: the completed native guard shell and existing Niratan build/run workflow.
- Produces: build/test evidence, runtime visual evidence, and a concise root-cause record.

- [ ] **Step 1: Run dictionary-focused regression tests**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Dictionary"
```

Expected: PASS; no dictionary test failures.

- [ ] **Step 2: Run the complete x64 test project**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64
```

Expected: PASS; no failed tests.

- [ ] **Step 3: Build the x64 application**

```powershell
dotnet build -p:Platform=x64
```

Expected: `Build succeeded.` with zero errors.

- [ ] **Step 4: Launch through the project workflow**

```powershell
.\build-and-run.ps1
```

Expected: the unpackaged x64 Niratan process opens a responsive top-level window. Leave the final verified instance running.

- [ ] **Step 5: Perform runtime visual verification**

Use the existing video lookup flow and a bright frame like the supplied reproduction screenshot:

1. Open the dictionary popup over video and inspect all four corners at the normal 12 DIP presentation.
2. Confirm the video remains visible outside the rounded silhouette and no black or white square wedges appear.
3. Scroll the popup and open one nested lookup; confirm the shell stays rounded and Shift-hover remains responsive.
4. Verify light and dark popup themes.
5. Resize the window and repeat at the current high-DPI scale.
6. Open the global popup presentation and confirm its HWND-rounded 8 DIP outer shape remains intact.

Expected: all six checks pass. If a black pixel remains, capture a new screenshot and inspect whether it is outside the native shell or inside the opaque WebView2 before changing another variable.

- [ ] **Step 6: Add the changelog entry**

Insert this issue near the top of `docs/CHANGELOG.md`:

```markdown
## popup 圆角出现黑色角块

**原因**：
- WinUI 3 WebView2 不能把透明网页像素合成到同窗口的兄弟 XAML 视频内容上；圆角外的透明像素会退回到视频窗口的黑色宿主背景。
- 对 WebView2 父级 Grid 添加 Composition 圆角裁剪不能改变 WebView2 的透明合成限制。

**解决**：
- 使用 WinUI 原生 Border 绘制 popup 外轮廓，并按圆角半径计算 12→4 DIP、8→3 DIP 的安全内缩，使矩形 WebView2 完全位于圆角轮廓内。
- 原生护边、WebView2 默认背景和网页根背景统一使用不透明主题色，避免初始化、导航和主题切换期间露出黑色 backing surface。

---
```

- [ ] **Step 7: Commit the verified changelog**

```powershell
git add -- docs/CHANGELOG.md
git diff --cached --check
git commit -m "docs: record popup corner guard fix"
```

Expected: the commit contains only `docs/CHANGELOG.md`.

---

## Final Verification Checklist

- [ ] `DictionaryPopupCornerGuardTests` was observed failing before the helper existed and passing afterward.
- [ ] Popup integration tests were observed failing against transparent corners and passing after the guard shell.
- [ ] Dictionary-focused and full x64 tests pass.
- [ ] x64 build succeeds.
- [ ] Niratan launches as a responsive unpackaged WinUI application.
- [ ] All four popup corners reveal the underlying bright video frame with no black/white wedges.
- [ ] 12 DIP, 8 DIP, and standalone presentations behave as specified.
- [ ] Light/dark theme, nested lookup, scrolling, resize, high DPI, and Shift-hover checks pass.
- [ ] No file under `native/hoshidicts/` changed.
