---
name: test
description: Run Niratan unit tests (x64 Debug) with xUnit + FluentAssertions
---

# Run Tests

Run the full test suite for Niratan.

## Test Framework

- xUnit v3 + FluentAssertions + Moq

## Commands

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64
```

## Notes

- Tests copy `native\out\hoshidicts_c_api.dll` to the test output directory automatically via MSBuild target.
- If native DLL is not built yet, run `.\build-native.ps1` first.
