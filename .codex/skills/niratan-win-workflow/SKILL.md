---
name: niratan-win-workflow
description: Route Niratan Win implementation, debugging, validation, upstream alignment, persistence, build, packaging, and release work to only the repository guidance required for Reader, Manga, Video, Dictionary, Popup, Anki, profiles, audio, sync, and imports.
---

# Niratan Win Workflow

Use this skill as the sole task router. Read only the sources required by the current change; do not preload every architecture or verification section.

## Start

1. Run `git status --short --branch` and preserve unrelated changes.
2. Inspect the nearest implementation, tests, and current truth-source section.
3. Select every route whose boundary the task touches.

## Route context

- Reader, WebView2, EPUB, Popup, Dictionary, Sasayaki, word audio, statistics, highlights, or Reader shortcuts: read the matching sections in `docs/ARCHITECTURE.md` and `docs/VERIFICATION.md`.
- Manga, CBZ/ZIP, EPUB manga, Mokuro, OCR, manga lookup, or image mining: read the Manga sections in those documents and the nearest `Niratan/Services/Manga`, `Niratan/Models/Manga`, `Niratan/Views/Manga`, and `Niratan.Tests/Services/Manga` files.
- Video, mpv, subtitles, remote media, playback windows, media history, or video mining: read the Video/YouTube sections in those documents and the nearest Video contracts.
- Profiles, AnkiConnect, sync, credentials, sidecars, catalog, migration, backup, or persistent user state: read the data, Anki, storage, and security sections in `docs/ARCHITECTURE.md`; use disposable fixtures.
- WinUI/XAML, navigation, localization, project files, dependencies, native DLL, build scripts, packaging, or runtime identity: inspect the project/script truth first; use the matching `.claude/skills/` guide only when it is still consistent with code.
- Release, version, tag, GitHub Actions, installer, or release assets: inspect `release.ps1` and the release workflow; do not reconstruct the sequence from prose.
- Niratan behavior comparison, upstream sync, or porting: start with `docs/reference/Niratan/AGENTS.md`, then read only the nearest feature code and affected Windows module.

Use `rg "^## |^### "` to locate relevant sections in long documents. Prefer code, typed contracts, tests, and scripts for facts they already express precisely.

## Verify

- Pure documentation or Skill changes: inspect links and rendered structure, run the Skill validator when applicable, then `git diff --check`.
- Pure logic changes: run the narrowest relevant tests, then broaden only when the changed boundary requires it.
- Runnable App changes: run affected tests, `dotnet build -p:Platform=x64`, and open the affected module through `.\build-and-run.ps1`.
- UI, media, external-service, and migration validation must use safe or disposable data. Report uncovered behavior when the required fixture, account, hardware, or service is unavailable.
- Finish by stating what changed, what ran, and what remains unverified.
