---
name: build
description: Build Niratan in x64 Debug; the project ensures and copies the native dictionary DLL
---

# Build Niratan

Build the WinUI 3 project in x64 Debug configuration.

`Niratan.csproj` runs `scripts/Ensure-NativeHoshidicts.ps1` when the x64 DLL is missing and copies the resulting DLL into the build output. Do not manually duplicate that sequence.

## Commands

```powershell
dotnet build -p:Platform=x64
```
