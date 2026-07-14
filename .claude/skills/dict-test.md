---
name: dict-test
description: Run dictionary-related tests (deinflector, lookup, import) per docs/VERIFICATION.md §3
---

# Dictionary Tests

Run dictionary-specific tests covering deinflection, lookup, and import logic.

## Affected Files

When modifying any of these, this skill is mandatory:
- `Niratan/Services/Dictionary/JapaneseDeinflector.cs`
- `Niratan/Services/Dictionary/DictionaryLookupService.cs`
- `Niratan/Services/Dictionary/PopupHtmlGenerator.cs`
- `Niratan/Views/Dictionary/DictionaryLookupPopup.cs`
- `Niratan/Views/Dictionary/DictionaryPopupOverlay.cs`
- `Niratan/Web/DictionaryPopup/popup.js`
- `Niratan/Views/Pages/NovelReaderPage.xaml.cs`

## Required Verification ([docs/VERIFICATION.md §3.2](../../docs/VERIFICATION.md))

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64
dotnet build -p:Platform=x64
```

## Interactive Verification

If popup or WebView2 lifecycle is involved, also launch the app:

```powershell
.\build-and-run.ps1
```

And verify:
- First lookup doesn't freeze UI
- Normal click, Shift hover, and nested popup lookup all return results
- Yomitan structured content renders as HTML (not raw JSON)
- Popup text, borders, overlay are readable in both dark and light themes
- Popup positioning works in both horizontal and vertical writing modes

## Deinflector Rules ([docs/VERIFICATION.md §3.3](../../docs/VERIFICATION.md))

When changing deinflector rules, also check:
- Condition flags match Android `Conditions` semantics
- `AddRule(...)` input/output conditions, rule group name, description match reference
- Special verbs and exception rules aren't swallowed by generic suffix rules
- `PosToConditions()` correctly parses Yomitan term `rules`

Reference:
```
docs/reference/hoshi/Niratan-Reader-Android/third_party/hoshidicts-kotlin-bridge/app/src/main/cpp/hoshidicts/src/deinflector.cpp
docs/reference/hoshi/Niratan-Reader-Android/third_party/hoshidicts-kotlin-bridge/app/src/main/cpp/hoshidicts/src/lookup.cpp
```

## Commands

```powershell
# Dictionary-specific tests
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Dictionary"

# Deinflector-specific tests
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Deinflector"
```
