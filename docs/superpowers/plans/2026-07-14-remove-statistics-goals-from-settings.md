# Remove Statistics Goals From Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Align the Windows statistics settings page with Niratan by removing daily and weekly goal editors without removing dashboard goals or changing saved goal values.

**Architecture:** Remove the settings-only XAML controls, bindings, and localized strings. Keep `NovelStatisticsSettings` and dashboard goal behavior intact; when the settings ViewModel saves visible options, copy the current goal values through unchanged.

**Tech Stack:** WinUI 3 XAML, C#/.NET 10, CommunityToolkit.Mvvm, xUnit v3, FluentAssertions.

## Global Constraints

- Do not change the statistics dashboard goal card, calculations, or persisted model shape.
- Preserve `DailyTargetType`, `DailyCharacterTarget`, `DailyDurationTargetMinutes`, and `WeeklyTargetDays` when saving remaining settings.
- Build and test only x64.

---

### Task 1: Remove settings-only goal editors

**Files:**
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`
- Modify: `Hoshi.Tests/ViewModels/Pages/StatisticsSettingsPageViewModelTests.cs`
- Modify: `Hoshi/Views/Pages/StatisticsSettingsPage.xaml`
- Modify: `Hoshi/ViewModels/Pages/StatisticsSettingsPageViewModel.cs`
- Modify: `Hoshi/Strings/en-US/Resources.resw`
- Modify: `Hoshi/Strings/zh-CN/Resources.resw`
- Modify: `docs/CHANGELOG.md`

**Interfaces:**
- Consumes: `NovelStatisticsSettings` persisted goal fields and existing statistics settings bindings.
- Produces: a statistics settings page containing only enable, autostart, and sync controls.

- [ ] **Step 1: Write the failing UI contract and preservation tests**

Assert that `StatisticsSettingsPage.xaml` excludes all `StatisticsDaily*` and `StatisticsWeekly*` controls, that settings-only localization keys are absent, and that saving autostart/sync retains existing goal values.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~StatisticsSettingsPageViewModelTests|FullyQualifiedName~AdvancedSettings_ExposesDedicatedStatisticsPage" --no-restore
```

Expected: failures show the goal controls and settings editor properties still exist.

- [ ] **Step 3: Implement the minimal removal**

Delete the daily/weekly goal XAML cards and settings-only resource entries. Remove the corresponding ViewModel editor properties and handlers; in `SaveSettings`, preserve the four target fields from `_settingsService.Current.StatisticsSettings`.

- [ ] **Step 4: Verify focused and full checks**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~StatisticsSettingsPageViewModelTests|FullyQualifiedName~AdvancedSettings_ExposesDedicatedStatisticsPage"
dotnet build -p:Platform=x64
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --no-build
```

Expected: focused tests pass, build reports 0 errors, and the full suite reports 0 failures.

- [ ] **Step 5: Launch and verify**

Start the exact x64 worktree executable, open Statistics settings, confirm Daily Goal and Weekly Goal are absent, then leave the responsive Hoshi instance running.

- [ ] **Step 6: Commit**

```powershell
git add Hoshi Hoshi.Tests docs
git commit -m "fix(settings): align statistics page with Niratan"
```
