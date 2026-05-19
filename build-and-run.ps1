$ErrorActionPreference = "Stop"
Write-Host "=== Stopping any existing Hoshi ==="
Stop-Process -Name "Hoshi" -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
Write-Host "=== Building Hoshi (x64) ==="
dotnet build -p:Platform=x64
Write-Host "=== Launching Hoshi ==="
Start-Process ".\Hoshi\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Hoshi.exe"
Write-Host "=== Done ==="
