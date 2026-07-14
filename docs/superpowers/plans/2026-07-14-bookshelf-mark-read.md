# Bookshelf Mark Read Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Niratan-compatible, localized Mark Read action to local novel cards and refresh the bookshelf from the canonical bookmark sidecar.

**Architecture:** The menu remains a native WinUI `MenuFlyoutItem` and UI-only code-behind forwards the selected item to a ViewModel command. The ViewModel confirms and refreshes; the novel service derives the final bookmark from `bookinfo.json` and performs all IO.

**Tech Stack:** WinUI 3, CommunityToolkit.Mvvm, C#/.NET 10, xUnit v3, Moq, FluentAssertions, RESW localization

## Global Constraints

- Match Niratan: maximum spine index, progress `1`, total character count, current timestamp, no statistics mutation, no sync.
- Do not add a menu icon or a success notification.
- Preserve View → ViewModel → Service layering; code-behind remains UI-only.
- Build and test x64 only by default.

---

### Task 1: Define and test canonical mark-read service behavior

**Files:**
- Modify: `Niratan.Tests/Services/Novels/NovelLibraryServiceTests.cs`
- Modify: `Niratan/Services/Novels/INovelLibraryService.cs`
- Modify: `Niratan/Services/Novels/NovelLibraryService.cs`
- Modify: `Niratan.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`
- Modify: `Niratan.Tests/Services/Sync/TtuBookImportServiceTests.cs`

**Interfaces:**
- Consumes: `INovelBookStorageService.ResolveRootPath`, `INovelBookSidecarService.LoadBookInfoAsync`, `SaveBookmarkAsync`.
- Produces: `Task<Result> MarkReadAsync(string bookId, CancellationToken ct = default)`.

- [ ] **Step 1: Write failing service tests**

```csharp
[Fact]
public async Task MarkReadAsync_WritesNiratanCompatibleFinalBookmark()
{
    var ct = TestContext.Current.CancellationToken;
    var storage = new Mock<INovelBookStorageService>();
    storage.Setup(service => service.ResolveRootPath("book-a")).Returns(@"D:\Books\book-a");
    var sidecars = new Mock<INovelBookSidecarService>();
    sidecars.Setup(service => service.LoadBookInfoAsync(@"D:\Books\book-a", ct))
        .ReturnsAsync(new NovelBookInfo(9000, new Dictionary<string, NovelBookInfoChapter>
        {
            ["a"] = new(1, 100, 100), ["b"] = new(null, 200, 200), ["c"] = new(7, 300, 300),
        }));
    sidecars.Setup(service => service.SaveBookmarkAsync(
        @"D:\Books\book-a",
        It.Is<NovelBookmark>(bookmark => bookmark.ChapterIndex == 7
            && bookmark.Progress == 1 && bookmark.CharacterCount == 9000
            && bookmark.LastModified != null), ct)).Returns(Task.CompletedTask);
    var sut = CreateSut(storage, sidecars);

    var result = await sut.MarkReadAsync("book-a", ct);

    result.IsSuccess.Should().BeTrue(result.Error);
    sidecars.VerifyAll();
}

[Fact]
public async Task MarkReadAsync_MissingBookInfoReturnsSuccessWithoutWriting()
{
    var ct = TestContext.Current.CancellationToken;
    var storage = new Mock<INovelBookStorageService>();
    storage.Setup(service => service.ResolveRootPath("book-a")).Returns(@"D:\Books\book-a");
    var sidecars = new Mock<INovelBookSidecarService>();
    sidecars.Setup(service => service.LoadBookInfoAsync(@"D:\Books\book-a", ct))
        .ReturnsAsync((NovelBookInfo?)null);
    var result = await CreateSut(storage, sidecars).MarkReadAsync("book-a", ct);
    result.IsSuccess.Should().BeTrue(result.Error);
    sidecars.Verify(service => service.SaveBookmarkAsync(
        It.IsAny<string>(), It.IsAny<NovelBookmark>(), It.IsAny<CancellationToken>()), Times.Never);
}
```

- [ ] **Step 2: Run service tests and verify RED**

Run: `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter FullyQualifiedName~NovelLibraryServiceTests.MarkReadAsync`

Expected: compile failure because `MarkReadAsync` does not exist.

- [ ] **Step 3: Add the interface and minimal service implementation**

```csharp
public async Task<Result> MarkReadAsync(string bookId, CancellationToken ct = default)
{
    var readOnly = ReadOnlyFailure();
    if (readOnly is not null)
        return readOnly;

    return await ExecuteAsync(async token =>
    {
        var root = _storage.ResolveRootPath(bookId);
        var bookInfo = await _sidecars.LoadBookInfoAsync(root, token);
        if (bookInfo is null)
            return Result.Success();
        var chapterIndex = bookInfo.ChapterInfo.Values
            .Select(chapter => chapter.SpineIndex)
            .Where(index => index.HasValue)
            .Select(index => index!.Value)
            .DefaultIfEmpty(0)
            .Max();
        await _sidecars.SaveBookmarkAsync(root,
            new NovelBookmark(chapterIndex, 1, bookInfo.CharacterCount, DateTimeOffset.UtcNow),
            token);
        return Result.Success();
    }, "Error marking novel as read", ct);
}
```

Add success stubs to the two concrete test fakes implementing `INovelLibraryService` so the suite compiles.

- [ ] **Step 4: Run service tests and verify GREEN**

Run: `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter FullyQualifiedName~NovelLibraryServiceTests.MarkReadAsync`

Expected: 2 passing tests.

### Task 2: Add customizable localized confirmation and ViewModel workflow

**Files:**
- Modify: `Niratan/Services/UI/IDialogService.cs`
- Modify: `Niratan/Services/UI/DialogService.cs`
- Modify: `Niratan/ViewModels/Pages/NovelLibraryPageViewModel.cs`
- Modify: `Niratan.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`
- Modify: `Niratan/Strings/en-US/Resources.resw`
- Modify: `Niratan/Strings/zh-CN/Resources.resw`

**Interfaces:**
- Consumes: `INovelLibraryService.MarkReadAsync`, `LoadNovelsAsync`, `ResourceStringHelper`.
- Produces: `MarkReadNovelCommand` and `ConfirmAsync(title, message, primaryButtonText, secondaryButtonText)`.

- [ ] **Step 1: Write failing ViewModel tests**

```csharp
[Fact]
public async Task MarkReadNovelCommand_ConfirmedMarksAndReloadsWithoutSuccessNotification()
{
    var item = BookItem("book-a");
    var library = new Mock<INovelLibraryService>();
    library.Setup(service => service.MarkReadAsync("book-a", It.IsAny<CancellationToken>()))
        .ReturnsAsync(Result.Success());
    library.Setup(service => service.GetNovelBooksAsync(null, It.IsAny<CancellationToken>()))
        .ReturnsAsync(Result<NovelBookCatalogSnapshot>.Success(
            new NovelBookCatalogSnapshot([item.Book], [])));
    var dialog = new Mock<IDialogService>();
    dialog.Setup(service => service.ConfirmAsync(
        It.Is<string>(title => title.Contains("book-a")), string.Empty, "Confirm", "Cancel"))
        .ReturnsAsync(true);
    var notification = new Mock<INotificationService>();
    var sut = CreateSut(library.Object, dialog.Object, notification.Object);
    await sut.MarkReadNovelCommand.ExecuteAsync(item);
    library.VerifyAll();
    notification.Verify(service => service.ShowSuccess(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
}

[Fact]
public async Task MarkReadNovelCommand_CancelledDoesNotWriteOrReload()
{
    var item = BookItem("book-a");
    var library = new Mock<INovelLibraryService>();
    var dialog = new Mock<IDialogService>();
    dialog.Setup(service => service.ConfirmAsync(
        It.IsAny<string>(), string.Empty, "Confirm", "Cancel")).ReturnsAsync(false);
    var sut = CreateSut(library.Object, dialog.Object);
    await sut.MarkReadNovelCommand.ExecuteAsync(item);
    library.Verify(service => service.MarkReadAsync(
        It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    library.Verify(service => service.GetNovelBooksAsync(
        It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
}

[Fact]
public async Task MarkReadNovelCommand_FailureShowsErrorWithoutReloading()
{
    var item = BookItem("book-a");
    var library = new Mock<INovelLibraryService>();
    library.Setup(service => service.MarkReadAsync("book-a", It.IsAny<CancellationToken>()))
        .ReturnsAsync(Result.Failure("disk full", "Mark read failed"));
    var dialog = new Mock<IDialogService>();
    dialog.Setup(service => service.ConfirmAsync(
        It.IsAny<string>(), string.Empty, "Confirm", "Cancel")).ReturnsAsync(true);
    var notification = new Mock<INotificationService>();
    var sut = CreateSut(library.Object, dialog.Object, notification.Object);
    await sut.MarkReadNovelCommand.ExecuteAsync(item);
    notification.Verify(service => service.ShowError("disk full", "Mark read failed"), Times.Once);
    library.Verify(service => service.GetNovelBooksAsync(
        It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
}
```

- [ ] **Step 2: Run ViewModel tests and verify RED**

Run: `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter FullyQualifiedName~NovelLibraryPageViewModelTests.MarkReadNovelCommand`

Expected: compile failure because the generated command and dialog overload do not exist.

- [ ] **Step 3: Add the dialog overload without changing delete callers**

```csharp
Task<bool> ConfirmAsync(
    string title,
    string message,
    string primaryButtonText,
    string secondaryButtonText);
```

The existing two-argument implementation delegates with `Delete` and `Cancel`. The four-argument implementation sets `Content` to `null` when the message is blank and keeps `DefaultButton = ContentDialogButton.Secondary`.

- [ ] **Step 4: Add localized resources and command**

English resources: `Mark Read`, `Mark "{0}" as read?`, `Confirm`, `Cancel`.

Chinese resources: `标记为已读`, `将“{0}”标记为已读？`, `确认`, `取消`.

```csharp
[RelayCommand]
private async Task MarkReadNovelAsync(NovelBookItemViewModel item)
{
    var confirmed = await _dialogService.ConfirmAsync(
        ResourceStringHelper.FormatString(
            "NovelBookMarkReadConfirmation/Title",
            "Mark \"{0}\" as read?",
            item.Book.Title),
        string.Empty,
        ResourceStringHelper.GetString(
            "NovelBookMarkReadConfirmation/PrimaryButtonText", "Confirm"),
        ResourceStringHelper.GetString(
            "NovelBookMarkReadConfirmation/CloseButtonText", "Cancel"));
    if (!confirmed)
        return;
    var result = await _novelLibraryService.MarkReadAsync(item.Book.Id, _pageCts.Token);
    if (!result.IsSuccess) {
        if (!result.IsCancelled)
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
        return;
    }
    await LoadNovelsAsync();
}
```

- [ ] **Step 5: Run ViewModel tests and verify GREEN**

Run: `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter FullyQualifiedName~NovelLibraryPageViewModelTests.MarkReadNovelCommand`

Expected: 3 passing tests.

### Task 3: Expose the icon-free localized context-menu action

**Files:**
- Modify: `Niratan/Views/Pages/NovelLibraryPage.xaml`
- Modify: `Niratan/Views/Pages/NovelLibraryPage.xaml.cs`
- Modify: `Niratan.Tests/Views/Pages/NovelLibraryPageAssetTests.cs`

**Interfaces:**
- Consumes: generated `MarkReadNovelCommand`.
- Produces: `NovelBookMarkReadMenuItem` and `MarkReadNovelMenuItem_Click`.

- [ ] **Step 1: Write the failing asset test**

```csharp
[Fact]
public void LocalNovelContextMenu_ExposesLocalizedIconFreeMarkReadCommand()
{
    xaml.Should().Contain("x:Uid=\"NovelBookMarkReadMenuItem\"");
    xaml.Should().Contain("Click=\"MarkReadNovelMenuItem_Click\"");
    code.Should().Contain("ViewModel.MarkReadNovelCommand.ExecuteAsync(novelItem)");
    xaml.Should().NotContain("<MenuFlyoutItem.Icon>");
    english.Should().Contain("NovelBookMarkReadMenuItem.Text");
    chinese.Should().Contain("NovelBookMarkReadMenuItem.Text");
}
```

- [ ] **Step 2: Run the asset test and verify RED**

Run: `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter FullyQualifiedName~LocalNovelContextMenu_ExposesLocalizedIconFreeMarkReadCommand`

Expected: FAIL because the menu item and handler are absent.

- [ ] **Step 3: Add the native menu item and UI-only handler**

```xml
<MenuFlyoutItem x:Uid="NovelBookMarkReadMenuItem"
                AutomationProperties.AutomationId="NovelBookMarkReadMenuItem"
                Tag="{x:Bind}"
                Click="MarkReadNovelMenuItem_Click" />
```

```csharp
private async void MarkReadNovelMenuItem_Click(object sender, RoutedEventArgs e)
{
    if ((sender as MenuFlyoutItem)?.Tag is NovelBookItemViewModel novelItem)
        await ViewModel.MarkReadNovelCommand.ExecuteAsync(novelItem);
}
```

- [ ] **Step 4: Run asset, service, and ViewModel tests**

Run: `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryServiceTests|FullyQualifiedName~NovelLibraryPageViewModelTests|FullyQualifiedName~NovelLibraryPageAssetTests"`

Expected: all selected tests pass.

### Task 4: Full verification and commit

**Files:**
- Verify all modified feature files.

**Interfaces:**
- Consumes: the complete implementation.
- Produces: passing x64 suite and a responsive current-worktree app.

- [ ] **Step 1: Run the full suite**

Run: `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64`

Expected: 0 failures.

- [ ] **Step 2: Build the application**

Run: `dotnet build Niratan/Niratan.csproj -c Debug -p:Platform=x64`

Expected: successful x64 build with `hoshidicts_c_api.dll` beside `Niratan.exe`.

- [ ] **Step 3: Launch and verify the application**

Run the worktree's absolute `build-and-run.ps1`; verify the absolute process path, non-zero main-window handle, and responsive window. Leave the final verified instance running.

- [ ] **Step 4: Commit the feature**

```powershell
git add -- Niratan Niratan.Tests
git commit -m "feat(library): add mark-read action"
```
