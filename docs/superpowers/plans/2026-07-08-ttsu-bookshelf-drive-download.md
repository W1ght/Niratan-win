# TTU Bookshelf Drive Download Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Niratan-aligned Google Drive remote book refresh and download/import from the novel library page.

**Architecture:** Extend the sync remote store with remote folder listing and bookdata download. Add a focused TTU book import service that downloads package bytes, converts/imports them, and applies sidecars. Bind this service through `NovelLibraryPageViewModel` commands and render remote book placeholders beside local books.

**Tech Stack:** WinUI 3, C#/.NET, CommunityToolkit.Mvvm, System.Net.Http, System.IO.Compression, xUnit v3, FluentAssertions, Moq.

## Global Constraints

- Do not modify `native/hoshidicts/`.
- Do not put business logic in code-behind.
- ViewModels do not access SQLite directly.
- Use WebView2 only for reader rendering; this work is bookshelf/service only.
- Google Drive stays behind narrow service interfaces.
- No Google SDK or new NuGet dependency unless implementation proves it necessary.

---

### Task 1: Remote Book Transport

**Files:**
- Modify: `Niratan/Models/Sync/TtuSyncModels.cs`
- Modify: `Niratan/Services/Sync/ITtuSyncRemoteStore.cs`
- Modify: `Niratan/Services/Sync/GoogleDriveTtuSyncRemoteStore.cs`
- Test: `Niratan.Tests/Services/Sync/GoogleDriveTtuSyncRemoteStoreTests.cs`

**Interfaces:**
- Produces `TtuRemoteBook`, `ListRemoteBooksAsync`, and `DownloadBookDataAsync`.

- [ ] Write failing tests for listing child folders, grouping sync files by parent, filtering `bookdata_`, and downloading `alt=media`.
- [ ] Run focused sync tests and verify failures reference missing interface/methods.
- [ ] Implement model/interface methods and Google Drive queries.
- [ ] Run focused sync tests and verify pass.

### Task 2: TTU Book Import Service

**Files:**
- Create: `Niratan/Services/Sync/ITtuBookImportService.cs`
- Create: `Niratan/Services/Sync/TtuBookImportService.cs`
- Modify: `Niratan/App.xaml.cs`
- Test: `Niratan.Tests/Services/Sync/TtuBookImportServiceTests.cs`

**Interfaces:**
- Consumes `ITtuSyncRemoteStore`, `INovelLibraryService`, `ITtuSyncService`.
- Produces `ImportRemoteBookAsync(TtuRemoteBook remoteBook, TtuBookImportOptions options, IProgress<double>? progress, CancellationToken ct)`.

- [ ] Write failing tests for downloading bookdata to a temp `.epub`, importing through `INovelLibraryService`, and applying progress/stat/audio via `ITtuSyncService` with import-only options.
- [ ] Run focused tests and verify expected failures.
- [ ] Implement the service minimally with temp-file cleanup.
- [ ] Run focused tests and verify pass.

### Task 3: Bookshelf ViewModel

**Files:**
- Modify: `Niratan/ViewModels/Pages/NovelLibraryPageViewModel.cs`
- Test: `Niratan.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`

**Interfaces:**
- Consumes `ITtuSyncRemoteStore`, `ITtuBookImportService`, `ISettingsService`.
- Produces `RemoteBooks`, `RefreshRemoteBooksCommand`, `DownloadRemoteBookCommand`, and download progress state.

- [ ] Write failing tests for refresh filtering local titles, disconnected state notification, and successful download removing remote item plus reloading local books.
- [ ] Run ViewModel tests and verify failures.
- [ ] Implement command state and filtering.
- [ ] Run ViewModel tests and verify pass.

### Task 4: Homepage UI

**Files:**
- Modify: `Niratan/Views/Pages/NovelLibraryPage.xaml`
- Modify: `Niratan/Strings/en-US/Resources.resw`
- Modify: `Niratan/Strings/zh-CN/Resources.resw`
- Test: `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs` or a new sync asset test if narrower.

**Interfaces:**
- Binds to `NovelLibraryPageViewModel.RemoteBooks` and commands.

- [ ] Write failing asset test for `NovelLibraryRefreshGoogleDriveButton` and remote book list/card AutomationIds.
- [ ] Run asset test and verify failure.
- [ ] Add button and remote book item template using native WinUI controls.
- [ ] Run asset test and verify pass.

### Task 5: Verification

**Files:**
- No production files unless test failures identify a small fix.

- [ ] Run `dotnet build -p:Platform=x64`.
- [ ] Run `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64`.
- [ ] Launch `Niratan.exe`, confirm top-level `Niratan` window responds, then close it if continuing work.
