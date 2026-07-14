# Statistics Calendar Compact Heatmap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the oversized WinUI reading-calendar item slots with the compact 12-pixel Niratan heatmap and consistent four-pixel gaps.

**Architecture:** Keep the existing `CalendarDays` data and `ListView` selection flow. Constrain the view layer at all three sizing boundaries: the day template, the `ListViewItem` container, and the `ItemsWrapGrid` slot, so WinUI theme touch-target defaults cannot expand the heatmap.

**Tech Stack:** WinUI 3 XAML, Windows App SDK, C#/.NET 10, xUnit v3, FluentAssertions.

## Global Constraints

- Do not modify `native/hoshidicts/` or either Niratan reference submodule.
- Keep the existing View → ViewModel → Service layering; this fix is XAML-only production code.
- Each visible day cell is exactly 12 by 12 effective pixels.
- The visible gap between adjacent cells is exactly four effective pixels.
- Keep seven rows, recent-year horizontal scrolling, selection, accessibility text, and detail updates.
- Add no dependency and change no statistics, cache, sidecar, or Google Drive schema.

---

### Task 1: Enforce compact heatmap slots

**Files:**
- Modify: `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs:2618-2645`
- Modify: `Niratan/Views/Controls/NovelStatisticsDashboardView.xaml:55-69`
- Modify: `Niratan/Views/Controls/NovelStatisticsDashboardView.xaml:472-496`

**Interfaces:**
- Consumes: `NovelStatisticsCalendarDayDisplay`, `CalendarDays`, and `SelectedCalendarDay` without changing their types.
- Produces: a 12-pixel day template in fixed 16-pixel item slots across seven rows.

- [ ] **Step 1: Write the failing XAML contract assertions**

In `NovelLibraryPage_ExposesStatisticsDashboard`, add:

```csharp
var calendarTemplateStart = dashboardXaml.IndexOf(
    "<DataTemplate x:Key=\"CalendarDayTemplate\"",
    StringComparison.Ordinal);
var calendarTemplateEnd = dashboardXaml.IndexOf(
    "</DataTemplate>",
    calendarTemplateStart,
    StringComparison.Ordinal);
calendarTemplateStart.Should().BeGreaterThanOrEqualTo(0);
calendarTemplateEnd.Should().BeGreaterThan(calendarTemplateStart);

var calendarTemplateXaml = dashboardXaml[calendarTemplateStart..calendarTemplateEnd];
calendarTemplateXaml.Should().Contain("<Grid Width=\"12\"");
calendarTemplateXaml.Should().Contain("Height=\"12\"");
calendarTemplateXaml.Should().Contain("Margin=\"2\"");

dashboardXaml.Should().Contain("<ListView MaxHeight=\"132\"");
dashboardXaml.Should().Contain("Padding=\"10\"");
dashboardXaml.Should().Contain("<Setter Property=\"MinWidth\" Value=\"0\" />");
dashboardXaml.Should().Contain("<Setter Property=\"MinHeight\" Value=\"0\" />");
dashboardXaml.Should().Contain("<ItemsWrapGrid ItemWidth=\"16\"");
dashboardXaml.Should().Contain("ItemHeight=\"16\"");
dashboardXaml.Should().Contain("MaximumRowsOrColumns=\"7\"");
```

- [ ] **Step 2: Run the asset test and confirm RED**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName=Niratan.Tests.Services.Novels.NovelReaderWebAssetTests.NovelLibraryPage_ExposesStatisticsDashboard"
```

Expected: FAIL because the day template is 18 pixels, the list is 190 pixels high, and the container/panel do not have compact fixed sizing.

- [ ] **Step 3: Implement the compact day template and list container**

Change `CalendarDayTemplate` to:

```xml
<Grid Width="12"
      Height="12"
      Margin="2"
      AutomationProperties.Name="{x:Bind AccessibleText}">
```

Change the calendar `ListView` and add its item-container style:

```xml
<ListView MaxHeight="132"
          Padding="10"
          x:Uid="NovelStatisticsCalendarControl"
          AutomationProperties.AutomationId="NovelStatisticsCalendar"
          ItemTemplate="{StaticResource CalendarDayTemplate}"
          ItemsSource="{Binding CalendarDays}"
          SelectedItem="{Binding SelectedCalendarDay, Mode=TwoWay}"
          SelectionMode="Single"
          ScrollViewer.HorizontalScrollBarVisibility="Auto"
          ScrollViewer.HorizontalScrollMode="Enabled"
          ScrollViewer.VerticalScrollBarVisibility="Disabled"
          ScrollViewer.VerticalScrollMode="Disabled">
    <ListView.ItemContainerStyle>
        <Style TargetType="ListViewItem">
            <Setter Property="Width" Value="16" />
            <Setter Property="Height" Value="16" />
            <Setter Property="MinWidth" Value="0" />
            <Setter Property="MinHeight" Value="0" />
            <Setter Property="Padding" Value="0" />
            <Setter Property="Margin" Value="0" />
            <Setter Property="HorizontalContentAlignment" Value="Left" />
            <Setter Property="VerticalContentAlignment" Value="Top" />
        </Style>
    </ListView.ItemContainerStyle>
```

Fix the panel slot:

```xml
<ItemsWrapGrid ItemWidth="16"
               ItemHeight="16"
               MaximumRowsOrColumns="7"
               Orientation="Vertical" />
```

- [ ] **Step 4: Run the asset and Dashboard tests and confirm GREEN**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --no-restore --filter "FullyQualifiedName~NovelReaderWebAssetTests.NovelLibraryPage_ExposesStatisticsDashboard|FullyQualifiedName~NovelStatisticsDashboard"
```

Expected: PASS with all matching tests and zero failures.

- [ ] **Step 5: Commit the compact layout**

```powershell
git add Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs Niratan/Views/Controls/NovelStatisticsDashboardView.xaml
git commit -m "fix(statistics): compact calendar heatmap spacing"
```

---

### Task 2: Document and verify the corrected heatmap

**Files:**
- Modify: `docs/CHANGELOG.md`
- Modify: `docs/VERIFICATION.md`

**Interfaces:**
- Consumes: the compact XAML contract from Task 1.
- Produces: persistent root-cause documentation and manual verification coverage.

- [ ] **Step 1: Record the root cause and verification requirement**

Add a changelog entry stating that default `ListViewItem` minimum extents expanded the heatmap slots and that fixed 16-pixel slots now render 12-pixel cells with four-pixel gaps. Update Dashboard verification to require seven compact rows, horizontal scrolling, no vertical scrollbar, and correct selection/detail behavior at wide and narrow widths.

- [ ] **Step 2: Run formatting and complete automated verification**

Run:

```powershell
git diff --check
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64
dotnet build -p:Platform=x64
```

Expected: no whitespace errors, all tests pass, and the x64 build completes with zero errors.

- [ ] **Step 3: Launch and perform UI verification**

Run:

```powershell
.\build-and-run.ps1
```

Open Statistics and verify at wide and narrow widths that cells are 12 pixels, gaps are four pixels, seven rows fit, horizontal scrolling remains available, and selecting a day updates the detail row.

- [ ] **Step 4: Commit documentation**

```powershell
git add docs/CHANGELOG.md docs/VERIFICATION.md
git commit -m "docs: record compact statistics heatmap"
```
