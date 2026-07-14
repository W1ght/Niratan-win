# Video Subtitle Appearance Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist player subtitle appearance changes globally and use the approved screenshot values for fresh-profile and reset defaults.

**Architecture:** `VideoSettings` is the canonical persisted source. The player view model loads it at construction and writes a replacement settings object whenever a user changes subtitle appearance, while an initialization guard prevents load-time writes. `VideoPlayerWindow` continues to render only view-model state.

**Tech Stack:** C#/.NET, WinUI 3, CommunityToolkit.Mvvm, xUnit v3, FluentAssertions, Moq.

## Global Constraints

- Do not put business persistence logic in `VideoPlayerWindow` code-behind.
- Persist through `ISettingsService.Set` and `SaveAsync`; do not access SQLite.
- Preserve all existing unrelated `VideoSettings` values when saving appearance changes.
- Target Windows 10+ x64; build with `dotnet build -p:Platform=x64`.
- Do not modify `native/hoshidicts/`.

---

## File Structure

- `Niratan/Models/Settings/JapaneseFontCatalog.cs`: exposes the default subtitle font selected by every video settings consumer.
- `Niratan/Models/Settings/VideoSettings.cs`: owns the approved initial and reset-equivalent subtitle values and a shallow settings copy operation.
- `Niratan/ViewModels/Pages/VideoPlayerViewModel.cs`: loads and persists player inspector appearance mutations.
- `Niratan.Tests/Models/Settings/VideoSettingsTests.cs`: documents model defaults.
- `Niratan.Tests/ViewModels/Pages/VideoPlayerViewModelSubtitleAppearanceTests.cs`: verifies defaults, reset behavior, and settings-service persistence.

### Task 1: Establish approved subtitle defaults

**Files:**
- Modify: `Niratan/Models/Settings/JapaneseFontCatalog.cs:11-31`
- Modify: `Niratan/Models/Settings/VideoSettings.cs:21-27, 142-143`
- Modify: `Niratan.Tests/Models/Settings/VideoSettingsTests.cs:8-47`
- Modify: `Niratan.Tests/ViewModels/Pages/VideoPlayerViewModelSubtitleAppearanceTests.cs:12-25, 71-91`

**Interfaces:**
- Consumes: `JapaneseFontCatalog.DefaultSubtitleFontFamily`, consumed by `VideoSettings` and `VideoPlayerViewModel`.
- Produces: default values `Noto Serif CJK JP`, `52`, `700`, `10`, `-51`, and `#FFFFFFFF`.

- [ ] **Step 1: Write the failing default-value tests**

Replace the existing expected defaults in `VideoSettingsTests.Defaults_AreAlignedWithNiratanVideoSettings` and `VideoPlayerViewModelSubtitleAppearanceTests.SubtitleAppearance_DefaultsMatchAsbplayerAndNiratanBaseline` with:

```csharp
settings.SubtitleFontFamily.Should().Be("Noto Serif CJK JP");
settings.SubtitleFontSize.Should().Be(52);
settings.SubtitleFontWeight.Should().Be(700);
settings.SubtitleShadowRadius.Should().Be(10);
settings.SubtitleVerticalPosition.Should().Be(-51);
settings.SubtitleColorHex.Should().Be("#FFFFFFFF");
```

Update the reset assertions to expect the same values.

- [ ] **Step 2: Run the focused tests and verify they fail**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSettingsTests|FullyQualifiedName~VideoPlayerViewModelSubtitleAppearanceTests"
```

Expected: FAIL because the existing baseline remains `Klee One`, `36`, `3`, and `0`.

- [ ] **Step 3: Implement the shared defaults**

Make the catalog reference the existing Noto option as the default, then set the model fields to the approved values:

```csharp
public const string DefaultSubtitleFontFamily = "Noto Serif CJK JP";

public static JapaneseFontOption DefaultFont { get; } = Fonts[2];
```

```csharp
private double _subtitleFontSize = 52;
private int _subtitleFontWeight = 700;
private double _subtitleShadowRadius = 10;
private double _subtitleVerticalPosition = -51;
```

Update the player declaration defaults and `VideoPlayerViewModel.ResetSubtitleAppearance()` to assign `52`, `700`, `JapaneseFontCatalog.DefaultSubtitleFontFamily`, `10`, `-51`, and `#FFFFFFFF`:

```csharp
public partial double SubtitleFontSize { get; set; } = 52;
public partial double SubtitleShadowRadius { get; set; } = 10;
public partial double SubtitleVerticalPosition { get; set; } = -51;
```

- [ ] **Step 4: Run the focused tests and verify they pass**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSettingsTests|FullyQualifiedName~VideoPlayerViewModelSubtitleAppearanceTests"
```

Expected: PASS with zero failed tests.

- [ ] **Step 5: Commit the default update**

```powershell
git add -- Niratan/Models/Settings/JapaneseFontCatalog.cs Niratan/Models/Settings/VideoSettings.cs Niratan/ViewModels/Pages/VideoPlayerViewModel.cs Niratan.Tests/Models/Settings/VideoSettingsTests.cs Niratan.Tests/ViewModels/Pages/VideoPlayerViewModelSubtitleAppearanceTests.cs
git commit -m "fix(video): align subtitle appearance defaults"
```

### Task 2: Persist player inspector subtitle appearance

**Files:**
- Modify: `Niratan/Models/Settings/VideoSettings.cs:142-143`
- Modify: `Niratan/ViewModels/Pages/VideoPlayerViewModel.cs:21-310, 1094-1249, 1552-1593`
- Modify: `Niratan.Tests/ViewModels/Pages/VideoPlayerViewModelSubtitleAppearanceTests.cs:9-122`

**Interfaces:**
- Consumes: `ISettingsService.Current`, `ISettingsService.Set(Expression<Func<AppSettings, VideoSettings>>, VideoSettings)`, and `ISettingsService.SaveAsync()`.
- Produces: `VideoPlayerViewModel` writes all subtitle appearance properties to a replacement `VideoSettings` instance after user-originated changes.

- [ ] **Step 1: Write the failing persistence regression test**

Add a test that constructs a mocked settings service with a non-default `AppSettings`, captures `Set`, changes the player’s font, size, weight, shadow, vertical position, and color, then asserts both persistence and a second view model’s loaded values:

```csharp
[Fact]
public void SubtitleAppearanceChanges_PersistAndAreRestoredForNextPlayer()
{
    var appSettings = new AppSettings { VideoSettings = new VideoSettings { SeekIntervalSeconds = 9 } };
    VideoSettings? saved = null;
    var settingsService = CreateSettingsService(appSettings, value => saved = value);
    var first = CreateSut(settingsService.Object);

    first.SetSubtitleFontFamily("Yu Gothic");
    first.SubtitleFontSize = 43;
    first.SubtitleFontWeight = 600;
    first.SetSubtitleShadowRadius(8.5);
    first.SubtitleVerticalPosition = -24;
    first.SetSubtitleColor("#112233");

    saved.Should().NotBeNull();
    saved!.SeekIntervalSeconds.Should().Be(9);
    saved.SubtitleVerticalPosition.Should().Be(-24);

    var second = CreateSut(settingsService.Object);
    second.SubtitleFontFamily.Should().Be("Yu Gothic");
    second.SubtitleFontSize.Should().Be(43);
    second.SubtitleShadowRadius.Should().Be(8.5);
    second.SubtitleVerticalPosition.Should().Be(-24);
}
```

Add this test helper below `CreateSut` and change `CreateSut` to accept its optional `ISettingsService` argument:

```csharp
private static Mock<ISettingsService> CreateSettingsService(
    AppSettings appSettings,
    Action<VideoSettings>? onSaved = null)
{
    var service = new Mock<ISettingsService>();
    service.SetupGet(candidate => candidate.Current).Returns(appSettings);
    service.Setup(candidate => candidate.Set(
            It.IsAny<Expression<Func<AppSettings, VideoSettings>>>(),
            It.IsAny<VideoSettings>()))
        .Callback<Expression<Func<AppSettings, VideoSettings>>, VideoSettings>(
            (_, value) =>
            {
                appSettings.VideoSettings = value;
                onSaved?.Invoke(value);
            });
    service.Setup(candidate => candidate.SaveAsync()).Returns(Task.CompletedTask);
    return service;
}

private static VideoPlayerViewModel CreateSut(ISettingsService? settingsService = null) =>
    new(new SubtitleParserService(), Mock.Of<IDictionaryPopupRequestService>(), settingsService);
```

- [ ] **Step 2: Run the regression test and verify it fails**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoPlayerViewModelSubtitleAppearanceTests.SubtitleAppearanceChanges_PersistAndAreRestoredForNextPlayer"
```

Expected: FAIL because the player view model does not write inspector mutations to `ISettingsService`.

- [ ] **Step 3: Implement isolated view-model persistence**

Add a `VideoSettings.Clone()` method; its `MemberwiseClone()` implementation is safe because every stored setting is a value type, enum, immutable string, or Boolean. Add nullable `ISettingsService` and `_isApplyingSettings` fields. Apply existing settings under the guard in the constructor. Add a private `SaveSubtitleAppearance()` method:

```csharp
public VideoSettings Clone() => (VideoSettings)MemberwiseClone();
```

```csharp
private void SaveSubtitleAppearance()
{
    if (_isApplyingSettings || _settingsService is null)
        return;

    var updatedSettings = _settingsService.Current.VideoSettings.Clone();
    updatedSettings.SubtitleFontFamily = SubtitleFontFamily;
    updatedSettings.SubtitleFontSize = SubtitleFontSize;
    updatedSettings.SubtitleFontWeight = SubtitleFontWeight;
    updatedSettings.SubtitleShadowRadius = SubtitleShadowRadius;
    updatedSettings.SubtitleBackgroundOpacity = SubtitleBackgroundOpacity;
    updatedSettings.SubtitleBackgroundDisabled = SubtitleBackgroundDisabled;
    updatedSettings.SubtitleVerticalPosition = SubtitleVerticalPosition;
    updatedSettings.SubtitleColorHex = SubtitleColorHex;
    updatedSettings.SubtitleLookupHighlightColorHex = SubtitleLookupHighlightColorHex;
    updatedSettings.SubtitleLookupHighlightTextColorHex = SubtitleLookupHighlightTextColorHex;
    updatedSettings.SubtitleMaskEnabled = SubtitleMaskEnabled;
    updatedSettings.SubtitleMaskMode = SubtitleMaskMode == "Transparent"
        ? VideoSubtitleMaskMode.Transparent
        : VideoSubtitleMaskMode.Blur;
    updatedSettings.SubtitleMaskBlurRadius = SubtitleMaskBlurRadius;
    updatedSettings.SubtitleMaskHiddenOpacity = SubtitleMaskHiddenOpacity;

    _settingsService.Set(settings => settings.VideoSettings, updatedSettings);
    _ = _settingsService.SaveAsync();
}
```

Use this constructor sequence to prevent loading a persisted profile from triggering a rewrite:

```csharp
_settingsService = settingsService;
if (_settingsService is not null)
{
    _isApplyingSettings = true;
    try
    {
        ApplySettings(_settingsService.Current.VideoSettings);
    }
    finally
    {
        _isApplyingSettings = false;
    }
}
```

Replace every subtitle appearance partial handler with the existing text/height update followed by `SaveSubtitleAppearance()`, including font family, font size, font weight, shadow radius, background opacity/disabled, vertical position, subtitle color, both lookup colors, mask enabled/mode/blur radius, and mask hidden opacity. Keep other player handlers unchanged. The replacement settings object only changes:

```csharp
SubtitleFontFamily = SubtitleFontFamily,
SubtitleFontSize = SubtitleFontSize,
SubtitleFontWeight = SubtitleFontWeight,
SubtitleShadowRadius = SubtitleShadowRadius,
SubtitleBackgroundOpacity = SubtitleBackgroundOpacity,
SubtitleBackgroundDisabled = SubtitleBackgroundDisabled,
SubtitleVerticalPosition = SubtitleVerticalPosition,
SubtitleColorHex = SubtitleColorHex,
SubtitleLookupHighlightColorHex = SubtitleLookupHighlightColorHex,
SubtitleLookupHighlightTextColorHex = SubtitleLookupHighlightTextColorHex,
SubtitleMaskEnabled = SubtitleMaskEnabled,
SubtitleMaskMode = SubtitleMaskMode == "Transparent" ? VideoSubtitleMaskMode.Transparent : VideoSubtitleMaskMode.Blur,
SubtitleMaskBlurRadius = SubtitleMaskBlurRadius,
SubtitleMaskHiddenOpacity = SubtitleMaskHiddenOpacity,
```

Call `_settingsService.Set(settings => settings.VideoSettings, updatedSettings);` followed by `_ = _settingsService.SaveAsync();`. Invoke this method from each subtitle appearance `On...Changed` partial handler only when `_isApplyingSettings` is false. Keep renderer application in `VideoPlayerWindow` unchanged.

- [ ] **Step 4: Run the regression and focused appearance tests**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoPlayerViewModelSubtitleAppearanceTests"
```

Expected: PASS with zero failed tests.

- [ ] **Step 5: Commit the persistence implementation**

```powershell
git add -- Niratan/Models/Settings/VideoSettings.cs Niratan/ViewModels/Pages/VideoPlayerViewModel.cs Niratan.Tests/ViewModels/Pages/VideoPlayerViewModelSubtitleAppearanceTests.cs
git commit -m "fix(video): persist subtitle appearance changes"
```

### Task 3: Verify application integration

**Files:**
- Verify only: `Niratan/Views/Video/VideoPlayerWindow.xaml.cs`
- Verify only: `Niratan/Views/Video/VideoPlayerWindow.SubtitleOverlay.cs`

**Interfaces:**
- Consumes: the view model’s persisted property notifications.
- Produces: no source change; verifies the existing renderer continues applying view-model appearance.

- [ ] **Step 1: Run the full test suite**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64
```

Expected: PASS with zero failed tests.

- [ ] **Step 2: Build the x64 application**

Run:

```powershell
dotnet build -p:Platform=x64
```

Expected: build succeeds with zero errors.

- [ ] **Step 3: Launch and manually verify**

Run:

```powershell
.\build-and-run.ps1
```

Expected: the Niratan top-level window opens. Open a video, change subtitle font/size/shadow/position in the inspector, close and reopen the player, and confirm the changed values remain. Use reset and confirm it restores `Noto Serif CJK JP`, `52`, `700`, `10`, `-51`, and white.
