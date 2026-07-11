# Niratan Bookshelf Regression Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every novel card open reliably, project a Niratan-style Reading shelf automatically, render every section as an adaptive multi-row grid, add cached Google Drive covers and three-way parallel imports, and keep statistics activation responsive.

**Architecture:** `NovelLibraryPageViewModel` projects one ordered collection of typed local/remote sections and coordinates independent page, catalog, cover, and import lifetimes. A focused Google Drive cover-cache service owns authenticated image IO. Statistics snapshot work leaves the UI context, while one guarded UI-only layout controller owns dashboard breakpoints.

**Tech Stack:** C# 14 / .NET 10, WinUI 3, Windows App SDK, CommunityToolkit.Mvvm, `HttpClient`, JSON sidecars, xUnit v3, FluentAssertions, Moq.

## Global Constraints

- Target Windows 10+ x64; build with `dotnet build -p:Platform=x64` and test with `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64`.
- Do not modify `native/hoshidicts/` or reintroduce foliate-js.
- Keep View code-behind UI-only; ViewModels expose state and commands; services own IO.
- ViewModels must not access SQLite, files, or `HttpClient` directly.
- EPUB and sidecar storage remain unchanged; no new database or ORM.
- Preserve all unrelated worktree changes and stage only files named by the active task.
- Use test-first red-green-refactor for every production behavior.

---

## File Structure

- Modify `Hoshi/ViewModels/Components/NovelShelfSectionViewModel.cs`: typed local/remote section projection.
- Modify `Hoshi/ViewModels/Components/RemoteNovelBookItemViewModel.cs`: observable cover and per-import state.
- Modify `Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs`: ordered section projection, independent cancellation, cover hydration, bounded parallel imports.
- Modify `Hoshi/Views/Pages/NovelLibraryPage.xaml`: one adaptive multi-row section surface and explicit card commands.
- Modify `Hoshi/Views/Pages/NovelLibraryPage.xaml.cs`: remove the unreliable local-card `DataContext` activation handler.
- Create `Hoshi/Services/Sync/IGoogleDriveCoverCacheService.cs`: narrow cover cache contract.
- Create `Hoshi/Services/Sync/GoogleDriveCoverCacheService.cs`: authenticated, atomic, validated cover cache.
- Create `Hoshi.Tests/TestUtils/TempDirectory.cs`: reusable disposable directory for new service/ViewModel tests.
- Modify `Hoshi/App.xaml.cs`: register the cover service.
- Modify `Hoshi/ViewModels/Pages/NovelStatisticsDashboardViewModel.cs`: yield the loading surface and retain generation safety.
- Modify `Hoshi/Services/Novels/NovelStatisticsDashboardService.cs`: leave the caller context before cache and sidecar work.
- Create `Hoshi/Views/Controls/NovelStatisticsDashboardLayout.cs`: pure breakpoint selection.
- Modify `Hoshi/Views/Controls/NovelStatisticsDashboardView.xaml(.cs)`: one guarded adaptive owner.
- Create focused tests under `Hoshi.Tests/Services/Sync`, `Hoshi.Tests/ViewModels/Pages`, and `Hoshi.Tests/Views/Pages`; update obsolete bookshelf/dashboard asset assertions in `NovelReaderWebAssetTests.cs`.

---

### Task 1: Unified Reading-to-Unshelved Multi-Row Sections and Reliable Card Activation

**Files:**
- Modify: `Hoshi/ViewModels/Components/NovelShelfSectionViewModel.cs`
- Modify: `Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs`
- Modify: `Hoshi/Views/Pages/NovelLibraryPage.xaml`
- Modify: `Hoshi/Views/Pages/NovelLibraryPage.xaml.cs`
- Modify: `Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`
- Create: `Hoshi.Tests/Views/Pages/NovelLibraryPageAssetTests.cs`
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Produces: `NovelLibraryPageViewModel.ShelfSections` in Reading/custom/Google Drive/Unshelved order.
- Produces: `NovelShelfSectionViewModel` with `Books`, `RemoteBooks`, `ShowsLocalBooks`, and `ShowsRemoteBooks`.
- Consumes: existing `OpenNovelCommand`, `NovelShelfState`, `NovelLibrarySortOption`, `RemoteBooks`.

- [ ] **Step 1: Add failing projection tests**

Replace the current setting-dependent projection fixture with explicit tests:

```csharp
[Fact]
public async Task InitializeAsync_AlwaysProjectsNonEmptyReadingFirst()
{
    var books = new[]
    {
        new NovelBook { Id = "reading", Title = "Reading", CurrentCharacterCount = 10, TotalCharacterCount = 100 },
        new NovelBook { Id = "complete", Title = "Complete", CurrentCharacterCount = 100, TotalCharacterCount = 100 },
        new NovelBook { Id = "new", Title = "New" },
    };
    var library = LibraryWithBooks(books);
    var shelves = ShelfService(new NovelShelfState(
        [new NovelShelf("收藏", ["complete"])],
        ["reading", "new"]));
    var settings = Mock.Of<ISettingsService>(service => service.Current == new AppSettings
    {
        BookshelfShowReading = false,
    });
    var sut = CreateSut(library, settingsService: settings, shelfService: shelves);

    await sut.InitializeAsync();

    sut.ShelfSections.Select(section => section.Id)
        .Should().Equal("reading", "shelf:收藏", "unshelved");
    sut.ShelfSections[0].Books.Select(item => item.Book.Id).Should().Equal("reading");
    sut.ShelfSections.Single(section => section.Id == "unshelved")
        .Books.Select(item => item.Book.Id).Should().Equal("reading", "new");
}

[Fact]
public async Task InitializeAsync_OmitsReadingWhenNoBookIsInProgress()
{
    var sut = CreateSut(novelService: LibraryWithBooks([
        new NovelBook { Id = "new", Title = "New" },
        new NovelBook { Id = "done", Title = "Done", CurrentCharacterCount = 10, TotalCharacterCount = 10 },
    ]));

    await sut.InitializeAsync();

    sut.ShelfSections.Should().ContainSingle(section => section.Id == "unshelved");
    sut.ShelfSections.Should().NotContain(section => section.Id == "reading");
}
```

Add these test helpers; do not introduce production test hooks:

```csharp
private static INovelLibraryService LibraryWithBooks(IReadOnlyList<NovelBook> books)
{
    var service = new Mock<INovelLibraryService>();
    service.Setup(value => value.GetNovelBooksAsync(null, It.IsAny<CancellationToken>()))
        .ReturnsAsync(Result<NovelBookCatalogSnapshot>.Success(
            new NovelBookCatalogSnapshot(books, [])));
    return service.Object;
}

private static INovelShelfService ShelfService(NovelShelfState state)
{
    var service = new Mock<INovelShelfService>();
    service.Setup(value => value.LoadAsync(It.IsAny<CancellationToken>()))
        .ReturnsAsync(Result<NovelShelfState>.Success(state));
    return service.Object;
}
```

- [ ] **Step 2: Add a failing XAML activation/layout asset test**

Create `NovelLibraryPageAssetTests.cs`:

```csharp
private static readonly string ProjectRoot = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Hoshi"));

[Fact]
public void ShelfSections_UseExplicitCommandsAndWrappingGrid()
{
    var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml"));
    var code = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml.cs"));

    xaml.Should().Contain("ItemsSource=\"{x:Bind ViewModel.ShelfSections, Mode=OneWay}\"");
    xaml.Should().Contain("Command=\"{Binding ViewModel.OpenNovelCommand, ElementName=ThisPage}\"");
    xaml.Should().Contain("CommandParameter=\"{x:Bind}\"");
    xaml.Should().Contain("<UniformGridLayout");
    xaml.Should().NotContain("StackPanel Orientation=\"Horizontal\"");
    xaml.Should().NotContain("NovelUnshelvedBooksRepeater");
    code.Should().NotContain("NovelBookButton_Click");
}
```

Replace the three obsolete assertions with:

```csharp
libraryXaml.Should().Contain("ItemsSource=\"{x:Bind ViewModel.ShelfSections, Mode=OneWay}\"");
libraryXaml.Should().NotContain("NovelUnshelvedBooksRepeater");
libraryViewModel.Should().Contain("\"unshelved\"");
libraryViewModel.Should().NotContain("ObservableCollection<NovelBookItemViewModel> UnshelvedBooks");
```

- [ ] **Step 3: Run the focused tests and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageViewModelTests|FullyQualifiedName~NovelLibraryPageAssetTests|FullyQualifiedName~NovelReaderWebAssetTests"
```

Expected: FAIL because `ShelfSections` does not exist, Reading still depends on `BookshelfShowReading`, and XAML still uses separate horizontal rails and a click handler.

- [ ] **Step 4: Implement the typed section model**

Replace the positional record with a focused class:

```csharp
public sealed class NovelShelfSectionViewModel
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public bool IsDerived { get; init; }
    public bool IsUnshelved { get; init; }
    public bool IsRemote { get; init; }
    public ObservableCollection<NovelBookItemViewModel> Books { get; init; } = [];
    public ObservableCollection<RemoteNovelBookItemViewModel> RemoteBooks { get; init; } = [];
    public bool ShowsLocalBooks => !IsRemote;
    public bool ShowsRemoteBooks => IsRemote;
}
```

Rename `RailSections` to `ShelfSections` and remove `UnshelvedBooks`. In `RebuildShelfProjections`, always calculate Reading, append non-empty Reading first, append persisted shelves, append Google Drive when `RemoteBooks.Count > 0`, then append Unshelved. Use `SortBooks` for non-manual order; use the persisted shelf or unshelved ID order when Manual is selected.

Call `RebuildShelfProjections` after local loads, remote refreshes, remote removal, shelf updates, and sort-option changes so every section stays current.

- [ ] **Step 5: Replace the page with one vertical multi-row section surface**

Bind the local card root directly:

```xml
<Button Width="180"
        Command="{Binding ViewModel.OpenNovelCommand, ElementName=ThisPage}"
        CommandParameter="{x:Bind}"
        AutomationProperties.AutomationId="{x:Bind AutomationId, Mode=OneTime}">
```

Render all sections through `ShelfSections`. Each section contains local and remote repeaters with complementary visibility and the same grid geometry:

```xml
<ItemsRepeater ItemsSource="{x:Bind Books}"
               ItemTemplate="{StaticResource NovelBookTemplate}"
               Visibility="{x:Bind ShowsLocalBooks, Converter={StaticResource BooleanToVisibilityConverter}}">
    <ItemsRepeater.Layout>
        <UniformGridLayout MinItemWidth="180" MinItemHeight="306"
                           MinColumnSpacing="12" MinRowSpacing="16"
                           ItemsJustification="Start" />
    </ItemsRepeater.Layout>
</ItemsRepeater>
```

Use the same layout for `RemoteBooks`. Remove the section-level horizontal `ScrollViewer`s, the separate Unshelved repeater, and the separate Google Drive rail. Remove `NovelBookButton_Click` from code-behind; retain UI-only drag/drop and shelf-dialog handlers.

- [ ] **Step 6: Run focused tests and build GREEN**

Run the Step 3 test command, then:

```powershell
dotnet build -p:Platform=x64
```

Expected: PASS; build has no new errors.

- [ ] **Step 7: Commit the unified shelf surface**

```powershell
git add Hoshi/ViewModels/Components/NovelShelfSectionViewModel.cs Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs Hoshi/Views/Pages/NovelLibraryPage.xaml Hoshi/Views/Pages/NovelLibraryPage.xaml.cs Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs Hoshi.Tests/Views/Pages/NovelLibraryPageAssetTests.cs Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "fix(bookshelf): restore niratan section activation and layout"
```

### Task 2: Authenticated Google Drive Cover Cache

**Files:**
- Create: `Hoshi/Services/Sync/IGoogleDriveCoverCacheService.cs`
- Create: `Hoshi/Services/Sync/GoogleDriveCoverCacheService.cs`
- Modify: `Hoshi/Helpers/AppDataHelper.cs`
- Modify: `Hoshi/ViewModels/Components/RemoteNovelBookItemViewModel.cs`
- Modify: `Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs`
- Modify: `Hoshi/App.xaml.cs`
- Create: `Hoshi.Tests/Services/Sync/GoogleDriveCoverCacheServiceTests.cs`
- Create: `Hoshi.Tests/TestUtils/TempDirectory.cs`
- Modify: `Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`

**Interfaces:**
- Produces: `IGoogleDriveCoverCacheService.GetCoverPathAsync(TtuRemoteFile?, CancellationToken)`.
- Consumes: `TtuRemoteFile.ThumbnailLink`, `IGoogleDriveAuthService`, shared `HttpClient`.
- Produces: observable `RemoteNovelBookItemViewModel.CoverImage` and `HasCover`.

- [ ] **Step 1: Write failing cover-cache tests**

Use a temporary cache root and queued `HttpMessageHandler`:

Create the shared helper first:

```csharp
namespace Hoshi.Tests.TestUtils;

public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"hoshi-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
```

```csharp
[Fact]
public async Task GetCoverPathAsync_DownloadsAuthenticatedS768ThumbnailAtomically()
{
    using var temp = new TempDirectory();
    var handler = new RecordingHandler(ImageResponse(PngBytes));
    var service = new GoogleDriveCoverCacheService(
        new HttpClient(handler), new FakeGoogleDriveAuthService("token"), temp.Path);
    var cover = new TtuRemoteFile("cover-id", "cover_1_6.png", ThumbnailLink: "https://thumb.test/image=s220");

    var path = await service.GetCoverPathAsync(cover, TestContext.Current.CancellationToken);

    path.Should().NotBeNull();
    File.ReadAllBytes(path!).Should().Equal(PngBytes);
    handler.Requests.Single().RequestUri!.ToString().Should().EndWith("=s768");
    handler.Requests.Single().Headers.Authorization!.Parameter.Should().Be("token");
    Directory.EnumerateFiles(temp.Path, "*.tmp").Should().BeEmpty();
}

[Fact]
public async Task GetCoverPathAsync_ReusesValidCacheWithoutNetwork()
{
    using var temp = new TempDirectory();
    var first = new GoogleDriveCoverCacheService(
        new HttpClient(new RecordingHandler(ImageResponse(PngBytes))),
        new FakeGoogleDriveAuthService("token"), temp.Path);
    var cover = new TtuRemoteFile("cover-id", "cover_1_6.png", ThumbnailLink: "https://thumb.test/image=s220");
    var expected = await first.GetCoverPathAsync(cover);
    var second = new GoogleDriveCoverCacheService(
        new HttpClient(new ThrowingHandler()),
        new FakeGoogleDriveAuthService("token"), temp.Path);

    var actual = await second.GetCoverPathAsync(cover);

    actual.Should().Be(expected);
}

[Fact]
public async Task GetCoverPathAsync_InvalidImageLeavesNoCacheAndReturnsNull()
{
    using var temp = new TempDirectory();
    var service = new GoogleDriveCoverCacheService(
        new HttpClient(new RecordingHandler(ImageResponse([1, 2, 3]))),
        new FakeGoogleDriveAuthService("token"), temp.Path);

    var path = await service.GetCoverPathAsync(
        new TtuRemoteFile("bad", "cover_1_6.png", ThumbnailLink: "https://thumb.test/bad"));

    path.Should().BeNull();
    Directory.EnumerateFiles(temp.Path).Should().BeEmpty();
}

[Theory]
[InlineData(null, null)]
[InlineData("cover-id", null)]
public async Task GetCoverPathAsync_MissingMetadataReturnsNull(
    string? id,
    string? thumbnail)
{
    using var temp = new TempDirectory();
    var service = new GoogleDriveCoverCacheService(
        new HttpClient(new ThrowingHandler()),
        new FakeGoogleDriveAuthService("token"), temp.Path);
    var cover = id == null ? null : new TtuRemoteFile(id, "cover_1_6.png", ThumbnailLink: thumbnail);

    (await service.GetCoverPathAsync(cover)).Should().BeNull();
}
```

Add these concrete cases using the same handlers:

```csharp
[Fact]
public async Task GetCoverPathAsync_ZeroLengthCacheIsReplaced()
{
    using var temp = new TempDirectory();
    var cover = new TtuRemoteFile("cover-id", "cover.png", ThumbnailLink: "https://thumb.test/cover=s220");
    var seed = new GoogleDriveCoverCacheService(
        new HttpClient(new RecordingHandler(ImageResponse(PngBytes))),
        new FakeGoogleDriveAuthService("token"), temp.Path);
    var path = await seed.GetCoverPathAsync(cover);
    File.WriteAllBytes(path!, []);
    var handler = new RecordingHandler(ImageResponse(PngBytes));
    var service = new GoogleDriveCoverCacheService(
        new HttpClient(handler), new FakeGoogleDriveAuthService("token"), temp.Path);

    var replaced = await service.GetCoverPathAsync(cover);

    handler.Requests.Should().ContainSingle();
    File.ReadAllBytes(replaced!).Should().Equal(PngBytes);
}

[Fact]
public async Task GetCoverPathAsync_HttpFailureReturnsNull()
{
    using var temp = new TempDirectory();
    var service = new GoogleDriveCoverCacheService(
        new HttpClient(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError))),
        new FakeGoogleDriveAuthService("token"), temp.Path);

    (await service.GetCoverPathAsync(
        new TtuRemoteFile("cover-id", "cover.png", ThumbnailLink: "https://thumb.test/cover")))
        .Should().BeNull();
}

[Fact]
public async Task GetCoverPathAsync_CancellationPropagates()
{
    using var temp = new TempDirectory();
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var service = new GoogleDriveCoverCacheService(
        new HttpClient(new ThrowingHandler()),
        new FakeGoogleDriveAuthService("token"), temp.Path);

    Func<Task> action = () => service.GetCoverPathAsync(
        new TtuRemoteFile("cover-id", "cover.png", ThumbnailLink: "https://thumb.test/cover"),
        cts.Token);

    await action.Should().ThrowAsync<OperationCanceledException>();
}
```

- [ ] **Step 2: Run the cache tests and verify RED**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~GoogleDriveCoverCacheServiceTests"
```

Expected: FAIL because the interface and service do not exist.

- [ ] **Step 3: Implement the narrow cache service**

Create the interface:

```csharp
public interface IGoogleDriveCoverCacheService
{
    Task<string?> GetCoverPathAsync(
        TtuRemoteFile? cover,
        CancellationToken ct = default);
}
```

Add `AppDataHelper.GetGoogleDriveCoverCachePath()` under `%APPDATA%/Hoshi/Cache/GoogleDriveCovers`.

The service constructor accepts `HttpClient`, `IGoogleDriveAuthService`, and an internal test cache-root overload. Hash `cover.Id` with SHA-256 for the filename. Return an existing non-empty recognized PNG/JPEG/GIF/BMP/WebP file. Otherwise delete the invalid entry, normalize a terminal `=s<number>` to `=s768`, send a bearer-authenticated GET, validate the leading magic bytes, write a unique `.tmp`, and `File.Move(temp, target, overwrite: true)` in `try/finally`. Rethrow `OperationCanceledException`; return `null` for `HttpRequestException`, `IOException`, and `UnauthorizedAccessException`.

- [ ] **Step 4: Add remote cover state and bounded hydration**

Add to `RemoteNovelBookItemViewModel`:

```csharp
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(HasCover))]
public partial BitmapImage? CoverImage { get; set; }

public bool HasCover => CoverImage != null;

public void ApplyCoverPath(string? path)
{
    CoverImage = string.IsNullOrWhiteSpace(path) || !File.Exists(path)
        ? null
        : new BitmapImage(new Uri(path));
}
```

Inject `IGoogleDriveCoverCacheService` into `NovelLibraryPageViewModel`. After publishing `RemoteBooks`, run one task per item through a six-slot `SemaphoreSlim`; call the service with the page-lifetime token and apply the returned path after the await resumes on the UI context. Catch per-item non-cancellation exceptions so one cover cannot fail the batch.

Bind `RemoteNovelBookTemplate` to `CoverImage`/`HasCover`, using the existing cloud glyph only as the inverse-visibility placeholder.

- [ ] **Step 5: Register DI and update test construction**

Register:

```csharp
services.AddSingleton<IGoogleDriveCoverCacheService, GoogleDriveCoverCacheService>();
```

Add an optional `IGoogleDriveCoverCacheService` argument to `CreateSut`; default to a fake returning `null`. Add this ViewModel test:

```csharp
[Fact]
public async Task RefreshRemoteBooksCommand_HydratesCoversWithoutReorderingCards()
{
    using var temp = new TempDirectory();
    var firstPath = WritePng(temp.Path, "first.png");
    var secondPath = WritePng(temp.Path, "second.png");
    var remote = new FakeTtuSyncRemoteStore
    {
        RemoteBooks = [RemoteBook("a", "A", "A", "cover-a"), RemoteBook("b", "B", "B", "cover-b")],
    };
    var covers = new FakeGoogleDriveCoverCacheService(new Dictionary<string, string>
    {
        ["cover-a"] = firstPath,
        ["cover-b"] = secondPath,
    });
    var sut = CreateSut(syncRemoteStore: remote, googleDriveCoverCacheService: covers);

    await sut.RefreshRemoteBooksCommand.ExecuteAsync(null);

    sut.RemoteBooks.Select(item => item.Book.Id).Should().Equal("a", "b");
    sut.RemoteBooks.Should().OnlyContain(item => item.HasCover);
}
```

Use the same valid 1x1 PNG bytes from the service tests and these helpers:

```csharp
private static readonly byte[] PngBytes = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

private static string WritePng(string directory, string name)
{
    var path = Path.Combine(directory, name);
    File.WriteAllBytes(path, PngBytes);
    return path;
}

private sealed class FakeGoogleDriveCoverCacheService(
    IReadOnlyDictionary<string, string> paths) : IGoogleDriveCoverCacheService
{
    public Task<string?> GetCoverPathAsync(TtuRemoteFile? cover, CancellationToken ct = default) =>
        Task.FromResult(cover != null && paths.TryGetValue(cover.Id, out var path)
            ? path
            : null);
}
```

- [ ] **Step 6: Run cover and ViewModel tests GREEN**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~GoogleDriveCoverCacheServiceTests|FullyQualifiedName~NovelLibraryPageViewModelTests"
dotnet build -p:Platform=x64
```

Expected: PASS.

- [ ] **Step 7: Commit cover support**

```powershell
git add Hoshi/Services/Sync/IGoogleDriveCoverCacheService.cs Hoshi/Services/Sync/GoogleDriveCoverCacheService.cs Hoshi/Helpers/AppDataHelper.cs Hoshi/ViewModels/Components/RemoteNovelBookItemViewModel.cs Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs Hoshi/Views/Pages/NovelLibraryPage.xaml Hoshi/App.xaml.cs Hoshi.Tests/TestUtils/TempDirectory.cs Hoshi.Tests/Services/Sync/GoogleDriveCoverCacheServiceTests.cs Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs
git commit -m "feat(sync): show cached google drive covers"
```

### Task 3: Three-Way Parallel Google Drive Imports with Independent Lifetimes

**Files:**
- Modify: `Hoshi/ViewModels/Components/RemoteNovelBookItemViewModel.cs`
- Modify: `Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs`
- Modify: `Hoshi/Views/Pages/NovelLibraryPage.xaml`
- Modify: `Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`

**Interfaces:**
- Produces: concurrent `DownloadRemoteBookCommand` with a three-slot import gate.
- Produces: per-card `RemoteNovelDownloadState` (`Idle`, `Queued`, `Downloading`, `Failed`).
- Consumes: existing `ITtuBookImportService.ImportRemoteBookAsync` and returned `Result<NovelBook>`.

- [ ] **Step 1: Write failing concurrency and isolation tests**

Create a controlled importer whose first four calls each expose `Started` and `Complete` task sources:

```csharp
private static RemoteNovelBookItemViewModel RemoteItem(string id) => new(new TtuRemoteBook(
    id,
    id.ToUpperInvariant(),
    id.ToUpperInvariant(),
    new TtuRemoteBookFiles(null, null, null,
        new TtuRemoteFile($"{id}-book", $"bookdata_{id}.zip"), null),
    0));

[Fact]
public async Task DownloadRemoteBookCommand_RunsThreeImportsAndQueuesFourth()
{
    var importer = new ControlledTtuBookImportService();
    var sut = CreateSut(ttuBookImportService: importer);
    sut.RemoteBooks = new([
        RemoteItem("a"), RemoteItem("b"), RemoteItem("c"), RemoteItem("d")
    ]);

    var tasks = sut.RemoteBooks
        .Select(item => sut.DownloadRemoteBookCommand.ExecuteAsync(item))
        .ToArray();
    await importer.WaitForStartedCountAsync(3);

    importer.StartedIds.Should().BeEquivalentTo(["a", "b", "c"]);
    sut.RemoteBooks.Single(item => item.Book.Id == "d").DownloadState
        .Should().Be(RemoteNovelDownloadState.Queued);

    importer.Complete("a");
    await importer.WaitForStartedCountAsync(4);
    importer.StartedIds.Should().Contain("d");
    importer.CompleteAll();
    await Task.WhenAll(tasks);
}

[Fact]
public async Task CompletedImport_DoesNotCancelAnotherActiveImport()
{
    var importer = new ControlledTtuBookImportService();
    var sut = CreateSut(ttuBookImportService: importer);
    var first = RemoteItem("a");
    var second = RemoteItem("b");
    sut.RemoteBooks = new([first, second]);
    var firstTask = sut.DownloadRemoteBookCommand.ExecuteAsync(first);
    var secondTask = sut.DownloadRemoteBookCommand.ExecuteAsync(second);
    await importer.WaitForStartedCountAsync(2);

    importer.Complete("a");
    await firstTask;

    importer.TokenFor("b").IsCancellationRequested.Should().BeFalse();
    importer.Complete("b");
    await secondTask;
}

[Fact]
public async Task FailedImport_LeavesOtherImportsRunningAndCardRetryable()
{
    var importer = new ControlledTtuBookImportService();
    var sut = CreateSut(ttuBookImportService: importer);
    var failed = RemoteItem("a");
    var active = RemoteItem("b");
    sut.RemoteBooks = new([failed, active]);
    var failedTask = sut.DownloadRemoteBookCommand.ExecuteAsync(failed);
    var activeTask = sut.DownloadRemoteBookCommand.ExecuteAsync(active);
    await importer.WaitForStartedCountAsync(2);

    importer.Fail("a", "network");
    await failedTask;

    failed.DownloadState.Should().Be(RemoteNovelDownloadState.Failed);
    importer.TokenFor("b").IsCancellationRequested.Should().BeFalse();
    importer.Complete("b");
    await activeTask;
}
```

Use this controlled importer in the three tests:

```csharp
private sealed class ControlledTtuBookImportService : ITtuBookImportService
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Result<NovelBook>>> _results = new();
    private readonly ConcurrentDictionary<string, CancellationToken> _tokens = new();
    private readonly SemaphoreSlim _started = new(0);
    private int _observedStarts;

    public ConcurrentQueue<string> StartedIds { get; } = new();

    public Task<Result<NovelBook>> ImportRemoteBookAsync(
        TtuRemoteBook remoteBook,
        TtuBookImportOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        StartedIds.Enqueue(remoteBook.Id);
        _tokens[remoteBook.Id] = ct;
        _started.Release();
        return _results.GetOrAdd(remoteBook.Id, _ =>
            new TaskCompletionSource<Result<NovelBook>>(
                TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(ct);
    }

    public async Task WaitForStartedCountAsync(int count)
    {
        while (_observedStarts < count)
        {
            await _started.WaitAsync(TestContext.Current.CancellationToken);
            _observedStarts++;
        }
    }

    public CancellationToken TokenFor(string id) => _tokens[id];

    public void Complete(string id) => _results[id].SetResult(Result<NovelBook>.Success(
        new NovelBook { Id = id, Title = id, FilePath = $"D:\\Books\\{id}.epub" }));

    public void Fail(string id, string error) =>
        _results[id].SetResult(Result<NovelBook>.Failure(error, "Import failed"));

    public void CompleteAll()
    {
        foreach (var id in StartedIds.Distinct())
            if (!_results[id].Task.IsCompleted) Complete(id);
    }
}
```

- [ ] **Step 2: Run the concurrency tests and verify RED**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageViewModelTests"
```

Expected: FAIL because the generated command rejects concurrent calls and all work shares `_cts`.

- [ ] **Step 3: Split cancellation lifetimes**

Replace `_cts` with:

```csharp
private readonly CancellationTokenSource _pageCts = new();
private CancellationTokenSource? _catalogLoadCts;
private CancellationTokenSource? _remoteListCts;
private readonly SemaphoreSlim _remoteImportGate = new(3, 3);
private readonly SemaphoreSlim _catalogRefreshGate = new(1, 1);
```

`LoadNovelsAsync` replaces only `_catalogLoadCts`. Remote refresh replaces only `_remoteListCts`. Imports link only to `_pageCts.Token`. `OnNavigatedFrom` cancels page, catalog, remote-list, cover, and statistics work. Do not call `Dispose` on `_pageCts` while command continuations may still observe it.

- [ ] **Step 4: Enable concurrent command execution and per-card state**

Add:

```csharp
public enum RemoteNovelDownloadState { Idle, Queued, Downloading, Failed }
```

Expose observable `DownloadState`, `DownloadProgress`, and computed `IsDownloading`/`CanRetry`. Implement:

```csharp
[RelayCommand(AllowConcurrentExecutions = true)]
private async Task DownloadRemoteBookAsync(RemoteNovelBookItemViewModel item)
{
    if (item == null || item.DownloadState is RemoteNovelDownloadState.Queued
        or RemoteNovelDownloadState.Downloading)
        return;

    item.DownloadState = RemoteNovelDownloadState.Queued;
    var enteredGate = false;
    try
    {
        await _remoteImportGate.WaitAsync(_pageCts.Token);
        enteredGate = true;
        item.DownloadState = RemoteNovelDownloadState.Downloading;
        item.DownloadProgress = 0;
        var settings = _settingsService.Current;
        var result = await _ttuBookImportService.ImportRemoteBookAsync(
            item.Book,
            new TtuBookImportOptions(
                SyncStatistics: settings.StatisticsSettings.EnableSync,
                SyncAudioBook: settings.SasayakiSettings.EnableSync,
                StatisticsSyncMode: settings.StatisticsSettings.SyncMode),
            new Progress<double>(value => item.DownloadProgress = Math.Clamp(value, 0, 1)),
            _pageCts.Token);
        if (!result.IsSuccess)
        {
            item.DownloadState = result.IsCancelled
                ? RemoteNovelDownloadState.Idle
                : RemoteNovelDownloadState.Failed;
            if (!result.IsCancelled) _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }
        RemoteBooks.Remove(item);
        RebuildShelfProjections();
        await RefreshCatalogAfterImportAsync();
    }
    finally
    {
        if (item.DownloadState == RemoteNovelDownloadState.Downloading)
            item.DownloadState = RemoteNovelDownloadState.Idle;
        if (enteredGate) _remoteImportGate.Release();
    }
}
```

Serialize catalog refresh with `_catalogRefreshGate`; it uses its own catalog token and never cancels imports.

- [ ] **Step 5: Update card visuals**

Show a progress bar for Queued/Downloading, use localized status text (`Queued`, `Downloading`, `Retry`), and keep the card invokable after Failed. Bind automation name to include the current state without changing the stable `RemoteNovelBookCard_<id>` AutomationId.

- [ ] **Step 6: Run concurrency tests and build GREEN**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageViewModelTests|FullyQualifiedName~NovelLibraryPageAssetTests"
dotnet build -p:Platform=x64
```

Expected: PASS.

- [ ] **Step 7: Commit independent parallel imports**

```powershell
git add Hoshi/ViewModels/Components/RemoteNovelBookItemViewModel.cs Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs Hoshi/Views/Pages/NovelLibraryPage.xaml Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs Hoshi.Tests/Views/Pages/NovelLibraryPageAssetTests.cs Hoshi/Strings/en-US/Resources.resw Hoshi/Strings/zh-CN/Resources.resw
git commit -m "fix(sync): allow independent parallel book imports"
```

### Task 4: Responsive Statistics Activation and Single Adaptive Layout Owner

**Files:**
- Modify: `Hoshi/ViewModels/Pages/NovelStatisticsDashboardViewModel.cs`
- Modify: `Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs`
- Modify: `Hoshi/Services/Novels/NovelStatisticsDashboardService.cs`
- Create: `Hoshi/Views/Controls/NovelStatisticsDashboardLayout.cs`
- Modify: `Hoshi/Views/Controls/NovelStatisticsDashboardView.xaml`
- Modify: `Hoshi/Views/Controls/NovelStatisticsDashboardView.xaml.cs`
- Modify: `Hoshi.Tests/ViewModels/Pages/NovelStatisticsDashboardViewModelTests.cs`
- Create: `Hoshi.Tests/Views/Controls/NovelStatisticsDashboardLayoutTests.cs`
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Produces: `NovelStatisticsDashboardLayoutMode Select(double width)`.
- Preserves: `ActivateAsync`, activation generation, cancellation, `SnapshotRefreshed` UI dispatch.
- Consumes: existing 840/1260 effective-pixel breakpoints.

- [ ] **Step 1: Write failing activation and breakpoint tests**

Add to the ViewModel tests:

```csharp
[Fact]
public async Task EnterStatistics_ShowsLoadingBeforeControlledSnapshotCompletes()
{
    var service = new ControlledDashboardService();
    var sut = CreateLibrarySut(service);
    await sut.InitializeAsync();

    var enter = sut.EnterStatisticsCommand.ExecuteAsync(null);
    await Task.Yield();

    sut.ShowStatisticsDashboard.Should().BeTrue();
    sut.StatisticsDashboard.IsLoading.Should().BeTrue();
    enter.IsCompleted.Should().BeFalse();

    service.CompleteNext(Snapshot());
    await enter;
}
```

Create layout tests:

```csharp
[Theory]
[InlineData(839, NovelStatisticsDashboardLayoutMode.OneColumn)]
[InlineData(840, NovelStatisticsDashboardLayoutMode.TwoColumns)]
[InlineData(1259, NovelStatisticsDashboardLayoutMode.TwoColumns)]
[InlineData(1260, NovelStatisticsDashboardLayoutMode.ThreeColumns)]
public void Select_UsesEffectivePixelBreakpoints(double width, NovelStatisticsDashboardLayoutMode expected) =>
    NovelStatisticsDashboardLayout.Select(width).Should().Be(expected);
```

Update asset expectations to assert there is no `AdaptiveTrigger`, one `SizeChanged` subscription, and a `_layoutMode == nextMode` early return.

- [ ] **Step 2: Run focused statistics tests and verify RED**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelStatisticsDashboard|FullyQualifiedName~NovelLibraryPageViewModelTests|FullyQualifiedName~NovelReaderWebAssetTests"
```

Expected: FAIL because the pure layout selector does not exist and XAML/code-behind both own the adaptive layout.

- [ ] **Step 3: Yield the loading surface and leave the UI context**

In `EnterStatisticsAsync`, set the surface first and yield once:

```csharp
ShowStatisticsDashboard = true;
await Task.Yield();
await StatisticsDashboard.ActivateAsync(...);
```

In `NovelStatisticsDashboardService.LoadSnapshotAsync`, move cache-key construction and the subsequent service pipeline away from the caller context:

```csharp
var today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
var cacheKey = await Task.Run(
    () => NovelStatisticsDashboardCache.CreateKey(books, today), ct)
    .ConfigureAwait(false);
```

Use `.ConfigureAwait(false)` for cache, book-info, statistics sidecar, and cache-store awaits. Keep snapshot events marshalled by the ViewModel's captured UI context. Do not move observable collection mutation or resource lookup into `Task.Run`.

- [ ] **Step 4: Replace duplicate adaptive ownership with one guarded path**

Create:

```csharp
internal enum NovelStatisticsDashboardLayoutMode { OneColumn, TwoColumns, ThreeColumns }

internal static class NovelStatisticsDashboardLayout
{
    public static NovelStatisticsDashboardLayoutMode Select(double width) => width switch
    {
        >= 1260 => NovelStatisticsDashboardLayoutMode.ThreeColumns,
        >= 840 => NovelStatisticsDashboardLayoutMode.TwoColumns,
        _ => NovelStatisticsDashboardLayoutMode.OneColumn,
    };
}
```

Remove `DashboardLayoutStates` and all `AdaptiveTrigger`s from XAML. In code-behind:

```csharp
private NovelStatisticsDashboardLayoutMode? _layoutMode;

private void ApplyAdaptiveLayout(double width)
{
    var nextMode = NovelStatisticsDashboardLayout.Select(width);
    if (_layoutMode == nextMode) return;
    _layoutMode = nextMode;
    switch (nextMode)
    {
        case NovelStatisticsDashboardLayoutMode.ThreeColumns:
            SetColumnCount(3);
            Place(TodayCard, 0, 0); Place(GoalCard, 1, 0); Place(WeekCard, 2, 0);
            Place(CalendarCard, 0, 1); Place(ShelfCard, 1, 1);
            Place(SelectedRangeCard, 0, 2); Place(SpeedCard, 1, 2);
            Place(RankingCard, 2, 1, 2);
            break;
        case NovelStatisticsDashboardLayoutMode.TwoColumns:
            SetColumnCount(2);
            Place(TodayCard, 0, 0); Place(GoalCard, 1, 0); Place(WeekCard, 2, 0);
            Place(CalendarCard, 3, 0); Place(ShelfCard, 4, 0);
            Place(SelectedRangeCard, 0, 1); Place(SpeedCard, 1, 1);
            Place(RankingCard, 2, 1);
            break;
        default:
            SetColumnCount(1);
            Place(TodayCard, 0, 0); Place(GoalCard, 1, 0); Place(WeekCard, 2, 0);
            Place(CalendarCard, 3, 0); Place(SelectedRangeCard, 4, 0);
            Place(SpeedCard, 5, 0); Place(RankingCard, 6, 0); Place(ShelfCard, 7, 0);
            break;
    }
}
```

Handle `Loaded` and `SizeChanged` directly without dispatcher re-queueing. This code remains UI-only.

- [ ] **Step 5: Run statistics tests and build GREEN**

Run the Step 2 command, then:

```powershell
dotnet build -p:Platform=x64
```

Expected: PASS and no new warnings beyond the repository's existing package advisory.

- [ ] **Step 6: Commit the statistics responsiveness fix**

```powershell
git add Hoshi/ViewModels/Pages/NovelStatisticsDashboardViewModel.cs Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs Hoshi/Services/Novels/NovelStatisticsDashboardService.cs Hoshi/Views/Controls/NovelStatisticsDashboardLayout.cs Hoshi/Views/Controls/NovelStatisticsDashboardView.xaml Hoshi/Views/Controls/NovelStatisticsDashboardView.xaml.cs Hoshi.Tests/ViewModels/Pages/NovelStatisticsDashboardViewModelTests.cs Hoshi.Tests/Views/Controls/NovelStatisticsDashboardLayoutTests.cs Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "fix(statistics): keep dashboard activation responsive"
```

### Task 5: Full Verification, Runtime UIA, and Documentation

**Files:**
- Modify: `docs/VERIFICATION.md`
- Modify: `docs/CHANGELOG.md`

**Interfaces:**
- Verifies all prior tasks together; introduces no production interface.

- [ ] **Step 1: Run all automated tests**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
```

Expected: PASS with zero failed tests.

- [ ] **Step 2: Run the final x64 build**

```powershell
dotnet build -p:Platform=x64
```

Expected: build succeeds. Record the existing `SQLitePCLRaw.lib.e_sqlite3` advisory separately; do not expand this task into dependency migration.

- [ ] **Step 3: Launch and verify objective startup**

```powershell
.\build-and-run.ps1
```

Confirm a responsive `Hoshi` top-level window with a non-zero main window handle. Leave the final verified instance running.

- [ ] **Step 4: Verify local shelves through UI Automation**

Use AutomationIds, not pointer coordinates:

1. Locate the Reading, custom, Google Drive, and Unshelved section headings in order.
2. Confirm each visible section wraps to more than one row when it has enough items.
3. Invoke `NovelBookCard_<bookId>` from Reading, a custom shelf, and Unshelved.
4. Wait for `NovelReaderBackButton` and `NovelWebView`, then return to the library between cases.
5. Resize to wide, medium, and narrow widths and confirm a single vertical scroll owner.

- [ ] **Step 5: Verify Google Drive covers and imports**

With an authenticated account, refresh cloud books and confirm cards appear before all covers finish. Start four remote imports and confirm three cards show active progress while the fourth shows Queued. Complete or cancel one and confirm the queued item starts without cancelling the remaining active imports.

- [ ] **Step 6: Verify statistics responsiveness**

Delete only `statistics_dashboard_cache_v1.json`, enter statistics, and confirm the loading surface is immediately reachable through UIA while the window remains responsive. Re-enter with a warm cache, resize repeatedly within and across 840/1260 effective pixels, return to the bookshelf during a load, and confirm no stale snapshot or perpetual busy state.

- [ ] **Step 7: Update verification and changelog documentation**

Add the new section AutomationId/command checks, multi-row grid checks, three-way import check, progressive cover behavior, and statistics responsiveness regression procedure to `docs/VERIFICATION.md`. Add one `docs/CHANGELOG.md` entry with the confirmed root causes and final solution; avoid a chronological work log.

- [ ] **Step 8: Re-run documentation-sensitive tests and commit**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderWebAssetTests|FullyQualifiedName~NovelLibraryPageAssetTests"
git add docs/VERIFICATION.md docs/CHANGELOG.md
git commit -m "docs: verify niratan bookshelf regressions"
```

Expected: focused tests PASS and only the two documentation files are staged for this commit.

---

## Plan Self-Review

- Every confirmed requirement maps to a task: card activation and multi-row sections (Task 1), covers (Task 2), parallel imports (Task 3), statistics responsiveness (Task 4), and runtime proof (Task 5).
- Section order, Reading duplication semantics, concurrency limits, cache identity, cancellation ownership, and breakpoints are explicit.
- New interfaces are introduced before their consumers and signatures remain consistent across tasks.
- No task modifies the prohibited native submodule, reader renderer, dictionary pipeline, or SQLite/video boundaries.
