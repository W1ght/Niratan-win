---
name: build-native
description: Build native hoshidicts_c_api.dll from submodule
---

# Build Native DLL

Build the native `hoshidicts_c_api.dll` from `native/hoshidicts/` submodule.

## When to Use

- After `git submodule update` pulls new native code
- When `native/out/hoshidicts_c_api.dll` is missing or stale
- Before running tests that depend on dictionary lookups

## Important

**Do NOT modify any code under `native/hoshidicts/`.** All dictionary functionality must use the C API DLL via P/Invoke. See agents.md Section 12.

## Commands

```powershell
.\build-native.ps1
```
