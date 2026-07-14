param(
    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = "Stop"

$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$localDll = Join-Path $RepositoryRoot "native\out\hoshidicts_c_api.dll"
if (Test-Path -LiteralPath $localDll) {
    return
}

$commonDirectory = (& git -C $RepositoryRoot rev-parse --path-format=absolute --git-common-dir 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commonDirectory)) {
    throw "Unable to resolve the common Git directory for: $RepositoryRoot"
}

$primaryRoot = Split-Path $commonDirectory -Parent
$sharedDll = Join-Path $primaryRoot "native\out\hoshidicts_c_api.dll"
if (Test-Path -LiteralPath $sharedDll) {
    New-Item -ItemType Directory -Path (Split-Path $localDll -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $sharedDll -Destination $localDll -Force
    Write-Host "Prepared native dictionary DLL from shared checkout: $sharedDll"
} else {
    $buildNativeScript = Join-Path $RepositoryRoot "build-native.ps1"
    if (-not (Test-Path -LiteralPath $buildNativeScript)) {
        throw "Native build script not found: $buildNativeScript"
    }

    $nativeExitCode = 0
    Push-Location $RepositoryRoot
    try {
        & $buildNativeScript
        $nativeExitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
    if ($nativeExitCode -ne 0) {
        throw "Native dictionary build failed with exit code $nativeExitCode."
    }
}

if (-not (Test-Path -LiteralPath $localDll)) {
    throw "Native dictionary DLL was not prepared: $localDll"
}
