# Niratan Novel File Storage and Shelves Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the runtime SQLite novel catalog with Niratan-compatible file storage, migrate existing books without data loss, and add persistent shelves plus their normal-bookshelf UI.

**Architecture:** The existing `Data/Novels` directory becomes the Windows Books directory, so current extracted EPUB content does not need a destructive physical move. Typed metadata, bookmark, book-info, order, and shelf JSON become the only novel truth; a migration-only SQLite reader exports and validates old rows before dropping legacy novel tables. The mixed data service is split so SQLite/Dapper remain available only to video and read-only external audio.

**Tech Stack:** C# 14 / .NET 10, WinUI 3, CommunityToolkit.Mvvm, `System.Text.Json`, Microsoft.Data.Sqlite + Dapper for migration/video only, xUnit v3, FluentAssertions, Moq.

## Global Constraints

- Target Windows 10+ x64; do not add ARM64 as a default verification target.
- Build with `dotnet build -p:Platform=x64` and test with `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64`.
- Do not modify `native/hoshidicts/`.
- Novel runtime operations must not read or write SQLite after successful cutover.
- Keep the video catalog in SQLite and keep external audio databases read-only.
- Preserve `metadata.json`, `bookmark.json`, `bookinfo.json`, `statistics.json`, `highlights.json`, `sasayaki_match.json`, and `sasayaki_playback.json` compatibility.
- Use typed models, atomic JSON replacement, local private storage, and no second novel database or ORM.
- Keep View code-behind UI-only; ViewModels expose state/commands and services own IO.
- Preserve all unrelated worktree changes and stage only files belonging to the current task.

---

## File Structure

New focused units:

- `Hoshi/Models/Novel/NovelBookMetadata.cs`: Niratan metadata DTO and domain mapping.
- `Hoshi/Models/Novel/NovelShelfModels.cs`: shelf/state models.
- `Hoshi/Services/Novels/NiratanJsonFileStore.cs`: shared JSON compatibility, read-status, and atomic writes.
- `Hoshi/Services/Novels/INovelBookStorageService.cs`: file-catalog contract.
- `Hoshi/Models/Novel/NovelBookStorageModels.cs`: catalog snapshot and recoverable scan warnings.
- `Hoshi/Services/Novels/NovelBookStorageService.cs`: metadata scan and book projection.
- `Hoshi/Services/Storage/NovelStorageMigrationService.cs`: legacy export, validation, cutover, and table retirement.
- `Hoshi/Services/Storage/IVideoDataService.cs` and `VideoDataService.cs`: video-only SQLite boundary.
- `Hoshi/Services/Novels/INovelShelfService.cs` and `NovelShelfService.cs`: shelf CRUD, membership, and order.
- `Hoshi/ViewModels/Components/NovelShelfSectionViewModel.cs`: section projection.
- `Hoshi/ViewModels/Dialogs/NovelShelfManagementViewModel.cs`: shelf-management commands.
- `Hoshi/Views/Dialogs/NovelShelfManagementDialog.xaml(.cs)`: UI-only shelf dialog.

Existing files change only where their responsibility changes: import/library/Reader/TTU consumers, DI, migrations, bookshelf XAML/ViewModel, tests, and architecture/verification documentation.

---

### Task 1: Niratan JSON and Metadata Contract

**Files:**
- Create: `Hoshi/Models/Novel/NovelBookMetadata.cs`
- Create: `Hoshi/Services/Novels/NiratanJsonFileStore.cs`
- Modify: `Hoshi/Services/Novels/NovelBookSidecarService.cs`
- Test: `Hoshi.Tests/Services/Novels/NiratanJsonFileStoreTests.cs`
- Test: `Hoshi.Tests/Services/Novels/NovelBookMetadataTests.cs`

**Interfaces:**
- Produces: `NovelBookMetadata`, `NovelJsonReadStatus`, `NovelJsonReadResult<T>`, `INiratanJsonFileStore.ReadAsync<T>()`, and `INiratanJsonFileStore.WriteAsync<T>()`.
- Consumes: existing Mac absolute-date compatibility from `NovelBookSidecarService`.

- [ ] **Step 1: Write failing metadata and atomic-store tests**

```csharp
[Fact]
public async Task Metadata_RoundTripsNiratanFieldsAndMacAbsoluteDate()
{
    using var temp = new TempDirectory();
    var store = new NiratanJsonFileStore();
    var path = Path.Combine(temp.Path, "metadata.json");
    var value = new NovelBookMetadata(
        "abc", "星", "abc.epub", "cover.jpg", "abc",
        new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero),
        "星・改", "default-ja", "ja");

    await store.WriteAsync(path, value, TestContext.Current.CancellationToken);
    var result = await store.ReadAsync<NovelBookMetadata>(path, TestContext.Current.CancellationToken);

    result.Status.Should().Be(NovelJsonReadStatus.Success);
    result.Value.Should().BeEquivalentTo(value);
    Directory.EnumerateFiles(temp.Path, "*.tmp").Should().BeEmpty();
}

[Fact]
public async Task ReadAsync_DistinguishesMissingFromInvalidJson()
{
    using var temp = new TempDirectory();
    var store = new NiratanJsonFileStore();
    var path = Path.Combine(temp.Path, "metadata.json");

    (await store.ReadAsync<NovelBookMetadata>(path)).Status
        .Should().Be(NovelJsonReadStatus.Missing);
    await File.WriteAllTextAsync(path, "{broken");
    (await store.ReadAsync<NovelBookMetadata>(path)).Status
        .Should().Be(NovelJsonReadStatus.Invalid);
}
```

- [ ] **Step 2: Run the focused tests and verify they fail**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NiratanJsonFileStoreTests|FullyQualifiedName~NovelBookMetadataTests"
```

Expected: FAIL because the new DTO and store types do not exist.

- [ ] **Step 3: Add the exact metadata and JSON-store contracts**

```csharp
public sealed record NovelBookMetadata(
    string Id,
    string Title,
    string? Epub,
    string? Cover,
    string Folder,
    DateTimeOffset LastAccess,
    string? RenamedTitle = null,
    string? ProfileId = null,
    string? BookLanguage = null)
{
    public string DisplayTitle => RenamedTitle ?? Title;
}

internal enum NovelJsonReadStatus { Missing, Success, Invalid }

internal sealed record NovelJsonReadResult<T>(
    NovelJsonReadStatus Status,
    T? Value,
    string? Error = null);

internal interface INiratanJsonFileStore
{
    Task<NovelJsonReadResult<T>> ReadAsync<T>(string path, CancellationToken ct = default);
    Task WriteAsync<T>(string path, T value, CancellationToken ct = default);
}
```

Implement `NiratanJsonFileStore` with camel-case, indented JSON, the existing 2001-01-01 Mac absolute-date converter, `FileMode.CreateNew` for a unique temp file, and `File.Move(temp, target, overwrite: true)` inside `try/finally`. Return `Invalid` for `JsonException`, `IOException`, and `UnauthorizedAccessException`; never replace an invalid source during reads.

- [ ] **Step 4: Make the existing bookmark/book-info service use the shared store**

Replace its private serializer and date converter with constructor injection plus a default constructor:

```csharp
private readonly INiratanJsonFileStore _json;

public NovelBookSidecarService() : this(new NiratanJsonFileStore()) { }

internal NovelBookSidecarService(INiratanJsonFileStore json) => _json = json;
```

Map `Missing` and `Invalid` to the existing nullable load result, and keep the public interface unchanged.

- [ ] **Step 5: Run the focused and existing sidecar tests**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NiratanJsonFileStoreTests|FullyQualifiedName~NovelBookMetadataTests|FullyQualifiedName~NovelBookSidecarServiceTests"
```

Expected: PASS.

- [ ] **Step 6: Commit the JSON contract**

```powershell
git add Hoshi/Models/Novel/NovelBookMetadata.cs Hoshi/Services/Novels/NiratanJsonFileStore.cs Hoshi/Services/Novels/NovelBookSidecarService.cs Hoshi.Tests/Services/Novels/NiratanJsonFileStoreTests.cs Hoshi.Tests/Services/Novels/NovelBookMetadataTests.cs
git commit -m "feat(storage): add niratan novel metadata contract"
```

### Task 2: File-Backed Novel Catalog

**Files:**
- Create: `Hoshi/Models/Novel/NovelBookStorageModels.cs`
- Create: `Hoshi/Services/Novels/INovelBookStorageService.cs`
- Create: `Hoshi/Services/Novels/NovelBookStorageService.cs`
- Modify: `Hoshi/Helpers/AppDataHelper.cs`
- Modify: `Hoshi/Models/NovelBook.cs`
- Test: `Hoshi.Tests/Services/Novels/NovelBookStorageServiceTests.cs`

**Interfaces:**
- Consumes: `INiratanJsonFileStore`, `INovelBookSidecarService`, `NovelBookMetadata`.
- Produces: `NovelBookCatalogSnapshot` and `INovelBookStorageService.LoadSnapshotAsync`, `LoadAsync`, `SaveMetadataAsync`, `UpdateLastAccessAsync`, `UpdateProfileAsync`, `LoadBookOrderAsync`, `SaveBookOrderAsync`, `DeleteAsync`, and `ResolveRootPath`.

- [ ] **Step 1: Write failing scan/projection tests**

```csharp
[Fact]
public async Task LoadSnapshotAsync_ScansMetadataAndProjectsBookmarkAndBookInfo()
{
    using var temp = new TempDirectory();
    var root = Directory.CreateDirectory(Path.Combine(temp.Path, "book-a")).FullName;
    var json = new NiratanJsonFileStore();
    await json.WriteAsync(Path.Combine(root, "metadata.json"),
        new NovelBookMetadata("a", "原題", "a.epub", "cover.jpg", "book-a",
            DateTimeOffset.Parse("2026-07-11T00:00:00Z"), "表示名", "default-ja", "ja"));
    var sidecars = new NovelBookSidecarService(json);
    await sidecars.SaveBookmarkAsync(root, new NovelBookmark(2, .25, 1234,
        DateTimeOffset.Parse("2026-07-11T01:00:00Z")));
    await sidecars.SaveBookInfoAsync(root, new NovelBookInfo(9000, []));

    var service = new NovelBookStorageService(temp.Path, json, sidecars);
    var snapshot = await service.LoadSnapshotAsync();
    var books = snapshot.Books;

    books.Should().ContainSingle();
    books[0].Id.Should().Be("a");
    books[0].Title.Should().Be("表示名");
    books[0].CurrentChapterIndex.Should().Be(2);
    books[0].CurrentCharacterCount.Should().Be(1234);
    books[0].TotalCharacterCount.Should().Be(9000);
    books[0].ExtractedPath.Should().Be(root);
}
```

Add companion cases for query filtering, corrupt metadata reporting, relative cover/EPUB paths, and deleting a book directory.

- [ ] **Step 2: Run the catalog tests and verify they fail**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelBookStorageServiceTests"
```

Expected: FAIL because `INovelBookStorageService` and `NovelBookStorageService` do not exist.

- [ ] **Step 3: Add the storage interface and path helper**

```csharp
public sealed record NovelBookCatalogSnapshot(
    IReadOnlyList<NovelBook> Books,
    IReadOnlyList<string> CorruptMetadataPaths);

public interface INovelBookStorageService
{
    Task<NovelBookCatalogSnapshot> LoadSnapshotAsync(string? queryText = null, CancellationToken ct = default);
    Task<NovelBook?> LoadAsync(string bookId, CancellationToken ct = default);
    Task SaveMetadataAsync(NovelBook book, CancellationToken ct = default);
    Task UpdateLastAccessAsync(string bookId, DateTimeOffset lastAccess, CancellationToken ct = default);
    Task UpdateProfileAsync(string bookId, string? profileId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> LoadBookOrderAsync(CancellationToken ct = default);
    Task SaveBookOrderAsync(IReadOnlyList<string> orderedBookIds, CancellationToken ct = default);
    Task DeleteAsync(string bookId, CancellationToken ct = default);
    string ResolveRootPath(string bookId);
}
```

Add `AppDataHelper.GetNovelBooksPath()` returning the existing `Data/Novels` directory, and make `GetNovelBookPath(bookId)` call it. This preserves all existing extracted content while changing the catalog truth.

- [ ] **Step 4: Implement the catalog scan and metadata mapping**

For every direct child directory, read `metadata.json`; skip directories without metadata; record invalid metadata IDs/paths for the warning surface in Task 8. Enrich the domain `NovelBook` with `bookmark.json` and `bookinfo.json`. Resolve relative cover and EPUB paths only inside the book root. Sort by `LastOpenedAt ?? ImportedAt`, then display title. `LoadBookOrderAsync` treats a missing file as an empty list and an invalid file as a recoverable storage error; `SaveBookOrderAsync` uses the same atomic store.

```csharp
foreach (var directory in Directory.EnumerateDirectories(_booksRoot))
{
    ct.ThrowIfCancellationRequested();
    var metadataResult = await _json.ReadAsync<NovelBookMetadata>(
        Path.Combine(directory, MetadataFileName), ct);
    if (metadataResult.Status == NovelJsonReadStatus.Invalid)
    {
        corruptMetadataPaths.Add(Path.Combine(directory, MetadataFileName));
        continue;
    }
    if (metadataResult.Status != NovelJsonReadStatus.Success || metadataResult.Value is null)
        continue;

    var bookmark = await _sidecars.LoadBookmarkAsync(directory, ct);
    var bookInfo = await _sidecars.LoadBookInfoAsync(directory, ct);
    books.Add(ToDomain(metadataResult.Value, directory, bookmark, bookInfo));
}
```

Add `OriginalTitle`, `RenamedTitle`, and `Folder` to `NovelBook`; keep `Title` as the current display title to avoid breaking existing consumers during cutover.

- [ ] **Step 5: Run the catalog tests**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelBookStorageServiceTests|FullyQualifiedName~NovelBookSidecarServiceTests"
```

Expected: PASS.

- [ ] **Step 6: Commit the catalog service**

```powershell
git add Hoshi/Helpers/AppDataHelper.cs Hoshi/Models/NovelBook.cs Hoshi/Models/Novel/NovelBookStorageModels.cs Hoshi/Services/Novels/INovelBookStorageService.cs Hoshi/Services/Novels/NovelBookStorageService.cs Hoshi.Tests/Services/Novels/NovelBookStorageServiceTests.cs
git commit -m "feat(storage): scan novels from metadata sidecars"
```

### Task 3: Idempotent Legacy SQLite Migration

**Files:**
- Create: `Hoshi/Models/Novel/NovelStorageMigrationModels.cs`
- Create: `Hoshi/Services/Storage/NovelStorageMigrationService.cs`
- Modify: `Hoshi/Services/Novels/NovelBookStorageService.cs`
- Test: `Hoshi.Tests/Services/Storage/NovelStorageMigrationServiceTests.cs`

**Interfaces:**
- Consumes: SQLite connection string, `INovelBookStorageService`, `INovelBookSidecarService`, `INiratanJsonFileStore`.
- Produces: `INovelStorageMigrationService.MigrateAsync()`, `NovelStorageMigrationResult`, and `INovelStorageAccessState`.

- [ ] **Step 1: Write a failing successful-migration fixture**

Create a temporary SQLite database with a real `NovelBooks` row and an existing book directory. Assert that migration writes metadata, preserves a newer existing bookmark, creates `book_order.json` and `shelves.json`, writes a manifest, and removes the legacy tables only after validation.

```csharp
var result = await service.MigrateAsync(ct);
result.IsSuccess.Should().BeTrue();
File.Exists(Path.Combine(bookRoot, "metadata.json")).Should().BeTrue();
File.Exists(Path.Combine(booksRoot, "book_order.json")).Should().BeTrue();
File.Exists(Path.Combine(booksRoot, "shelves.json")).Should().BeTrue();
(await TableExistsAsync(db, "NovelBooks")).Should().BeFalse();
```

Add a second fixture with malformed existing metadata and assert `IsReadOnly == true`, the backup exists, and `NovelBooks` remains.

- [ ] **Step 2: Run the migration tests and verify they fail**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStorageMigrationServiceTests"
```

Expected: FAIL because the migration service does not exist.

- [ ] **Step 3: Add migration result and manifest models**

```csharp
public sealed record NovelStorageMigrationResult(
    bool IsSuccess,
    bool IsReadOnly,
    string? ErrorMessage,
    int MigratedBookCount);

public sealed record NovelStorageMigrationManifest(
    int Version,
    DateTimeOffset CompletedAt,
    IReadOnlyList<NovelStorageMigrationBook> Books);

public sealed record NovelStorageMigrationBook(
    string Id,
    string Folder,
    int ChapterIndex,
    int CharacterCount,
    int TotalCharacterCount,
    string? ProfileId);

public interface INovelStorageAccessState
{
    bool IsReadOnly { get; }
    string? ErrorMessage { get; }
}

internal sealed class NovelStorageAccessState : INovelStorageAccessState
{
    public bool IsReadOnly { get; private set; }
    public string? ErrorMessage { get; private set; }

    public void Apply(NovelStorageMigrationResult result)
    {
        IsReadOnly = result.IsReadOnly;
        ErrorMessage = result.ErrorMessage;
    }
}
```

- [ ] **Step 4: Implement migration in an explicit fail-closed order**

Use `sqlite_master` to detect `NovelBooks`. If it is absent, ensure missing global files contain `[]` and return success. If present:

1. Copy `hoshi.db` to a versioned `.pre-novel-files-v1.bak` path while no connection is open.
2. Load legacy rows with every current column.
3. Write missing metadata; preserve valid existing metadata.
4. Write bookmark/book-info only when missing or invalid.
5. Write order sorted by `ManualSortOrder`; write empty shelves only when missing.
6. Rescan and compare every manifest field.
7. Drop `NovelReadingProgress`, `NovelReaderSettings`, and `NovelBooks` in one SQLite transaction.
8. Write `novel_storage_migration_v1.json` atomically after table retirement.

Catch validation, JSON, IO, SQLite, and authorization failures before retirement and return a read-only result without dropping tables. If the process stops after retirement but before the manifest write, the next run observes no legacy table, validates the file catalog, and writes the manifest.

```csharp
public async Task<NovelStorageMigrationResult> MigrateAsync(CancellationToken ct = default)
{
    try
    {
        if (!await LegacyTableExistsAsync(ct))
            return await CompleteWithoutLegacyTableAsync(ct);

        await CreateBackupAsync(ct);
        var rows = await LoadLegacyBooksAsync(ct);
        foreach (var row in rows)
            await ExportBookAsync(row, ct);

        var manifest = BuildManifest(rows);
        var scanned = await _storage.LoadSnapshotAsync(ct: ct);
        ValidateManifest(manifest, scanned.Books);
        await DropLegacyTablesAsync(ct);
        await _json.WriteAsync(_manifestPath,
            manifest with { CompletedAt = DateTimeOffset.UtcNow }, ct);
        return new NovelStorageMigrationResult(true, false, null, rows.Count);
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex) when (ex is JsonException or IOException or
                               UnauthorizedAccessException or SqliteException or
                               InvalidDataException)
    {
        _logger.LogError(ex, "Novel storage migration failed; novel writes are disabled");
        return new NovelStorageMigrationResult(false, true, ex.Message, 0);
    }
}
```

- [ ] **Step 5: Add retry and no-table tests**

Assert an interrupted first run can be repeated, a completed manifest produces a no-op success, and a fresh video-only database succeeds without creating novel tables.

- [ ] **Step 6: Run the migration suite**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStorageMigrationServiceTests"
```

Expected: PASS.

- [ ] **Step 7: Commit the migration service**

```powershell
git add Hoshi/Models/Novel/NovelStorageMigrationModels.cs Hoshi/Services/Storage/NovelStorageMigrationService.cs Hoshi/Services/Novels/NovelBookStorageService.cs Hoshi.Tests/Services/Storage/NovelStorageMigrationServiceTests.cs
git commit -m "feat(storage): migrate sqlite novels to sidecars"
```

### Task 4: Cut Novel Library, Import, Reader Progress, and TTU Progress Over to Files

**Files:**
- Modify: `Hoshi/Services/Novels/NovelLibraryService.cs`
- Modify: `Hoshi/Services/Novels/NovelEpubImportService.cs`
- Modify: `Hoshi/ViewModels/Pages/NovelReaderPageViewModel.cs`
- Modify: `Hoshi/Services/Sync/TtuSyncService.cs`
- Modify: `Hoshi/Services/Novels/INovelLibraryService.cs`
- Test: `Hoshi.Tests/Services/Novels/NovelLibraryServiceTests.cs`
- Test: `Hoshi.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs`
- Test: `Hoshi.Tests/Services/Sync/TtuSyncServiceTests.cs`

**Interfaces:**
- Consumes: `INovelBookStorageService`, `INovelStorageAccessState`, and existing sidecar services.
- Produces: a working existing `INovelLibraryService` whose persistence is entirely file-backed.

- [ ] **Step 1: Update tests to require file-backed calls**

Change library tests to mock `INovelBookStorageService`. Change Reader tests to assert one bookmark write per save. Change TTU import tests to assert imported progress writes `bookmark.json` and never calls a data service.

- [ ] **Step 2: Run the focused tests and verify they fail**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryServiceTests|FullyQualifiedName~NovelReaderPageViewModelTests|FullyQualifiedName~TtuSyncServiceTests"
```

Expected: FAIL while constructors and expectations still use `IDataService`.

- [ ] **Step 3: Cut `NovelLibraryService` over**

Replace the mixed data dependency with `INovelBookStorageService` and `INovelBookSidecarService`:

```csharp
public NovelLibraryService(
    INovelBookStorageService storage,
    INovelBookSidecarService sidecars,
    INovelStorageAccessState accessState,
    INovelEpubImportService epubImportService,
    ILogger<NovelLibraryService> logger)
```

Change `INovelLibraryService.GetNovelBooksAsync` to return `Result<NovelBookCatalogSnapshot>`, then map get/get-one/last-opened/profile/delete directly to storage. `SaveProgressAsync` resolves the root and writes one `NovelBookmark` with a Mac-compatible `LastModified`; it does not update SQLite. `SaveNovelBookOrderAsync` calls `SaveBookOrderAsync`, so Task 4 compiles and preserves manual order before the shelf service starts consuming the same file in Task 6.

Every mutation begins with:

```csharp
if (_accessState.IsReadOnly)
    return Result.Failure(
        _accessState.ErrorMessage ?? "Novel storage migration requires recovery.",
        "Novel library is read-only");
```

```csharp
public async Task<Result> SaveNovelBookOrderAsync(
    IReadOnlyList<string> orderedBookIds,
    CancellationToken ct = default)
{
    if (_accessState.IsReadOnly)
        return Result.Failure(
            _accessState.ErrorMessage ?? "Novel storage migration requires recovery.",
            "Novel library is read-only");

    try
    {
        await _storage.SaveBookOrderAsync(orderedBookIds, ct);
        return Result.Success();
    }
    catch (OperationCanceledException) { return Result.Cancelled(); }
    catch (Exception ex) { return Result.Failure(ex.Message, "Error saving novel order"); }
}
```

- [ ] **Step 4: Publish imports with private EPUB metadata**

`NovelEpubImportService` copies the selected EPUB to `<bookRoot>/<bookId>.epub`, parses the private copy into the same root, and returns a book whose metadata points to the relative EPUB and cover. `NovelLibraryService.ImportEpubAsync` calls `SaveMetadataAsync` before announcing success. On parse or metadata failure, delete the incomplete root.

```csharp
var bookId = Guid.NewGuid().ToString("N");
var bookRoot = AppDataHelper.GetNovelBookPath(bookId);
var epubName = $"{bookId}.epub";
var privateEpubPath = Path.Combine(bookRoot, epubName);
try
{
    File.Copy(filePath, privateEpubPath, overwrite: false);
    var epubBook = await Task.Run(() => _epubParser.Parse(privateEpubPath, bookRoot), ct);
    return Result<NovelImportResult>.Success(
        new NovelImportResult(CreateBook(bookId, epubName, bookRoot, epubBook)));
}
catch
{
    if (Directory.Exists(bookRoot)) Directory.Delete(bookRoot, recursive: true);
    throw;
}
```

- [ ] **Step 5: Remove duplicate Reader progress writes**

Keep `SaveProgressDebounced` and `SaveProgressNowAsync`, but make each call only `INovelLibraryService.SaveProgressAsync` before statistics flush. Remove the private duplicate `SaveBookmarkSidecarAsync` helper.

```csharp
await _novelLibraryService.SaveProgressAsync(
    CurrentBook.Id,
    CurrentChapterIndex,
    Progress,
    CurrentCharacterCount,
    TotalCharacterCount,
    ct);
await FlushStatisticsAsync(ct: ct);
```

- [ ] **Step 6: Remove the TTU novel data-service dependency**

Delete `_dataService` and its constructor parameter from `TtuSyncService`. `ImportProgressAsync` already computes and writes the canonical bookmark; remove the subsequent `SaveNovelProgressAsync` call. Leave Merge/Replace statistics behavior unchanged for the later sync phase.

```csharp
var bookmark = new NovelBookmark(
    resolved.ChapterIndex,
    resolved.ChapterProgress,
    progress.ExploredCharCount,
    progress.LastBookmarkModified);
await _bookSidecars.SaveBookmarkAsync(bookRootPath, bookmark, ct);
```

- [ ] **Step 7: Run the focused tests**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryServiceTests|FullyQualifiedName~NovelReaderPageViewModelTests|FullyQualifiedName~TtuSyncServiceTests|FullyQualifiedName~TtuBookImportServiceTests"
```

Expected: PASS.

- [ ] **Step 8: Commit the novel call-chain cutover**

```powershell
git add Hoshi/Services/Novels Hoshi/ViewModels/Pages/NovelReaderPageViewModel.cs Hoshi/Services/Sync/TtuSyncService.cs Hoshi.Tests/Services/Novels Hoshi.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs Hoshi.Tests/Services/Sync/TtuSyncServiceTests.cs
git commit -m "refactor(novels): use sidecars as runtime truth"
```

### Task 5: Split the Video SQLite Boundary and Retire Novel Schema Creation

**Files:**
- Create: `Hoshi/Services/Storage/IVideoDataService.cs`
- Create: `Hoshi/Services/Storage/VideoDataService.cs`
- Delete: `Hoshi/Services/Storage/IDataService.cs`
- Delete: `Hoshi/Services/Storage/DataService.cs`
- Modify: `Hoshi/Services/Video/VideoLibraryService.cs`
- Modify: `Hoshi/Services/Video/VideoThumbnailService.cs`
- Modify: `Hoshi/App.xaml.cs`
- Modify: `Hoshi/Services/Storage/Migrations/Migration_003.cs`
- Modify: `Hoshi/Services/Storage/Migrations/Migration_004.cs`
- Modify: `Hoshi/Services/Storage/Migrations/Migration_005.cs`
- Modify: `Hoshi/Services/Storage/Migrations/Migration_006.cs`
- Modify: `Hoshi/Services/Storage/Migrations/Migration_007.cs`
- Modify: `Hoshi/Services/Storage/Migrations/Migration_010.cs`
- Test: `Hoshi.Tests/Services/Storage/VideoDataServiceTests.cs`
- Replace: `Hoshi.Tests/Services/Storage/NovelDataServiceTests.cs` with migration coverage from Task 3.
- Modify: `Hoshi.Tests/Services/Video/VideoLibraryServiceTests.cs`
- Modify: `Hoshi.Tests/Services/Video/VideoThumbnailServiceTests.cs`

**Interfaces:**
- Consumes: successful Task 4 cutover and Task 3 migration.
- Produces: `IVideoDataService` as the only app-business SQLite interface.

- [ ] **Step 1: Update video tests to use `IVideoDataService`**

Replace each `Mock<IDataService>` with `Mock<IVideoDataService>` and retain the same video method expectations. Add a source contract asserting the video interface contains no `NovelBook` member.

- [ ] **Step 2: Run video/storage tests and verify they fail**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoDataServiceTests|FullyQualifiedName~VideoLibraryServiceTests|FullyQualifiedName~VideoThumbnailServiceTests"
```

Expected: FAIL because `IVideoDataService` does not exist.

- [ ] **Step 3: Extract the exact video interface and implementation**

Move all methods from `GetVideosAsync` through `UpdateVideoProfileIdAsync` into `IVideoDataService` and `VideoDataService`; copy the existing SQL unchanged. Update both video services and DI:

```csharp
public interface IVideoDataService
{
    Task<IReadOnlyList<VideoItem>> GetVideosAsync(string? queryText = null, CancellationToken ct = default);
    Task<VideoItem?> GetVideoAsync(string videoId, CancellationToken ct = default);
    Task UpsertVideoAsync(VideoItem video, CancellationToken ct = default);
    Task DeleteVideoAsync(string videoId, CancellationToken ct = default);
    Task UpdateVideoLastOpenedAsync(string videoId, DateTime lastOpenedAt, CancellationToken ct = default);
    Task SaveVideoProgressAsync(string videoId, double positionSeconds, double durationSeconds, CancellationToken ct = default);
    Task SaveVideoPlaybackStateAsync(string videoId, VideoPlaybackState state, CancellationToken ct = default);
    Task<IReadOnlyList<VideoCollection>> GetVideoCollectionsAsync(CancellationToken ct = default);
    Task UpsertVideoCollectionAsync(VideoCollection collection, CancellationToken ct = default);
    Task DeleteVideoCollectionAsync(string collectionId, CancellationToken ct = default);
    Task SetVideoCollectionItemsAsync(string collectionId, IReadOnlyList<string> videoIds, CancellationToken ct = default);
    Task UpdateVideoThumbnailPathAsync(string videoId, string? thumbnailPath, CancellationToken ct = default);
    Task UpdateVideoFavoriteAsync(string videoId, bool isFavorite, CancellationToken ct = default);
    Task MarkVideoWatchedAsync(string videoId, DateTime watchedAt, CancellationToken ct = default);
    Task ClearVideoProgressAsync(string videoId, CancellationToken ct = default);
    Task UpdateVideoProfileIdAsync(string videoId, string? profileId, CancellationToken ct = default);
}

services.AddSingleton<IVideoDataService, VideoDataService>();
```

Delete `IDataService` and `DataService` only after `rg "IDataService|DataService" Hoshi Hoshi.Tests` reports no intended references.

- [ ] **Step 4: Stop fresh databases from creating novel tables**

Make `Migration_003.UpAsync` a version-preserving no-op for fresh installs. Make migrations 004-007 first query `sqlite_master` and return when `NovelBooks` is absent; this still upgrades genuinely old databases before Task 3 exports them. Retain every existing `ALTER TABLE` statement inside the guard (for example, Migration 004 keeps both `ExtractedPath` and `ChapterCount`). Keep Migration 010's existing table guard for both video and legacy novel tables.

```csharp
private static async Task<bool> LegacyTableExistsAsync(
    SqliteConnection connection,
    DbTransaction transaction) =>
    await connection.ExecuteScalarAsync<long>(
        "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'NovelBooks';",
        transaction: transaction) > 0;

public async Task UpAsync(SqliteConnection connection, DbTransaction transaction)
{
    if (!await LegacyTableExistsAsync(connection, transaction))
        return;
    await connection.ExecuteAsync("""
        ALTER TABLE NovelBooks ADD COLUMN ExtractedPath TEXT;
        ALTER TABLE NovelBooks ADD COLUMN ChapterCount INTEGER NOT NULL DEFAULT 0;
        """, transaction: transaction);
}
```

- [ ] **Step 5: Wire startup migration before normal app initialization**

After `DatabaseMigrator.MigrateAsync()` and before `InitializeAppAsync()`, resolve and run `INovelStorageMigrationService`. Register storage, migration, and video services before building the provider. If the result is read-only, keep the main window available and publish a persistent `InfoBar`/notification message while all novel mutation commands return a recovery error.

```csharp
services.AddSingleton<INiratanJsonFileStore, NiratanJsonFileStore>();
services.AddSingleton<INovelBookStorageService, NovelBookStorageService>();
services.AddSingleton<INovelStorageMigrationService, NovelStorageMigrationService>();
services.AddSingleton<NovelStorageAccessState>();
services.AddSingleton<INovelStorageAccessState>(provider =>
    provider.GetRequiredService<NovelStorageAccessState>());
services.AddSingleton<IVideoDataService, VideoDataService>();

var migrationResult = await GetService<INovelStorageMigrationService>().MigrateAsync();
GetService<NovelStorageAccessState>().Apply(migrationResult);
if (migrationResult.IsReadOnly)
    GetService<INotificationService>().ShowError(
        migrationResult.ErrorMessage ?? "Novel storage migration requires recovery.",
        "Novel library is read-only");

await InitializeAppAsync();
```

- [ ] **Step 6: Run storage and video tests**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Storage|FullyQualifiedName~VideoDataServiceTests|FullyQualifiedName~VideoLibraryServiceTests|FullyQualifiedName~VideoThumbnailServiceTests"
```

Expected: PASS, including fresh-database tests that find video tables and no novel tables.

- [ ] **Step 7: Commit the SQLite boundary split**

```powershell
git add Hoshi/Services/Storage Hoshi/Services/Video Hoshi/App.xaml.cs Hoshi.Tests/Services/Storage Hoshi.Tests/Services/Video
git commit -m "refactor(storage): limit sqlite to video data"
```

### Task 6: Persistent Shelf Service

**Files:**
- Create: `Hoshi/Models/Novel/NovelShelfModels.cs`
- Create: `Hoshi/Services/Novels/INovelShelfService.cs`
- Create: `Hoshi/Services/Novels/NovelShelfService.cs`
- Modify: `Hoshi/Services/Novels/NovelLibraryService.cs`
- Test: `Hoshi.Tests/Services/Novels/NovelShelfServiceTests.cs`
- Test: `Hoshi.Tests/Services/Novels/NovelLibraryServiceTests.cs`

**Interfaces:**
- Consumes: `INovelBookStorageService.LoadSnapshotAsync`, `LoadBookOrderAsync`, `SaveBookOrderAsync`, and `INiratanJsonFileStore`.
- Produces: `NovelShelfState` and all shelf mutation methods used by the bookshelf ViewModel.

- [ ] **Step 1: Write the complete shelf behavior matrix as failing tests**

Cover create, case-insensitive duplicate rejection, rename, shelf reorder, delete-without-book-delete, move one/many books, move to unshelved, reorder inside a shelf, reorder unshelved, imported-ID append, unknown-ID cleanup, book-delete cleanup, corrupt-file preservation, and atomic writes.

```csharp
[Fact]
public async Task DeleteShelf_MakesBooksUnshelvedWithoutDeletingThem()
{
    var state = await service.CreateAsync("收藏", ct);
    await service.MoveBooksAsync(["a", "b"], "收藏", ct);

    var result = await service.DeleteAsync("收藏", ct);

    result.IsSuccess.Should().BeTrue();
    result.Value!.Shelves.Should().BeEmpty();
    result.Value.UnshelvedBookOrder.Should().ContainInOrder("a", "b");
    (await storage.LoadAsync("a", ct)).Should().NotBeNull();
}
```

- [ ] **Step 2: Run the shelf tests and verify they fail**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelShelfServiceTests"
```

Expected: FAIL because shelf models and service do not exist.

- [ ] **Step 3: Add shelf models and interface**

```csharp
public sealed record NovelShelf(string Name, IReadOnlyList<string> BookIds);

public sealed record NovelShelfState(
    IReadOnlyList<NovelShelf> Shelves,
    IReadOnlyList<string> UnshelvedBookOrder);

public interface INovelShelfService
{
    Task<Result<NovelShelfState>> LoadAsync(CancellationToken ct = default);
    Task<Result<NovelShelfState>> CreateAsync(string name, CancellationToken ct = default);
    Task<Result<NovelShelfState>> RenameAsync(string oldName, string newName, CancellationToken ct = default);
    Task<Result<NovelShelfState>> ReorderShelvesAsync(IReadOnlyList<string> names, CancellationToken ct = default);
    Task<Result<NovelShelfState>> DeleteAsync(string name, CancellationToken ct = default);
    Task<Result<NovelShelfState>> MoveBooksAsync(IReadOnlyList<string> bookIds, string? targetShelf, CancellationToken ct = default);
    Task<Result<NovelShelfState>> ReorderBookAsync(string sourceId, string targetId, string? shelf, CancellationToken ct = default);
    Task<Result<NovelShelfState>> RemoveBookAsync(string bookId, CancellationToken ct = default);
}
```

- [ ] **Step 4: Implement normalized, serialized shelf mutations**

Use one `SemaphoreSlim` around load-modify-write. Validate trimmed unique names. Remove moved IDs from every section before appending to the target. Normalize against current storage IDs. Preserve invalid JSON and return a failure until an explicit reset path is chosen; do not overwrite it during ordinary mutations.

```csharp
public async Task<Result<NovelShelfState>> MoveBooksAsync(
    IReadOnlyList<string> bookIds,
    string? targetShelf,
    CancellationToken ct = default)
{
    await _gate.WaitAsync(ct);
    try
    {
        var state = await LoadCoreAsync(ct);
        var moving = bookIds.ToHashSet(StringComparer.Ordinal);
        var shelves = state.Shelves
            .Select(s => s with { BookIds = s.BookIds.Where(id => !moving.Contains(id)).ToList() })
            .ToList();
        var unshelved = state.UnshelvedBookOrder.Where(id => !moving.Contains(id)).ToList();

        if (targetShelf is null)
            unshelved.AddRange(bookIds.Where(id => !unshelved.Contains(id, StringComparer.Ordinal)));
        else
        {
            var index = shelves.FindIndex(s => string.Equals(s.Name, targetShelf, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return Result<NovelShelfState>.Failure("Shelf not found.");
            shelves[index] = shelves[index] with { BookIds = shelves[index].BookIds.Concat(bookIds).Distinct(StringComparer.Ordinal).ToList() };
        }

        return await SaveCoreAsync(new NovelShelfState(shelves, unshelved), ct);
    }
    finally { _gate.Release(); }
}
```

- [ ] **Step 5: Run the shelf suite**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelShelfServiceTests"
```

Expected: PASS.

- [ ] **Step 6: Wire deletion cleanup and register the service**

Inject `INovelShelfService` into `NovelLibraryService`. Before deleting the private book directory, call `RemoveBookAsync`; abort deletion when shelf cleanup fails so no stale shelf reference is silently created.

```csharp
var shelfResult = await _shelves.RemoveBookAsync(bookId, ct);
if (!shelfResult.IsSuccess)
    return Result.Failure(shelfResult.Error!, shelfResult.ErrorTitle ?? "Delete failed");
await _storage.DeleteAsync(bookId, ct);
```

Add `services.AddSingleton<INovelShelfService, NovelShelfService>();`.

- [ ] **Step 7: Run shelf and library deletion tests**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelShelfServiceTests|FullyQualifiedName~NovelLibraryServiceTests"
```

Expected: PASS.

- [ ] **Step 8: Commit the shelf service**

```powershell
git add Hoshi/Models/Novel/NovelShelfModels.cs Hoshi/Services/Novels/INovelShelfService.cs Hoshi/Services/Novels/NovelShelfService.cs Hoshi/Services/Novels/NovelLibraryService.cs Hoshi/App.xaml.cs Hoshi.Tests/Services/Novels/NovelShelfServiceTests.cs Hoshi.Tests/Services/Novels/NovelLibraryServiceTests.cs
git commit -m "feat(bookshelf): add niratan shelf persistence"
```

### Task 7: Shelf Section Projection and Commands

**Files:**
- Create: `Hoshi/ViewModels/Components/NovelShelfSectionViewModel.cs`
- Create: `Hoshi/ViewModels/Dialogs/NovelShelfManagementViewModel.cs`
- Modify: `Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs`
- Modify: `Hoshi/Models/Settings/AppSettings.cs`
- Test: `Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`
- Create: `Hoshi.Tests/ViewModels/Dialogs/NovelShelfManagementViewModelTests.cs`

**Interfaces:**
- Consumes: `INovelShelfService` and file-backed `INovelLibraryService`.
- Produces: `RailSections`, `UnshelvedBooks`, shelf management commands, explicit move commands, and in-section reorder commands.

- [ ] **Step 1: Add failing ViewModel tests for section order and commands**

Assert Reading (when enabled) precedes custom shelves in `RailSections`, Google Drive remains its own rail section, and `UnshelvedBooks` contains the remaining local books in `book_order.json` order. Assert corrupt metadata paths populate a non-blocking storage warning. Assert move/delete/reorder commands call the shelf service and rebuild projections. Assert a shelf operation failure raises the existing notification service rather than mutating local collections optimistically.

- [ ] **Step 2: Run the ViewModel tests and verify they fail**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageViewModelTests|FullyQualifiedName~NovelShelfManagementViewModelTests"
```

Expected: FAIL because section and management ViewModels do not exist.

- [ ] **Step 3: Add the section projection**

```csharp
public sealed record NovelShelfSectionViewModel(
    string Id,
    string DisplayName,
    bool IsDerived,
    bool IsUnshelved,
    ObservableCollection<NovelBookItemViewModel> Books);
```

Add `ObservableCollection<NovelShelfSectionViewModel> RailSections`, `ObservableCollection<NovelBookItemViewModel> UnshelvedBooks`, and `ObservableCollection<string> NovelStorageWarnings` to the library ViewModel. Expose `HasNovelStorageWarnings => NovelStorageWarnings.Count > 0`, subscribe to collection changes, and raise `OnPropertyChanged(nameof(HasNovelStorageWarnings))`. Populate warnings from `NovelBookCatalogSnapshot.CorruptMetadataPaths`. Add `BookshelfShowReading` to `AppSettings`, defaulting to `false` like Niratan. A Reading book has positive current character progress and is not complete. A book may appear in Reading and its stored custom/unshelved section because Reading is derived.

- [ ] **Step 4: Add explicit shelf commands**

Expose relay commands for create/rename/delete/reorder shelves, move selected books, move one book, and reorder inside a section. Rebuild all section projections from service state after each successful operation. Keep current import/delete/sort/sync commands intact.

```csharp
[RelayCommand]
private async Task MoveBooksToShelfAsync(NovelShelfMoveRequest request)
{
    var result = await _shelfService.MoveBooksAsync(
        request.BookIds,
        request.TargetShelfName,
        _cts.Token);
    if (!result.IsSuccess)
    {
        _notificationService.ShowError(result.Error!, result.ErrorTitle ?? "Shelf update failed");
        return;
    }

    RebuildShelfProjections(result.Value!, NovelBooks, RemoteBooks);
}

public sealed record NovelShelfMoveRequest(
    IReadOnlyList<string> BookIds,
    string? TargetShelfName);
```

- [ ] **Step 5: Run the ViewModel tests**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageViewModelTests|FullyQualifiedName~NovelShelfManagementViewModelTests"
```

Expected: PASS.

- [ ] **Step 6: Commit the shelf ViewModels**

```powershell
git add Hoshi/ViewModels/Components/NovelShelfSectionViewModel.cs Hoshi/ViewModels/Dialogs/NovelShelfManagementViewModel.cs Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs Hoshi/Models/Settings/AppSettings.cs Hoshi.Tests/ViewModels
git commit -m "feat(bookshelf): project novels into shelf sections"
```

### Task 8: WinUI Shelf Surface and Management Dialog

**Files:**
- Create: `Hoshi/Views/Dialogs/NovelShelfManagementDialog.xaml`
- Create: `Hoshi/Views/Dialogs/NovelShelfManagementDialog.xaml.cs`
- Modify: `Hoshi/Views/Pages/NovelLibraryPage.xaml`
- Modify: `Hoshi/Views/Pages/NovelLibraryPage.xaml.cs`
- Modify: `Hoshi/Strings/en-US/Resources.resw`
- Modify: `Hoshi/Strings/zh-CN/Resources.resw`
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Consumes: Task 7 section and management ViewModels.
- Produces: user-visible Reading/custom/Unshelved sections, native command surface, context moves, and shelf-management dialog.

- [ ] **Step 1: Add failing XAML/resource contract assertions**

Require these stable IDs and localization keys:

```csharp
libraryXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelLibraryCommandBar\"");
libraryXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelShelfSectionsControl\"");
libraryXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelShelfManagementButton\"");
libraryXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelStorageWarningInfoBar\"");
dialogXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelShelfList\"");
dialogXaml.Should().Contain("AutomationProperties.AutomationId=\"NovelShelfCreateButton\"");
```

Also assert English and Simplified Chinese keys exist for Reading, Unshelved, Manage Shelves, Create, Rename, Delete, Move To, and Remove From Shelf.

- [ ] **Step 2: Run the UI contract test and verify it fails**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderWebAssetTests"
```

Expected: FAIL because the shelf surface is absent.

- [ ] **Step 3: Replace the ad-hoc header row with a native CommandBar**

Use `AppBarToggleButton` for Statistics and `AppBarButton`/overflow items for sort, selection, sync, shelf management, and import. Keep primary commands keyboard reachable and move secondary actions to overflow at narrow width.

```xml
<CommandBar x:Name="NovelLibraryCommandBar"
            AutomationProperties.AutomationId="NovelLibraryCommandBar"
            DefaultLabelPosition="Right">
    <AppBarToggleButton x:Name="NovelLibraryStatisticsButton"
                        Icon="ReportDocument"
                        IsChecked="{x:Bind ViewModel.ShowStatisticsDashboard, Mode=TwoWay}" />
    <AppBarButton x:Name="NovelShelfManagementButton"
                  Icon="Folder"
                  Click="ShelfManagementButton_Click" />
    <AppBarButton Icon="Add" Command="{x:Bind ViewModel.ImportNovelCommand}" />
</CommandBar>
```

- [ ] **Step 4: Render shelf sections with explicit scroll ownership**

The page owns vertical scrolling. Reading and custom shelves use a horizontal `ScrollViewer` plus `ItemsControl`; Unshelved uses the adaptive main grid. Do not nest a vertically scrolling `GridView` inside the page scroller. Bind context-menu move commands to `ViewModel.MoveBookCommand` and pass typed parameters.

```xml
<ScrollViewer Grid.Row="2" VerticalScrollBarVisibility="Auto">
    <StackPanel Spacing="16">
        <ItemsControl x:Name="NovelShelfSectionsControl"
                      AutomationProperties.AutomationId="NovelShelfSectionsControl"
                      ItemsSource="{x:Bind ViewModel.RailSections, Mode=OneWay}">
            <ItemsControl.ItemTemplate>
                <DataTemplate x:DataType="vmc:NovelShelfSectionViewModel">
                    <StackPanel Spacing="8">
                        <TextBlock Text="{x:Bind DisplayName}" Style="{StaticResource SubtitleTextBlockStyle}" />
                        <ScrollViewer HorizontalScrollBarVisibility="Auto"
                                      VerticalScrollBarVisibility="Disabled">
                            <ItemsControl ItemsSource="{x:Bind Books}"
                                          ItemTemplate="{StaticResource NovelBookTemplate}" />
                        </ScrollViewer>
                    </StackPanel>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
        <TextBlock Text="Unshelved" Style="{StaticResource SubtitleTextBlockStyle}" />
        <ItemsRepeater ItemsSource="{x:Bind ViewModel.UnshelvedBooks, Mode=OneWay}"
                       ItemTemplate="{StaticResource NovelBookTemplate}">
            <ItemsRepeater.Layout>
                <UniformGridLayout MinItemWidth="180" MinItemHeight="286" />
            </ItemsRepeater.Layout>
        </ItemsRepeater>
    </StackPanel>
</ScrollViewer>
```

Use page rows `Auto,Auto,*`: CommandBar in row 0, warning in row 1, and the scroll owner in row 2. Place the warning above the scroll owner:

```xml
<InfoBar x:Name="NovelStorageWarningInfoBar"
         Grid.Row="1"
         AutomationProperties.AutomationId="NovelStorageWarningInfoBar"
         IsOpen="{x:Bind ViewModel.HasNovelStorageWarnings, Mode=OneWay}"
         IsClosable="False"
         Severity="Warning"
         Message="Some book metadata could not be read. The original files were preserved." />
```

- [ ] **Step 5: Add the management dialog**

Use a `ContentDialog` with a reorderable shelf list, create field, rename action, and delete confirmation. Code-behind only maps UI events to ViewModel commands and dialog results.

```xml
<ContentDialog x:Class="Hoshi.Views.Dialogs.NovelShelfManagementDialog"
               x:Name="RootDialog"
               Title="Manage Shelves"
               CloseButtonText="Close">
    <Grid RowDefinitions="Auto,*" RowSpacing="12">
        <Grid ColumnDefinitions="*,Auto" ColumnSpacing="8">
            <TextBox x:Name="ShelfNameBox"
                     AutomationProperties.AutomationId="NovelShelfNameBox" />
            <Button Grid.Column="1"
                    AutomationProperties.AutomationId="NovelShelfCreateButton"
                    Click="CreateShelfButton_Click" />
        </Grid>
        <ListView Grid.Row="1"
                  AutomationProperties.AutomationId="NovelShelfList"
                  ItemsSource="{x:Bind ViewModel.Shelves, Mode=OneWay}"
                  CanReorderItems="True" />
    </Grid>
</ContentDialog>
```

Forward the text from UI-only code-behind; validation and state changes stay in the ViewModel:

```csharp
private async void CreateShelfButton_Click(object sender, RoutedEventArgs e)
{
    if (ViewModel.CreateShelfCommand.CanExecute(ShelfNameBox.Text))
        await ViewModel.CreateShelfCommand.ExecuteAsync(ShelfNameBox.Text);
}
```

- [ ] **Step 6: Run UI contracts and ViewModel tests**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderWebAssetTests|FullyQualifiedName~NovelLibraryPageViewModelTests|FullyQualifiedName~NovelShelfManagementViewModelTests"
```

Expected: PASS.

- [ ] **Step 7: Commit the shelf UI**

```powershell
git add Hoshi/Views/Pages/NovelLibraryPage.xaml Hoshi/Views/Pages/NovelLibraryPage.xaml.cs Hoshi/Views/Dialogs/NovelShelfManagementDialog.xaml Hoshi/Views/Dialogs/NovelShelfManagementDialog.xaml.cs Hoshi/Strings Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "feat(bookshelf): add winui shelf management"
```

### Task 9: Full Regression, Documentation, and Launch Verification

**Files:**
- Modify: `docs/ARCHITECTURE.md`
- Modify: `docs/VERIFICATION.md`
- Modify: `docs/CHANGELOG.md`
- Verify: all files changed by Tasks 1-8.

**Interfaces:**
- Consumes: the complete phase-one implementation.
- Produces: documented architecture truth and evidence that the file-storage/shelf increment is safe to hand to phase two.

- [ ] **Step 1: Update architecture truth**

Document file-backed novel metadata/progress/order/shelves, the migration-only legacy SQLite source, video-only business SQLite, and the service/ViewModel boundaries. Remove statements claiming SQLite is the novel source of truth.

- [ ] **Step 2: Update verification and changelog**

Add migration fixtures, shelf CRUD/reorder/move checks, corrupt JSON recovery, and file-backed Reader reopen checks to `docs/VERIFICATION.md`. Add one concise user-visible changelog entry for persistent shelves and safer Niratan-compatible book storage.

- [ ] **Step 3: Run the complete x64 test suite**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
```

Expected: PASS with zero failed tests.

- [ ] **Step 4: Build x64**

Run:

```powershell
dotnet build -p:Platform=x64
```

Expected: `Build succeeded.` with zero errors.

- [ ] **Step 5: Launch and verify the actual WinUI app**

Run:

```powershell
.\build-and-run.ps1
```

Verify a responsive top-level Hoshi window, the existing library migrates once, books retain progress/Profile/cover/title, import creates metadata, shelf create/rename/reorder/delete works, context and multi-select moves work, in-section drag persists, delete cleans shelf references, and close/reopen preserves the same sections.

- [ ] **Step 6: Verify data-failure behavior with disposable fixtures**

Using copied test data rather than user originals, verify malformed metadata produces a recoverable warning, malformed shelves are not overwritten, a forced migration failure leaves legacy tables and backup intact, and retry completes after the fixture is repaired.

- [ ] **Step 7: Inspect final diff and commit documentation/verification changes**

```powershell
git diff --check
git status --short
git add docs/ARCHITECTURE.md docs/VERIFICATION.md docs/CHANGELOG.md
git commit -m "docs: record niratan novel storage migration"
```

Expected: only intended task files remain changed; unrelated pre-existing files stay untouched.
