$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$project = Join-Path $repositoryRoot "Hoshi\Hoshi.csproj"
$ensureNativeScript = Join-Path $repositoryRoot "scripts\Ensure-NativeHoshidicts.ps1"
$outputDirectory = Join-Path $repositoryRoot "Hoshi\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64"
$executable = Join-Path $outputDirectory "Hoshi.exe"
$nativeDll = Join-Path $outputDirectory "hoshidicts_c_api.dll"

Write-Host "=== Stopping any existing Hoshi ==="
Stop-Process -Name "Hoshi" -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Host "=== Preparing native dictionary DLL ==="
& $ensureNativeScript -RepositoryRoot $repositoryRoot

Write-Host "=== Building Hoshi (x64) ==="
dotnet build $project -c Debug -p:Platform=x64
if ($LASTEXITCODE -ne 0) {
    throw "Hoshi build failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $executable)) {
    throw "Hoshi executable not found: $executable"
}
if (-not (Test-Path -LiteralPath $nativeDll)) {
    throw "Native dictionary DLL not found beside Hoshi.exe: $nativeDll"
}

Write-Host "=== Launching Hoshi ==="
$process = Start-Process -FilePath $executable -WorkingDirectory $outputDirectory -PassThru
$deadline = [DateTime]::UtcNow.AddSeconds(20)
do {
    Start-Sleep -Milliseconds 250
    $process.Refresh()
    if ($process.HasExited) {
        throw "Hoshi exited during startup with code $($process.ExitCode)."
    }
} while ($process.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)

if ($process.MainWindowHandle -eq 0) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    throw "Hoshi did not create a main window within 20 seconds."
}

Write-Host "=== Hoshi ready ==="
Write-Host "PID: $($process.Id)"
Write-Host "Executable: $($process.Path)"
