$ErrorActionPreference = "Stop"

Write-Host "=== Stopping any existing Hoshi ==="
Stop-Process -Name "Hoshi" -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

# Build native DLL if not present
$nativeDll = ".\native\out\hoshidicts_c_api.dll"
if (-not (Test-Path $nativeDll)) {
    Write-Host "=== Native DLL not found, building... ==="
    & .\build-native.ps1
}

Write-Host "=== Building Hoshi (x64) ==="
dotnet build -p:Platform=x64

$hoshiOutDir = ".\Hoshi\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64"
if ((Test-Path $nativeDll) -and (Test-Path $hoshiOutDir)) {
    Copy-Item $nativeDll "$hoshiOutDir\hoshidicts_c_api.dll" -Force
}

Write-Host "=== Launching Hoshi ==="
Start-Process "$hoshiOutDir\Hoshi.exe"
Write-Host "=== Done ==="
