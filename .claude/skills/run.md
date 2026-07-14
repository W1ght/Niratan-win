---
name: run
description: Build and launch Niratan (build-and-run.ps1)
---

# Build and Run Niratan

Build the project and launch the app for manual testing.

## Steps

Executes `build-and-run.ps1` which:
1. Stops any existing Niratan process
2. Builds native DLL if missing (`build-native.ps1`)
3. Builds `Niratan.slnx` (x64)
4. Copies `hoshidicts_c_api.dll` to build output
5. Launches `Niratan.exe`

## Prerequisites

- Test EPUB at `C:\Users\Wight\Downloads\哈利波特1魔法石.epub`

## Commands

```powershell
.\build-and-run.ps1
```
