# Book Sync Progress and Sasayaki Position Restore Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show a Niratan-aligned blocking bookshelf sync indicator and preserve the Google Drive Sasayaki playback position when audio and subtitles are imported or rematched.

**Architecture:** `NovelLibraryPageViewModel` derives busy state from its existing concurrent set of active book syncs, while `NovelLibraryPage` renders a localized native WinUI overlay. Sasayaki matching remains responsible only for match data; the existing playback sidecar remains the source of truth and the Reader applies that state after loading media instead of writing a zero position.

**Tech Stack:** C#/.NET 10, WinUI 3, Windows App SDK, CommunityToolkit.Mvvm, xUnit v3, FluentAssertions, Moq, Serilog.

## Global Constraints

- Build only x64 with `dotnet build -p:Platform=x64`; do not build ARM64 by default.
- Run tests with `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64`.
- Do not modify any file under `native/hoshidicts/`.
- Preserve the View → ViewModel → Service layering; code-behind only coordinates UI controls and file pickers.
- Do not change Google Drive file formats, TTU filenames, or remote data models.
- Do not invent percentage progress for the per-book sidecar sync operation.
- Keep the existing success, error, cancellation, and already-synced notification semantics.
- Keep EPUB rendering and dictionary paths out of scope.

---

## File Structure

- `Niratan/ViewModels/Pages/NovelLibraryPageViewModel.cs` — expose book-sync busy state from the existing `_activeNovelSyncs` set.
- `Niratan/Views/Pages/NovelLibraryPage.xaml` — render the blocking native WinUI synchronization overlay.
- `Niratan/Strings/en-US/Resources.resw` — English overlay name and visible text.
- `Niratan/Strings/zh-CN/Resources.resw` — Simplified Chinese overlay name and visible text.
- `Niratan.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs` — verify single, concurrent, duplicate, unavailable, failure, and cancellation busy-state transitions.
- `Niratan.Tests/Views/Pages/NovelLibraryPageAssetTests.cs` — verify the XAML overlay, binding, localization, automation ID, and hit-test surface.
- `Niratan/Services/Sasayaki/SasayakiMatchService.cs` — stop match creation from owning or resetting playback state.
- `Niratan/Services/Sasayaki/SasayakiPlayer.cs` — retain a requested seek until media is open and clamp it against the real duration.
- `Niratan/Views/Pages/NovelReaderPage.xaml.cs` — load the existing playback state through the sidecar service and apply it to the newly loaded player.
- `Niratan.Tests/Services/Sasayaki/SasayakiMatchServiceTests.cs` — integration-test match creation against a real playback sidecar.
- `Niratan.Tests/Services/Sasayaki/SasayakiPlayerTests.cs` — test saved-position normalization before and after media duration is known.
- `Niratan.Tests/Views/Pages/NovelReaderSasayakiAssetTests.cs` — constrain Reader import orchestration to load/apply, never reset, playback state.
- `docs/CHANGELOG.md` — record the root causes and the focused fixes.

---

### Task 1: Niratan-Aligned Bookshelf Sync Overlay

**Files:**
- Modify: `Niratan/ViewModels/Pages/NovelLibraryPageViewModel.cs`
- Modify: `Niratan/Views/Pages/NovelLibraryPage.xaml`
- Modify: `Niratan/Strings/en-US/Resources.resw`
- Modify: `Niratan/Strings/zh-CN/Resources.resw`
- Modify: `Niratan.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`
- Modify: `Niratan.Tests/Views/Pages/NovelLibraryPageAssetTests.cs`

**Interfaces:**
- Consumes: `ConcurrentDictionary<string, byte> _activeNovelSyncs`, `Task SyncNovelCoreAsync(NovelBookItemViewModel, TtuSyncDirection)`, and `BooleanToVisibilityConverter`.
- Produces: `public bool IsBookSyncing` and a `NovelBookSyncProgressOverlay` XAML automation surface.

- [ ] **Step 1: Add failing ViewModel tests for busy-state lifetime**

Add this test next to the existing book-sync command tests in `NovelLibraryPageViewModelTests.cs`:

```csharp
[Fact]
public async Task SyncNovelCommand_KeepsBusyStateUntilEveryBookFinishes()
{
    var gates = new ConcurrentDictionary<string, TaskCompletionSource<TtuSyncResult>>();
    var started = new SemaphoreSlim(0);
    var sync = new RecordingTtuSyncService
    {
        Handler = (book, _, _) =>
        {
            var gate = gates.GetOrAdd(
                book.Id,
                _ => new TaskCompletionSource<TtuSyncResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously));
            started.Release();
            return gate.Task;
        },
    };
    var sut = CreateSut(
        settingsService: EnabledSyncSettings(),
        ttuSyncService: sync);
    var ct = TestContext.Current.CancellationToken;

    var first = sut.ImportNovelFromTtuCommand.ExecuteAsync(BookItem("book-1"));
    (await started.WaitAsync(TimeSpan.FromSeconds(2), ct)).Should().BeTrue();
    sut.IsBookSyncing.Should().BeTrue();

    var second = sut.ExportNovelCommand.ExecuteAsync(BookItem("book-2"));
    (await started.WaitAsync(TimeSpan.FromSeconds(2), ct)).Should().BeTrue();
    sut.IsBookSyncing.Should().BeTrue();

    gates["book-1"].SetResult(new TtuSyncResult(TtuSyncResultKind.Imported, "book-1"));
    await first;
    sut.IsBookSyncing.Should().BeTrue();

    gates["book-2"].SetResult(new TtuSyncResult(TtuSyncResultKind.Exported, "book-2"));
    await second;
    sut.IsBookSyncing.Should().BeFalse();
}
```

Extend `SyncNovelCommand_WhenSyncUnavailable_DoesNotCallServiceAndShowsError` after command execution:

```csharp
sut.IsBookSyncing.Should().BeFalse();
```

Add the same cleanup assertion after command execution in both `SyncNovelCommand_WhenServiceFails_ShowsLocalizedError` and `SyncNovelCommand_WhenCancelled_ShowsNoError`:

```csharp
sut.IsBookSyncing.Should().BeFalse();
```

Extend `SyncNovelCommand_DeduplicatesSameBookButAllowsDifferentBooks` after the duplicate completes and after `Task.WhenAll`:

```csharp
sut.IsBookSyncing.Should().BeTrue();
```

```csharp
sut.IsBookSyncing.Should().BeFalse();
```

- [ ] **Step 2: Run the focused ViewModel tests and verify RED**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageViewModelTests.SyncNovelCommand_KeepsBusyStateUntilEveryBookFinishes|FullyQualifiedName~NovelLibraryPageViewModelTests.SyncNovelCommand_WhenSyncUnavailable_DoesNotCallServiceAndShowsError|FullyQualifiedName~NovelLibraryPageViewModelTests.SyncNovelCommand_WhenServiceFails_ShowsLocalizedError|FullyQualifiedName~NovelLibraryPageViewModelTests.SyncNovelCommand_WhenCancelled_ShowsNoError|FullyQualifiedName~NovelLibraryPageViewModelTests.SyncNovelCommand_DeduplicatesSameBookButAllowsDifferentBooks"
```

Expected: compilation fails because `NovelLibraryPageViewModel` does not expose `IsBookSyncing`.

- [ ] **Step 3: Derive busy state from the active-sync dictionary**

Add beside the other projected ViewModel properties:

```csharp
public bool IsBookSyncing => !_activeNovelSyncs.IsEmpty;
```

Immediately after the successful `_activeNovelSyncs.TryAdd(item.Book.Id, 0)` guard, notify the UI:

```csharp
OnPropertyChanged(nameof(IsBookSyncing));
```

Replace the existing removal in `finally` with:

```csharp
if (_activeNovelSyncs.TryRemove(item.Book.Id, out _))
    OnPropertyChanged(nameof(IsBookSyncing));
```

This uses the concurrent dictionary as the only counter, so a duplicate request never increments state and one finishing book cannot hide another active book.

- [ ] **Step 4: Run the focused ViewModel tests and verify GREEN**

Run the command from Step 2.

Expected: all five selected tests pass with zero failures.

- [ ] **Step 5: Add a failing XAML and localization contract**

Add this test to `NovelLibraryPageAssetTests.cs`:

```csharp
[Fact]
public void BookSyncProgressOverlay_IsBlockingLocalizedAndBoundToBusyState()
{
    var xaml = File.ReadAllText(
        Path.Combine(ProjectRoot, "Views", "Pages", "NovelLibraryPage.xaml"));
    var englishResources = File.ReadAllText(
        Path.Combine(ProjectRoot, "Strings", "en-US", "Resources.resw"));
    var chineseResources = File.ReadAllText(
        Path.Combine(ProjectRoot, "Strings", "zh-CN", "Resources.resw"));

    xaml.Should().Contain(
        "AutomationProperties.AutomationId=\"NovelBookSyncProgressOverlay\"");
    xaml.Should().Contain(
        "Visibility=\"{x:Bind ViewModel.IsBookSyncing, Converter={StaticResource BooleanToVisibilityConverter}, Mode=OneWay}\"");
    xaml.Should().Contain("Panel.ZIndex=\"100\"");
    xaml.Should().Contain("Background=\"Transparent\"");
    xaml.Should().Contain("<ProgressRing");
    xaml.Should().Contain("x:Uid=\"NovelBookSyncProgressText\"");

    foreach (var key in new[]
    {
        "NovelBookSyncProgressOverlay.AutomationProperties.Name",
        "NovelBookSyncProgressText.Text",
    })
    {
        englishResources.Should().Contain($"name=\"{key}\"");
        chineseResources.Should().Contain($"name=\"{key}\"");
    }
}
```

- [ ] **Step 6: Run the overlay contract and verify RED**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageAssetTests.BookSyncProgressOverlay_IsBlockingLocalizedAndBoundToBusyState"
```

Expected: the test fails because `NovelBookSyncProgressOverlay` is absent.

- [ ] **Step 7: Add the native WinUI overlay and localized copy**

Add this as the last child of the root `Grid` in `NovelLibraryPage.xaml`, after `NovelStatisticsDashboardView` and before the root `Grid` closes:

```xml
<Grid x:Uid="NovelBookSyncProgressOverlay"
      Grid.RowSpan="3"
      Panel.ZIndex="100"
      Background="Transparent"
      AutomationProperties.AutomationId="NovelBookSyncProgressOverlay"
      Visibility="{x:Bind ViewModel.IsBookSyncing, Converter={StaticResource BooleanToVisibilityConverter}, Mode=OneWay}">
    <Border Background="{ThemeResource SolidBackgroundFillColorBaseBrush}"
            Opacity="0.82" />
    <StackPanel HorizontalAlignment="Center"
                VerticalAlignment="Center"
                Spacing="12">
        <ProgressRing Width="48"
                      Height="48"
                      IsActive="{x:Bind ViewModel.IsBookSyncing, Mode=OneWay}" />
        <TextBlock x:Uid="NovelBookSyncProgressText"
                   HorizontalAlignment="Center"
                   Style="{StaticResource BodyLargeStrongTextBlockStyle}" />
    </StackPanel>
</Grid>
```

Add beside the existing per-book sync strings in `en-US/Resources.resw`:

```xml
<data name="NovelBookSyncProgressOverlay.AutomationProperties.Name" xml:space="preserve"><value>Book synchronization in progress</value></data>
<data name="NovelBookSyncProgressText.Text" xml:space="preserve"><value>Syncing...</value></data>
```

Add the matching entries in `zh-CN/Resources.resw`:

```xml
<data name="NovelBookSyncProgressOverlay.AutomationProperties.Name" xml:space="preserve"><value>正在同步书籍</value></data>
<data name="NovelBookSyncProgressText.Text" xml:space="preserve"><value>正在同步…</value></data>
```

- [ ] **Step 8: Run all Task 1 tests and commit**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelLibraryPageViewModelTests|FullyQualifiedName~NovelLibraryPageAssetTests|FullyQualifiedName~NovelLibraryTtuSyncAssetTests"
```

Expected: all selected tests pass with zero failures.

Commit:

```powershell
git add -- Niratan/ViewModels/Pages/NovelLibraryPageViewModel.cs Niratan/Views/Pages/NovelLibraryPage.xaml Niratan/Strings/en-US/Resources.resw Niratan/Strings/zh-CN/Resources.resw Niratan.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs Niratan.Tests/Views/Pages/NovelLibraryPageAssetTests.cs
git commit -m "feat(sync): show bookshelf sync progress"
```

---

### Task 2: Preserve and Restore Synced Sasayaki Playback State

**Files:**
- Create: `Niratan.Tests/Services/Sasayaki/SasayakiMatchServiceTests.cs`
- Create: `Niratan.Tests/Services/Sasayaki/SasayakiPlayerTests.cs`
- Create: `Niratan.Tests/Views/Pages/NovelReaderSasayakiAssetTests.cs`
- Modify: `Niratan/Services/Sasayaki/SasayakiMatchService.cs`
- Modify: `Niratan/Services/Sasayaki/SasayakiPlayer.cs`
- Modify: `Niratan/Views/Pages/NovelReaderPage.xaml.cs`

**Interfaces:**
- Consumes: `ISasayakiSidecarService.LoadPlaybackAsync`, `ISasayakiSidecarService.SaveMatchAsync`, `ApplySasayakiPlayback(SasayakiPlaybackData)`, and `SasayakiPlayer.LoadAsync(string)`.
- Produces: match creation that never writes playback state, `internal static double SasayakiPlayer.NormalizeSeekSeconds(double requested, double duration)`, a pending seek completed by `MediaOpened`, and Reader import that applies the existing `SasayakiPlaybackData` after media loading.

- [ ] **Step 1: Write failing integration tests for playback preservation**

Create `Niratan.Tests/Services/Sasayaki/SasayakiMatchServiceTests.cs`:

```csharp
using FluentAssertions;
using Niratan.Models;
using Niratan.Models.Novel;
using Niratan.Models.Sasayaki;
using Niratan.Services.Novels;
using Niratan.Services.Sasayaki;

namespace Niratan.Tests.Services.Sasayaki;

public sealed class SasayakiMatchServiceTests
{
    [Fact]
    public async Task MatchAsync_PreservesExistingPlaybackSidecar()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var chapterPath = Path.Combine(temp.Path, "chapter.xhtml");
        var subtitlePath = Path.Combine(temp.Path, "audio.srt");
        await File.WriteAllTextAsync(
            chapterPath,
            "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>本文</p></body></html>",
            ct);
        await File.WriteAllTextAsync(
            subtitlePath,
            "1\n00:00:01,000 --> 00:00:02,000\n本文\n",
            ct);

        var book = new NovelBook
        {
            Id = "book-1",
            Title = "本",
            FilePath = Path.Combine(temp.Path, "book.epub"),
            ExtractedPath = temp.Path,
        };
        var epub = new EpubBook
        {
            Chapters =
            [
                new EpubChapter
                {
                    Href = chapterPath,
                    MediaType = "application/xhtml+xml",
                },
            ],
        };
        var parser = new Mock<IEpubParserService>();
        parser.Setup(service => service.Parse(book.FilePath, temp.Path)).Returns(epub);
        var sidecars = new SasayakiSidecarService();
        var expected = new SasayakiPlaybackData
        {
            LastPosition = 21448.206,
            Delay = 0.35,
            Rate = 1.25,
            AudioBookmark = 42,
        };
        await sidecars.SavePlaybackAsync(temp.Path, expected, ct);
        var sut = new SasayakiMatchService(parser.Object, sidecars);

        var result = await sut.MatchAsync(
            book,
            Path.Combine(temp.Path, "audio.m4b"),
            subtitlePath,
            SasayakiSettings.DefaultSearchWindow,
            ct);

        result.IsValid.Should().BeTrue();
        var actual = await sidecars.LoadPlaybackAsync(temp.Path, ct);
        actual.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task MatchAsync_WithoutPlaybackSidecar_DoesNotCreateOne()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var chapterPath = Path.Combine(temp.Path, "chapter.xhtml");
        var subtitlePath = Path.Combine(temp.Path, "audio.srt");
        await File.WriteAllTextAsync(
            chapterPath,
            "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>本文</p></body></html>",
            ct);
        await File.WriteAllTextAsync(
            subtitlePath,
            "1\n00:00:01,000 --> 00:00:02,000\n本文\n",
            ct);

        var book = new NovelBook
        {
            Id = "book-1",
            Title = "本",
            FilePath = Path.Combine(temp.Path, "book.epub"),
            ExtractedPath = temp.Path,
        };
        var parser = new Mock<IEpubParserService>();
        parser.Setup(service => service.Parse(book.FilePath, temp.Path)).Returns(new EpubBook
        {
            Chapters =
            [
                new EpubChapter
                {
                    Href = chapterPath,
                    MediaType = "application/xhtml+xml",
                },
            ],
        });
        var sidecars = new SasayakiSidecarService();
        var sut = new SasayakiMatchService(parser.Object, sidecars);

        await sut.MatchAsync(
            book,
            Path.Combine(temp.Path, "audio.m4b"),
            subtitlePath,
            SasayakiSettings.DefaultSearchWindow,
            ct);

        File.Exists(Path.Combine(temp.Path, ISasayakiSidecarService.PlaybackFileName))
            .Should().BeFalse();
        var playback = await sidecars.LoadPlaybackAsync(temp.Path, ct);
        playback.LastPosition.Should().Be(0);
        playback.Rate.Should().Be(1);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run the match-service tests and verify RED**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~SasayakiMatchServiceTests"
```

Expected: the existing-playback test reports that position/rate/delay/bookmark were replaced, and the missing-playback test reports that `sasayaki_playback.json` was created.

- [ ] **Step 3: Stop the match service from owning playback state**

In `SasayakiMatchService.MatchAsync`, keep the match save and remove the empty playback save. The end of the method must be:

```csharp
await _sidecarService.SaveMatchAsync(bookRootPath, matchData, cancellationToken);
return matchData;
```

- [ ] **Step 4: Run the match-service tests and verify GREEN**

Run the command from Step 2.

Expected: both tests pass with zero failures.

- [ ] **Step 5: Write failing player normalization and Reader import contracts**

Create `Niratan.Tests/Services/Sasayaki/SasayakiPlayerTests.cs`:

```csharp
using FluentAssertions;
using Niratan.Services.Sasayaki;

namespace Niratan.Tests.Services.Sasayaki;

public sealed class SasayakiPlayerTests
{
    [Theory]
    [InlineData(-5, 120, 0)]
    [InlineData(42.5, 120, 42.5)]
    [InlineData(150, 120, 120)]
    [InlineData(42.5, 0, 42.5)]
    [InlineData(double.NaN, 120, 0)]
    [InlineData(double.PositiveInfinity, 120, 0)]
    public void NormalizeSeekSeconds_PreservesPendingPositionAndClampsKnownDuration(
        double requested,
        double duration,
        double expected)
    {
        SasayakiPlayer.NormalizeSeekSeconds(requested, duration).Should().Be(expected);
    }
}
```

Create `Niratan.Tests/Views/Pages/NovelReaderSasayakiAssetTests.cs`:

```csharp
using FluentAssertions;

namespace Niratan.Tests.Views.Pages;

public sealed class NovelReaderSasayakiAssetTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));

    [Fact]
    public void AudioSubtitleImport_LoadsAndAppliesPlaybackWithoutResettingPosition()
    {
        var code = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs"));
        var playerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Services", "Sasayaki", "SasayakiPlayer.cs"));
        var start = code.IndexOf(
            "private async Task LoadSasayakiAsync",
            StringComparison.Ordinal);
        var end = code.IndexOf(
            "private async Task LoadSasayakiSidecarAsync",
            start,
            StringComparison.Ordinal);

        start.Should().BeGreaterThanOrEqualTo(0);
        end.Should().BeGreaterThan(start);
        var method = code[start..end];
        method.Should().Contain("SasayakiSidecarService.LoadPlaybackAsync");
        method.Should().Contain("ApplySasayakiPlayback(playback)");
        method.Should().NotContain("SaveSasayakiPlaybackAsync(0)");
        playerCode.Should().Contain("private double? _pendingSeekSeconds");
        playerCode.Should().Contain("player.MediaOpened += OnMediaOpened");
        playerCode.Should().Contain("ApplyPendingSeek(sender)");
    }
}
```

- [ ] **Step 6: Run the Reader asset contract and verify RED**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~SasayakiPlayerTests|FullyQualifiedName~NovelReaderSasayakiAssetTests"
```

Expected: compilation fails because `NormalizeSeekSeconds` is absent; the asset contract also reports that Reader does not load/apply playback, still contains `SaveSasayakiPlaybackAsync(0)`, and the player has no pending seek.

- [ ] **Step 7: Retain seeks until media is open, then load and apply existing Reader playback**

Add these fields beside the existing `SasayakiPlayer` fields:

```csharp
private double? _pendingSeekSeconds;
private bool _isMediaOpened;
```

In `LoadAsync`, create the player, subscribe events, assign `_player`, and only then assign the source:

```csharp
var player = new MediaPlayer
{
    AudioCategory = MediaPlayerAudioCategory.Media,
};

player.MediaOpened += OnMediaOpened;
player.MediaEnded += OnMediaEnded;
player.MediaFailed += OnMediaFailed;

_player = player;
player.Source = MediaSource.CreateFromStorageFile(file);
```

Replace `Seek` with pending-seek behavior:

```csharp
public void Seek(double seconds)
{
    var player = _player;
    if (player == null)
        return;

    _pendingSeekSeconds = NormalizeSeekSeconds(seconds, 0);
    if (_isMediaOpened)
        ApplyPendingSeek(player);
}

internal static double NormalizeSeekSeconds(double requested, double duration)
{
    if (!double.IsFinite(requested))
        return 0;

    var normalized = Math.Max(0, requested);
    return double.IsFinite(duration) && duration > 0
        ? Math.Min(normalized, duration)
        : normalized;
}
```

Add media-open and pending-seek helpers before `OnMediaEnded`:

```csharp
private void OnMediaOpened(MediaPlayer sender, object args)
{
    _isMediaOpened = true;
    ApplyPendingSeek(sender);
}

private void ApplyPendingSeek(MediaPlayer player)
{
    if (_pendingSeekSeconds is not double requested)
        return;

    try
    {
        var target = NormalizeSeekSeconds(
            requested,
            player.NaturalDuration.TotalSeconds);
        player.Position = TimeSpan.FromSeconds(target);
        _pendingSeekSeconds = null;
        Log.Information("[Sasayaki] Seeked to {Seconds:F1}s", target);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "[Sasayaki] Failed to restore playback position");
    }
}
```

At the start of `Play`, retry an unapplied seek after media has opened:

```csharp
if (_isMediaOpened)
    ApplyPendingSeek(_player);
```

In `StopInternal`, unsubscribe `MediaOpened` beside the existing event unsubscriptions:

```csharp
_player.MediaOpened -= OnMediaOpened;
```

Reset pending state immediately after stopping the position timer and before the existing `_player == null` guard:

```csharp
_pendingSeekSeconds = null;
_isMediaOpened = false;
```

In `LoadSasayakiAsync`, immediately after setting the loading status, load the current sidecar through the existing service:

```csharp
var bookRootPath = GetSasayakiBookRootPath();
var playback = string.IsNullOrWhiteSpace(bookRootPath)
    ? new SasayakiPlaybackData()
    : await SasayakiSidecarService.LoadPlaybackAsync(bookRootPath);
```

After `await _sasayakiPlayer.LoadAsync(audiobookPath);`, replace the settings-rate assignment, zero-state projection, and `SaveSasayakiPlaybackAsync(0)` call with:

```csharp
ApplySasayakiPlayback(playback);
UpdateSasayakiChromeVisibility();
```

Keep `ApplySasayakiPlayback` as the single Reader projection point. It already normalizes negative positions, applies delay and rate, seeks the player, updates cue navigation, updates the position text, and does not persist a replacement zero.

- [ ] **Step 8: Run all focused Sasayaki tests and commit**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~SasayakiMatchServiceTests|FullyQualifiedName~SasayakiPlayerTests|FullyQualifiedName~NovelReaderSasayakiAssetTests|FullyQualifiedName~SasayakiSidecarServiceTests|FullyQualifiedName~TtuSyncServiceTests"
```

Expected: all selected tests pass with zero failures, including the existing non-zero remote audiobook position import tests.

Commit:

```powershell
git add -- Niratan/Services/Sasayaki/SasayakiMatchService.cs Niratan/Services/Sasayaki/SasayakiPlayer.cs Niratan/Views/Pages/NovelReaderPage.xaml.cs Niratan.Tests/Services/Sasayaki/SasayakiMatchServiceTests.cs Niratan.Tests/Services/Sasayaki/SasayakiPlayerTests.cs Niratan.Tests/Views/Pages/NovelReaderSasayakiAssetTests.cs
git commit -m "fix(sasayaki): preserve synced playback position"
```

---

### Task 3: Changelog, Full Verification, and Exact Worktree Launch

**Files:**
- Modify: `docs/CHANGELOG.md`

**Interfaces:**
- Consumes: the completed overlay and playback-preservation behavior from Tasks 1 and 2.
- Produces: documented root cause, a clean x64 build/test result, and a responsive exact-worktree Niratan instance left running.

- [ ] **Step 1: Record the root causes and fixes**

Insert this section immediately below `# Changelog`:

```markdown
## 书籍同步缺少过程提示且 Sasayaki 位置被导入流程重置

**原因**：
- 书籍同步只在结束后显示通知，执行期间没有绑定到活动同步状态的持续 UI。
- Reader 导入音频/字幕后显式保存零位置，书架匹配服务也创建空播放 sidecar，覆盖了 Google Drive 导入的 `lastPosition`。

**解决**：
- 按 Niratan 在任意书籍同步期间显示阻塞式“正在同步…”遮罩，并以活动同步集合保证并发完成时不会提前隐藏。
- Sasayaki 匹配只保存匹配数据；Reader 加载媒体后读取并应用现有播放 sidecar，保留同步进来的位置、延迟、速率和 cue。

---
```

- [ ] **Step 2: Stop only the exact worktree executable before rebuilding**

Run:

```powershell
$exe = 'D:\CODE\Yukari\.worktrees\niratan-sync-parity\Niratan\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Niratan.exe'
Get-Process -Name Niratan -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe } |
    Stop-Process
```

Expected: only the `niratan-sync-parity` process stops; main-checkout and installed Niratan processes remain running.

- [ ] **Step 3: Run the complete x64 build**

Run:

```powershell
dotnet build -p:Platform=x64
```

Expected: exit code 0 and zero build errors. Existing dependency or analyzer warnings must be reported but do not count as new failures unless their count or content changes because of this work.

- [ ] **Step 4: Run the complete x64 test suite**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --no-build
```

Expected: all tests pass with zero failures and zero skipped tests.

- [ ] **Step 5: Launch the exact worktree executable and verify the window**

Run:

```powershell
$exe = 'D:\CODE\Yukari\.worktrees\niratan-sync-parity\Niratan\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Niratan.exe'
$process = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 4
$process.Refresh()
[pscustomobject]@{
    Id = $process.Id
    Path = $process.Path
    MainWindowHandle = $process.MainWindowHandle
    MainWindowTitle = $process.MainWindowTitle
    Responding = $process.Responding
} | Format-List
```

Expected: `Path` is the exact `niratan-sync-parity` executable, `MainWindowHandle` is non-zero, title is `Niratan`, and `Responding` is `True`.

- [ ] **Step 6: Verify the visible sync lifecycle without changing direction settings**

Use the Computer Use workflow against the window handle from Step 5:

1. Right-click a local book.
2. Expand the single **同步** submenu.
3. Click **导入** once.
4. Confirm `NovelBookSyncProgressOverlay` appears with “正在同步…”.
5. Confirm the overlay remains until the result notification appears, then disappears.
6. Do not click **导出** during this verification.

Expected: there is immediate process feedback and the bookshelf cannot be operated through the overlay.

- [ ] **Step 7: Verify playback preservation using the focused tests and sidecar evidence**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --no-build --filter "FullyQualifiedName~SasayakiMatchServiceTests.MatchAsync_PreservesExistingPlaybackSidecar|FullyQualifiedName~NovelReaderSasayakiAssetTests.AudioSubtitleImport_LoadsAndAppliesPlaybackWithoutResettingPosition|FullyQualifiedName~TtuSyncServiceTests.SyncBookAsync_ImportOnlyImportsNewerAudioBookWhenBookmarkProgressIsSynced"
```

Expected: all three preservation contracts pass. The tests jointly prove that Google Drive imports a non-zero audiobook position, matching does not replace it, and Reader import loads/applies rather than resets it.

- [ ] **Step 8: Commit documentation and verify the worktree is clean**

Run:

```powershell
git add -- docs/CHANGELOG.md
git commit -m "docs: record sync feedback and playback fix"
git status --short
git log -6 --oneline
```

Expected: the changelog commit succeeds, `git status --short` prints nothing, and the recent log includes the Task 1, Task 2, and Task 3 commits. Leave the verified exact-worktree Niratan instance running.
