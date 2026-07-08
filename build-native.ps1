$ErrorActionPreference = "Stop"
Write-Host "=== Building hoshidicts native library ==="

# Ensure submodules are initialized
if (-not (Test-Path "native/hoshidicts/external/zstd/CMakeLists.txt")) {
    Write-Host "Initializing hoshidicts submodules..."
    git submodule update --init --recursive native/hoshidicts
}

# Find CMake
$cmake = Get-Command cmake -ErrorAction SilentlyContinue
if (-not $cmake) {
    $cmake = "C:\Program Files\CMake\bin\cmake.exe"
    if (-not (Test-Path $cmake)) {
        $cmake = $null
    }
}
if (-not $cmake) {
    Write-Error "CMake not found. Install it via: winget install Kitware.CMake"
    exit 1
}

Write-Host "Using CMake: $cmake"

# Detect available C++ toolchain
$generator = $null
$vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vsWhere) {
    $vsPath = & $vsWhere -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
    if ($vsPath) {
        $vsVersion = & $vsWhere -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion 2>$null
        $vsMajor = 17
        if ($vsVersion -match '^(\d+)') {
            $vsMajor = [int]$Matches[1]
        } elseif ($vsPath -match '\\18\\') {
            $vsMajor = 18
        }

        Write-Host "Found Visual Studio at: $vsPath"
        $generator = if ($vsMajor -ge 18) { "Visual Studio 18 2026" } else { "Visual Studio 17 2022" }
    }
}

if (-not $generator) {
    # Try LLVM MinGW first (self-contained Clang + Windows SDK)
    $llvmMingwDir = $null
    $llvmMingwFound = Get-ChildItem -Path "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter "x86_64-w64-mingw32-clang.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($llvmMingwFound) { $llvmMingwDir = Split-Path $llvmMingwFound.FullName -Parent }
    if (-not $llvmMingwDir) {
        # Check winget link directory
        $altDir = Get-ChildItem -Path "$env:LOCALAPPDATA\Microsoft\WinGet\Links" -Filter "x86_64-w64-mingw32-clang.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($altDir) { $llvmMingwDir = Split-Path $altDir.FullName -Parent }
    }
    if ($llvmMingwDir) {
        $env:Path = "$llvmMingwDir;$env:Path"
        Write-Host "Using LLVM MinGW toolchain: $llvmMingwDir"
        $generator = "Ninja"
    }
}

if (-not $generator) {
    # Check for clang-cl (standalone LLVM, needs Windows SDK)
    $clang = Get-Command clang-cl -ErrorAction SilentlyContinue
    if (-not $clang) { $clang = Get-Command clang -ErrorAction SilentlyContinue }
    if (-not $clang) {
        $clangPaths = @(
            "C:\Program Files\LLVM\bin\clang-cl.exe",
            "C:\Program Files\LLVM\bin\clang.exe"
        )
        foreach ($p in $clangPaths) {
            if (Test-Path $p) { $clang = $p; break }
        }
    }
    if (-not $clang) {
        $found = Get-ChildItem -Path "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter "clang-cl.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) { $clang = $found.FullName }
    }
    if ($clang) {
        $clangDir = Split-Path $clang -Parent
        if ($clangDir -notin $env:Path.Split(';')) {
            $env:Path = "$clangDir;$env:Path"
        }
        Write-Host "Using Clang: $clang"
        $generator = "Ninja"
    }
}

if ($generator -eq "Ninja") {
    # Ensure Ninja is findable
    $ninja = Get-Command ninja -ErrorAction SilentlyContinue
    if (-not $ninja) {
        $foundNinja = Get-ChildItem -Path "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter "ninja.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($foundNinja) { $env:Path = "$(Split-Path $foundNinja.FullName -Parent);$env:Path" }
    }
}

if (-not $generator) {
    Write-Error @"
No C++ compiler found.

To build the native dictionary engine, install one of:
  1. Visual Studio 2022 Community (free) with "Desktop development with C++" workload
     https://visualstudio.microsoft.com/downloads/
  2. Visual Studio Build Tools (command-line only):
     winget install Microsoft.VisualStudio.2022.BuildTools --override "--add Microsoft.VisualStudio.Workload.VCTools"
  3. LLVM/Clang:
     winget install LLVM.LLVM

After installing, re-run: .\build-native.ps1
"@
    exit 1
}

$nativeDir = "$PSScriptRoot\native\hoshidicts_c_api"
$buildDir = "$nativeDir\build"
$outDir = "$PSScriptRoot\native\out"

New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# Configure
Write-Host "Configuring CMake with generator: $generator..."
Push-Location $buildDir
try {
    if ($generator -eq "Visual Studio 17 2022") {
        & $cmake .. -G $generator -A x64
    } else {
        & $cmake .. -G $generator -DCMAKE_BUILD_TYPE=Release
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "CMake configuration failed"
        exit 1
    }

    # Build
    Write-Host "Building..."
    & $cmake --build . --config Release
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Native build failed"
        exit 1
    }
} finally {
    Pop-Location
}

# Copy output
$dll = Get-ChildItem -Path $buildDir -Recurse -Filter "hoshidicts_c_api.dll" | Select-Object -First 1
if ($dll) {
    Copy-Item $dll.FullName "$outDir\hoshidicts_c_api.dll" -Force
    Write-Host "Native DLL: $($dll.FullName)"
    Write-Host "Copied to: $outDir\hoshidicts_c_api.dll"

    # Also copy to Hoshi output directories
    $hoshiOutDirs = @(
        "$PSScriptRoot\Hoshi\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64",
        "$PSScriptRoot\Hoshi\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64"
    )
    foreach ($dir in $hoshiOutDirs) {
        if (Test-Path $dir) {
            Copy-Item "$outDir\hoshidicts_c_api.dll" "$dir\hoshidicts_c_api.dll" -Force
            Write-Host "Copied to: $dir"
        }
    }
} else {
    Write-Error "Could not find built DLL"
    exit 1
}

Write-Host "=== Native build complete ==="
