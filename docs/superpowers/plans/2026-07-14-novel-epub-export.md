# Novel EPUB Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a bookshelf context-menu action that saves the untouched private EPUB to a user-selected `.epub` file.

**Architecture:** `NovelLibraryPage` binds the selected local book to a ViewModel command. The ViewModel asks `IDialogService` for a destination, then calls `INovelLibraryService`; the library service validates paths and performs an asynchronous byte-for-byte copy. A focused pure helper creates safe suggested file names.

**Tech Stack:** C#/.NET, WinUI 3, Windows App SDK 2.0 `Microsoft.Windows.Storage.Pickers.FileSavePicker`, CommunityToolkit.Mvvm, xUnit v3, FluentAssertions, Moq.

## Global Constraints

- Export only the original EPUB retained in Niratan private storage; do not include sidecars.
- Do not modify `native/hoshidicts/`.
- Keep View → ViewModel → Service layering; no business logic in code-behind and no file I/O in the ViewModel.
- Keep the operation asynchronous and do not block the UI thread.
- Do not add a second storage or database technology.
- Target Windows 10+ x64; do not build ARM64 by default.
- Preserve unrelated dirty-worktree changes.
- Verify with `dotnet build -p:Platform=x64` and `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64`.

---

## File Map

- Create `Niratan/Services/Novels/NovelExportFileName.cs`: pure creation of the save picker's safe base file name.
- Modify `Niratan/Services/Novels/INovelLibraryService.cs`: expose `ExportEpubAsync`.
- Modify `Niratan/Services/Novels/NovelLibraryService.cs`: validate and asynchronously copy the private EPUB.
- Modify `Niratan/Services/UI/IDialogService.cs`: expose a save-file picker boundary.
- Modify `Niratan/Services/UI/DialogService.cs`: configure the Windows App SDK picker and return its selected path.
- Modify `Niratan/ViewModels/Pages/NovelLibraryPageViewModel.cs`: coordinate picker, export result, and notifications.
- Modify `Niratan/Views/Pages/NovelLibraryPage.xaml`: add the local-book context-menu item.
- Modify `Niratan/Strings/en-US/Resources.resw` and `Niratan/Strings/zh-CN/Resources.resw`: localize the item and its automation name.
- Modify `Niratan.Tests/Services/Novels/NovelLibraryServiceTests.cs`: prove exact copying and safe failure behavior.
- Create `Niratan.Tests/Services/Novels/NovelExportFileNameTests.cs`: prove suggested-name sanitization.
- Modify `Niratan.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`: prove cancel, success, and failure orchestration; update its hand-written interface fake.
- Modify `Niratan.Tests/Services/Sync/TtuBookImportServiceTests.cs`: update its hand-written interface fake for the new method.
- Modify `Niratan.Tests/Views/Pages/NovelLibraryPageAssetTests.cs`: prove the menu binding and localized accessibility assets.

---

### Task 1: Safe, byte-exact library export

**Files:**
- Modify: `Niratan/Services/Novels/INovelLibraryService.cs`
- Modify: `Niratan/Services/Novels/NovelLibraryService.cs`
- Modify: `Niratan.Tests/Services/Novels/NovelLibraryServiceTests.cs`
- Modify: `Niratan.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`
- Modify: `Niratan.Tests/Services/Sync/TtuBookImportServiceTests.cs`

**Interfaces:**
- Consumes: `INovelBookStorageService.LoadAsync(string bookId, CancellationToken ct)` and `NovelBook.FilePath`.
- Produces: `Task<Result> INovelLibraryService.ExportEpubAsync(string bookId, string destinationPath, CancellationToken ct = default)`.

- [ ] **Step 1: Write failing service tests for exact copying and source protection**

Add these tests to `NovelLibraryServiceTests`:

```csharp
[Fact]
public async Task ExportEpubAsync_CopiesPrivateEpubBytesExactly()
{
    var ct = TestContext.Current.CancellationToken;
    using var temp = new TempDirectory();
    var source = Path.Combine(temp.Path, "private.epub");
    var destination = Path.Combine(temp.Path, "exported.epub");
    var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0xFF };
    await File.WriteAllBytesAsync(source, bytes, ct);
    var storage = new Mock<INovelBookStorageService>();
    storage.Setup(service => service.LoadAsync("book-a", ct))
        .ReturnsAsync(new NovelBook { Id = "book-a", FilePath = source });
    var sut = CreateSut(storage: storage);

    var result = await sut.ExportEpubAsync("book-a", destination, ct);

    result.IsSuccess.Should().BeTrue(result.Error);
    (await File.ReadAllBytesAsync(destination, ct)).Should().Equal(bytes);
    (await File.ReadAllBytesAsync(source, ct)).Should().Equal(bytes);
}

[Fact]
public async Task ExportEpubAsync_SourceEqualDestinationFailsWithoutChangingSource()
{
    var ct = TestContext.Current.CancellationToken;
    using var temp = new TempDirectory();
    var source = Path.Combine(temp.Path, "private.epub");
    var bytes = new byte[] { 1, 2, 3 };
    await File.WriteAllBytesAsync(source, bytes, ct);
    var storage = new Mock<INovelBookStorageService>();
    storage.Setup(service => service.LoadAsync("book-a", ct))
        .ReturnsAsync(new NovelBook { Id = "book-a", FilePath = source });
    var sut = CreateSut(storage: storage);

    var result = await sut.ExportEpubAsync("book-a", source, ct);

    result.IsSuccess.Should().BeFalse();
    result.ErrorTitle.Should().Be("EPUB export failed");
    (await File.ReadAllBytesAsync(source, ct)).Should().Equal(bytes);
}

[Fact]
public async Task ExportEpubAsync_MissingPrivateEpubReturnsFileNotFound()
{
    var ct = TestContext.Current.CancellationToken;
    using var temp = new TempDirectory();
    var storage = new Mock<INovelBookStorageService>();
    storage.Setup(service => service.LoadAsync("book-a", ct))
        .ReturnsAsync(new NovelBook
        {
            Id = "book-a",
            FilePath = Path.Combine(temp.Path, "missing.epub"),
        });
    var sut = CreateSut(storage: storage);

    var result = await sut.ExportEpubAsync(
        "book-a",
        Path.Combine(temp.Path, "exported.epub"),
        ct);

    result.IsSuccess.Should().BeFalse();
    result.ErrorTitle.Should().Be("EPUB file not found");
}

[Fact]
public async Task ExportEpubAsync_MissingCatalogBookReturnsExportFailure()
{
    var ct = TestContext.Current.CancellationToken;
    using var temp = new TempDirectory();
    var storage = new Mock<INovelBookStorageService>();
    storage.Setup(service => service.LoadAsync("missing", ct))
        .ReturnsAsync((NovelBook?)null);
    var sut = CreateSut(storage: storage);

    var result = await sut.ExportEpubAsync(
        "missing",
        Path.Combine(temp.Path, "exported.epub"),
        ct);

    result.IsSuccess.Should().BeFalse();
    result.ErrorTitle.Should().Be("EPUB export failed");
    result.Error.Should().Contain("Book not found");
}

[Fact]
public async Task ExportEpubAsync_NonEpubDestinationFailsBeforeOpeningSource()
{
    var ct = TestContext.Current.CancellationToken;
    var storage = new Mock<INovelBookStorageService>(MockBehavior.Strict);
    var sut = CreateSut(storage: storage);

    var result = await sut.ExportEpubAsync("book-a", "exported.zip", ct);

    result.IsSuccess.Should().BeFalse();
    result.ErrorTitle.Should().Be("EPUB export failed");
    storage.VerifyNoOtherCalls();
}

[Fact]
public async Task ExportEpubAsync_RemainsAvailableWhenLibraryIsReadOnly()
{
    var ct = TestContext.Current.CancellationToken;
    using var temp = new TempDirectory();
    var source = Path.Combine(temp.Path, "private.epub");
    var destination = Path.Combine(temp.Path, "exported.epub");
    await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 }, ct);
    var storage = new Mock<INovelBookStorageService>();
    storage.Setup(service => service.LoadAsync("book-a", ct))
        .ReturnsAsync(new NovelBook { Id = "book-a", FilePath = source });
    var accessState = new Mock<INovelStorageAccessState>();
    accessState.SetupGet(state => state.IsReadOnly).Returns(true);
    var sut = CreateSut(storage: storage, accessState: accessState);

    var result = await sut.ExportEpubAsync("book-a", destination, ct);

    result.IsSuccess.Should().BeTrue(result.Error);
    File.Exists(destination).Should().BeTrue();
}
```

Use the existing `Niratan.Tests.TestUtils.TempDirectory` namespace import if it is not already present.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryServiceTests"
```

Expected: compilation fails because `NovelLibraryService.ExportEpubAsync` does not exist.

- [ ] **Step 3: Add the interface and minimal asynchronous implementation**

Add to `INovelLibraryService`:

```csharp
Task<Result> ExportEpubAsync(
    string bookId,
    string destinationPath,
    CancellationToken ct = default);
```

Add to `NovelLibraryService` without calling `ReadOnlyFailure`, because exporting is read-only:

```csharp
public Task<Result> ExportEpubAsync(
    string bookId,
    string destinationPath,
    CancellationToken ct = default) =>
    ExecuteAsync(
        async token =>
        {
            if (string.IsNullOrWhiteSpace(destinationPath)
                || !string.Equals(
                    Path.GetExtension(destinationPath),
                    ".epub",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure(
                    "Choose a valid .epub destination.",
                    "EPUB export failed");
            }

            var book = await _storage.LoadAsync(bookId, token);
            if (book is null)
                return Result.Failure("Book not found.", "EPUB export failed");
            if (string.IsNullOrWhiteSpace(book.FilePath) || !File.Exists(book.FilePath))
            {
                return Result.Failure(
                    "The private EPUB file no longer exists.",
                    "EPUB file not found");
            }

            var sourcePath = Path.GetFullPath(book.FilePath);
            var targetPath = Path.GetFullPath(destinationPath);
            if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure(
                    "The export destination must differ from the private EPUB.",
                    "EPUB export failed");
            }

            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(targetDirectory) || !Directory.Exists(targetDirectory))
            {
                return Result.Failure(
                    "The export folder does not exist.",
                    "EPUB export failed");
            }

            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);
            await using var target = new FileStream(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            await source.CopyToAsync(target, token);
            _logger.LogInformation("Exported novel EPUB {BookId} to {DestinationPath}", bookId, targetPath);
            return Result.Success();
        },
        "EPUB export failed",
        ct);
```

Add `using System.IO;` to `NovelLibraryService.cs`.

Add a stub returning `Result.Success()` to both hand-written interface fakes so the focused project compiles:

```csharp
public Task<Result> ExportEpubAsync(
    string bookId,
    string destinationPath,
    CancellationToken ct = default) =>
    Task.FromResult(Result.Success());
```

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run the Task 1 focused test command again.

Expected: all `NovelLibraryServiceTests` pass.

- [ ] **Step 5: Commit the service boundary**

```powershell
git add Niratan/Services/Novels/INovelLibraryService.cs Niratan/Services/Novels/NovelLibraryService.cs Niratan.Tests/Services/Novels/NovelLibraryServiceTests.cs Niratan.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs Niratan.Tests/Services/Sync/TtuBookImportServiceTests.cs
git commit -m "feat(novels): export private EPUB bytes"
```

---

### Task 2: Save picker and ViewModel orchestration

**Files:**
- Create: `Niratan/Services/Novels/NovelExportFileName.cs`
- Create: `Niratan.Tests/Services/Novels/NovelExportFileNameTests.cs`
- Modify: `Niratan/Services/UI/IDialogService.cs`
- Modify: `Niratan/Services/UI/DialogService.cs`
- Modify: `Niratan/ViewModels/Pages/NovelLibraryPageViewModel.cs`
- Modify: `Niratan.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`

**Interfaces:**
- Consumes: `INovelLibraryService.ExportEpubAsync(string, string, CancellationToken)` from Task 1.
- Produces: `NovelExportFileName.CreateBaseName(string? title)`, `IDialogService.SaveFilePickerAsync(string suggestedFileName, string fileTypeDescription, string fileExtension)`, and generated `ExportNovelCommand`.

- [ ] **Step 1: Write failing tests for safe suggested names**

Create `NovelExportFileNameTests.cs`:

```csharp
using FluentAssertions;
using Niratan.Services.Novels;

namespace Niratan.Tests.Services.Novels;

public sealed class NovelExportFileNameTests
{
    [Theory]
    [InlineData("星の本", "星の本")]
    [InlineData("星?空.epub", "星_空")]
    [InlineData("星.epub.epub", "星")]
    [InlineData(".epub", "book")]
    [InlineData("   ", "book")]
    public void CreateBaseName_ReturnsSafeName(string title, string expected)
    {
        NovelExportFileName.CreateBaseName(title).Should().Be(expected);
    }
}
```

- [ ] **Step 2: Run the name tests and verify RED**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelExportFileNameTests"
```

Expected: compilation fails because `NovelExportFileName` does not exist.

- [ ] **Step 3: Implement the focused name helper**

Create `NovelExportFileName.cs`:

```csharp
using System;
using System.IO;
using System.Linq;

namespace Niratan.Services.Novels;

internal static class NovelExportFileName
{
    public static string CreateBaseName(string? title)
    {
        var value = title?.Trim() ?? string.Empty;
        while (value.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
            value = value[..^5];

        var invalid = Path.GetInvalidFileNameChars();
        value = new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.');

        return string.IsNullOrWhiteSpace(value) ? "book" : value;
    }
}
```

- [ ] **Step 4: Run the name tests and verify GREEN**

Run the Task 2 name-test command again.

Expected: all `NovelExportFileNameTests` cases pass.

- [ ] **Step 5: Write failing ViewModel tests for cancel, success, and failure**

Add these tests to `NovelLibraryPageViewModelTests`:

```csharp
[Fact]
public async Task ExportNovelCommand_PickerCancellationDoesNothing()
{
    var dialog = new Mock<IDialogService>();
    dialog.Setup(service => service.SaveFilePickerAsync("星_空", "EPUB books", ".epub"))
        .ReturnsAsync((string?)null);
    var library = new Mock<INovelLibraryService>();
    var notification = new Mock<INotificationService>();
    var sut = CreateSut(
        novelService: library.Object,
        dialogService: dialog.Object,
        notificationService: notification.Object);

    await sut.ExportNovelCommand.ExecuteAsync(new NovelBookItemViewModel(
        new NovelBook { Id = "book-a", Title = "星?空.epub" }));

    library.Verify(service => service.ExportEpubAsync(
        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    notification.VerifyNoOtherCalls();
}

[Fact]
public async Task ExportNovelCommand_SuccessExportsSelectedBookAndNotifies()
{
    var destination = @"D:\Exports\星.epub";
    var dialog = new Mock<IDialogService>();
    dialog.Setup(service => service.SaveFilePickerAsync("星", "EPUB books", ".epub"))
        .ReturnsAsync(destination);
    var library = new Mock<INovelLibraryService>();
    library.Setup(service => service.ExportEpubAsync(
            "book-a", destination, It.IsAny<CancellationToken>()))
        .ReturnsAsync(Result.Success());
    var notification = new Mock<INotificationService>();
    var sut = CreateSut(
        novelService: library.Object,
        dialogService: dialog.Object,
        notificationService: notification.Object);

    await sut.ExportNovelCommand.ExecuteAsync(new NovelBookItemViewModel(
        new NovelBook { Id = "book-a", Title = "星" }));

    notification.Verify(service => service.ShowSuccess("EPUB exported.", "Novel exported"));
}

[Fact]
public async Task ExportNovelCommand_FailureShowsServiceError()
{
    var destination = @"D:\Exports\星.epub";
    var dialog = new Mock<IDialogService>();
    dialog.Setup(service => service.SaveFilePickerAsync("星", "EPUB books", ".epub"))
        .ReturnsAsync(destination);
    var library = new Mock<INovelLibraryService>();
    library.Setup(service => service.ExportEpubAsync(
            "book-a", destination, It.IsAny<CancellationToken>()))
        .ReturnsAsync(Result.Failure("source missing", "EPUB file not found"));
    var notification = new Mock<INotificationService>();
    var sut = CreateSut(
        novelService: library.Object,
        dialogService: dialog.Object,
        notificationService: notification.Object);

    await sut.ExportNovelCommand.ExecuteAsync(new NovelBookItemViewModel(
        new NovelBook { Id = "book-a", Title = "星" }));

    notification.Verify(service => service.ShowError("source missing", "EPUB file not found"));
}
```

- [ ] **Step 6: Run the ViewModel tests and verify RED**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageViewModelTests"
```

Expected: compilation fails because the save-picker method and `ExportNovelCommand` do not exist.

- [ ] **Step 7: Add the picker boundary and ViewModel command**

Add to `IDialogService`:

```csharp
Task<string?> SaveFilePickerAsync(
    string suggestedFileName,
    string fileTypeDescription,
    string fileExtension);
```

Add `using System.Collections.Generic;` to `DialogService.cs`, then add:

```csharp
public async Task<string?> SaveFilePickerAsync(
    string suggestedFileName,
    string fileTypeDescription,
    string fileExtension)
{
    if (_xamlRoot == null)
        throw new InvalidOperationException("XamlRoot must be initialized.");

    var picker = new FileSavePicker(_xamlRoot.ContentIslandEnvironment.AppWindowId)
    {
        SuggestedStartLocation = PickerLocationId.Downloads,
        SuggestedFileName = suggestedFileName,
        DefaultFileExtension = fileExtension,
        ShowOverwritePrompt = true,
    };
    picker.FileTypeChoices.Add(fileTypeDescription, new List<string> { fileExtension });
    var result = await picker.PickSaveFileAsync();
    return result?.Path;
}
```

Add this relay command near the existing import command in `NovelLibraryPageViewModel`:

```csharp
[RelayCommand]
private async Task ExportNovelAsync(NovelBookItemViewModel item)
{
    var destinationPath = await _dialogService.SaveFilePickerAsync(
        NovelExportFileName.CreateBaseName(item.Book.Title),
        "EPUB books",
        ".epub");
    if (destinationPath is null)
        return;

    var result = await _novelLibraryService.ExportEpubAsync(
        item.Book.Id,
        destinationPath,
        _pageCts.Token);
    if (result.IsCancelled)
        return;
    if (!result.IsSuccess)
    {
        _notificationService.ShowError(result.Error!, result.ErrorTitle!);
        return;
    }

    _notificationService.ShowSuccess("EPUB exported.", "Novel exported");
}
```

- [ ] **Step 8: Run focused tests and verify GREEN**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelExportFileNameTests|FullyQualifiedName~NovelLibraryPageViewModelTests"
```

Expected: all selected tests pass.

- [ ] **Step 9: Build x64 to validate the Windows App SDK picker API**

Run:

```powershell
dotnet build -p:Platform=x64
```

Expected: build succeeds with zero errors. In particular, `FileSavePicker(WindowId)`, `DefaultFileExtension`, `FileTypeChoices`, `ShowOverwritePrompt`, and `PickSaveFileAsync()` compile against Windows App SDK 2.0.1.

- [ ] **Step 10: Commit picker orchestration**

```powershell
git add Niratan/Services/Novels/NovelExportFileName.cs Niratan.Tests/Services/Novels/NovelExportFileNameTests.cs Niratan/Services/UI/IDialogService.cs Niratan/Services/UI/DialogService.cs Niratan/ViewModels/Pages/NovelLibraryPageViewModel.cs Niratan.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs
git commit -m "feat(novels): add EPUB save workflow"
```

---

### Task 3: Context-menu UI, localization, and end-to-end verification

**Files:**
- Modify: `Niratan/Views/Pages/NovelLibraryPage.xaml`
- Modify: `Niratan/Strings/en-US/Resources.resw`
- Modify: `Niratan/Strings/zh-CN/Resources.resw`
- Modify: `Niratan.Tests/Views/Pages/NovelLibraryPageAssetTests.cs`

**Interfaces:**
- Consumes: generated `NovelLibraryPageViewModel.ExportNovelCommand` from Task 2.
- Produces: local-book menu item `NovelBookExportMenuItem` and localized `.Text` and `.AutomationProperties.Name` resources.

- [ ] **Step 1: Write the failing XAML/resource asset test**

Add to `NovelLibraryPageAssetTests`:

```csharp
[Fact]
public void LocalNovelContextMenu_ExposesLocalizedExportCommand()
{
    var xaml = File.ReadAllText(
        Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml"));
    var english = File.ReadAllText(
        Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw"));
    var chinese = File.ReadAllText(
        Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw"));

    xaml.Should().Contain("x:Uid=\"NovelBookExportMenuItem\"");
    xaml.Should().Contain("AutomationProperties.AutomationId=\"NovelBookExportMenuItem\"");
    xaml.Should().Contain("Command=\"{Binding ViewModel.ExportNovelCommand, ElementName=ThisPage}\"");
    xaml.Should().Contain("CommandParameter=\"{x:Bind}\"");
    english.Should().Contain("name=\"NovelBookExportMenuItem.Text\"");
    english.Should().Contain("name=\"NovelBookExportMenuItem.AutomationProperties.Name\"");
    chinese.Should().Contain("name=\"NovelBookExportMenuItem.Text\"");
    chinese.Should().Contain("name=\"NovelBookExportMenuItem.AutomationProperties.Name\"");
}
```

- [ ] **Step 2: Run the asset test and verify RED**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageAssetTests.LocalNovelContextMenu_ExposesLocalizedExportCommand"
```

Expected: test fails because the menu item is absent.

- [ ] **Step 3: Add the bound menu item and localized resources**

Insert this item before `NovelBookMoveToShelfMenuItem` in the local book `MenuFlyout`:

```xml
<MenuFlyoutItem x:Uid="NovelBookExportMenuItem"
                Text="Export EPUB…"
                AutomationProperties.AutomationId="NovelBookExportMenuItem"
                Command="{Binding ViewModel.ExportNovelCommand, ElementName=ThisPage}"
                CommandParameter="{x:Bind}" />
```

Add to `Strings/en-US/Resources.resw`:

```xml
<data name="NovelBookExportMenuItem.Text" xml:space="preserve"><value>Export EPUB…</value></data>
<data name="NovelBookExportMenuItem.AutomationProperties.Name" xml:space="preserve"><value>Export EPUB</value></data>
```

Add to `Strings/zh-CN/Resources.resw`:

```xml
<data name="NovelBookExportMenuItem.Text" xml:space="preserve"><value>导出 EPUB…</value></data>
<data name="NovelBookExportMenuItem.AutomationProperties.Name" xml:space="preserve"><value>导出 EPUB</value></data>
```

- [ ] **Step 4: Run the asset test and verify GREEN**

Run the Task 3 asset-test command again.

Expected: the new asset test passes.

- [ ] **Step 5: Run all x64 tests and the x64 build**

Run:

```powershell
dotnet build -p:Platform=x64
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64
```

Expected: both commands exit with code 0 and report zero failed tests.

- [ ] **Step 6: Launch and verify the real WinUI flow**

Run:

```powershell
.\build-and-run.ps1
```

Verify objective startup evidence: a responsive Niratan top-level window with a non-zero main window handle. On the bookshelf, right-click a local novel and verify **导出 EPUB…** or **Export EPUB…** appears before **Move to shelf…**. Choose it, save to a temporary destination, confirm the success notification, and compare the exported file bytes with the book's private `NovelBook.FilePath`. Cancel a second export and confirm no error or success notification appears. Leave the final verified app instance running.

- [ ] **Step 7: Commit the user-visible feature**

```powershell
git add Niratan/Views/Pages/NovelLibraryPage.xaml Niratan/Strings/en-US/Resources.resw Niratan/Strings/zh-CN/Resources.resw Niratan.Tests/Views/Pages/NovelLibraryPageAssetTests.cs
git commit -m "feat(novels): expose EPUB export in bookshelf menu"
```

- [ ] **Step 8: Review final scope and repository state**

Run:

```powershell
git status --short
git diff HEAD~3 --stat
```

Expected: only pre-existing unrelated user changes remain unstaged, and the three feature commits contain no changes under `native/hoshidicts/`.
