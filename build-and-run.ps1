$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$project = Join-Path $repositoryRoot "Niratan\Niratan.csproj"
$ensureNativeScript = Join-Path $repositoryRoot "scripts\Ensure-NativeHoshidicts.ps1"
$outputDirectory = Join-Path $repositoryRoot "Niratan\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64"
$executable = Join-Path $outputDirectory "Niratan.exe"
$nativeDll = Join-Path $outputDirectory "hoshidicts_c_api.dll"

Write-Host "=== Stopping any existing Niratan ==="
$expectedExecutable = [System.IO.Path]::GetFullPath($executable)
$workspaceProcesses = @(Get-CimInstance Win32_Process -Filter "Name = 'Niratan.exe'" |
    Where-Object {
        $_.ExecutablePath -and
        [System.IO.Path]::GetFullPath($_.ExecutablePath).Equals(
            $expectedExecutable,
            [System.StringComparison]::OrdinalIgnoreCase)
    })
foreach ($workspaceProcess in $workspaceProcesses) {
    Stop-Process -Id $workspaceProcess.ProcessId -Force
}
if ($workspaceProcesses.Count -gt 0) {
    Start-Sleep -Seconds 1
}

Write-Host "=== Preparing native dictionary DLL ==="
& $ensureNativeScript -RepositoryRoot $repositoryRoot

Write-Host "=== Building Niratan (x64) ==="
dotnet build $project -c Debug -p:Platform=x64
if ($LASTEXITCODE -ne 0) {
    throw "Niratan build failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $executable)) {
    throw "Niratan executable not found: $executable"
}
if (-not (Test-Path -LiteralPath $nativeDll)) {
    throw "Native dictionary DLL not found beside Niratan.exe: $nativeDll"
}

Write-Host "=== Launching Niratan ==="
$process = Start-Process -FilePath $executable -WorkingDirectory $outputDirectory -PassThru
$deadline = [DateTime]::UtcNow.AddSeconds(20)
do {
    Start-Sleep -Milliseconds 250
    $process.Refresh()
    if ($process.HasExited) {
        throw "Niratan exited during startup with code $($process.ExitCode)."
    }
} while ($process.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)

if ($process.MainWindowHandle -eq 0) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    throw "Niratan did not create a main window within 20 seconds."
}

Write-Host "=== Niratan ready ==="
Write-Host "PID: $($process.Id)"
Write-Host "Executable: $($process.Path)"
