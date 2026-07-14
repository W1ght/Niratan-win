---
name: verify-reader
description: Full reader verification flow - build, launch, screenshot, diagnostics (docs/VERIFICATION.md §2)
---

# Verify Reader

Execute the full reader verification flow defined in [docs/VERIFICATION.md §2](../../docs/VERIFICATION.md).

## Prerequisites

- `NIRATAN_NOVEL_READER_ARTIFACT_DIR` env var set (default: `docs/superpowers/artifacts/novel-reader`)
- Test EPUB available at `C:\Users\Wight\Downloads\哈利波特1魔法石.epub`

## Verification Flow

### 1. Build and Launch

```powershell
.\build-and-run.ps1
```

### 2. Verify via UI Automation

Use FlaUI / UIA3 to:
1. Navigate to `NovelNavItem`
2. Open the test EPUB (Harry Potter) via `NovelBookCard_<bookId>`
3. Wait for `NovelReaderPage` to appear
4. Wait for `window.__niratanReaderState.bridgeReady == true`
5. Wait for `statusText` to show `EPUB loaded`
6. Wait for `hasRenderedText == true`

### 3. Pagination Check

- Flip pages multiple times (via keyboard, not coordinate clicks)
- Check no content drift, clipping, blank pages, or chapter state errors
- Resize window and confirm reflow works

### 4. Capture Diagnostics

Read `window.__niratanReaderState` and verify:
- `bridgeReady == true`
- `statusText` does NOT contain `Reader bridge error`
- `sectionCount > 0`
- `hasRenderedText == true`
- `readerRect.height > 0`
- `contentRect.height > 0`
- Bottom blank ratio < 20%

### 5. Save Artifacts

Save to `$env:NIRATAN_NOVEL_READER_ARTIFACT_DIR`:
- WebView2 content screenshot
- `window.__niratanReaderState` JSON
- UIA tree summary
- Window screenshot

### 6. Assertions

- Reading area is NOT blank
- Not stuck on title/error state
- Content fills the reading area
- Scroll/page state consistent (no out-of-bounds)

## Harry Potter Regression Case

```
Book: Harry Potter and the Sorcerer's Stone
Path: C:\Users\Wight\Downloads\哈利波特1魔法石.epub
Expected: bridge ready, EPUB loaded, visible content, no errors
```

## Commands

```powershell
# Full verification
.\build-and-run.ps1
# Wait for app, then run UIA verification
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReader"
```
