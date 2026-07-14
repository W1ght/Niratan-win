# Worktree Native DLL and Exact Launch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make fresh linked worktrees automatically acquire `hoshidicts_c_api.dll` and make `build-and-run.ps1` build, launch, and verify the executable rooted beside that script.

**Architecture:** A checked-in PowerShell preparation script resolves the current worktree and the primary checkout through Git's common directory, copying the shared native DLL before falling back to the existing native build. Both MSBuild projects call it before copying native output, while the run script anchors all build and launch paths to `$PSScriptRoot` and verifies a real window.

**Tech Stack:** PowerShell 5.1, MSBuild, .NET 10, WinUI 3, xUnit v3, FluentAssertions

## Global Constraints

- Target Windows 10+ x64; do not add a default ARM64 build.
- Do not modify any file under `native/hoshidicts/`.
- Preserve the existing `build-native.ps1` as the native compilation fallback.
- A successful launch requires a live process with a non-zero main-window handle.

---

### Task 1: Lock down development-script behavior

**Files:**
- Create: `Niratan.Tests/Build/DevelopmentScriptAssetTests.cs`
- Test: `Niratan.Tests/Build/DevelopmentScriptAssetTests.cs`

**Interfaces:**
- Consumes: repository-root scripts and project XML as text assets.
- Produces: regression tests for `Ensure-NativeHoshidicts.ps1`, root-anchored launch, and both MSBuild preparation targets.

- [ ] **Step 1: Write the failing asset tests**

```csharp
[Fact]
public void BuildProjects_PrepareNativeDllBeforeCopyingIt()
{
    Project("Niratan", "Niratan.csproj").Should().Contain("Ensure-NativeHoshidicts.ps1");
    Project("Niratan.Tests", "Niratan.Tests.csproj").Should().Contain("Ensure-NativeHoshidicts.ps1");
}

[Fact]
public void BuildAndRun_AnchorsBuildAndLaunchToItsOwnWorktree()
{
    var script = RootFile("build-and-run.ps1");
    script.Should().Contain("$PSScriptRoot");
    script.Should().Contain("Start-Process -FilePath $executable");
    script.Should().Contain("MainWindowHandle");
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter FullyQualifiedName~DevelopmentScriptAssetTests`

Expected: FAIL because the preparation script and exact launch code do not exist.

- [ ] **Step 3: Keep the failing tests unchanged for the implementation tasks**

The assertions define the externally reviewable script contract; do not weaken them to match implementation details that omit worktree rooting or window verification.

### Task 2: Automatically prepare the native DLL

**Files:**
- Create: `scripts/Ensure-NativeHoshidicts.ps1`
- Modify: `Niratan/Niratan.csproj`
- Modify: `Niratan.Tests/Niratan.Tests.csproj`
- Test: `Niratan.Tests/Build/DevelopmentScriptAssetTests.cs`

**Interfaces:**
- Consumes: optional `-RepositoryRoot`, `git rev-parse --path-format=absolute --git-common-dir`, primary checkout `native/out/hoshidicts_c_api.dll`, and `build-native.ps1`.
- Produces: `<current worktree>/native/out/hoshidicts_c_api.dll` or a terminating error.

- [ ] **Step 1: Add the preparation script**

```powershell
param([string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$localDll = Join-Path $RepositoryRoot 'native\out\hoshidicts_c_api.dll'
if (Test-Path -LiteralPath $localDll) { return }
$commonDirectory = (& git -C $RepositoryRoot rev-parse --path-format=absolute --git-common-dir).Trim()
$primaryRoot = Split-Path $commonDirectory -Parent
$sharedDll = Join-Path $primaryRoot 'native\out\hoshidicts_c_api.dll'
if (Test-Path -LiteralPath $sharedDll) {
    New-Item -ItemType Directory -Path (Split-Path $localDll -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $sharedDll -Destination $localDll -Force
} else {
    & (Join-Path $RepositoryRoot 'build-native.ps1')
}
if (-not (Test-Path -LiteralPath $localDll)) { throw "Native dictionary DLL was not prepared: $localDll" }
```

- [ ] **Step 2: Wire preparation before x64 copy targets**

Add an `EnsureNativeHoshidicts` target to each project with `BeforeTargets="CopyNativeHoshidicts"`, an x64/missing-file condition, and an `Exec` call to the script using `$(MSBuildProjectDirectory)`-anchored absolute paths.

```xml
<Target Name="EnsureNativeHoshidicts"
        BeforeTargets="CopyNativeHoshidicts"
        Condition="'$(Platform)' == 'x64' And !Exists('$(MSBuildProjectDirectory)\..\native\out\hoshidicts_c_api.dll')">
  <Exec Command="powershell -NoProfile -ExecutionPolicy Bypass -File &quot;$(MSBuildProjectDirectory)\..\scripts\Ensure-NativeHoshidicts.ps1&quot; -RepositoryRoot &quot;$(MSBuildProjectDirectory)\..&quot;" />
</Target>
```

- [ ] **Step 3: Run the focused tests and verify the native part is GREEN**

Run: `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter FullyQualifiedName~DevelopmentScriptAssetTests`

Expected: the project-wiring test passes; the launch-script test still fails.

### Task 3: Anchor and verify build-and-run

**Files:**
- Modify: `build-and-run.ps1`
- Test: `Niratan.Tests/Build/DevelopmentScriptAssetTests.cs`

**Interfaces:**
- Consumes: `$PSScriptRoot`, `scripts/Ensure-NativeHoshidicts.ps1`, explicit `Niratan/Niratan.csproj` path.
- Produces: an x64 Debug Niratan process whose executable is the current worktree output and whose main window is ready.

- [ ] **Step 1: Replace relative build and launch paths**

```powershell
$repositoryRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$project = Join-Path $repositoryRoot 'Niratan\Niratan.csproj'
$outputDirectory = Join-Path $repositoryRoot 'Niratan\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64'
$executable = Join-Path $outputDirectory 'Niratan.exe'
& (Join-Path $repositoryRoot 'scripts\Ensure-NativeHoshidicts.ps1') -RepositoryRoot $repositoryRoot
dotnet build $project -c Debug -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'Niratan build failed.' }
if (-not (Test-Path -LiteralPath $executable)) { throw "Niratan executable not found: $executable" }
$process = Start-Process -FilePath $executable -WorkingDirectory $outputDirectory -PassThru
```

- [ ] **Step 2: Verify the launched process owns a top-level window**

Poll for at most 20 seconds, refreshing the process each pass. Throw if it exits or never obtains a non-zero `MainWindowHandle`; otherwise print PID and executable path.

```powershell
$deadline = [DateTime]::UtcNow.AddSeconds(20)
do {
    Start-Sleep -Milliseconds 250
    $process.Refresh()
    if ($process.HasExited) { throw "Niratan exited during startup with code $($process.ExitCode)." }
} while ($process.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)
if ($process.MainWindowHandle -eq 0) { throw 'Niratan did not create a main window within 20 seconds.' }
```

- [ ] **Step 3: Run the focused tests and verify GREEN**

Run: `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter FullyQualifiedName~DevelopmentScriptAssetTests`

Expected: PASS.

### Task 4: Prove fresh-worktree recovery and exact launch

**Files:**
- Verify: ignored output under `native/out`, `Niratan/bin`, and `Niratan.Tests/bin`

**Interfaces:**
- Consumes: the new preparation and launch paths.
- Produces: runtime evidence that no manual DLL copy and no caller-directory assumption remain.

- [ ] **Step 1: Remove only ignored generated copies in the feature worktree**

Resolve and verify all absolute targets are under the feature worktree, then remove its `native/out/hoshidicts_c_api.dll` and generated app/test DLL copies.

- [ ] **Step 2: Run a normal x64 test build**

Run: `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64`

Expected: preparation copies the primary checkout DLL and all tests pass without `DllNotFoundException`.

- [ ] **Step 3: Invoke the worktree script from the primary checkout**

Run from `D:\CODE\Yukari`: `& 'D:\CODE\Yukari\.worktrees\bookshelf-mark-read\build-and-run.ps1'`

Expected: printed executable path begins with `D:\CODE\Yukari\.worktrees\bookshelf-mark-read\`, and the reported process has a non-zero main-window handle.

- [ ] **Step 4: Commit the infrastructure fix**

```powershell
git add -- build-and-run.ps1 scripts/Ensure-NativeHoshidicts.ps1 Niratan/Niratan.csproj Niratan.Tests/Niratan.Tests.csproj Niratan.Tests/Build/DevelopmentScriptAssetTests.cs
git commit -m "fix(dev): prepare native dll in worktrees"
```
