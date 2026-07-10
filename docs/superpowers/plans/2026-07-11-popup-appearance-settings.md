# Popup Appearance Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move popup presentation controls to Appearance and give every root and nested dictionary popup Niratan-aligned size, scale, action-bar, and full-width behavior.

**Architecture:** Keep `DictionaryDisplaySettings` as the persisted and profile-cloned source of truth. Normalize values through a pure constraints helper, bind them from the shared Appearance ViewModel, and pass one immutable lookup snapshot into layout, native popup chrome, and generated WebView2 content. Route structured-content links in-place for action-bar history while text selection continues to create configured child popups.

**Tech Stack:** C#/.NET, WinUI 3, Windows App SDK WebView2, CommunityToolkit.Mvvm, xUnit v3, FluentAssertions, JavaScript, CSS.

## Global Constraints

- Target Windows 10+ x64; do not build ARM64 by default.
- Build with `dotnet build -p:Platform=x64`.
- Test with `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64`.
- Do not modify `native/hoshidicts/`.
- Keep WebView2 as the popup content renderer; do not replace structured content with WinUI text controls.
- Keep View code-behind UI-only and keep persistence in the ViewModel/service layers.
- Treat popup JavaScript messages as untrusted and validate narrow, typed message bodies.
- Preserve the generation-scoped `Opacity=0`/`contentReady` reveal gate.
- Preserve existing saved size values when valid; missing fields use Niratan defaults.
- Width is `100...1400`, step `10`, default `320`.
- Height is `100...800`, step `10`, default `250`.
- Scale is `0.8...1.5`, step `0.05`, default `1.0`.
- Show Action Bar defaults to `false`; Full-width defaults to `false`.
- Every nested popup consumes all five appearance fields, including Full-width.

---

## File Structure

- Create `Hoshi/Models/Settings/DictionaryPopupAppearanceConstraints.cs`: canonical defaults, ranges, steps, and normalization.
- Create `Hoshi.Tests/Models/Settings/DictionaryPopupAppearanceConstraintsTests.cs`: real unit coverage for the settings contract.
- Create `Hoshi/Services/Dictionary/DictionaryPopupScaleCss.cs`: culture-invariant Niratan scale declarations and custom-CSS pixel transformation.
- Create `Hoshi.Tests/Services/Dictionary/DictionaryPopupScaleCssTests.cs`: focused scale and custom-CSS tests.
- Create `Hoshi/Views/Dictionary/DictionaryPopupRedirectRouter.cs`: pure classification of in-place structured links versus nested text lookups.
- Create `Hoshi.Tests/Views/Dictionary/DictionaryPopupRedirectRouterTests.cs`: routing tests independent of WinUI.
- Modify `Hoshi/Models/Settings/DictionaryDisplaySettings.cs`: add scale/action/full-width and change size defaults.
- Modify `Hoshi/ViewModels/Pages/SettingsPageViewModel.cs`: own Appearance bindings and persistence.
- Modify `Hoshi/ViewModels/Pages/DictionarySettingsPageViewModel.cs`: remove popup presentation ownership.
- Modify `Hoshi/Views/Controls/ReaderAppearanceSettingsContent.xaml`: render the Niratan Popup section.
- Modify `Hoshi/Views/Pages/DictionarySettingsPage.xaml`: remove Popup Size cards.
- Modify `Hoshi/Strings/en-US/Resources.resw` and `Hoshi/Strings/zh-CN/Resources.resw`: localize the new section, controls, and action bar.
- Modify `Hoshi/Views/Dictionary/DictionaryPopupLayoutCalculator.cs`: resolve full-width bottom placement and shared root/child dimensions.
- Modify `Hoshi/Views/Dictionary/DictionaryPopupOverlay.cs`: remove child caps, route in-place redirects, and propagate one normalized settings snapshot.
- Modify `Hoshi/Views/Dictionary/DictionaryLookupPopup.cs`: add the native action bar, navigation state, and in-place result injection.
- Modify `Hoshi/Services/Dictionary/PopupHtmlGenerator.cs`: inject scale declarations and in-place result scripts.
- Modify `Hoshi/Web/DictionaryPopup/popup.css`: consume Niratan scale variables.
- Modify `Hoshi/Web/DictionaryPopup/popup.js`: expose in-place redirect/history state without moving lookup logic into JavaScript.
- Modify focused existing tests under `Hoshi.Tests/Services/Dictionary`, `Hoshi.Tests/Services/Profiles`, and `Hoshi.Tests/Services/Novels`.
- Modify `docs/VERIFICATION.md`: record the popup appearance runtime matrix and automation identifiers.

---

### Task 1: Define and snapshot the popup appearance contract

**Files:**
- Create: `Hoshi/Models/Settings/DictionaryPopupAppearanceConstraints.cs`
- Create: `Hoshi.Tests/Models/Settings/DictionaryPopupAppearanceConstraintsTests.cs`
- Modify: `Hoshi/Models/Settings/DictionaryDisplaySettings.cs`
- Modify: `Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs`
- Modify: `Hoshi.Tests/Services/Dictionary/DictionaryPopupRequestServiceTests.cs`
- Modify: `Hoshi.Tests/Services/Profiles/ProfileSettingsStoreTests.cs`

**Interfaces:**
- Produces: `DictionaryPopupAppearanceConstraints.NormalizeWidth(int) -> int`.
- Produces: `DictionaryPopupAppearanceConstraints.NormalizeHeight(int) -> int`.
- Produces: `DictionaryPopupAppearanceConstraints.NormalizeScale(double) -> double`.
- Produces: `DictionaryDisplaySettings.PopupScale`, `.PopupActionBar`, and `.PopupFullWidth`.
- Consumed later by: Appearance ViewModel, layout, popup host, generator, profile cloning, and request snapshots.

- [ ] **Step 1: Write the failing constraints and defaults tests**

Create `Hoshi.Tests/Models/Settings/DictionaryPopupAppearanceConstraintsTests.cs`:

```csharp
using FluentAssertions;
using Hoshi.Models.Settings;

namespace Hoshi.Tests.Models.Settings;

public sealed class DictionaryPopupAppearanceConstraintsTests
{
    [Fact]
    public void Defaults_MatchNiratan()
    {
        var settings = new DictionaryDisplaySettings();

        DictionaryPopupAppearanceConstraints.MinWidth.Should().Be(100);
        DictionaryPopupAppearanceConstraints.MaxWidth.Should().Be(1400);
        DictionaryPopupAppearanceConstraints.WidthStep.Should().Be(10);
        DictionaryPopupAppearanceConstraints.MinHeight.Should().Be(100);
        DictionaryPopupAppearanceConstraints.MaxHeight.Should().Be(800);
        DictionaryPopupAppearanceConstraints.HeightStep.Should().Be(10);
        DictionaryPopupAppearanceConstraints.MinScale.Should().Be(0.8);
        DictionaryPopupAppearanceConstraints.MaxScale.Should().Be(1.5);
        DictionaryPopupAppearanceConstraints.ScaleStep.Should().Be(0.05);
        settings.PopupMaxWidth.Should().Be(320);
        settings.PopupMaxHeight.Should().Be(250);
        settings.PopupScale.Should().Be(1.0);
        settings.PopupActionBar.Should().BeFalse();
        settings.PopupFullWidth.Should().BeFalse();
    }

    [Theory]
    [InlineData(50, 100)]
    [InlineData(320, 320)]
    [InlineData(1600, 1400)]
    public void NormalizeWidth_ClampsToNiratanRange(int value, int expected) =>
        DictionaryPopupAppearanceConstraints.NormalizeWidth(value).Should().Be(expected);

    [Theory]
    [InlineData(50, 100)]
    [InlineData(250, 250)]
    [InlineData(820, 800)]
    public void NormalizeHeight_ClampsToNiratanRange(int value, int expected) =>
        DictionaryPopupAppearanceConstraints.NormalizeHeight(value).Should().Be(expected);

    [Theory]
    [InlineData(0.5, 0.8)]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 1.5)]
    public void NormalizeScale_ClampsToNiratanRange(double value, double expected) =>
        DictionaryPopupAppearanceConstraints.NormalizeScale(value).Should().Be(expected);

    [Fact]
    public void NormalizeScale_NonFiniteValueUsesDefault()
    {
        DictionaryPopupAppearanceConstraints.NormalizeScale(double.NaN).Should().Be(1.0);
        DictionaryPopupAppearanceConstraints.NormalizeScale(double.PositiveInfinity).Should().Be(1.0);
    }
}
```

Update `DictionaryDisplaySettings_DefaultsMatchHoshiReaderAndroid` to rename it
`DictionaryDisplaySettings_DefaultsMatchHoshiAndNiratan` and assert the five
values above instead of `560` and `420`.

Extend `CreateAsync_CapturesSettingsSnapshotForPopupDisplay` with:

```csharp
DictionaryDisplaySettings = new DictionaryDisplaySettings(
    CollapsedDictionaries: ["A"],
    CustomCSS: ".term{}",
    MaxResults: 5,
    PopupMaxWidth: 1200,
    PopupMaxHeight: 700,
    PopupScale: 1.25,
    PopupActionBar: true,
    PopupFullWidth: true),
```

and assertions for all five values on `request.DisplaySettings` after the
source settings object has been replaced.

In `ActivateAsync_PersistsCurrentProfileAndLoadsTargetProfile`, assign the
edited default profile this complete popup snapshot:

```csharp
settings.Current.DictionaryDisplaySettings = new DictionaryDisplaySettings(
    MaxResults: 9,
    PopupMaxWidth: 1200,
    PopupMaxHeight: 700,
    PopupScale: 1.25,
    PopupActionBar: true,
    PopupFullWidth: true);
```

After switching back to `default-ja`, assert:

```csharp
settings.Current.DictionaryDisplaySettings.Should().Match<DictionaryDisplaySettings>(value =>
    value.MaxResults == 9
    && value.PopupMaxWidth == 1200
    && value.PopupMaxHeight == 700
    && value.PopupScale == 1.25
    && value.PopupActionBar
    && value.PopupFullWidth);
```

- [ ] **Step 2: Run the tests to verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopupAppearanceConstraintsTests|FullyQualifiedName~DictionaryDisplaySettings_Defaults|FullyQualifiedName~CreateAsync_CapturesSettingsSnapshotForPopupDisplay|FullyQualifiedName~ProfileSettingsStoreTests"
```

Expected: compilation fails because the constraints type and three settings
properties do not exist, and existing defaults are still `560 x 420`.

- [ ] **Step 3: Add the minimal settings contract**

Create `DictionaryPopupAppearanceConstraints.cs`:

```csharp
using System;

namespace Hoshi.Models.Settings;

internal static class DictionaryPopupAppearanceConstraints
{
    public const int MinWidth = 100;
    public const int MaxWidth = 1400;
    public const int WidthStep = 10;
    public const int DefaultWidth = 320;
    public const int MinHeight = 100;
    public const int MaxHeight = 800;
    public const int HeightStep = 10;
    public const int DefaultHeight = 250;
    public const double MinScale = 0.8;
    public const double MaxScale = 1.5;
    public const double ScaleStep = 0.05;
    public const double DefaultScale = 1.0;

    public static int NormalizeWidth(int value) => Math.Clamp(value, MinWidth, MaxWidth);
    public static int NormalizeHeight(int value) => Math.Clamp(value, MinHeight, MaxHeight);
    public static double NormalizeScale(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, MinScale, MaxScale) : DefaultScale;
}
```

Change the end of the `DictionaryDisplaySettings` primary constructor to:

```csharp
int ScanLength = 16,
int PopupMaxWidth = 320,
int PopupMaxHeight = 250,
double PopupScale = 1.0,
bool PopupActionBar = false,
bool PopupFullWidth = false
```

No custom clone code is required for scalar record fields. Keep the existing
`with` clones in `ProfileSettingsStore` and `DictionaryPopupRequestService`,
and prove their behavior with the snapshot tests.

- [ ] **Step 4: Run focused tests to verify GREEN**

Run the Step 2 command. Expected: all selected tests pass.

- [ ] **Step 5: Commit the contract**

```powershell
git add -- Hoshi/Models/Settings/DictionaryDisplaySettings.cs Hoshi/Models/Settings/DictionaryPopupAppearanceConstraints.cs Hoshi.Tests/Models/Settings/DictionaryPopupAppearanceConstraintsTests.cs Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs Hoshi.Tests/Services/Dictionary/DictionaryPopupRequestServiceTests.cs Hoshi.Tests/Services/Profiles/ProfileSettingsStoreTests.cs
git commit -m "feat: define popup appearance settings"
```

---

### Task 2: Move popup controls from Dictionary to Appearance

**Files:**
- Modify: `Hoshi/ViewModels/Pages/SettingsPageViewModel.cs`
- Modify: `Hoshi/ViewModels/Pages/DictionarySettingsPageViewModel.cs`
- Modify: `Hoshi/Views/Controls/ReaderAppearanceSettingsContent.xaml`
- Modify: `Hoshi/Views/Pages/DictionarySettingsPage.xaml`
- Modify: `Hoshi/Strings/en-US/Resources.resw`
- Modify: `Hoshi/Strings/zh-CN/Resources.resw`
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Consumes: `DictionaryPopupAppearanceConstraints` and `DictionaryDisplaySettings` from Task 1.
- Produces: two-way `SettingsPageViewModel` properties `PopupMaxWidth`,
  `PopupMaxHeight`, `PopupScale`, `PopupActionBar`, and `PopupFullWidth`.
- Produces: read-only display strings `PopupMaxWidthText`,
  `PopupMaxHeightText`, and `PopupScaleText`.

- [ ] **Step 1: Write the failing Appearance ownership asset test**

Add this test to `NovelReaderWebAssetTests.cs`:

```csharp
[Fact]
public void PopupAppearanceSettings_AreOwnedByAppearanceAndMatchNiratanControls()
{
    var appearanceXaml = File.ReadAllText(Path.Combine(
        ProjectRoot, "Views", "Controls", "ReaderAppearanceSettingsContent.xaml"));
    var dictionaryXaml = File.ReadAllText(Path.Combine(
        ProjectRoot, "Views", "Pages", "DictionarySettingsPage.xaml"));
    var appearanceViewModel = File.ReadAllText(Path.Combine(
        ProjectRoot, "ViewModels", "Pages", "SettingsPageViewModel.cs"));
    var dictionaryViewModel = File.ReadAllText(Path.Combine(
        ProjectRoot, "ViewModels", "Pages", "DictionarySettingsPageViewModel.cs"));

    appearanceXaml.Should().Contain("x:Uid=\"PopupAppearanceSectionHeader\"");
    appearanceXaml.Should().Contain("Minimum=\"100\" Maximum=\"1400\"");
    appearanceXaml.Should().Contain("Minimum=\"100\" Maximum=\"800\"");
    appearanceXaml.Should().Contain("Minimum=\"0.8\" Maximum=\"1.5\"");
    appearanceXaml.Should().Contain("AutomationProperties.AutomationId=\"PopupActionBarToggle\"");
    appearanceXaml.Should().Contain("AutomationProperties.AutomationId=\"PopupFullWidthToggle\"");
    appearanceViewModel.Should().Contain("partial double PopupScale");
    appearanceViewModel.Should().Contain("current with { PopupFullWidth = value }");

    dictionaryXaml.Should().NotContain("DictionaryPopupMaxWidthCard");
    dictionaryXaml.Should().NotContain("DictionaryPopupMaxHeightCard");
    dictionaryViewModel.Should().NotContain("PopupMaxWidth");
    dictionaryViewModel.Should().NotContain("PopupMaxHeight");
}
```

Extend the resource-key contract test to require:

```csharp
"PopupAppearanceSectionHeader.Text",
"PopupWidthCard.Header",
"PopupWidthCard.Description",
"PopupHeightCard.Header",
"PopupHeightCard.Description",
"PopupScaleCard.Header",
"PopupScaleCard.Description",
"PopupActionBarCard.Header",
"PopupActionBarCard.Description",
"PopupFullWidthCard.Header",
"PopupFullWidthCard.Description",
```

- [ ] **Step 2: Run the asset test to verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~PopupAppearanceSettings_AreOwnedByAppearanceAndMatchNiratanControls|FullyQualifiedName~SettingsResourceKeys"
```

Expected: the Appearance XAML and ViewModel assertions fail because popup
controls still live on Dictionary settings.

- [ ] **Step 3: Add ViewModel properties and persistence**

Add generated properties near the other Appearance properties:

```csharp
[ObservableProperty]
public partial int PopupMaxWidth { get; set; }

[ObservableProperty]
public partial int PopupMaxHeight { get; set; }

[ObservableProperty]
public partial double PopupScale { get; set; }

[ObservableProperty]
public partial bool PopupActionBar { get; set; }

[ObservableProperty]
public partial bool PopupFullWidth { get; set; }

public string PopupMaxWidthText => $"{PopupMaxWidth} px";
public string PopupMaxHeightText => $"{PopupMaxHeight} px";
public string PopupScaleText => PopupScale.ToString("0.00", CultureInfo.InvariantCulture);
```

Add `using System.Globalization;`. In `LoadSettingsAsync`, before clearing
`_isInitializing`, load a normalized snapshot:

```csharp
var popup = _settingsService.Current.DictionaryDisplaySettings;
PopupMaxWidth = DictionaryPopupAppearanceConstraints.NormalizeWidth(popup.PopupMaxWidth);
PopupMaxHeight = DictionaryPopupAppearanceConstraints.NormalizeHeight(popup.PopupMaxHeight);
PopupScale = DictionaryPopupAppearanceConstraints.NormalizeScale(popup.PopupScale);
PopupActionBar = popup.PopupActionBar;
PopupFullWidth = popup.PopupFullWidth;
```

Add one update helper and change handlers:

```csharp
private void ApplyPopupSetting(Func<DictionaryDisplaySettings, DictionaryDisplaySettings> update)
{
    if (_isInitializing) return;
    var current = _settingsService.Current.DictionaryDisplaySettings;
    _settingsService.Set(s => s.DictionaryDisplaySettings, update(current));
}

partial void OnPopupMaxWidthChanged(int value)
{
    OnPropertyChanged(nameof(PopupMaxWidthText));
    ApplyPopupSetting(current => current with
    {
        PopupMaxWidth = DictionaryPopupAppearanceConstraints.NormalizeWidth(value),
    });
}

partial void OnPopupMaxHeightChanged(int value)
{
    OnPropertyChanged(nameof(PopupMaxHeightText));
    ApplyPopupSetting(current => current with
    {
        PopupMaxHeight = DictionaryPopupAppearanceConstraints.NormalizeHeight(value),
    });
}

partial void OnPopupScaleChanged(double value)
{
    OnPropertyChanged(nameof(PopupScaleText));
    ApplyPopupSetting(current => current with
    {
        PopupScale = DictionaryPopupAppearanceConstraints.NormalizeScale(value),
    });
}

partial void OnPopupActionBarChanged(bool value) =>
    ApplyPopupSetting(current => current with { PopupActionBar = value });

partial void OnPopupFullWidthChanged(bool value) =>
    ApplyPopupSetting(current => current with { PopupFullWidth = value });
```

Delete popup constants, properties, commands, load statements, and change
handlers from `DictionarySettingsPageViewModel`.

- [ ] **Step 4: Move the XAML controls and resources**

Delete the existing Popup Size section from `DictionarySettingsPage.xaml`.
Append this section after Reader Display in
`ReaderAppearanceSettingsContent.xaml`:

```xml
<TextBlock x:Uid="PopupAppearanceSectionHeader"
           Style="{StaticResource SettingsSectionHeaderTextBlockStyle}"
           Text="Popup" />
<toolkit:SettingsCard x:Uid="PopupWidthCard"
                      HorizontalAlignment="Stretch"
                      HorizontalContentAlignment="Stretch"
                      HeaderIcon="{ui:FontIcon Glyph=&#xE9A6;}">
    <StackPanel Orientation="Horizontal" Spacing="12">
        <TextBlock MinWidth="64" VerticalAlignment="Center"
                   Text="{x:Bind ViewModel.PopupMaxWidthText, Mode=OneWay}" />
        <Slider Width="220" Minimum="100" Maximum="1400"
                SmallChange="10" StepFrequency="10"
                Value="{x:Bind ViewModel.PopupMaxWidth, Mode=TwoWay}" />
    </StackPanel>
</toolkit:SettingsCard>
<toolkit:SettingsCard x:Uid="PopupHeightCard"
                      HorizontalAlignment="Stretch"
                      HorizontalContentAlignment="Stretch"
                      HeaderIcon="{ui:FontIcon Glyph=&#xE9A5;}">
    <StackPanel Orientation="Horizontal" Spacing="12">
        <TextBlock MinWidth="64" VerticalAlignment="Center"
                   Text="{x:Bind ViewModel.PopupMaxHeightText, Mode=OneWay}" />
        <Slider Width="220" Minimum="100" Maximum="800"
                SmallChange="10" StepFrequency="10"
                Value="{x:Bind ViewModel.PopupMaxHeight, Mode=TwoWay}" />
    </StackPanel>
</toolkit:SettingsCard>
<toolkit:SettingsCard x:Uid="PopupScaleCard"
                      HorizontalAlignment="Stretch"
                      HorizontalContentAlignment="Stretch"
                      HeaderIcon="{ui:FontIcon Glyph=&#xE8A3;}">
    <StackPanel Orientation="Horizontal" Spacing="12">
        <TextBlock MinWidth="64" VerticalAlignment="Center"
                   Text="{x:Bind ViewModel.PopupScaleText, Mode=OneWay}" />
        <Slider Width="220" Minimum="0.8" Maximum="1.5"
                SmallChange="0.05" StepFrequency="0.05"
                Value="{x:Bind ViewModel.PopupScale, Mode=TwoWay}" />
    </StackPanel>
</toolkit:SettingsCard>
<toolkit:SettingsCard x:Uid="PopupActionBarCard"
                      HorizontalAlignment="Stretch"
                      HorizontalContentAlignment="Stretch"
                      HeaderIcon="{ui:FontIcon Glyph=&#xE8BB;}">
    <ToggleSwitch AutomationProperties.AutomationId="PopupActionBarToggle"
                  IsOn="{x:Bind ViewModel.PopupActionBar, Mode=TwoWay}" />
</toolkit:SettingsCard>
<toolkit:SettingsCard x:Uid="PopupFullWidthCard"
                      HorizontalAlignment="Stretch"
                      HorizontalContentAlignment="Stretch"
                      HeaderIcon="{ui:FontIcon Glyph=&#xE9A6;}">
    <ToggleSwitch AutomationProperties.AutomationId="PopupFullWidthToggle"
                  IsOn="{x:Bind ViewModel.PopupFullWidth, Mode=TwoWay}" />
</toolkit:SettingsCard>
```

Add English values `Popup`, `Width`, `Height`, `Scale`, `Show Action Bar`, and
`Full-width` with concise descriptions. Add equivalent Chinese values `弹窗`,
`宽度`, `高度`, `缩放`, `显示操作栏`, and `全宽显示`.

- [ ] **Step 5: Run focused tests and build**

Run the Step 2 command, then:

```powershell
dotnet build -p:Platform=x64
```

Expected: selected tests pass and the x64 build succeeds with valid x:Bind
generation.

- [ ] **Step 6: Commit the settings move**

```powershell
git add -- Hoshi/ViewModels/Pages/SettingsPageViewModel.cs Hoshi/ViewModels/Pages/DictionarySettingsPageViewModel.cs Hoshi/Views/Controls/ReaderAppearanceSettingsContent.xaml Hoshi/Views/Pages/DictionarySettingsPage.xaml Hoshi/Strings/en-US/Resources.resw Hoshi/Strings/zh-CN/Resources.resw Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "feat: move popup controls to appearance"
```

---

### Task 3: Unify root, child, and full-width layout

**Files:**
- Modify: `Hoshi/Views/Dictionary/DictionaryPopupLayoutCalculator.cs`
- Modify: `Hoshi/Views/Dictionary/DictionaryPopupOverlay.cs`
- Modify: `Hoshi.Tests/Views/Dictionary/DictionaryPopupLayoutCalculatorTests.cs`
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Consumes: normalized dimensions and `PopupFullWidth` from Task 1.
- Changes: `DictionaryPopupLayoutCalculator.Resolve(..., bool isVertical, bool isFullWidth = false)`.
- Produces: one layout path used by root and child popup hosts.

- [ ] **Step 1: Write failing layout tests**

Update existing `Resolve` calls to pass `isFullWidth: false`, then add:

```csharp
[Fact]
public void FullWidthLayout_UsesAvailableWidthAndBottomPlacement()
{
    var layout = DictionaryPopupLayoutCalculator.Resolve(
        new DictionaryPopupAnchorRect(480, 120, 42, 18),
        screenWidth: 1000,
        screenHeight: 800,
        maxWidth: 320,
        maxHeight: 250,
        minWidth: 100,
        isVertical: false,
        isFullWidth: true);

    layout.Left.Should().Be(6);
    layout.Top.Should().Be(544);
    layout.Width.Should().Be(988);
    layout.Height.Should().Be(250);
}

[Fact]
public void FullWidthLayout_ClampsHeightToViewport()
{
    var layout = DictionaryPopupLayoutCalculator.Resolve(
        new DictionaryPopupAnchorRect(0, 0, 1, 1),
        screenWidth: 420,
        screenHeight: 180,
        maxWidth: 1400,
        maxHeight: 800,
        minWidth: 100,
        isVertical: false,
        isFullWidth: true);

    layout.Should().Be(new DictionaryPopupLayoutResult(6, 6, 408, 168));
}
```

Add an asset assertion that `DictionaryPopupOverlay.cs` no longer contains
`ChildPopupMaxWidth`, `ChildPopupMaxHeight`, `HardMaxPopupWidth`, or
`HardMaxPopupHeight`, and does contain `_displaySettings.PopupFullWidth`.

- [ ] **Step 2: Run layout tests to verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopupLayoutCalculatorTests|FullyQualifiedName~DictionaryPopupSettings"
```

Expected: compilation fails because `Resolve` has no `isFullWidth` parameter,
and the overlay asset still contains old hard caps.

- [ ] **Step 3: Implement full-width geometry**

Add `isFullWidth` to `Resolve` and place this branch before vertical/horizontal
anchored layout:

```csharp
if (isFullWidth)
{
    width = Math.Max(0, screenWidth - ScreenBorderPadding * 2);
    height = Math.Min(Math.Max(0, maxHeight), Math.Max(0, screenHeight - ScreenBorderPadding * 2));
    centerX = screenWidth / 2;
    centerY = screenHeight - ScreenBorderPadding - height / 2;
    return FromCenter(centerX, centerY, width, height, screenWidth, screenHeight);
}
```

In `DictionaryPopupOverlay`:

- Replace all popup min/max constants with the Task 1 constraints constants.
- Delete root percentage caps and child caps.
- Resolve `maxWidth` as `min(screenWidth - 12, normalized configured width)`.
- Resolve `maxHeight` as `min(screenHeight - 12, normalized configured height)`.
- Pass `_displaySettings.PopupFullWidth` for root and every child.
- Make the no-rectangle child fallback call the same layout calculator with the
  parent's bounds as its anchor instead of a separate capped child algorithm.
- Keep the embedded root path unchanged.

The final configured helpers are:

```csharp
private double ConfiguredPopupWidth() =>
    DictionaryPopupAppearanceConstraints.NormalizeWidth(_displaySettings.PopupMaxWidth);

private double ConfiguredPopupHeight() =>
    DictionaryPopupAppearanceConstraints.NormalizeHeight(_displaySettings.PopupMaxHeight);
```

- [ ] **Step 4: Run focused tests and build**

Run the Step 2 command, then `dotnet build -p:Platform=x64`.
Expected: tests pass and the build succeeds.

- [ ] **Step 5: Commit unified layout**

```powershell
git add -- Hoshi/Views/Dictionary/DictionaryPopupLayoutCalculator.cs Hoshi/Views/Dictionary/DictionaryPopupOverlay.cs Hoshi.Tests/Views/Dictionary/DictionaryPopupLayoutCalculatorTests.cs Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "feat: apply popup layout settings to nested lookups"
```

---

### Task 4: Apply Niratan-compatible popup scale

**Files:**
- Create: `Hoshi/Services/Dictionary/DictionaryPopupScaleCss.cs`
- Create: `Hoshi.Tests/Services/Dictionary/DictionaryPopupScaleCssTests.cs`
- Modify: `Hoshi/Services/Dictionary/PopupHtmlGenerator.cs`
- Modify: `Hoshi/Web/DictionaryPopup/popup.css`
- Modify: `Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs`

**Interfaces:**
- Consumes: normalized `DictionaryDisplaySettings.PopupScale`.
- Produces: `DictionaryPopupScaleCss.BuildDeclarations(double) -> string`.
- Produces: `DictionaryPopupScaleCss.ScaleCustomCss(string) -> string`.
- Produces: generator output that applies scale before `renderPopup()` for root and child injections.

- [ ] **Step 1: Write failing scale tests**

Create `DictionaryPopupScaleCssTests.cs`:

```csharp
using FluentAssertions;
using Hoshi.Services.Dictionary;

namespace Hoshi.Tests.Services.Dictionary;

public sealed class DictionaryPopupScaleCssTests
{
    [Fact]
    public void BuildDeclarations_UsesInvariantNiratanDimensions()
    {
        var css = DictionaryPopupScaleCss.BuildDeclarations(1.25);

        css.Should().Contain("--popup-scale:1.25;");
        css.Should().Contain("--popup-root-font-size:20px;");
        css.Should().Contain("--popup-body-font-size:18.75px;");
        css.Should().Contain("--popup-expression-font-size:32.5px;");
        css.Should().NotContain(',');
    }

    [Fact]
    public void ScaleCustomCss_RewritesSignedAndDecimalPixelLengths()
    {
        DictionaryPopupScaleCss.ScaleCustomCss(".x{margin:-2.5px;padding:8px}")
            .Should().Be(".x{margin:calc(-2.5px * var(--popup-scale));padding:calc(8px * var(--popup-scale))}");
    }
}
```

Add a generator test:

```csharp
[Fact]
public void PopupHtml_AppliesScaleBeforeRendering()
{
    var settings = new DictionaryDisplaySettings(
        PopupScale: 1.25,
        CustomCSS: ".custom{padding:8px}");
    var generator = new PopupHtmlGenerator();

    var shell = generator.GenerateShellHtml(settings: settings);
    var injection = generator.GenerateInjectionScript([], [], settings);

    shell.Should().Contain("--popup-scale:1.25;");
    shell.Should().Contain("calc(8px * var(--popup-scale))");
    injection.IndexOf("--popup-scale", StringComparison.Ordinal)
        .Should().BeLessThan(injection.IndexOf("window.hoshiInjectResults", StringComparison.Ordinal));
}
```

- [ ] **Step 2: Run scale tests to verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopupScaleCssTests|FullyQualifiedName~PopupHtml_AppliesScaleBeforeRendering"
```

Expected: compilation fails because `DictionaryPopupScaleCss` does not exist.

- [ ] **Step 3: Implement declarations and custom CSS scaling**

Create `DictionaryPopupScaleCss.cs` with invariant formatting and the Niratan
variable set:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using Hoshi.Models.Settings;

namespace Hoshi.Services.Dictionary;

internal static partial class DictionaryPopupScaleCss
{
    public static string BuildDeclarations(double requestedScale)
    {
        var scale = DictionaryPopupAppearanceConstraints.NormalizeScale(requestedScale);
        string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        string Px(double value) => $"{Number(value * scale)}px";

        return string.Join(string.Empty,
            $"--popup-scale:{Number(scale)};",
            $"--popup-root-font-size:{Px(16)};",
            $"--popup-body-font-size:{Px(15)};",
            $"--popup-dictionary-font-size:{Px(14)};",
            $"--popup-expression-font-size:{Px(26)};",
            $"--popup-expression-reading-size:{Px(13)};",
            $"--popup-tag-font-size:{Px(11)};",
            $"--popup-small-tag-font-size:{Px(10)};",
            $"--popup-dict-label-font-size:{Px(10)};",
            $"--popup-pitch-font-size:{Px(13)};",
            $"--popup-arrow-size:{Px(8)};",
            $"--popup-overlay-close-size:{Px(20)};",
            $"--popup-button-size:{Px(28)};",
            $"--popup-space-1:{Px(1)};",
            $"--popup-space-2:{Px(2)};",
            $"--popup-space-3:{Px(3)};",
            $"--popup-space-4:{Px(4)};",
            $"--popup-space-5:{Px(5)};",
            $"--popup-space-6:{Px(6)};",
            $"--popup-space-8:{Px(8)};",
            $"--popup-space-10:{Px(10)};",
            $"--popup-space-18:{Px(18)};",
            $"--popup-space-20:{Px(20)};",
            $"--popup-space-neg-2:{Px(-2)};",
            $"--popup-space-neg-4:{Px(-4)};");
    }

    public static string ScaleCustomCss(string css) =>
        PixelLengthRegex().Replace(css ?? string.Empty,
            match => $"calc({match.Groups[1].Value}px * var(--popup-scale))");

    [GeneratedRegex(@"(-?(?:\d+(?:\.\d+)?|\.\d+))px", RegexOptions.CultureInvariant)]
    private static partial Regex PixelLengthRegex();
}
```

In `PopupHtmlGenerator`, place `BuildDeclarations(settings.PopupScale)` in the
shell's root style and set the same declarations on `document.documentElement`
and `document.body` before assigning `window.customCSS` and calling
`window.hoshiInjectResults`. Pass `ScaleCustomCss(settings.CustomCSS)` instead
of raw custom CSS in both shell and injection paths.

In `popup.css`, define the variables at `1.0` defaults and replace fixed popup
text, icon, button, spacing, padding, gap, and radius dimensions with the
matching variables. At minimum the final rules must include:

```css
html { font-size: var(--popup-root-font-size); }
body { font-size: var(--popup-body-font-size); }
#popup-viewport { padding: 0 var(--popup-space-10); }
.expression { font-size: var(--popup-expression-font-size); }
.reading { font-size: var(--popup-expression-reading-size); }
.dictionary-title { font-size: var(--popup-dictionary-font-size); }
.tag { font-size: var(--popup-tag-font-size); }
.button-slot { width: var(--popup-button-size); height: var(--popup-button-size); }
```

Keep percentage, `em`, and structured-content author dimensions unchanged.

- [ ] **Step 4: Run focused scale tests and dictionary tests**

Run the Step 2 command, then:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryLookupServiceTests|FullyQualifiedName~DictionaryPopup"
```

Expected: all selected tests pass.

- [ ] **Step 5: Commit scale support**

```powershell
git add -- Hoshi/Services/Dictionary/DictionaryPopupScaleCss.cs Hoshi/Services/Dictionary/PopupHtmlGenerator.cs Hoshi/Web/DictionaryPopup/popup.css Hoshi.Tests/Services/Dictionary/DictionaryPopupScaleCssTests.cs Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs
git commit -m "feat: scale dictionary popup content"
```

---

### Task 5: Add the Niratan action bar and in-place history

**Files:**
- Create: `Hoshi/Views/Dictionary/DictionaryPopupRedirectRouter.cs`
- Create: `Hoshi.Tests/Views/Dictionary/DictionaryPopupRedirectRouterTests.cs`
- Modify: `Hoshi/Views/Dictionary/DictionaryLookupPopup.cs`
- Modify: `Hoshi/Views/Dictionary/DictionaryPopupOverlay.cs`
- Modify: `Hoshi/Services/Dictionary/PopupHtmlGenerator.cs`
- Modify: `Hoshi/Web/DictionaryPopup/popup.js`
- Modify: `Hoshi/Strings/en-US/Resources.resw`
- Modify: `Hoshi/Strings/zh-CN/Resources.resw`
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Produces: `DictionaryPopupRedirectMode { InPlace, Nested }`.
- Produces: `DictionaryPopupRedirectRouter.Resolve(DictionaryPopupRedirectRequest) -> DictionaryPopupRedirectMode`.
- Produces: `DictionaryLookupPopup.ShowRedirectResultsAsync(...)` for structured links.
- Produces: `DictionaryLookupPopup.NavigateBackAsync()` and `.NavigateForwardAsync()`.
- Produces: typed `navigationState` message body `{ generation, canGoBack, canGoForward }`.
- Consumes: `PopupActionBar` on every root and child popup.

- [ ] **Step 1: Write failing redirect routing tests**

Create `DictionaryPopupRedirectRouterTests.cs`:

```csharp
using FluentAssertions;
using Hoshi.Views.Dictionary;

namespace Hoshi.Tests.Views.Dictionary;

public sealed class DictionaryPopupRedirectRouterTests
{
    [Fact]
    public void StructuredLinkWithoutSelectionCoordinatesRedirectsInPlace()
    {
        DictionaryPopupRedirectRouter.Resolve(new DictionaryPopupRedirectRequest("語"))
            .Should().Be(DictionaryPopupRedirectMode.InPlace);
    }

    [Fact]
    public void SelectedPopupTextCreatesNestedPopup()
    {
        DictionaryPopupRedirectRouter.Resolve(new DictionaryPopupRedirectRequest(
            "語", X: 20, Y: 30, Width: 10, Height: 14, Source: "click"))
            .Should().Be(DictionaryPopupRedirectMode.Nested);
    }
}
```

Add an asset test that asserts:

```csharp
popupCode.Should().Contain("new CommandBar");
popupCode.Should().Contain("PopupActionBar");
popupCode.Should().Contain("NavigateBackAsync");
popupCode.Should().Contain("NavigateForwardAsync");
popupCode.Should().Contain("case \"navigationState\"");
overlayCode.Should().Contain("DictionaryPopupRedirectMode.InPlace");
popupJs.Should().Contain("window.hoshiRedirectResults");
popupJs.Should().Contain("postNavigationState");
popupJs.Should().Contain("canGoBack: backStack.length > 0");
popupJs.Should().Contain("canGoForward: forwardStack.length > 0");
```

- [ ] **Step 2: Run routing/action-bar tests to verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryPopupRedirectRouterTests|FullyQualifiedName~DictionaryPopupActionBar"
```

Expected: compilation fails because the router does not exist and asset
assertions fail because the popup has only Sasayaki controls.

- [ ] **Step 3: Implement the pure redirect router**

Create `DictionaryPopupRedirectRouter.cs`:

```csharp
namespace Hoshi.Views.Dictionary;

internal enum DictionaryPopupRedirectMode
{
    InPlace,
    Nested,
}

internal static class DictionaryPopupRedirectRouter
{
    public static DictionaryPopupRedirectMode Resolve(DictionaryPopupRedirectRequest request) =>
        request.X is null
        && request.Y is null
        && string.IsNullOrWhiteSpace(request.Source)
            ? DictionaryPopupRedirectMode.InPlace
            : DictionaryPopupRedirectMode.Nested;
}
```

- [ ] **Step 4: Add JavaScript in-place history and typed state**

Add to `popup.js` beside the existing navigation stack:

```javascript
function postNavigationState() {
  postPopupMessage('navigationState', {
    generation: Number(window.popupRenderGeneration || 0),
    canGoBack: backStack.length > 0,
    canGoForward: forwardStack.length > 0
  });
}

window.hoshiRedirectResults = function (entriesJson, count) {
  flushPendingHistoryRestore();
  backStack.push(snapshot());
  forwardStack.length = 0;
  document.documentElement.style.visibility = 'hidden';
  window.lookupEntries = entriesJson;
  window.entryCount = count;
  selectedDictionaries = {};
  audioUrls = {};
  disconnectDictionaryColumns();
  document.getElementById('entries-container').innerHTML = '';
  window.renderPopup();
  postNavigationState();
};

window.navigateBack = function () {
  navigate(backStack, forwardStack);
  postNavigationState();
};

window.navigateForward = function () {
  navigate(forwardStack, backStack);
  postNavigationState();
};
```

Call `postNavigationState()` after shell/root result replacement clears both
stacks. Keep lookup execution native; JavaScript receives serialized results
only.

Add `PopupHtmlGenerator.GenerateRedirectInjectionScript`:

```csharp
public string GenerateRedirectInjectionScript(
    List<DictionaryLookupResult> results,
    Dictionary<string, string> styles,
    DictionaryDisplaySettings displaySettings,
    ThemeMode themeMode,
    long renderGeneration,
    AudioSettings? audioSettings = null,
    AnkiSettings? ankiSettings = null,
    string? traceId = null)
```

It applies the same theme, scale, dictionary, audio, Anki, and trace variables
as `GenerateInjectionScript`, then calls:

```javascript
window.hoshiRedirectResults(serializedEntries, resultCount);
```

- [ ] **Step 5: Add the native WinUI CommandBar**

In `DictionaryLookupPopup`, create compact Back, Forward, and Close
`AppBarButton`s with Fluent icons `\uE72B`, `\uE72A`, and `\uE711`. Use localized
resource names `DictionaryPopupBackButton`, `DictionaryPopupForwardButton`, and
`DictionaryPopupCloseButton` for AutomationId, AutomationProperties.Name,
labels, tooltips, and Narrator.

Add an `_actionBar` `CommandBar` with collapsed labels, transparent background,
no overflow, and `Visibility.Collapsed`. Add a dedicated Auto row above the
existing Sasayaki row and WebView2 star row:

```csharp
_surfaceRoot = new Grid
{
    RowDefinitions =
    {
        new RowDefinition { Height = GridLength.Auto },
        new RowDefinition { Height = GridLength.Auto },
        new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
    },
    Children = { _actionBar, _sasayakiControlsBar, _contentWebView },
};
Grid.SetRow(_actionBar, 0);
Grid.SetRow(_sasayakiControlsBar, 1);
Grid.SetRow(_contentWebView, 2);
```

At the start of every results injection, set action-bar visibility from
`displaySettings.PopupActionBar`, disable Back/Forward until current state
arrives, and keep Close enabled. Implement:

```csharp
public async Task NavigateBackAsync() =>
    await _contentWebView.CoreWebView2.ExecuteScriptAsync("window.navigateBack?.()");

public async Task NavigateForwardAsync() =>
    await _contentWebView.CoreWebView2.ExecuteScriptAsync("window.navigateForward?.()");
```

Close raises the existing `DismissRequested` event. Back/Forward click handlers
await the methods above and log unexpected WebView failures.

Parse `navigationState` only when the body is an object, its generation equals
the active `_displayGeneration`, and both flags are JSON booleans. Enqueue the
validated values onto the WebView dispatcher before changing button state.

Add `ShowRedirectResultsAsync` that uses the new generator method without
changing the native popup size, z-order, opacity, or render generation.

- [ ] **Step 6: Route structured links in place and selections to children**

At the top of `DictionaryPopupOverlay.HandleRedirectAsync`, after validating
the query and choosing the parent, branch on the pure router. For `InPlace`,
run the existing async native lookup with the active settings, keep the current
popup when no results are found, and call:

```csharp
await parent.ShowRedirectResultsAsync(
    results,
    _currentStyles,
    _displaySettings,
    _currentTheme,
    _currentAudioSettings,
    _currentAnkiSettings,
    traceId,
    cancellationToken);
```

Return without creating a child. Keep the existing child path for `Nested` and
continue passing `_displaySettings` to `ShowResultsWarmAsync` so every child
gets action-bar visibility and its own history.

- [ ] **Step 7: Run focused tests and build**

Run the Step 2 command, then:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryLookupServiceTests|FullyQualifiedName~DictionaryPopup"
dotnet build -p:Platform=x64
```

Expected: selected tests pass and the x64 build succeeds.

- [ ] **Step 8: Commit action-bar history**

```powershell
git add -- Hoshi/Views/Dictionary/DictionaryPopupRedirectRouter.cs Hoshi/Views/Dictionary/DictionaryLookupPopup.cs Hoshi/Views/Dictionary/DictionaryPopupOverlay.cs Hoshi/Services/Dictionary/PopupHtmlGenerator.cs Hoshi/Web/DictionaryPopup/popup.js Hoshi/Strings/en-US/Resources.resw Hoshi/Strings/zh-CN/Resources.resw Hoshi.Tests/Views/Dictionary/DictionaryPopupRedirectRouterTests.cs Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "feat: add popup action bar history"
```

---

### Task 6: Complete verification and documentation

**Files:**
- Modify: `docs/VERIFICATION.md`
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs` only if a final contract gap is found before implementation; do not weaken passing assertions.

**Interfaces:**
- Consumes: all Tasks 1-5.
- Produces: repeatable automated and runtime verification for all popup hosts and nested configuration.

- [ ] **Step 1: Add the verification matrix before running it**

Add this checklist to the Dictionary verification section of
`docs/VERIFICATION.md`:

```markdown
### Popup appearance

1. In Appearance > Popup, verify width `100...1400`, height `100...800`,
   scale `0.8...1.5`, Show Action Bar, and Full-width.
2. Verify new/default settings resolve to `320 x 250`, scale `1.00`, with both
   toggles off.
3. In reader and video lookup, test `320 x 250` and `1400 x 800`, scale `0.8`
   and `1.5`, light and dark themes, and window resize containment.
4. Enable Show Action Bar, follow a structured-content dictionary link, then
   verify Back, Forward, and Close with mouse and keyboard.
5. Open a nested text-selection lookup. Verify it inherits width, height,
   scale, action-bar visibility, and Full-width; close it and confirm the parent
   remains visible.
6. With Full-width enabled, verify each popup uses the available width and
   bottom placement while the configured height remains effective.
```

- [ ] **Step 2: Run dictionary-focused tests**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Dictionary"
```

Expected: all dictionary-focused tests pass with zero failures.

- [ ] **Step 3: Run the full build and test suite**

```powershell
dotnet build -p:Platform=x64
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
```

Expected: build and all tests pass with zero failures.

- [ ] **Step 4: Launch and verify the WinUI app**

Run:

```powershell
.\build-and-run.ps1
```

Confirm a responsive Hoshi top-level window exists. Use the existing UI
automation path and `C:\Users\Wight\Downloads\哈利波特1魔法石.epub` to execute
the six runtime checks written in Step 1. Also verify video popup configuration
on the existing test video flow. Leave the final verified app instance running.

- [ ] **Step 5: Review the diff and commit verification docs**

Run:

```powershell
git diff --check
git status --short
```

Expected: no whitespace errors; `.codex/` remains untracked and is not staged.

Commit:

```powershell
git add -- docs/VERIFICATION.md
git commit -m "docs: verify popup appearance settings"
```

- [ ] **Step 6: Record final evidence**

Capture in the handoff:

- Focused dictionary test count and result.
- Full test count and result.
- Build result.
- Launch/window evidence.
- Reader, video, nested, action-bar, full-width, scale, resize, and theme runtime results.
- Any environment limitation that prevented a specific manual check, without
  describing unverified behavior as passing.
