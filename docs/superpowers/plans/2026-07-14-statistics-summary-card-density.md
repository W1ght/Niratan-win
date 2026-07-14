# Statistics Summary Card Density Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enlarge the Today goal ring to the Hoshi reference size and remove the stretched empty interior from the This Week card.

**Architecture:** Keep the existing dashboard view model, adaptive placement, and data templates unchanged. Express both visual requirements as XAML layout contracts: a fixed `118×118` ring host with a directly sized `118×118` `ProgressRing`, and a top-aligned week card whose height is content-driven inside a shared Grid row.

**Tech Stack:** WinUI 3 XAML, C#/.NET 10, xUnit v3, FluentAssertions

## Global Constraints

- Target Windows 10+ x64 and do not build ARM64 by default.
- Preserve the existing MVVM boundaries; this change is view-only.
- Do not change statistics calculations, bindings, breakpoints, card order, or weekday tile dimensions.
- Use `dotnet build -p:Platform=x64` and `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64` for verification.

---

### Task 1: Lock the summary card layout contract

**Files:**
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs:2610`
- Modify: `Hoshi/Views/Controls/NovelStatisticsDashboardView.xaml:258-390`

**Interfaces:**
- Consumes: the existing `NovelStatisticsDashboardView.xaml` resource file loaded by `NovelLibraryPage_ExposesStatisticsDashboard`.
- Produces: a Today ring host and `ProgressRing` with exact effective size `118×118`, plus a `WeekCard` with `VerticalAlignment="Top"`.

- [ ] **Step 1: Write the failing XAML contract assertions**

Add the following assertions after the existing Today and Week card AutomationId checks:

```csharp
var todayCardStart = dashboardXaml.IndexOf(
    "<Border x:Name=\"TodayCard\"",
    StringComparison.Ordinal);
var todayCardEnd = dashboardXaml.IndexOf(
    "<Border x:Name=\"GoalCard\"",
    todayCardStart,
    StringComparison.Ordinal);
todayCardStart.Should().BeGreaterThanOrEqualTo(0);
todayCardEnd.Should().BeGreaterThan(todayCardStart);

var todayCardXaml = dashboardXaml[todayCardStart..todayCardEnd];
var todayProgressRingStart = todayCardXaml.IndexOf(
    "<ProgressRing IsActive=\"True\"",
    StringComparison.Ordinal);
var todayProgressRingEnd = todayCardXaml.IndexOf(
    "/>",
    todayProgressRingStart,
    StringComparison.Ordinal);
todayProgressRingStart.Should().BeGreaterThanOrEqualTo(0);
todayProgressRingEnd.Should().BeGreaterThan(todayProgressRingStart);

var todayProgressRingXaml = todayCardXaml[
    todayProgressRingStart..(todayProgressRingEnd + 2)];
todayProgressRingXaml.Should().Contain("Width=\"118\"");
todayProgressRingXaml.Should().Contain("Height=\"118\"");

var weekCardStart = dashboardXaml.IndexOf(
    "<Border x:Name=\"WeekCard\"",
    StringComparison.Ordinal);
var weekCardEnd = dashboardXaml.IndexOf(
    "<Border x:Name=\"SelectedRangeCard\"",
    weekCardStart,
    StringComparison.Ordinal);
weekCardStart.Should().BeGreaterThanOrEqualTo(0);
weekCardEnd.Should().BeGreaterThan(weekCardStart);

var weekCardXaml = dashboardXaml[weekCardStart..weekCardEnd];
weekCardXaml.Should().Contain("VerticalAlignment=\"Top\"");
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderWebAssetTests.NovelLibraryPage_ExposesStatisticsDashboard"
```

Expected: FAIL because the Today `ProgressRing` has no direct width or height and therefore uses WinUI's default `32×32` style; the Week card also has no `VerticalAlignment="Top"`.

- [ ] **Step 3: Implement the minimal XAML changes**

Change the Today ring host and control to:

```xml
<Grid Grid.Column="1"
      Width="118"
      Height="118">
    <ProgressRing IsActive="True"
                  Width="118"
                  Height="118"
                  IsIndeterminate="False"
                  Maximum="100"
                  Value="{Binding Today.TargetPercent}" />
</Grid>
```

Change the Week card opening element to include:

```xml
<Border x:Name="WeekCard"
        Grid.Row="2"
        Grid.Column="0"
        VerticalAlignment="Top"
        AutomationProperties.AutomationId="NovelStatisticsWeekCard"
        Style="{StaticResource DashboardCardStyle}"
        Visibility="{Binding HasData, Converter={StaticResource BooleanToVisibilityConverter}}">
```

- [ ] **Step 4: Run the focused test and dashboard tests to verify GREEN**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderWebAssetTests.NovelLibraryPage_ExposesStatisticsDashboard"
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --no-build --filter "FullyQualifiedName~NovelStatisticsDashboard|FullyQualifiedName~NovelReaderWebAssetTests.NovelLibraryPage_ExposesStatisticsDashboard"
```

Expected: the exact contract test passes, followed by all dashboard-focused tests passing.

- [ ] **Step 5: Commit the implementation**

```powershell
git add Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs Hoshi/Views/Controls/NovelStatisticsDashboardView.xaml
git commit -m "fix(statistics): rebalance summary card density"
```

### Task 2: Verify the rendered dashboard and integration

**Files:**
- Modify: `docs/CHANGELOG.md`
- Modify: `docs/VERIFICATION.md`

**Interfaces:**
- Consumes: the updated summary card XAML from Task 1.
- Produces: documented root cause, repeatable visual verification, and a merge-ready feature branch.

- [ ] **Step 1: Update the change and verification records**

Add this changelog entry above the existing dashboard entries:

```markdown
## Dashboard 今日圆环偏小且本周卡片过高

**原因**：
- 今日目标仅放大了宿主 Grid；`ProgressRing` 仍被 WinUI 默认样式固定为 32×32，因此视觉尺寸没有变化。
- 宽屏三列布局中，本周卡片与更高的排行卡共用同一 Grid 行；Border 默认纵向拉伸，使本周卡片内部出现大块空白。

**解决**：
- 将宿主与 `ProgressRing` 控件本身都固定为 118×118 effective pixels，对齐 Hoshi 的视觉层级。
- 让本周卡片顶部对齐并按自身内容高度呈现，保留指标、七日状态和自适应列布局。

---
```

Extend dashboard verification item 13 with this exact requirement:

```markdown
Today 目标环保持 118×118 effective pixels；This Week 卡片高度随自身内容收紧，不得因同一 Grid 行中的更高卡片而纵向拉伸。
```

- [ ] **Step 2: Run automated verification**

Run:

```powershell
git diff --check
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
dotnet build -p:Platform=x64 --no-restore
```

Expected: all tests pass and the build reports zero errors; existing package advisory warnings may remain.

- [ ] **Step 3: Run screenshot-only UI verification**

Launch with `./build-and-run.ps1`. Open Statistics using coordinate interactions derived from a screenshot-only window state (`include_text: false`). At narrow and wide widths, confirm:

- the Today ring is visibly larger and remains centered around its percentage;
- the This Week card ends shortly after the weekday tiles, with no empty stretched interior;
- no controls are clipped and the dashboard remains responsive;
- no full accessibility-tree enumeration is requested for the 365-day calendar.

- [ ] **Step 4: Commit documentation**

```powershell
git add docs/CHANGELOG.md docs/VERIFICATION.md
git commit -m "docs: record summary card density alignment"
```

- [ ] **Step 5: Finish the branch**

Use `superpowers:verification-before-completion`, then `superpowers:finishing-a-development-branch`. Under the user's standing auto-confirm instruction, choose local merge, preserve the dirty main worktree with a temporary stash, fast-forward `main`, restore the stash, rerun x64 tests/build on merged `main`, clean up this owned worktree and branch, and launch the merged app.
