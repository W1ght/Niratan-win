# Video Subtitle Soft Shadow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the eight-direction hard subtitle outline with Niratan-compatible Gaussian shadow rendering.

**Architecture:** A Win2D `CanvasControl` replaces the native `TextBlock` stack and asynchronous PNG image. `VideoSubtitleCanvasRenderer` renders a single blurred black shadow, Canvas selection regions, the crisp foreground, and optionally a second mask blur over the completed composite. WebView2 remains fully hidden and non-hit-testable as a narrow text-boundary bridge only.

**Tech Stack:** WinUI 3, C#/.NET 10, Microsoft.Graphics.Win2D, xUnit v3, FluentAssertions

## Global Constraints

- Keep `SubtitleShadowRadius` clamped to `0...10` and interpret it as Gaussian blur radius.
- Use one black 90%-opacity shadow offset by `(0, 1)` DIP.
- Preserve the hidden WebView2 text-boundary bridge and existing subtitle mask behavior, but keep Canvas as the only visible and interactive layer.
- Add no dependencies and do not move business logic into code-behind.

---

### Task 1: Define Niratan-compatible shadow parameters

**Files:**
- Modify: `Niratan/Services/Video/VideoSubtitleShadowLayout.cs`
- Modify: `Niratan.Tests/Services/Video/VideoSubtitleShadowLayoutTests.cs`

**Interfaces:**
- Produces: `VideoSubtitleShadowLayout.Create(double radius, double contentOpacity) -> VideoSubtitleShadowStyle`
- Produces: `VideoSubtitleShadowStyle(float BlurRadius, float OffsetX, float OffsetY, float Opacity)`

- [ ] **Step 1: Replace the eight-offset expectations with a failing maximum-radius semantic test**

```csharp
var style = VideoSubtitleShadowLayout.Create(10, 1);
style.Should().Be(new VideoSubtitleShadowStyle(10, 0, 1, 0.9f));
```

- [ ] **Step 2: Run the focused test and confirm it fails because `Create` and `VideoSubtitleShadowStyle` do not exist**

Run: `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSubtitleShadowLayoutTests" --no-restore`

- [ ] **Step 3: Replace the eight-offset model with the clamped single-shadow style**

```csharp
public readonly record struct VideoSubtitleShadowStyle(
    float BlurRadius,
    float OffsetX,
    float OffsetY,
    float Opacity);

public static VideoSubtitleShadowStyle Create(double radius, double contentOpacity)
{
    var blurRadius = (float)Math.Clamp(radius, 0, 10);
    var opacity = (float)(Math.Clamp(contentOpacity, 0, 1) * 0.9);
    return blurRadius <= 0 || opacity <= 0
        ? new(0, 0, 0, 0)
        : new(blurRadius, 0, 1, opacity);
}
```

- [ ] **Step 4: Run the focused test and confirm all shadow layout cases pass**

Run: `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSubtitleShadowLayoutTests" --no-restore`

### Task 2: Render the soft shadow and subtitle composite with Win2D

**Files:**
- Create: `Niratan/Services/Video/VideoSubtitleCanvasRenderer.cs`
- Delete: `Niratan/Services/Video/VideoSubtitleMaskBitmapRenderer.cs`
- Modify: `Niratan.Tests/Services/Video/VideoSubtitleLookupAssetTests.cs`

**Interfaces:**
- Consumes: `VideoSubtitleShadowLayout.Create(double, double)`
- Produces: `VideoSubtitleCanvasRenderer.Draw(CanvasDrawingSession, Size, VideoSubtitleCanvasRenderOptions)`

- [ ] **Step 1: Update the subtitle asset contract to require `VideoSubtitleCanvasRenderer`, `GaussianBlurEffect`, and crisp foreground drawing; require the PNG renderer to be absent**

- [ ] **Step 2: Run the focused asset tests and confirm they fail because the canvas renderer is missing**

Run: `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSubtitleLookupAssetTests" --no-restore`

- [ ] **Step 3: Implement the minimal renderer**

The renderer must create a centered/wrapped text layout, draw one black text
source through `GaussianBlurEffect`, offset it by `(0, 1)`, draw the configured
foreground sharply, and optionally apply `MaskBlurRadius` to the combined
command list.

- [ ] **Step 4: Run the focused asset tests and shadow parameter tests**

Run: `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSubtitleLookupAssetTests|FullyQualifiedName~VideoSubtitleShadowLayoutTests" --no-restore`

### Task 3: Replace the native layer with one CanvasControl

**Files:**
- Modify: `Niratan/Views/Video/VideoPlayerWindow.xaml`
- Modify: `Niratan/Views/Video/VideoPlayerWindow.SubtitleOverlay.cs`
- Modify: `Niratan/Views/Video/VideoPlayerWindow.xaml.cs`
- Modify: `Niratan.Tests/Services/Video/VideoSubtitleLookupAssetTests.cs`

**Interfaces:**
- Consumes: `VideoSubtitleCanvasRenderer.Draw(...)`
- Preserves: hidden `SubtitleWebView` text-boundary bridge; Canvas owns lookup hit testing and selection visuals

- [ ] **Step 1: Add failing asset assertions for exactly one named subtitle `CanvasControl` and removal of the eight `SubtitleShadowText*` elements and `SubtitleMaskBlurImage`**

- [ ] **Step 2: Run the focused asset tests and confirm the old XAML fails the new contract**

- [ ] **Step 3: Replace the old XAML layers, invalidate the canvas from `UpdateSubtitleNativeTextAppearance`, and draw current ViewModel state in the canvas draw handler**

- [ ] **Step 4: Remove obsolete bitmap generation state and size-change branches**

- [ ] **Step 5: Run all subtitle-focused tests**

Run: `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSubtitle" --no-restore`

### Task 4: Verify build, tests, and launch

**Files:**
- Modify only if verification exposes a defect in the preceding tasks.

- [ ] **Step 1: Build x64**

Run: `dotnet build -p:Platform=x64`

- [ ] **Step 2: Run the full x64 test suite**

Run: `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64`

- [ ] **Step 3: Launch through the repository workflow and confirm a responsive top-level Niratan window**

Run: `.\build-and-run.ps1`

- [ ] **Step 4: If the test video is available, set shadow to `10.0` and confirm the subtitle has one soft halo without eight-direction copies**

