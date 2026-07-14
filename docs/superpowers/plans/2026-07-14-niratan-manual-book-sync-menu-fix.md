# Niratan Manual Book Sync Menu Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore functional per-book sync actions and make Manual mode expose exactly one Sync submenu containing Import and Export.

**Architecture:** Keep sync decisions and remote work in `NovelLibraryPageViewModel` and `TtuSyncService`. Replace the `MenuFlyout`'s broken cross-namescope page bindings with a narrow UI-only bridge: the flyout opening handler applies the existing ViewModel visibility properties, and click handlers forward the tagged book item to the existing asynchronous commands.

**Tech Stack:** C#/.NET 10, WinUI 3, Windows App SDK, CommunityToolkit.Mvvm, xUnit v3, FluentAssertions

## Global Constraints

- Build and test Windows x64 only; do not add an ARM64 validation requirement.
- Preserve `View → ViewModel → Service` layering; code-behind may only manage flyout presentation and forward UI events to ViewModel commands.
- Do not modify any code under `native/hoshidicts/`.
- Do not change Google Drive formats, authentication, credential storage, automatic Reader sync, or remote-book download behavior.
- Preserve the local Client Secret behavior implemented by the existing credential store.
- Do not add dependencies.
- Do not perform a live Google Drive write during automated or UI verification.
- Keep the change small and reviewable.

---

## File map

- `Niratan.Tests/Views/Pages/NovelLibraryPageAssetTests.cs`: adds the regression contract that fails while sync actions depend on `ElementName=ThisPage` from inside the flyout namescope.
- `Niratan/Views/Pages/NovelLibraryPage.xaml`: declares the flyout opening event, tags each action with the templated book item, and replaces broken command/visibility bindings with UI event hooks.
- `Niratan/Views/Pages/NovelLibraryPage.xaml.cs`: applies mutually exclusive Auto/Manual presentation and forwards the selected book to the existing ViewModel commands.

### Task 1: Restore the per-book sync flyout bridge

**Files:**
- Modify: `Niratan.Tests/Views/Pages/NovelLibraryPageAssetTests.cs`
- Modify: `Niratan/Views/Pages/NovelLibraryPage.xaml`
- Modify: `Niratan/Views/Pages/NovelLibraryPage.xaml.cs`

**Interfaces:**
- Consumes: `NovelLibraryPageViewModel.ShowAutomaticBookSyncAction : bool`
- Consumes: `NovelLibraryPageViewModel.ShowManualBookSyncAction : bool`
- Consumes: `IAsyncRelayCommand<NovelBookItemViewModel> SyncNovelCommand`
- Consumes: `IAsyncRelayCommand<NovelBookItemViewModel> ImportNovelFromTtuCommand`
- Consumes: `IAsyncRelayCommand<NovelBookItemViewModel> ExportNovelCommand`
- Produces: `NovelBookContextFlyout_Opening(object sender, object e)`
- Produces: `SyncNovelMenuItem_Click(object sender, RoutedEventArgs e)`
- Produces: `ImportNovelFromTtuMenuItem_Click(object sender, RoutedEventArgs e)`
- Produces: `ExportNovelMenuItem_Click(object sender, RoutedEventArgs e)`

- [ ] **Step 1: Write the failing XAML contract test**

Add this test to `NovelLibraryPageAssetTests`:

```csharp
[Fact]
public void LocalNovelSyncFlyout_UsesExplicitUiBridgeInsteadOfCrossNamescopeBindings()
{
    var xaml = File.ReadAllText(
        Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml"));
    var code = File.ReadAllText(
        Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml.cs"));

    xaml.Should().Contain("Opening=\"NovelBookContextFlyout_Opening\"");
    xaml.Should().Contain("Click=\"SyncNovelMenuItem_Click\"");
    xaml.Should().Contain("Click=\"ImportNovelFromTtuMenuItem_Click\"");
    xaml.Should().Contain("Click=\"ExportNovelMenuItem_Click\"");
    xaml.Should().NotContain(
        "Command=\"{Binding ViewModel.SyncNovelCommand, ElementName=ThisPage}\"");
    xaml.Should().NotContain(
        "Visibility=\"{Binding ViewModel.ShowAutomaticBookSyncAction");
    xaml.Should().NotContain(
        "Visibility=\"{Binding ViewModel.ShowManualBookSyncAction");

    code.Should().Contain(
        "automaticItem.Visibility = ViewModel.ShowAutomaticBookSyncAction");
    code.Should().Contain(
        "manualSubmenu.Visibility = ViewModel.ShowManualBookSyncAction");
    code.Should().Contain("ViewModel.SyncNovelCommand.ExecuteAsync(novelItem)");
    code.Should().Contain("ViewModel.ImportNovelFromTtuCommand.ExecuteAsync(novelItem)");
    code.Should().Contain("ViewModel.ExportNovelCommand.ExecuteAsync(novelItem)");
}
```

- [ ] **Step 2: Run the new test and verify RED**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageAssetTests.LocalNovelSyncFlyout_UsesExplicitUiBridgeInsteadOfCrossNamescopeBindings"
```

Expected: FAIL because `NovelBookContextFlyout_Opening` and the three click handlers are absent, while the broken `ElementName` command and visibility bindings are still present.

- [ ] **Step 3: Replace broken flyout bindings with explicit UI hooks**

In the local-book template in `NovelLibraryPage.xaml`, update the sync portion of the flyout to this shape while preserving existing localized `x:Uid` values, icons, automation IDs, and surrounding menu items:

```xml
<MenuFlyout Opening="NovelBookContextFlyout_Opening">
    <!-- Existing Match, separator, and Move items remain unchanged. -->
    <MenuFlyoutItem x:Uid="NovelBookSyncMenuItem"
                    AutomationProperties.AutomationId="NovelBookSyncMenuItem"
                    Tag="{x:Bind}"
                    Visibility="Collapsed"
                    Click="SyncNovelMenuItem_Click">
        <MenuFlyoutItem.Icon>
            <FontIcon Glyph="&#xE895;" />
        </MenuFlyoutItem.Icon>
    </MenuFlyoutItem>
    <MenuFlyoutSubItem x:Uid="NovelBookSyncSubmenu"
                       AutomationProperties.AutomationId="NovelBookSyncSubmenu"
                       Visibility="Collapsed">
        <MenuFlyoutSubItem.Icon>
            <FontIcon Glyph="&#xE895;" />
        </MenuFlyoutSubItem.Icon>
        <MenuFlyoutItem x:Uid="NovelBookSyncImportMenuItem"
                        AutomationProperties.AutomationId="NovelBookSyncImportMenuItem"
                        Tag="{x:Bind}"
                        Click="ImportNovelFromTtuMenuItem_Click">
            <MenuFlyoutItem.Icon>
                <FontIcon Glyph="&#xE896;" />
            </MenuFlyoutItem.Icon>
        </MenuFlyoutItem>
        <MenuFlyoutItem x:Uid="NovelBookSyncExportMenuItem"
                        AutomationProperties.AutomationId="NovelBookSyncExportMenuItem"
                        Tag="{x:Bind}"
                        Click="ExportNovelMenuItem_Click">
            <MenuFlyoutItem.Icon>
                <FontIcon Glyph="&#xE898;" />
            </MenuFlyoutItem.Icon>
        </MenuFlyoutItem>
    </MenuFlyoutSubItem>
    <!-- Existing separator and Delete item remain unchanged. -->
</MenuFlyout>
```

Do not alter the local-card `OpenNovelCommand`, the remote-book template, or any sync service.

- [ ] **Step 4: Add the UI-only presentation and command-forwarding handlers**

Add `using Microsoft.UI.Xaml.Automation;` to `NovelLibraryPage.xaml.cs`, then add these handlers inside `NovelLibraryPage`:

```csharp
private void NovelBookContextFlyout_Opening(object sender, object e)
{
    if (sender is not MenuFlyout flyout)
        return;

    var automaticItem = flyout.Items
        .OfType<MenuFlyoutItem>()
        .FirstOrDefault(item =>
            AutomationProperties.GetAutomationId(item) == "NovelBookSyncMenuItem");
    var manualSubmenu = flyout.Items
        .OfType<MenuFlyoutSubItem>()
        .FirstOrDefault(item =>
            AutomationProperties.GetAutomationId(item) == "NovelBookSyncSubmenu");

    if (automaticItem != null)
    {
        automaticItem.Visibility = ViewModel.ShowAutomaticBookSyncAction
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    if (manualSubmenu != null)
    {
        manualSubmenu.Visibility = ViewModel.ShowManualBookSyncAction
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}

private async void SyncNovelMenuItem_Click(object sender, RoutedEventArgs e)
{
    if ((sender as MenuFlyoutItem)?.Tag is NovelBookItemViewModel novelItem)
        await ViewModel.SyncNovelCommand.ExecuteAsync(novelItem);
}

private async void ImportNovelFromTtuMenuItem_Click(object sender, RoutedEventArgs e)
{
    if ((sender as MenuFlyoutItem)?.Tag is NovelBookItemViewModel novelItem)
        await ViewModel.ImportNovelFromTtuCommand.ExecuteAsync(novelItem);
}

private async void ExportNovelMenuItem_Click(object sender, RoutedEventArgs e)
{
    if ((sender as MenuFlyoutItem)?.Tag is NovelBookItemViewModel novelItem)
        await ViewModel.ExportNovelCommand.ExecuteAsync(novelItem);
}
```

These handlers must not inspect timestamps, choose directions, call services, or show notifications; those responsibilities remain in the ViewModel and service.

- [ ] **Step 5: Run the new contract test and verify GREEN**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageAssetTests.LocalNovelSyncFlyout_UsesExplicitUiBridgeInsteadOfCrossNamescopeBindings"
```

Expected: PASS.

- [ ] **Step 6: Run focused novel-library and sync regression tests**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageAssetTests|FullyQualifiedName~NovelLibraryPageViewModelTests|FullyQualifiedName~TtuSyncServiceTests"
```

Expected: all selected tests PASS, including the existing Auto/Import/Export direction mapping, result notification, cancellation, and deduplication tests.

- [ ] **Step 7: Commit the tested fix**

```powershell
git add -- Niratan.Tests/Views/Pages/NovelLibraryPageAssetTests.cs Niratan/Views/Pages/NovelLibraryPage.xaml Niratan/Views/Pages/NovelLibraryPage.xaml.cs
git commit -m "fix(sync): restore per-book flyout commands"
```

### Task 2: Verify the x64 app and Niratan menu behavior

**Files:**
- Verify only: `Niratan/Niratan.csproj`
- Verify only: `Niratan.Tests/Niratan.Tests.csproj`
- Verify only: `Niratan/Views/Pages/NovelLibraryPage.xaml`

**Interfaces:**
- Consumes: the Task 1 flyout handlers and existing ViewModel commands.
- Produces: build, test, process-path, responsive-window, and UI Automation evidence without a Google Drive write.

- [ ] **Step 1: Stop only the running sync-worktree executable before rebuilding**

Run from `D:\CODE\Yukari\.worktrees\niratan-sync-parity`:

```powershell
$exe = [System.IO.Path]::GetFullPath('.\Niratan\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Niratan.exe')
Get-CimInstance Win32_Process -Filter "Name='Niratan.exe'" |
    Where-Object { $_.ExecutablePath -eq $exe } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

Expected: only the exact `niratan-sync-parity` process stops; main-checkout and installed Niratan processes remain untouched.

- [ ] **Step 2: Run the repository-required x64 build**

```powershell
dotnet build -p:Platform=x64
```

Expected: build succeeds with 0 errors. Existing NU1903 warnings do not fail the build.

- [ ] **Step 3: Run the complete x64 test suite**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64
```

Expected: all tests PASS. Existing xUnit1051 warnings may remain; no new warning category should be introduced by this fix.

- [ ] **Step 4: Launch the exact sync-worktree executable**

```powershell
$exe = 'D:\CODE\Yukari\.worktrees\niratan-sync-parity\Niratan\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Niratan.exe'
$outDir = Split-Path -Parent $exe
Start-Process -FilePath $exe -WorkingDirectory $outDir
```

Verify with process inspection that the running `Niratan.exe` path equals `$exe`, its main window handle is non-zero, its title is `Niratan`, and it is responding. Leave this verified worktree instance running.

- [ ] **Step 5: Verify Manual mode exposes one Sync entry**

Using Windows UI Automation against the window owned by the exact worktree process:

1. Confirm the local setting remains `EnableSync=true` and `SyncMode=Manual` without editing settings.
2. Open a local novel card context menu.
3. Confirm the top level contains exactly one item named **Sync**, and that item is a submenu.
4. Expand the submenu and confirm it contains **Import** and **Export**.
5. Close the menu with Escape.

Do not invoke **Import** or **Export** during this verification because Export writes remote Google Drive data and Import can modify local reading sidecars.

- [ ] **Step 6: Confirm the worktree is clean and record evidence**

```powershell
git status --short
git log -2 --oneline
```

Expected: the worktree is clean; the latest code commit is `fix(sync): restore per-book flyout commands`, preceded by this implementation-plan commit or the design commit depending on when the plan is committed.
