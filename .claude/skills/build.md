---
name: build
description: Build Niratan (x64 Debug) with native DLL copy
---

# Build Niratan

Build the WinUI 3 project in x64 Debug configuration.

## Steps

1. If native DLL (`native\out\hoshidicts_c_api.dll`) is missing, run `.\build-native.ps1` first.
2. Run `dotnet build -p:Platform=x64`
3. Copy `native\out\hoshidicts_c_api.dll` to the build output directory:
   ```
   Niratan\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\
   ```

## Commands

```powershell
dotnet build -p:Platform=x64
```
