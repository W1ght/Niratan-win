# Google Drive Import Progress and Statistics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Google Drive TTU imports restore the exact Niratan reading position and, when statistics sync is enabled, import the selected remote book's statistics.

**Architecture:** Generate `bookinfo.json` as part of EPUB import so character offsets are resolvable before the Reader opens. Pass the selected `TtuRemoteBookFiles` snapshot through `TtuSyncOptions` so new-book import never re-discovers a Drive folder from the converted EPUB title; retain title-based discovery for normal manual and automatic sync.

**Tech Stack:** C#/.NET, WinUI 3 service layer, xUnit v3, FluentAssertions, Moq, JSON sidecars.

## Global Constraints

- Target Windows 10+ x64; build with `dotnet build -p:Platform=x64`.
- Run tests with `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64`.
- Do not modify `native/hoshidicts/` or either reference repository.
- Do not access real Google Drive in automated verification; use fake `ITtuSyncRemoteStore` data only.
- Do not remove or bypass the statistics-sync switch: disabled means no remote statistics request and no `statistics.json` creation.
- Do not introduce another EPUB layout or character-counting engine; reuse `ReaderTextFilter.CountReadableCharacters`.
- Preserve the View → ViewModel → Service layering and keep all file IO in services.
- Preserve all unrelated dirty worktree changes and stage only the files listed by each task.

---

## File Structure

- `Niratan/Services/Novels/NovelEpubImportService.cs`: parse an imported EPUB, calculate chapter character counts, and save initial `bookinfo.json` before returning success.
- `Niratan.Tests/Services/Novels/NovelEpubImportServiceTests.cs`: prove `bookinfo.json` exists and contains Reader-compatible chapter mappings at import completion.
- `Niratan/Models/Sync/TtuSyncModels.cs`: carry an optional immutable snapshot of already-discovered remote files in `TtuSyncOptions`.
- `Niratan/Services/Sync/TtuSyncService.cs`: prefer the supplied snapshot while leaving existing title-based discovery unchanged when no snapshot is supplied.
- `Niratan.Tests/Services/Sync/TtuSyncServiceTests.cs`: cover snapshot identity, no re-list, chapter boundary mapping, book-end mapping, and statistics switch behavior.
- `Niratan/Services/Sync/TtuBookImportService.cs`: pass the selected remote files to sync and remove a newly imported local book if sidecar synchronization throws.
- `Niratan.Tests/Services/Sync/TtuBookImportServiceTests.cs`: cover exact snapshot propagation and incomplete-import cleanup.
- `docs/CHANGELOG.md`: record the root causes and resolution after code verification.

---

### Task 1: Generate `bookinfo.json` During EPUB Import

**Files:**
- Modify: `Niratan/Services/Novels/NovelEpubImportService.cs:13-103`
- Test: `Niratan.Tests/Services/Novels/NovelEpubImportServiceTests.cs:10-111`

**Interfaces:**
- Consumes: `INovelBookSidecarService.CreateBookInfo(IReadOnlyList<EpubChapter>, IReadOnlyList<int>, string?)` and `SaveBookInfoAsync(string, NovelBookInfo, CancellationToken)`.
- Produces: a successful `NovelImportResult` whose `Book.ExtractedPath` already contains `bookinfo.json`.

- [ ] **Step 1: Add a failing import-sidecar test and update test constructors**

Add `INovelBookSidecarService` to every `NovelEpubImportService` construction in this test file. Use the real `NovelBookSidecarService` because the assertion concerns the serialized sidecar contract.

```csharp
private static NovelEpubImportService CreateSut()
{
    var parser = new EpubParserService(NullLogger<EpubParserService>.Instance);
    return new NovelEpubImportService(
        parser,
        new NovelBookSidecarService(),
        NullLogger<NovelEpubImportService>.Instance);
}
```

Add this test; its parser callback writes controlled XHTML into the generated private directory so the count uses the same file paths the real parser returns:

```csharp
[Fact]
public async Task ImportAsync_SavesBookInfoBeforeReturningSuccess()
{
    var ct = TestContext.Current.CancellationToken;
    using var temp = new TempDirectory();
    var sourcePath = Path.Combine(temp.Path, "source.epub");
    await File.WriteAllTextAsync(sourcePath, "epub", ct);
    var booksRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "Novels")).FullName;
    var parser = new Mock<IEpubParserService>();
    parser
        .Setup(service => service.Parse(It.IsAny<string>(), It.IsAny<string>()))
        .Returns((string _, string outputDirectory) =>
        {
            var first = Path.Combine(outputDirectory, "chapter-1.xhtml");
            var second = Path.Combine(outputDirectory, "chapter-2.xhtml");
            File.WriteAllText(first, "<body><ruby>星<rt>ほし</rt></ruby>A!</body>");
            File.WriteAllText(second, "<body>読む。</body>");
            return new EpubBook
            {
                Title = "星を読む",
                ContainerDirectory = outputDirectory,
                Chapters =
                [
                    new EpubChapter { Href = first, SpineIndex = 0 },
                    new EpubChapter { Href = second, SpineIndex = 1 },
                ],
            };
        });
    var sidecars = new NovelBookSidecarService();
    var sut = new NovelEpubImportService(
        parser.Object,
        sidecars,
        NullLogger<NovelEpubImportService>.Instance,
        id => Path.Combine(booksRoot, id));

    var result = await sut.ImportAsync(sourcePath, ct);

    result.IsSuccess.Should().BeTrue(result.Error);
    var bookInfo = await sidecars.LoadBookInfoAsync(result.Value!.Book.ExtractedPath!, ct);
    bookInfo.Should().NotBeNull();
    bookInfo!.CharacterCount.Should().Be(4);
    bookInfo.ChapterInfo["chapter-1.xhtml"].Should().Be(
        new NovelBookInfoChapter(SpineIndex: 0, CurrentTotal: 0, ChapterCount: 2));
    bookInfo.ChapterInfo["chapter-2.xhtml"].Should().Be(
        new NovelBookInfoChapter(SpineIndex: 1, CurrentTotal: 2, ChapterCount: 2));
}
```

- [ ] **Step 2: Run the focused test and verify the constructor/sidecar failure**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelEpubImportServiceTests.ImportAsync_SavesBookInfoBeforeReturningSuccess"
```

Expected: FAIL because `NovelEpubImportService` does not accept the sidecar dependency and does not write `bookinfo.json`.

- [ ] **Step 3: Inject the sidecar service and write Reader-compatible book info**

Update the public and internal constructors and add the field:

```csharp
private readonly INovelBookSidecarService _sidecars;

public NovelEpubImportService(
    IEpubParserService epubParser,
    INovelBookSidecarService sidecars,
    ILogger<NovelEpubImportService> logger)
    : this(epubParser, sidecars, logger, AppDataHelper.GetNovelBookPath)
{
}

internal NovelEpubImportService(
    IEpubParserService epubParser,
    INovelBookSidecarService sidecars,
    ILogger<NovelEpubImportService> logger,
    Func<string, string> bookRootResolver)
{
    _epubParser = epubParser;
    _sidecars = sidecars;
    _logger = logger;
    _bookRootResolver = bookRootResolver;
}
```

Immediately after `_epubParser.Parse(...)` completes and before constructing the `NovelBook`, calculate and save the initial mapping:

```csharp
var chapterCharacterCounts = await Task.Run(
    () => epubBook.Chapters
        .Select(chapter => CountReadableCharacters(chapter.Href))
        .ToArray(),
    ct);
var bookInfo = _sidecars.CreateBookInfo(
    epubBook.Chapters,
    chapterCharacterCounts,
    epubBook.ContainerDirectory);
await _sidecars.SaveBookInfoAsync(bookRoot, bookInfo, ct);
```

Add `using System.Linq;` and this helper, matching `NovelReaderPage` behavior for a missing spine resource:

```csharp
private static int CountReadableCharacters(string chapterPath)
{
    if (!File.Exists(chapterPath))
        return 0;

    return ReaderTextFilter.CountReadableCharacters(File.ReadAllText(chapterPath));
}
```

Update the existing internal-constructor test to pass a `NovelBookSidecarService` as the second argument.

- [ ] **Step 4: Run the EPUB import and text-filter tests**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelEpubImportServiceTests|FullyQualifiedName~ReaderTextFilterTests|FullyQualifiedName~NovelBookSidecarServiceTests"
```

Expected: PASS with all selected tests green.

- [ ] **Step 5: Commit the isolated EPUB-import change**

```powershell
git add -- Niratan/Services/Novels/NovelEpubImportService.cs Niratan.Tests/Services/Novels/NovelEpubImportServiceTests.cs
git commit -m "fix(novels): create book info during EPUB import"
```

---

### Task 2: Use the Selected Drive File Snapshot and Resolve Exact Positions

**Files:**
- Modify: `Niratan/Models/Sync/TtuSyncModels.cs:22-29`
- Modify: `Niratan/Services/Sync/TtuSyncService.cs:35-53`
- Test: `Niratan.Tests/Services/Sync/TtuSyncServiceTests.cs:137-375,437-535`

**Interfaces:**
- Consumes: the selected `TtuRemoteBookFiles` value supplied by the new-book import path.
- Produces: `TtuSyncOptions.KnownRemoteFiles` of type `TtuRemoteBookFiles?`; when non-null, `SyncBookAsync` performs no `ListBookFilesAsync(book.Title)` call.

- [ ] **Step 1: Add failing tests for snapshot use, statistics gating, and boundary positions**

Add counters to `FakeTtuSyncRemoteStore`:

```csharp
public int ListBookFilesCallCount { get; private set; }
public int GetStatisticsCallCount { get; private set; }

public Task<TtuRemoteBookFiles> ListBookFilesAsync(
    string bookTitle,
    CancellationToken ct = default)
{
    ListBookFilesCallCount++;
    return Task.FromResult(new TtuRemoteBookFiles(
        ProgressFile,
        StatisticsFile,
        AudioBookFile,
        BookData: null,
        Cover: null));
}

public Task<IReadOnlyList<NovelReadingStatistic>?> GetStatisticsAsync(
    TtuRemoteFile file,
    CancellationToken ct = default)
{
    GetStatisticsCallCount++;
    return StatisticsFailure == null
        ? Task.FromResult(Statistics)
        : Task.FromException<IReadOnlyList<NovelReadingStatistic>?>(StatisticsFailure);
}
```

Add a helper that captures the exact files already discovered for the selected Drive card:

```csharp
private static TtuRemoteBookFiles KnownFiles(FakeTtuSyncRemoteStore remote) => new(
    remote.ProgressFile,
    remote.StatisticsFile,
    remote.AudioBookFile,
    BookData: null,
    Cover: null);
```

Add the main regression test:

```csharp
[Fact]
public async Task SyncBookAsync_ImportUsesKnownFilesWithoutRelistingConvertedTitle()
{
    var ct = TestContext.Current.CancellationToken;
    using var temp = new TempBookDirectory();
    var sidecars = await CreateSidecarsAsync(temp.Path, ct);
    var remote = RemoteImportPayload();
    var sut = CreateSut(sidecars, remote);
    var book = CreateBook(temp.Path);
    book.Title = "EPUB metadata title differs";

    var result = await sut.SyncBookAsync(
        book,
        ImportAllOptions() with { KnownRemoteFiles = KnownFiles(remote) },
        ct);

    result.Kind.Should().Be(TtuSyncResultKind.Imported);
    remote.ListBookFilesCallCount.Should().Be(0);
    (await sidecars.Book.LoadBookmarkAsync(temp.Path, ct)).Should().Be(
        new NovelBookmark(1, 0.25, 1250, DateTimeOffset.FromUnixTimeMilliseconds(2000)));
    (await sidecars.Statistics.LoadAsync(temp.Path, ct)).Should().ContainSingle()
        .Which.CharactersRead.Should().Be(30);
}
```

Add the statistics-switch test:

```csharp
[Fact]
public async Task SyncBookAsync_KnownFilesDoNotFetchStatisticsWhenStatisticsSyncIsDisabled()
{
    var ct = TestContext.Current.CancellationToken;
    using var temp = new TempBookDirectory();
    var sidecars = await CreateSidecarsAsync(temp.Path, ct);
    var remote = RemoteImportPayload();
    var sut = CreateSut(sidecars, remote);

    var result = await sut.SyncBookAsync(
        CreateBook(temp.Path),
        new TtuSyncOptions(
            Direction: TtuSyncDirection.ImportFromTtu,
            SyncStatistics: false,
            KnownRemoteFiles: KnownFiles(remote)),
        ct);

    result.Kind.Should().Be(TtuSyncResultKind.Imported);
    remote.GetStatisticsCallCount.Should().Be(0);
    File.Exists(Path.Combine(temp.Path, NovelStatisticsSidecarService.StatisticsFileName))
        .Should().BeFalse();
}
```

Add the missing-remote-statistics test so an absent optional file cannot block progress import:

```csharp
[Fact]
public async Task SyncBookAsync_KnownFilesWithoutStatisticsStillImportProgress()
{
    var ct = TestContext.Current.CancellationToken;
    using var temp = new TempBookDirectory();
    var sidecars = await CreateSidecarsAsync(temp.Path, ct);
    var remote = RemoteImportPayload();
    remote.StatisticsFile = null;
    var sut = CreateSut(sidecars, remote);

    var result = await sut.SyncBookAsync(
        CreateBook(temp.Path),
        ImportAllOptions() with { KnownRemoteFiles = KnownFiles(remote) },
        ct);

    result.Kind.Should().Be(TtuSyncResultKind.Imported);
    (await sidecars.Book.LoadBookmarkAsync(temp.Path, ct))!.CharacterCount.Should().Be(1250);
    remote.GetStatisticsCallCount.Should().Be(0);
    File.Exists(Path.Combine(temp.Path, NovelStatisticsSidecarService.StatisticsFileName))
        .Should().BeFalse();
}
```

Add boundary coverage using fresh temporary books per case:

```csharp
[Theory]
[InlineData(1000, 1, 0.0)]
[InlineData(2000, 1, 1.0)]
public async Task SyncBookAsync_ImportMapsChapterBoundaryAndBookEnd(
    int exploredCharacters,
    int expectedChapter,
    double expectedProgress)
{
    var ct = TestContext.Current.CancellationToken;
    using var temp = new TempBookDirectory();
    var sidecars = await CreateSidecarsAsync(temp.Path, ct);
    var remote = RemoteImportPayload();
    remote.Progress = remote.Progress! with
    {
        ExploredCharCount = exploredCharacters,
        Progress = exploredCharacters / 2000d,
    };
    var sut = CreateSut(sidecars, remote);

    await sut.SyncBookAsync(
        CreateBook(temp.Path),
        ImportAllOptions() with { KnownRemoteFiles = KnownFiles(remote) },
        ct);

    var bookmark = await sidecars.Book.LoadBookmarkAsync(temp.Path, ct);
    bookmark!.ChapterIndex.Should().Be(expectedChapter);
    bookmark.Progress.Should().Be(expectedProgress);
}
```

- [ ] **Step 2: Run the focused sync tests and verify they fail**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~TtuSyncServiceTests.SyncBookAsync_ImportUsesKnownFiles|FullyQualifiedName~TtuSyncServiceTests.SyncBookAsync_KnownFiles|FullyQualifiedName~TtuSyncServiceTests.SyncBookAsync_ImportMapsChapterBoundary"
```

Expected: FAIL to compile because `KnownRemoteFiles` does not exist; after adding only the option, the first test must still fail because the service calls `ListBookFilesAsync`.

- [ ] **Step 3: Add the optional snapshot and prefer it in `TtuSyncService`**

Extend `TtuSyncOptions` without changing existing call sites:

```csharp
public sealed record TtuSyncOptions(
    TtuSyncDirection Direction = TtuSyncDirection.Auto,
    bool SyncBookData = false,
    bool SyncStatistics = false,
    StatisticsSyncMode StatisticsSyncMode = StatisticsSyncMode.Merge,
    bool SyncAudioBook = false,
    bool ImportOnly = false,
    TtuRemoteBookFiles? KnownRemoteFiles = null);
```

Replace the unconditional remote listing in `SyncBookAsync`:

```csharp
var remoteFiles = options.KnownRemoteFiles
    ?? await _remoteStore.ListBookFilesAsync(book.Title, ct);
```

No export call site supplies `KnownRemoteFiles`; therefore title-based lookup and update behavior remain unchanged outside new-book import.

- [ ] **Step 4: Run all TTU sync service tests**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~TtuSyncServiceTests"
```

Expected: PASS, including the existing Merge, Replace, rollback, auto-direction, and audiobook cases.

- [ ] **Step 5: Commit the sync-service change**

```powershell
git add -- Niratan/Models/Sync/TtuSyncModels.cs Niratan/Services/Sync/TtuSyncService.cs Niratan.Tests/Services/Sync/TtuSyncServiceTests.cs
git commit -m "fix(sync): use selected Drive sidecar snapshot"
```

---

### Task 3: Pass the Snapshot Through Drive Import and Clean Failed Imports

**Files:**
- Modify: `Niratan/Services/Sync/TtuBookImportService.cs:31-92`
- Test: `Niratan.Tests/Services/Sync/TtuBookImportServiceTests.cs:10-169`

**Interfaces:**
- Consumes: `TtuSyncOptions.KnownRemoteFiles` from Task 2 and `INovelLibraryService.DeleteNovelAsync(string, CancellationToken)`.
- Produces: successful imports whose sync service receives the exact `remoteBook.Files` instance; failed post-import synchronization removes the newly created local book.

- [ ] **Step 1: Strengthen the existing import test and add a failing cleanup test**

Update the existing options assertion:

```csharp
sync.Options.Should().Be(new TtuSyncOptions(
    Direction: TtuSyncDirection.ImportFromTtu,
    SyncBookData: false,
    SyncStatistics: true,
    StatisticsSyncMode: StatisticsSyncMode.Merge,
    SyncAudioBook: true,
    ImportOnly: true,
    KnownRemoteFiles: remoteBook.Files));
sync.Options!.KnownRemoteFiles.Should().BeSameAs(remoteBook.Files);
```

Make `FakeTtuSyncService` optionally throw:

```csharp
public Exception? Failure { get; set; }

public Task<TtuSyncResult> SyncBookAsync(
    NovelBook book,
    TtuSyncOptions options,
    CancellationToken ct = default)
{
    Book = book;
    Options = options;
    return Failure == null
        ? Task.FromResult(new TtuSyncResult(TtuSyncResultKind.Imported, book.Title))
        : Task.FromException<TtuSyncResult>(Failure);
}
```

Record deletion in `FakeNovelLibraryService`:

```csharp
public string? DeletedBookId { get; private set; }

public Task<Result> DeleteNovelAsync(string bookId, CancellationToken ct = default)
{
    DeletedBookId = bookId;
    return Task.FromResult(Result.Success());
}
```

Add the cleanup test:

```csharp
[Fact]
public async Task ImportRemoteBookAsync_RemovesNewBookWhenSidecarSyncFails()
{
    var ct = TestContext.Current.CancellationToken;
    var remote = new FakeRemoteStore();
    var converter = new FakeConverter();
    var library = new FakeNovelLibraryService();
    var sync = new FakeTtuSyncService { Failure = new IOException("progress fetch failed") };
    var service = new TtuBookImportService(remote, converter, library, sync);
    var remoteBook = CreateRemoteBook();

    var result = await service.ImportRemoteBookAsync(
        remoteBook,
        new TtuBookImportOptions(SyncStatistics: true),
        progress: null,
        ct);

    result.IsSuccess.Should().BeFalse();
    result.ErrorTitle.Should().Be("Google Drive import failed");
    library.DeletedBookId.Should().Be("book-1");
}
```

Extract the current inline `TtuRemoteBook` construction into this helper so both tests use the same controlled file snapshot:

```csharp
private static TtuRemoteBook CreateRemoteBook() => new(
    Id: "folder-id",
    Title: "星を読む",
    SanitizedTitle: "星を読む",
    Files: new TtuRemoteBookFiles(
        Progress: new TtuRemoteFile("progress-id", "progress_1_6_2000_0.5.json"),
        Statistics: new TtuRemoteFile(
            "stats-id",
            "statistics_1_6_1_1_1_0_0_0_0_1_1_1_1_3600_3600_na.json"),
        AudioBook: new TtuRemoteFile("audio-id", "audioBook_1_6_2000_42.json"),
        BookData: new TtuRemoteFile("bookdata-id", "bookdata_1_6_1200_2000_1000.zip"),
        Cover: null),
    Progress: 0.5);
```

- [ ] **Step 2: Run the import-service tests and verify both regressions fail**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~TtuBookImportServiceTests"
```

Expected: FAIL because `KnownRemoteFiles` is not passed and the imported book is not deleted after the sync exception.

- [ ] **Step 3: Pass the snapshot and clean up only the newly imported book**

Track the imported book outside the `try` body:

```csharp
NovelBook? importedBook = null;
```

After local import succeeds, assign it and pass the exact snapshot:

```csharp
importedBook = importResult.Value;
await _syncService.SyncBookAsync(
    importedBook,
    new TtuSyncOptions(
        Direction: TtuSyncDirection.ImportFromTtu,
        SyncBookData: false,
        SyncStatistics: options.SyncStatistics,
        StatisticsSyncMode: options.StatisticsSyncMode,
        SyncAudioBook: options.SyncAudioBook,
        ImportOnly: true,
        KnownRemoteFiles: remoteBook.Files),
    ct);
```

Clean up in both cancellation and exception paths using a non-cancelled token; never delete before `ImportEpubAsync` has returned a new book:

```csharp
catch (OperationCanceledException)
{
    await TryDeleteImportedBookAsync(importedBook);
    return Result<NovelBook>.Cancelled();
}
catch (Exception ex)
{
    await TryDeleteImportedBookAsync(importedBook);
    return Result<NovelBook>.Failure(ex.Message, "Google Drive import failed");
}
```

Add the best-effort helper without masking the original sync error:

```csharp
private async Task TryDeleteImportedBookAsync(NovelBook? book)
{
    if (book == null)
        return;

    try
    {
        await _libraryService.DeleteNovelAsync(book.Id, CancellationToken.None);
    }
    catch
    {
    }
}
```

- [ ] **Step 4: Run Drive import, library, and sync tests**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~TtuBookImportServiceTests|FullyQualifiedName~NovelLibraryPageViewModelTests|FullyQualifiedName~TtuSyncServiceTests"
```

Expected: PASS. Existing `NovelLibraryPageViewModelTests` must continue proving that `StatisticsSettings.EnableSync` controls `TtuBookImportOptions.SyncStatistics`.

- [ ] **Step 5: Commit the import orchestration change**

```powershell
git add -- Niratan/Services/Sync/TtuBookImportService.cs Niratan.Tests/Services/Sync/TtuBookImportServiceTests.cs
git commit -m "fix(sync): preserve Drive import sidecars"
```

---

### Task 4: Record the Fix and Run Full Verification

**Files:**
- Modify: `docs/CHANGELOG.md:3`
- Verify: all files changed by Tasks 1-3 plus the existing Reader worktree changes.

**Interfaces:**
- Consumes: the completed behavior from Tasks 1-3.
- Produces: an evidence-backed x64 build/test result and a root-cause changelog entry.

- [ ] **Step 1: Add the changelog entry without altering existing uncommitted entries**

Prepend this section after `# Changelog` and keep the existing Reader entries intact:

```markdown
## Google Drive 下载书籍未恢复进度且统计未导入

**原因**：
- 新书导入完成后按 EPUB 元数据标题重新查询 Drive 文件夹；当远端目录标题与 EPUB 标题不一致时，会命中错误或空目录，丢失用户所选书籍的 progress/statistics 文件快照。
- 普通 EPUB 导入返回时尚未生成 `bookinfo.json`，远端全书字符位置无法在首次打开前换算为正确 spine 章节。

**解决**：
- Drive 新书导入把已选择的远端文件快照直接传给同步服务；普通手动/自动同步仍保持按书名发现目录。
- EPUB 导入阶段复用 Reader 字符过滤规则生成 `bookinfo.json`，再导入 bookmark；统计仍严格受统计同步开关及 Merge/Replace 模式控制。
- 新增标题不一致、首次跨章定位、章节边界、统计开关、sidecar 导入失败清理等回归测试。

---
```

- [ ] **Step 2: Run whitespace and focused JavaScript regressions already present in the dirty worktree**

Run:

```powershell
git diff --check
node --test Niratan.Tests/Web/NovelReader/reader-bridge.runtime.test.js Niratan.Tests/Web/NovelReader/selection.runtime.test.js
```

Expected: `git diff --check` exits 0 and all Node tests pass.

- [ ] **Step 3: Run the complete x64 .NET test suite**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64
```

Expected: all tests pass with zero failed and zero skipped tests unless the repository already marks a test skipped.

- [ ] **Step 4: Build the x64 application**

Run:

```powershell
dotnet build -p:Platform=x64
```

Expected: build succeeds with zero errors.

- [ ] **Step 5: Build and launch Niratan for controlled Reader verification**

Run:

```powershell
.\build-and-run.ps1
```

Use a local fake Drive fixture or a locally imported converted TTU book with controlled `bookmark.json`, `bookinfo.json`, and `statistics.json`; do not authenticate to or mutate real Google Drive. Verify:

- the first visible Reader render is already on the expected later chapter and does not jump after opening;
- the Reader progress header matches the imported character offset;
- with statistics sync enabled in the fixture, the Dashboard includes the imported record;
- with statistics sync disabled, no statistics sidecar is created by import;
- same-chapter and cross-chapter page turns still work.

- [ ] **Step 6: Commit the changelog after verification**

`docs/CHANGELOG.md` already contains uncommitted entries from the preceding Reader fixes. Review its complete staged diff before committing so those entries remain intentional:

```powershell
git add -- docs/CHANGELOG.md
git diff --cached --check
git diff --cached -- docs/CHANGELOG.md
git commit -m "docs: record Reader and Drive import fixes"
```

- [ ] **Step 7: Review final scope**

Run:

```powershell
git status --short
git log -6 --oneline
```

Expected: only the already-known reference submodule dirtiness and any intentionally uncommitted prior Reader files remain; no temporary TTU download, generated EPUB, test sidecar, or credential file is present.
