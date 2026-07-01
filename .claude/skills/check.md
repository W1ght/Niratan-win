---
name: check
description: Build + test in one pass — quick pre-commit / pre-PR check
---

# Quick Check

Run build and tests in sequence. Use before committing or opening a PR.

## What It Does

1. `dotnet build -p:Platform=x64`
2. `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64`

Fails fast if build fails; tests only run after a clean build.

## Commands

```powershell
dotnet build -p:Platform=x64 && dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
```
