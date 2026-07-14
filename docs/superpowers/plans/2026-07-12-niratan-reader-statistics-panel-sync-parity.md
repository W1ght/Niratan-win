# Niratan Reader Statistics Panel and Sync Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make same-chapter page turns count immediately, align the in-Reader statistics panel with Niratan v1.3.0, and add Niratan-compatible Google Drive statistics auto-sync across Reader open, bookmark, background, and close boundaries.

**Architecture:** Convert WebView2 page results to typed Reader events, apply navigation/statistics policy in `NovelReaderPageViewModel`, and leave `NovelReaderPage` as validation and UI orchestration only. Add a transient `IReaderAutoSyncCoordinator` around the existing TTU service for open import, 30-second coalescing, single-flight export, and final flush. Extract the compact panel into a native WinUI control bound to language-aware ViewModel projections.

**Tech Stack:** C#/.NET 10, WinUI 3, Windows App SDK 2.0, CommunityToolkit.Mvvm, WebView2, xUnit v3, FluentAssertions, Moq.

## Global Constraints

- Behavior source: `docs/reference/Niratan` at `v1.3.0`, commit `e40ca3a`.
- Target Windows 10+ x64; do not build ARM64 by default.
- Build: `dotnet build -p:Platform=x64`.
- Test: `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64`.
- Do not modify `native/hoshidicts/` or replace WebView2 EPUB rendering.
- Keep statistics/network policy out of View code-behind; ViewModels do not access SQLite or Google Drive directly.
- Preserve TTU v1.6 fields, `statistics.json`, Merge/Replace semantics, and the Bookshelf dashboard.
- Treat WebView2 messages, EPUBs, sidecars, and remote responses as untrusted input.
- Add no database, chart, or synchronization package.
- Preserve unrelated working-tree changes, especially `Niratan/Views/Dictionary/DictionaryPopupOverlay.cs` and pre-existing plan files.

## File Map

- `Niratan/Models/Novel/ReaderPageNavigationModels.cs`: typed page result, direction, event, and outcome.
- `Niratan/Services/Novels/ReaderStatisticsEventClassifier.cs`: bridge parsing and pure movement classification.
- `Niratan/ViewModels/Pages/NovelReaderPageViewModel.cs`: manual movement, autostart, display projection, and lifecycle orchestration.
- `Niratan/Views/Controls/ReaderStatisticsPanelContent.xaml(.cs)`: compact Session/Today/All Time UI.
- `Niratan/ViewModels/Pages/StatisticsSettingsPageViewModel.cs`: master/global-sync visibility.
- `Niratan/Services/Sync/IReaderAutoSyncCoordinator.cs`: narrow per-Reader sync contract.
- `Niratan/Services/Sync/ReaderAutoSyncCoordinator.cs`: open import, debounce, single-flight, replay, cancel, and logging.

---

### Task 1: Type and repair the page-turn bridge contract

**Files:**
- Create: `Niratan/Models/Novel/ReaderPageNavigationModels.cs`
- Modify: `Niratan/Services/Novels/ReaderStatisticsEventClassifier.cs`
- Modify: `Niratan/Views/Pages/NovelReaderPage.xaml.cs`
- Modify: `Niratan.Tests/Services/Novels/ReaderStatisticsEventClassifierTests.cs`
- Modify: `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Consumes: bridge strings `scrolled`, `limit`, `forward`, `backward`.
- Produces: `ReaderPageNavigationEvent`, `ReaderPageNavigationOutcome`, `TryCreateEvent`, typed `IsActualPageMovement`, and typed `AdjacentChapterTarget`.

- [ ] **Step 1: Write failing bridge parser tests**

```csharp
[Theory]
[InlineData("scrolled", "forward", 0.30, ReaderPageNavigationResult.Scrolled, ReaderPageNavigationDirection.Forward)]
[InlineData("limit", "backward", 0.00, ReaderPageNavigationResult.Limit, ReaderPageNavigationDirection.Backward)]
public void TryCreateEvent_AcceptsBridgeVocabulary(
    string result,
    string direction,
    double progress,
    ReaderPageNavigationResult expectedResult,
    ReaderPageNavigationDirection expectedDirection)
{
    ReaderStatisticsEventClassifier.TryCreateEvent(
        result, direction, progress, out var readerEvent).Should().BeTrue();
    readerEvent.Should().Be(new ReaderPageNavigationEvent(
        expectedResult, expectedDirection, progress));
}

[Theory]
[InlineData("moved", "forward")]
[InlineData("unknown", "forward")]
[InlineData("scrolled", "sideways")]
public void TryCreateEvent_RejectsUnknownVocabulary(string result, string direction)
{
    ReaderStatisticsEventClassifier.TryCreateEvent(
        result, direction, 0.5, out _).Should().BeFalse();
}
```

Change movement tests to use `ReaderPageNavigationResult.Scrolled`. Add this asset assertion:

```csharp
var script = File.ReadAllText(Path.Combine(ReaderRoot, "reader-bridge.js"));
var classifier = File.ReadAllText(Path.Combine(
    ProjectRoot, "Services", "Novels", "ReaderStatisticsEventClassifier.cs"));
script.Should().Contain("return \"scrolled\";");
classifier.Should().Contain("ReaderPageNavigationResult.Scrolled");
classifier.Should().NotContain("\"moved\"");
```

- [ ] **Step 2: Verify RED**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ReaderStatisticsEventClassifierTests|FullyQualifiedName~NovelReaderWebAssetTests"
```

Expected: FAIL because the typed models/parser do not exist and the classifier expects `moved`.

- [ ] **Step 3: Create typed models**

```csharp
namespace Niratan.Models.Novel;

public enum ReaderPageNavigationResult { Scrolled, Limit }
public enum ReaderPageNavigationDirection { Forward, Backward }

public readonly record struct ReaderPageNavigationEvent(
    ReaderPageNavigationResult Result,
    ReaderPageNavigationDirection Direction,
    double Progress);

public readonly record struct ReaderPageNavigationOutcome(
    bool DidMove,
    int? AdjacentChapterIndex)
{
    public static ReaderPageNavigationOutcome NoMovement => new(false, null);
    public static ReaderPageNavigationOutcome SameChapterMovement => new(true, null);
    public static ReaderPageNavigationOutcome AdjacentChapter(int index) => new(true, index);
}
```

- [ ] **Step 4: Implement strict typed parsing/classification**

```csharp
public static bool TryCreateEvent(
    string? result,
    string? direction,
    double progress,
    out ReaderPageNavigationEvent readerEvent)
{
    readerEvent = default;
    if (!double.IsFinite(progress)) return false;

    var parsedResult = result switch
    {
        "scrolled" => ReaderPageNavigationResult.Scrolled,
        "limit" => ReaderPageNavigationResult.Limit,
        _ => (ReaderPageNavigationResult?)null,
    };
    var parsedDirection = direction switch
    {
        "forward" => ReaderPageNavigationDirection.Forward,
        "backward" => ReaderPageNavigationDirection.Backward,
        _ => (ReaderPageNavigationDirection?)null,
    };
    if (parsedResult is null || parsedDirection is null) return false;

    readerEvent = new(
        parsedResult.Value,
        parsedDirection.Value,
        Math.Clamp(progress, 0, 1));
    return true;
}

public static bool IsActualPageMovement(
    ReaderPageNavigationEvent readerEvent,
    double previousProgress) =>
    readerEvent.Result == ReaderPageNavigationResult.Scrolled
    && HasProgressMovement(previousProgress, readerEvent.Progress);
```

Implement typed `AdjacentChapterTarget` by accepting only `Limit`, adding/subtracting one from the current chapter, and returning null outside `[0, chapterCount)`. In `pageChanged`, parse first; log and ignore invalid events. Keep the bridge's Niratan `scrolled` value.

- [ ] **Step 5: Verify GREEN and commit**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ReaderStatisticsEventClassifierTests|FullyQualifiedName~NovelReaderWebAssetTests"
git add Niratan/Models/Novel/ReaderPageNavigationModels.cs Niratan/Services/Novels/ReaderStatisticsEventClassifier.cs Niratan/Views/Pages/NovelReaderPage.xaml.cs Niratan.Tests/Services/Novels/ReaderStatisticsEventClassifierTests.cs Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "fix(statistics): recognize reader page movement"
```

Expected: targeted tests PASS.

---

### Task 2: Move manual statistics policy into the Reader ViewModel

**Files:**
- Modify: `Niratan/ViewModels/Pages/NovelReaderPageViewModel.cs`
- Modify: `Niratan/Views/Pages/NovelReaderPage.xaml.cs`
- Modify: `Niratan.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs`

**Interfaces:**
- Consumes: `ReaderPageNavigationEvent`, `ISettingsService.Current.StatisticsSettings`.
- Produces: `HandleManualPageNavigationAsync`, `StartStatisticsForAutostart`, `ToggleStatisticsTrackingCommand`.

- [ ] **Step 1: Write failing same-page and boundary tests**

```csharp
[Fact]
public async Task ManualSameChapterPageTurn_UpdatesBookmarkAndCheckpointsOnce()
{
    using var temp = new TempBookDirectory();
    var statisticsSession = new FakeReaderStatisticsSession();
    var sut = CreateInitializedSut(
        temp.Path,
        new ReaderHighlightService(),
        statisticsSession: statisticsSession,
        statisticsSettings: new NovelStatisticsSettings
    {
        EnableStatistics = true,
        AutostartMode = StatisticsAutostartMode.PageTurn,
    });
    sut.SetChapterCharacterCounts([100]);
    sut.SetChapter(0, 1);
    sut.UpdateProgress(0.20);

    var outcome = await sut.HandleManualPageNavigationAsync(
        new(ReaderPageNavigationResult.Scrolled,
            ReaderPageNavigationDirection.Forward,
            0.30),
        TestContext.Current.CancellationToken);

    outcome.Should().Be(ReaderPageNavigationOutcome.SameChapterMovement);
    sut.CurrentCharacterCount.Should().Be(30);
    sut.IsStatisticsTracking.Should().BeTrue();
    statisticsSession.Checkpoints.Should().Equal(
        (30, ReaderStatisticsCheckpointReason.ReadingMovement));
}
```

Add one adjacent `Limit` test expecting `AdjacentChapter(1)` plus one `AdjacentChapter` checkpoint, and one final-book `Limit` test expecting Page Turn autostart but no checkpoint.

- [ ] **Step 2: Verify RED**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderPageViewModelTests"
```

Expected: FAIL because the ViewModel has no typed handler or settings dependency.

- [ ] **Step 3: Inject settings and add commands**

Store `ISettingsService`. Update every test constructor with a mock returning a real `AppSettings`. Implement:

```csharp
public void StartStatisticsForAutostart(StatisticsAutostartMode trigger)
{
    var settings = _settingsService.Current.StatisticsSettings;
    if (!settings.EnableStatistics
        || IsStatisticsTracking
        || settings.AutostartMode != trigger) return;
    StartStatisticsTracking();
}

[RelayCommand]
private async Task ToggleStatisticsTrackingAsync()
{
    if (!_settingsService.Current.StatisticsSettings.EnableStatistics) return;
    if (IsStatisticsTracking) await StopStatisticsTrackingAsync();
    else StartStatisticsTracking();
}
```

Extend the existing test helper with explicit optional settings/language inputs:

```csharp
private static NovelReaderPageViewModel CreateInitializedSut(
    string bookRootPath,
    IReaderHighlightService highlightService,
    INovelBookSidecarService? novelBookSidecarService = null,
    IReaderStatisticsSession? statisticsSession = null,
    NovelStatisticsSettings? statisticsSettings = null,
    ContentLanguageProfile? language = null)
{
    var novelService = new Mock<INovelLibraryService>();
    novelService
        .Setup(service => service.GetNovelBookAsync(
            "book-1", It.IsAny<CancellationToken>()))
        .ReturnsAsync(Result<NovelBook?>.Success(new NovelBook
        {
            Id = "book-1",
            Title = "Book One",
            FilePath = Path.Combine(bookRootPath, "book.epub"),
            ExtractedPath = bookRootPath,
        }));
    novelService
        .Setup(service => service.MarkOpenedAsync(
            "book-1", It.IsAny<CancellationToken>()))
        .ReturnsAsync(Result.Success());
    var appSettings = new AppSettings
    {
        StatisticsSettings = statisticsSettings ?? new NovelStatisticsSettings(),
    };
    var settings = new Mock<ISettingsService>();
    settings.SetupGet(service => service.Current).Returns(appSettings);
    var profile = new NoOpProfileRuntimeService(language ?? ContentLanguageProfile.Japanese);

    // Retain the existing INovelLibraryService, messenger, highlight, and sidecar setup.
    var sut = new NovelReaderPageViewModel(
        novelService.Object,
        Mock.Of<INotificationService>(),
        new FakeMessenger(),
        highlightService,
        novelBookSidecarService ?? new NovelBookSidecarService(),
        statisticsSession ?? new FakeReaderStatisticsSession(),
        profile,
        settings.Object);
    sut.InitializeAsync(new NovelReaderNavigationArgs("book-1")).GetAwaiter().GetResult();
    return sut;
}
```

Change `NoOpProfileRuntimeService` to accept `ContentLanguageProfile language` and construct `ActiveResolution` with that language.

- [ ] **Step 4: Implement the manual movement workflow**

```csharp
public async Task<ReaderPageNavigationOutcome> HandleManualPageNavigationAsync(
    ReaderPageNavigationEvent readerEvent,
    CancellationToken ct = default)
{
    StartStatisticsForAutostart(StatisticsAutostartMode.PageTurn);

    if (ReaderStatisticsEventClassifier.IsActualPageMovement(readerEvent, Progress))
    {
        UpdateProgress(readerEvent.Progress);
        await SaveProgressNowAsync(flushStatistics: false, ct);
        await CheckpointReadingAsync(ReaderStatisticsCheckpointReason.ReadingMovement, ct);
        return ReaderPageNavigationOutcome.SameChapterMovement;
    }

    var adjacent = ReaderStatisticsEventClassifier.AdjacentChapterTarget(
        readerEvent, CurrentChapterIndex, ChapterCount);
    if (!adjacent.HasValue) return ReaderPageNavigationOutcome.NoMovement;

    UpdateProgress(readerEvent.Progress);
    await SaveProgressNowAsync(flushStatistics: false, ct);
    await CheckpointReadingAsync(ReaderStatisticsCheckpointReason.AdjacentChapter, ct);
    return ReaderPageNavigationOutcome.AdjacentChapter(adjacent.Value);
}
```

The page parses/forwards, clears forward history when `DidMove`, loads an adjacent chapter when requested, and saves the new destination without a second statistics checkpoint. Delete private code-behind autostart/toggle policy.

- [ ] **Step 5: Verify GREEN and commit**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderPageViewModelTests|FullyQualifiedName~NovelReaderWebAssetTests"
git add Niratan/ViewModels/Pages/NovelReaderPageViewModel.cs Niratan/Views/Pages/NovelReaderPage.xaml.cs Niratan.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs
git commit -m "refactor(statistics): move page-turn policy into reader viewmodel"
```

Expected: same-chapter test records 30 raw characters and exactly one checkpoint.

---

### Task 3: Build the compact language-aware Reader statistics panel

**Files:**
- Create: `Niratan/Views/Controls/ReaderStatisticsPanelContent.xaml`
- Create: `Niratan/Views/Controls/ReaderStatisticsPanelContent.xaml.cs`
- Modify: `Niratan/Views/Pages/NovelReaderPage.xaml`
- Modify: `Niratan/Views/Pages/NovelReaderPage.xaml.cs`
- Modify: `Niratan/ViewModels/Pages/NovelReaderPageViewModel.cs`
- Modify: `Niratan/Strings/en-US/Resources.resw`
- Modify: `Niratan/Strings/zh-CN/Resources.resw`
- Modify: `Niratan.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs`
- Modify: `Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Consumes: `_profileRuntime.ActiveLanguage`, raw statistics, `ToggleStatisticsTrackingCommand`.
- Produces: `IsEnglishStatisticsContent`, converted count/speed text, and `ReaderStatisticsPanelContent`.

- [ ] **Step 1: Write failing language-projection tests**

```csharp
[Theory]
[InlineData("ja", false, "11", "1,805 / h")]
[InlineData("en", true, "3", "361 / h")]
public async Task StatisticsPanel_UsesActiveContentLanguageUnits(
    string languageId,
    bool expectedEnglish,
    string expectedCount,
    string expectedSpeed)
{
    using var temp = new TempBookDirectory();
    var statistics = new FakeReaderStatisticsSession();
    var sut = CreateInitializedSut(
        temp.Path,
        new ReaderHighlightService(),
        language: ContentLanguageProfile.FromId(languageId),
        statisticsSession: statistics);
    await sut.LoadStatisticsAsync(TestContext.Current.CancellationToken);

    statistics.Publish(new ReaderStatisticsSessionState(
        true, false,
        Statistic(11, 60, 1_805),
        Statistic(11, 60, 1_805),
        Statistic(11, 60, 1_805),
        []));

    sut.IsEnglishStatisticsContent.Should().Be(expectedEnglish);
    sut.StatisticsSessionCharactersText.Should().Be(expectedCount);
    sut.StatisticsSessionSpeedText.Should().Be(expectedSpeed);
}
```

Add a separate test proving remaining time uses raw remaining characters and raw speed, not converted display units.

- [ ] **Step 2: Write the failing panel asset test**

```csharp
var panelXaml = File.ReadAllText(Path.Combine(
    ProjectRoot, "Views", "Controls", "ReaderStatisticsPanelContent.xaml"));
var readerXaml = File.ReadAllText(Path.Combine(
    ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml"));
panelXaml.Should().Contain("ReaderStatisticsSessionSection");
panelXaml.Should().Contain("ReaderStatisticsTodaySection");
panelXaml.Should().Contain("ReaderStatisticsAllTimeSection");
panelXaml.Should().Contain("ToggleStatisticsTrackingCommand");
readerXaml.Should().Contain("<controls:ReaderStatisticsPanelContent");
readerXaml.Should().Contain("MinWidth=\"520\"");
readerXaml.Should().NotContain("<Grid Width=\"1120\"\r\n                  Height=\"560\"");
```

- [ ] **Step 3: Verify RED**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderPageViewModelTests|FullyQualifiedName~NovelReaderWebAssetTests"
```

Expected: FAIL because counts are raw-only and the panel control is absent.

- [ ] **Step 4: Implement language-aware projection**

```csharp
public bool IsEnglishStatisticsContent =>
    _profileRuntime.ActiveLanguage.Id == ContentLanguageProfile.English.Id;

private int DisplayStatisticsUnits(int rawCharacters) =>
    _profileRuntime.ActiveLanguage.DisplayUnitsFromRawCharacters(rawCharacters);

private string FormatStatisticsCount(int rawCharacters) =>
    FormatCount(DisplayStatisticsUnits(rawCharacters));

private string FormatStatisticsSpeed(int rawCharactersPerHour) =>
    $"{FormatCount(DisplayStatisticsUnits(rawCharactersPerHour))} / h";
```

Use these helpers for Session, Today, and All Time count/speed properties. Keep storage and remaining-time formulas in raw units. Raise `IsEnglishStatisticsContent` with the existing statistics projection notifications.

- [ ] **Step 5: Create the compact panel control**

Create `ReaderStatisticsPanelContent.xaml` with one scroll owner and explicit rows:

```xml
<UserControl x:Class="Niratan.Views.Controls.ReaderStatisticsPanelContent"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:converters="using:Niratan.Helpers.UI.Converters">
    <UserControl.Resources>
        <converters:BooleanToVisibilityConverter x:Key="BooleanToVisibilityConverter" />
        <Style x:Key="StatisticsValueStyle" TargetType="TextBlock">
            <Setter Property="Grid.Column" Value="1" />
            <Setter Property="HorizontalAlignment" Value="Right" />
            <Setter Property="FontFamily" Value="Consolas" />
            <Setter Property="FontWeight" Value="SemiBold" />
        </Style>
    </UserControl.Resources>
    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel Spacing="20">
            <StackPanel x:Name="ReaderStatisticsSessionSection"
                        AutomationProperties.AutomationId="ReaderStatisticsSessionSection"
                        Spacing="10">
                <Grid ColumnDefinitions="*,Auto">
                    <TextBlock x:Uid="ReaderStatisticsSessionHeader"
                               Style="{StaticResource BodyStrongTextBlockStyle}" />
                    <Button Grid.Column="1"
                            x:Uid="ReaderStatisticsStartButton"
                            AutomationProperties.AutomationId="NovelReaderStatisticsStartStopButton"
                            Command="{Binding ToggleStatisticsTrackingCommand}"
                            Visibility="{Binding IsStatisticsTracking, Converter={StaticResource BooleanToVisibilityConverter}, ConverterParameter=Invert}">
                        <FontIcon Glyph="&#xE768;" />
                    </Button>
                    <Button Grid.Column="1"
                            x:Uid="ReaderStatisticsStopButton"
                            AutomationProperties.AutomationId="NovelReaderStatisticsStartStopButton"
                            Command="{Binding ToggleStatisticsTrackingCommand}"
                            Visibility="{Binding IsStatisticsTracking, Converter={StaticResource BooleanToVisibilityConverter}}">
                        <FontIcon Glyph="&#xE769;" />
                    </Button>
                </Grid>
                <Grid ColumnDefinitions="*,Auto">
                    <Grid>
                        <TextBlock x:Uid="ReaderStatisticsCharactersLabel"
                                   Visibility="{Binding IsEnglishStatisticsContent, Converter={StaticResource BooleanToVisibilityConverter}, ConverterParameter=Invert}" />
                        <TextBlock x:Uid="ReaderStatisticsApproximateWordsLabel"
                                   Visibility="{Binding IsEnglishStatisticsContent, Converter={StaticResource BooleanToVisibilityConverter}}" />
                    </Grid>
                    <TextBlock Style="{StaticResource StatisticsValueStyle}"
                               Text="{Binding StatisticsSessionCharactersText}" />
                </Grid>
                <Grid ColumnDefinitions="*,Auto"><TextBlock x:Uid="ReaderStatisticsSpeedLabel" /><TextBlock Style="{StaticResource StatisticsValueStyle}" Text="{Binding StatisticsSessionSpeedText}" /></Grid>
                <Grid ColumnDefinitions="*,Auto"><TextBlock x:Uid="ReaderStatisticsTimeLabel" /><TextBlock Style="{StaticResource StatisticsValueStyle}" Text="{Binding StatisticsSessionTimeText}" /></Grid>
                <Grid ColumnDefinitions="*,Auto"><TextBlock x:Uid="ReaderStatisticsBookRemainingLabel" /><TextBlock Style="{StaticResource StatisticsValueStyle}" Text="{Binding StatisticsBookRemainingTimeText}" /></Grid>
                <Grid ColumnDefinitions="*,Auto"><TextBlock x:Uid="ReaderStatisticsChapterRemainingLabel" /><TextBlock Style="{StaticResource StatisticsValueStyle}" Text="{Binding StatisticsChapterRemainingTimeText}" /></Grid>
            </StackPanel>
            <StackPanel x:Name="ReaderStatisticsTodaySection"
                        AutomationProperties.AutomationId="ReaderStatisticsTodaySection"
                        Spacing="10">
                <TextBlock x:Uid="ReaderStatisticsTodayHeader" Style="{StaticResource BodyStrongTextBlockStyle}" />
                <Grid ColumnDefinitions="*,Auto"><Grid><TextBlock x:Uid="ReaderStatisticsCharactersLabel" Visibility="{Binding IsEnglishStatisticsContent, Converter={StaticResource BooleanToVisibilityConverter}, ConverterParameter=Invert}" /><TextBlock x:Uid="ReaderStatisticsApproximateWordsLabel" Visibility="{Binding IsEnglishStatisticsContent, Converter={StaticResource BooleanToVisibilityConverter}}" /></Grid><TextBlock Style="{StaticResource StatisticsValueStyle}" Text="{Binding StatisticsTodayCharactersText}" /></Grid>
                <Grid ColumnDefinitions="*,Auto"><TextBlock x:Uid="ReaderStatisticsSpeedLabel" /><TextBlock Style="{StaticResource StatisticsValueStyle}" Text="{Binding StatisticsTodaySpeedText}" /></Grid>
                <Grid ColumnDefinitions="*,Auto"><TextBlock x:Uid="ReaderStatisticsTimeLabel" /><TextBlock Style="{StaticResource StatisticsValueStyle}" Text="{Binding StatisticsTodayTimeText}" /></Grid>
            </StackPanel>
            <StackPanel x:Name="ReaderStatisticsAllTimeSection"
                        AutomationProperties.AutomationId="ReaderStatisticsAllTimeSection"
                        Spacing="10">
                <TextBlock x:Uid="ReaderStatisticsAllTimeHeader" Style="{StaticResource BodyStrongTextBlockStyle}" />
                <Grid ColumnDefinitions="*,Auto"><Grid><TextBlock x:Uid="ReaderStatisticsCharactersLabel" Visibility="{Binding IsEnglishStatisticsContent, Converter={StaticResource BooleanToVisibilityConverter}, ConverterParameter=Invert}" /><TextBlock x:Uid="ReaderStatisticsApproximateWordsLabel" Visibility="{Binding IsEnglishStatisticsContent, Converter={StaticResource BooleanToVisibilityConverter}}" /></Grid><TextBlock Style="{StaticResource StatisticsValueStyle}" Text="{Binding StatisticsAllTimeCharactersText}" /></Grid>
                <Grid ColumnDefinitions="*,Auto"><TextBlock x:Uid="ReaderStatisticsSpeedLabel" /><TextBlock Style="{StaticResource StatisticsValueStyle}" Text="{Binding StatisticsAllTimeSpeedText}" /></Grid>
                <Grid ColumnDefinitions="*,Auto"><TextBlock x:Uid="ReaderStatisticsTimeLabel" /><TextBlock Style="{StaticResource StatisticsValueStyle}" Text="{Binding StatisticsAllTimeTimeText}" /></Grid>
            </StackPanel>
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

The `.xaml.cs` contains only `InitializeComponent()`:

```csharp
using Microsoft.UI.Xaml.Controls;

namespace Niratan.Views.Controls;

public sealed partial class ReaderStatisticsPanelContent : UserControl
{
    public ReaderStatisticsPanelContent() => InitializeComponent();
}
```

- [ ] **Step 6: Host the compact control**

Keep `ReaderStatisticsPanelDialog`, set `MinWidth="520"`, `MaxWidth="560"`, remove its 1120-pixel inner grid, and host:

```xml
<controls:ReaderStatisticsPanelContent
    MaxWidth="520"
    DataContext="{x:Bind ViewModel, Mode=OneWay}" />
```

Delete the click handler/named icon refresh. Remove `FlushStatisticsAsync` from `StatisticsButton_Click`; the one-second projection already keeps values current. Add localized English/Chinese keys for approximate words, sections, metrics, and start/stop accessible names.

- [ ] **Step 7: Verify GREEN, build, and commit**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderPageViewModelTests|FullyQualifiedName~NovelReaderWebAssetTests"
dotnet build -p:Platform=x64
git add Niratan/Views/Controls/ReaderStatisticsPanelContent.xaml Niratan/Views/Controls/ReaderStatisticsPanelContent.xaml.cs Niratan/Views/Pages/NovelReaderPage.xaml Niratan/Views/Pages/NovelReaderPage.xaml.cs Niratan/ViewModels/Pages/NovelReaderPageViewModel.cs Niratan/Strings/en-US/Resources.resw Niratan/Strings/zh-CN/Resources.resw Niratan.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs Niratan.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "feat(statistics): align reader statistics panel"
```

Expected: targeted tests and x64 build PASS.

---

### Task 4: Align statistics settings with global Google Drive sync

**Files:**
- Modify: `Niratan/ViewModels/Pages/StatisticsSettingsPageViewModel.cs`
- Modify: `Niratan/Views/Pages/StatisticsSettingsPage.xaml`
- Modify: `Niratan/Views/Pages/StatisticsSettingsPage.xaml.cs`
- Modify: `Niratan.Tests/ViewModels/Pages/StatisticsSettingsPageViewModelTests.cs`
- Modify: `Niratan.Tests/Services/Sync/TtuSyncSettingsAssetTests.cs`

**Interfaces:**
- Consumes: `AppSettings.TtuSyncSettings.EnableSync` and existing statistics preferences.
- Produces: `ShowStatisticsOptions`, `IsGlobalSyncEnabled`, `ShowStatisticsSyncOptions`, `RefreshGlobalSyncState`.

- [ ] **Step 1: Write failing visibility/preservation tests**

```csharp
[Fact]
public void Visibility_FollowsMasterAndGlobalSyncWithoutErasingPreferences()
{
    var settings = new AppSettings
    {
        StatisticsSettings = new NovelStatisticsSettings
        {
            EnableStatistics = true,
            EnableSync = true,
            SyncMode = StatisticsSyncMode.Replace,
        },
        TtuSyncSettings = new TtuSyncSettings { EnableSync = false },
    };
    var sut = CreateViewModel(settings);

    sut.ShowStatisticsOptions.Should().BeTrue();
    sut.ShowStatisticsSyncOptions.Should().BeFalse();
    sut.EnableSync.Should().BeTrue();

    settings.TtuSyncSettings.EnableSync = true;
    sut.RefreshGlobalSyncState();

    sut.ShowStatisticsSyncOptions.Should().BeTrue();
    sut.EnableSync.Should().BeTrue();
    sut.SelectedSyncMode.Should().Be(StatisticsSyncMode.Replace);
}
```

Add this helper to the settings test class:

```csharp
private static StatisticsSettingsPageViewModel CreateViewModel(AppSettings current)
{
    var service = new Mock<ISettingsService>();
    service.SetupGet(item => item.Current).Returns(current);
    service.Setup(item => item.Set(
            It.IsAny<Expression<Func<AppSettings, NovelStatisticsSettings>>>(),
            It.IsAny<NovelStatisticsSettings>()))
        .Callback<Expression<Func<AppSettings, NovelStatisticsSettings>>, NovelStatisticsSettings>(
            (_, value) => current.StatisticsSettings = value);
    service.Setup(item => item.SaveAsync()).Returns(Task.CompletedTask);
    return new StatisticsSettingsPageViewModel(service.Object);
}
```

Add a test that disabling statistics hides both subordinate groups while preserving Autostart, goals, stats-sync, and Merge/Replace values.

- [ ] **Step 2: Verify RED**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~StatisticsSettingsPageViewModelTests|FullyQualifiedName~TtuSyncSettingsAssetTests"
```

Expected: FAIL because derived visibility properties do not exist.

- [ ] **Step 3: Implement visibility state**

```csharp
[ObservableProperty]
public partial bool IsGlobalSyncEnabled { get; private set; }

public bool ShowStatisticsOptions => EnableStatistics;
public bool ShowStatisticsSyncOptions => EnableStatistics && IsGlobalSyncEnabled;

public void RefreshGlobalSyncState()
{
    IsGlobalSyncEnabled = _settingsService.Current.TtuSyncSettings.EnableSync;
    OnPropertyChanged(nameof(ShowStatisticsSyncOptions));
}

partial void OnEnableStatisticsChanged(bool value)
{
    OnPropertyChanged(nameof(ShowStatisticsOptions));
    OnPropertyChanged(nameof(ShowStatisticsSyncOptions));
    SaveSettings();
}
```

Call `RefreshGlobalSyncState()` after load and from `OnNavigatedTo`.

- [ ] **Step 4: Group XAML by derived visibility**

Keep the master Enable card visible. Wrap the existing Autostart/Goal cards and Sync cards as follows without changing their inner bindings:

```xml
<StackPanel Spacing="4"
            Visibility="{x:Bind ViewModel.ShowStatisticsOptions, Mode=OneWay, Converter={StaticResource BooleanToVisibilityConverter}}">
</StackPanel>
<StackPanel Spacing="4"
            Visibility="{x:Bind ViewModel.ShowStatisticsSyncOptions, Mode=OneWay, Converter={StaticResource BooleanToVisibilityConverter}}">
</StackPanel>
```

Place the existing complete Autostart, Daily Goal, and Weekly Goal elements inside the first wrapper; place the existing complete Sync header, EnableSync toggle, and SyncMode ComboBox inside the second wrapper. Do not change their current inner bindings and do not mutate preference values when a group hides. Add exact asset assertions:

```csharp
pageXaml.Should().Contain("ViewModel.ShowStatisticsOptions");
pageXaml.Should().Contain("ViewModel.ShowStatisticsSyncOptions");
viewModelCode.Should().NotContain("EnableSync = false");
viewModelCode.Should().NotContain("SelectedSyncMode = StatisticsSyncMode.Merge;");
```

- [ ] **Step 5: Verify GREEN and commit**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~StatisticsSettingsPageViewModelTests|FullyQualifiedName~TtuSyncSettingsAssetTests"
git add Niratan/ViewModels/Pages/StatisticsSettingsPageViewModel.cs Niratan/Views/Pages/StatisticsSettingsPage.xaml Niratan/Views/Pages/StatisticsSettingsPage.xaml.cs Niratan.Tests/ViewModels/Pages/StatisticsSettingsPageViewModelTests.cs Niratan.Tests/Services/Sync/TtuSyncSettingsAssetTests.cs
git commit -m "feat(statistics): gate sync settings behind google drive"
```

Expected: targeted tests PASS and hidden settings remain persisted.

---

### Task 5: Add the per-Reader Google Drive auto-sync coordinator

**Files:**
- Create: `Niratan/Services/Sync/IReaderAutoSyncCoordinator.cs`
- Create: `Niratan/Services/Sync/ReaderAutoSyncCoordinator.cs`
- Create: `Niratan.Tests/Services/Sync/ReaderAutoSyncCoordinatorTests.cs`
- Modify: `Niratan/App.xaml.cs`

**Interfaces:**
- Consumes: `ITtuSyncService`, `ISettingsService.Current`, `IGoogleDriveAuthService.HasCredentials`, `NovelBook`.
- Produces: `ImportOnOpenAsync`, `ScheduleExport`, `FlushAsync`, `Cancel`.

- [ ] **Step 1: Write failing prerequisite and open-import tests**

```csharp
[Fact]
public async Task ImportOnOpenAsync_UsesAutoImportOnlyAndStatisticsOptions()
{
    var sync = new Mock<ITtuSyncService>();
    sync.Setup(service => service.SyncBookAsync(
            Book,
            new TtuSyncOptions(
                Direction: TtuSyncDirection.Auto,
                SyncBookData: true,
                SyncStatistics: true,
                StatisticsSyncMode: StatisticsSyncMode.Replace,
                SyncAudioBook: true,
                ImportOnly: true),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(new TtuSyncResult(TtuSyncResultKind.Imported, Book.Title));

    var sut = CreateCoordinator(sync.Object, EnabledSettings(), credentials: true);

    (await sut.ImportOnOpenAsync(Book, TestContext.Current.CancellationToken))
        .Should().BeTrue();
    sync.VerifyAll();
}

[Theory]
[InlineData(false, true, true)]
[InlineData(true, false, true)]
[InlineData(true, true, false)]
public async Task ImportOnOpenAsync_SkipsWhenPrerequisiteIsMissing(
    bool globalSync, bool autoSync, bool credentials)
{
    var sync = new Mock<ITtuSyncService>(MockBehavior.Strict);
    var settings = EnabledSettings();
    settings.TtuSyncSettings.EnableSync = globalSync;
    settings.TtuSyncSettings.EnableAutoSync = autoSync;
    var sut = CreateCoordinator(sync.Object, settings, credentials);

    (await sut.ImportOnOpenAsync(Book, TestContext.Current.CancellationToken))
        .Should().BeFalse();
}
```

Add explicit fixtures/helpers to the coordinator test class:

```csharp
private static readonly NovelBook Book = new()
{
    Id = "book-1",
    Title = "Book One",
    ExtractedPath = "C:\\Books\\book-1",
};

private static AppSettings EnabledSettings() => new()
{
    TtuSyncSettings = new TtuSyncSettings
    {
        EnableSync = true,
        EnableAutoSync = true,
        UploadBooks = true,
    },
    StatisticsSettings = new NovelStatisticsSettings
    {
        EnableSync = true,
        SyncMode = StatisticsSyncMode.Replace,
    },
    SasayakiSettings = new SasayakiSettings
    {
        EnableSasayaki = true,
        EnableSync = true,
    },
};

private static ReaderAutoSyncCoordinator CreateCoordinator(
    ITtuSyncService sync,
    AppSettings current,
    bool credentials,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    var settings = new Mock<ISettingsService>();
    settings.SetupGet(service => service.Current).Returns(current);
    var auth = new Mock<IGoogleDriveAuthService>();
    auth.SetupGet(service => service.HasCredentials).Returns(credentials);
    return new ReaderAutoSyncCoordinator(
        sync,
        settings.Object,
        auth.Object,
        Mock.Of<ILogger<ReaderAutoSyncCoordinator>>(),
        delay ?? ((duration, ct) => Task.Delay(duration, ct)));
}
```

- [ ] **Step 2: Write failing debounce/single-flight tests**

Use the internal delay delegate allowed by `InternalsVisibleTo("Niratan.Tests")`. Hold delay and first export with `TaskCompletionSource` values:

```csharp
sut.ScheduleExport(Book);
sut.ScheduleExport(Book);
delayRequests.Should().ContainSingle();
delayRequests[0].Delay.Should().Be(TimeSpan.FromSeconds(30));

delayRequests[0].Release();
await firstExportStarted.Task;
sut.ScheduleExport(Book);
firstExportFinished.SetResult();
await sut.FlushAsync(Book, ct);

exportOptions.Should().HaveCount(2);
exportOptions.Should().OnlyContain(option =>
    option.Direction == TtuSyncDirection.ExportToTtu && !option.ImportOnly);
```

Add tests for contained network failure, cancellation, `Cancel`, and final `FlushAsync` completion.

- [ ] **Step 3: Verify RED**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ReaderAutoSyncCoordinatorTests"
```

Expected: FAIL because coordinator types do not exist.

- [ ] **Step 4: Add the interface**

```csharp
public interface IReaderAutoSyncCoordinator
{
    Task<bool> ImportOnOpenAsync(NovelBook book, CancellationToken ct = default);
    void ScheduleExport(NovelBook book);
    Task FlushAsync(NovelBook book, CancellationToken ct = default);
    void Cancel();
}
```

- [ ] **Step 5: Implement prerequisites/options/open import**

Constructor dependencies: `ITtuSyncService`, `ISettingsService`, `IGoogleDriveAuthService`, `ILogger<ReaderAutoSyncCoordinator>`. Add an internal five-argument overload ending in `Func<TimeSpan,CancellationToken,Task>` for tests; the public four-argument constructor delegates to it with `DefaultDelay`.

```csharp
private bool CanAutoSync()
{
    var sync = _settingsService.Current.TtuSyncSettings;
    return sync.EnableSync
        && sync.EnableAutoSync
        && _googleDriveAuthService.HasCredentials;
}

private TtuSyncOptions CreateOptions(TtuSyncDirection direction, bool importOnly)
{
    var current = _settingsService.Current;
    return new(
        Direction: direction,
        SyncBookData: current.TtuSyncSettings.UploadBooks,
        SyncStatistics: current.TtuSyncSettings.EnableSync
            && current.StatisticsSettings.EnableSync,
        StatisticsSyncMode: current.StatisticsSettings.SyncMode,
        SyncAudioBook: current.SasayakiSettings.EnableSasayaki
            && current.SasayakiSettings.EnableSync,
        ImportOnly: importOnly);
}
```

`ImportOnOpenAsync` skips when prerequisites fail; otherwise call `SyncBookAsync(book, CreateOptions(Auto, true), ct)` and return true only for `Imported`. Catch requested cancellation and network/OAuth exceptions, log only book ID plus exception, and return false. Never log credentials, tokens, client secrets, or remote bodies.

- [ ] **Step 6: Implement coalescing and final flush**

Use a private lock for pending/book/debounce fields and a `SemaphoreSlim` for export single-flight. `ScheduleExport` sets pending and starts exactly one 30-second delay. After delay, consume pending inside the semaphore and call `ExportToTtu`; if another change arrived while exporting, consume it in one follow-up pass. `FlushAsync` cancels/awaits the delay, then runs the same pending loop. `Cancel` prevents later delayed work without changing data.

```csharp
private static readonly TimeSpan ExportDelay = TimeSpan.FromSeconds(30);
private static Task DefaultDelay(TimeSpan delay, CancellationToken ct) =>
    Task.Delay(delay, ct);

public void ScheduleExport(NovelBook book)
{
    if (!CanAutoSync()) return;
    lock (_stateGate)
    {
        if (_cancelled) return;
        _pending = true;
        _pendingBook = book;
        if (_debounceTask != null) return;
        _debounceCts = new CancellationTokenSource();
        _debounceTask = RunDebounceAsync(_debounceCts.Token);
    }
}

private async Task RunDebounceAsync(CancellationToken ct)
{
    try
    {
        await _delayAsync(ExportDelay, ct);
        await RunPendingExportsAsync(CancellationToken.None);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    finally
    {
        lock (_stateGate)
        {
            _debounceCts?.Dispose();
            _debounceCts = null;
            _debounceTask = null;
        }
    }
}

private async Task RunPendingExportsAsync(CancellationToken ct)
{
    await _exportGate.WaitAsync(ct);
    try
    {
        while (true)
        {
            NovelBook? book;
            lock (_stateGate)
            {
                if (!_pending || _cancelled) return;
                _pending = false;
                book = _pendingBook;
            }
            if (book == null) return;
            try
            {
                await _ttuSyncService.SyncBookAsync(
                    book,
                    CreateOptions(TtuSyncDirection.ExportToTtu, importOnly: false),
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reader export sync failed for {BookId}", book.Id);
            }
        }
    }
    finally
    {
        _exportGate.Release();
    }
}

public async Task FlushAsync(NovelBook book, CancellationToken ct = default)
{
    if (!CanAutoSync()) return;
    Task? debounceTask;
    lock (_stateGate)
    {
        if (_cancelled) return;
        _pending = true;
        _pendingBook = book;
        debounceTask = _debounceTask;
        _debounceCts?.Cancel();
    }
    if (debounceTask != null)
    {
        try { await debounceTask; }
        catch (OperationCanceledException) { }
    }
    await RunPendingExportsAsync(ct);
}

public void Cancel()
{
    lock (_stateGate)
    {
        _cancelled = true;
        _pending = false;
        _pendingBook = null;
        _debounceCts?.Cancel();
    }
}
```

Catch each export exception, log safely, and continue only if a new pending change exists.

- [ ] **Step 7: Register, verify GREEN, and commit**

Register:

```csharp
services.AddTransient<IReaderAutoSyncCoordinator, ReaderAutoSyncCoordinator>();
```

Then run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ReaderAutoSyncCoordinatorTests|FullyQualifiedName~TtuSyncServiceTests"
git add Niratan/Services/Sync/IReaderAutoSyncCoordinator.cs Niratan/Services/Sync/ReaderAutoSyncCoordinator.cs Niratan.Tests/Services/Sync/ReaderAutoSyncCoordinatorTests.cs Niratan/App.xaml.cs
git commit -m "feat(sync): add reader auto-sync coordinator"
```

Expected: tests PASS without a real Drive request.

---

### Task 6: Integrate auto-sync with Reader state and lifecycle

**Files:**
- Modify: `Niratan/ViewModels/Pages/NovelReaderPageViewModel.cs`
- Modify: `Niratan/Views/Pages/NovelReaderPage.xaml.cs`
- Modify: `Niratan.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs`
- Modify: `Niratan.Tests/Views/Pages/NovelReaderStatisticsLifecycleTests.cs`

**Interfaces:**
- Consumes: `IReaderAutoSyncCoordinator` from Task 5.
- Produces: open-state reload, export scheduling after successful saves, awaited final sync after local checkpoint.

- [ ] **Step 1: Write failing open-import reload test**

```csharp
[Fact]
public async Task InitializeAsync_WhenOpenSyncImports_ReloadsBookBeforeRestore()
{
    var local = new NovelBook { Id = "book-1", Progress = 0.10, CurrentChapter = 0 };
    var imported = new NovelBook { Id = "book-1", Progress = 0.65, CurrentChapter = 2 };
    var library = new Mock<INovelLibraryService>();
    library.SetupSequence(service => service.GetNovelBookAsync(
            "book-1", It.IsAny<CancellationToken>()))
        .ReturnsAsync(Result<NovelBook?>.Success(local))
        .ReturnsAsync(Result<NovelBook?>.Success(imported));
    library.Setup(service => service.MarkOpenedAsync(
            "book-1", It.IsAny<CancellationToken>()))
        .ReturnsAsync(Result.Success());
    var autoSync = new Mock<IReaderAutoSyncCoordinator>();
    autoSync.Setup(service => service.ImportOnOpenAsync(
            local, It.IsAny<CancellationToken>()))
        .ReturnsAsync(true);

    var settings = new Mock<ISettingsService>();
    settings.SetupGet(service => service.Current).Returns(new AppSettings());
    var sut = new NovelReaderPageViewModel(
        library.Object,
        Mock.Of<INotificationService>(),
        new FakeMessenger(),
        new ReaderHighlightService(),
        new NovelBookSidecarService(),
        new FakeReaderStatisticsSession(),
        new NoOpProfileRuntimeService(ContentLanguageProfile.Japanese),
        settings.Object,
        autoSync.Object);
    await sut.InitializeAsync(
        new NovelReaderNavigationArgs("book-1"),
        TestContext.Current.CancellationToken);

    sut.CurrentBook.Should().BeSameAs(imported);
    autoSync.VerifyAll();
}
```

- [ ] **Step 2: Write failing save/lifecycle ordering tests**

Verify `ScheduleExport(CurrentBook)` occurs only after a successful `SaveProgressAsync`. Record close calls and assert:

```csharp
events.Should().Equal(
    "save-bookmark",
    "checkpoint-Close",
    "schedule-export",
    "flush-export");
```

Call close twice concurrently and assert one final flush. Preserve one Background and one later Close boundary.

- [ ] **Step 3: Verify RED**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderPageViewModelTests|FullyQualifiedName~NovelReaderStatisticsLifecycleTests"
```

Expected: FAIL because the ViewModel does not consume the coordinator.

- [ ] **Step 4: Import before final restore/baseline**

Inject `IReaderAutoSyncCoordinator`. After profile activation and before `MarkOpenedAsync`, call `ImportOnOpenAsync`. When it returns true, call `GetNovelBookAsync` again and replace `CurrentBook` only on a successful non-null result. Notify `ReaderTitle` after the final book is chosen. Existing page initialization then loads imported statistics and restores imported bookmark progress before `On` autostart.

- [ ] **Step 5: Schedule only after successful bookmark writes**

Add `bool scheduleAutoSync = true` to `SaveProgressNowAsync` after `flushStatistics`. In immediate and debounced save paths, inspect the `Result` from `SaveProgressAsync`. On success, and only when scheduling is enabled:

```csharp
if (scheduleAutoSync)
    _readerAutoSyncCoordinator.ScheduleExport(CurrentBook);
_messenger.Send(new NovelLibraryChangedMessage());
```

On failure, show the existing notification error and do not schedule. Preserve `flushStatistics` behavior.

- [ ] **Step 6: Await ordered final sync**

Close/background call `SaveProgressNowAsync(flushStatistics: false, scheduleAutoSync: false, ct: ct)`, checkpoint statistics, then mark/flush once:

```csharp
if (CurrentBook != null)
{
    _readerAutoSyncCoordinator.ScheduleExport(CurrentBook);
    await _readerAutoSyncCoordinator.FlushAsync(CurrentBook, ct);
}
```

After the close-only flush succeeds or is safely contained, call `_readerAutoSyncCoordinator.Cancel()`; do not cancel after Background because the Reader may resume. Keep `_lifecycleCheckpointLock` and `_lifecycleCloseTask`. Explicit Back and app close await the task; `OnNavigatedFrom` remains fallback only.

- [ ] **Step 7: Verify GREEN and commit**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderPageViewModelTests|FullyQualifiedName~NovelReaderStatisticsLifecycleTests|FullyQualifiedName~ReaderStatisticsSessionTests|FullyQualifiedName~ReaderAutoSyncCoordinatorTests|FullyQualifiedName~TtuSyncServiceTests"
git add Niratan/ViewModels/Pages/NovelReaderPageViewModel.cs Niratan/Views/Pages/NovelReaderPage.xaml.cs Niratan.Tests/ViewModels/Pages/NovelReaderPageViewModelTests.cs Niratan.Tests/Views/Pages/NovelReaderStatisticsLifecycleTests.cs
git commit -m "feat(statistics): sync reader progress lifecycle"
```

Expected: all targeted lifecycle tests PASS.

---

### Task 7: Verify complete Niratan-aligned behavior

**Files:**
- Modify: `docs/VERIFICATION.md`
- Modify: `docs/CHANGELOG.md`

**Interfaces:**
- Consumes: Tasks 1-6.
- Produces: automated/runtime evidence and concise maintenance documentation.

- [ ] **Step 1: Run formatting and diff checks**

```powershell
dotnet format Niratan.sln --verify-no-changes
git diff --check
```

Expected: exit 0. If files changed by this feature need formatting, run `dotnet format Niratan.sln`, inspect the diff, then rerun verification.

- [ ] **Step 2: Run targeted statistics/sync tests**

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Statistics|FullyQualifiedName~TtuSync|FullyQualifiedName~GoogleDrive|FullyQualifiedName~NovelReaderWebAssetTests"
```

Expected: zero failed tests.

- [ ] **Step 3: Run full x64 build and tests**

```powershell
dotnet build -p:Platform=x64
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64
```

Expected: PASS without new warnings.

- [ ] **Step 4: Launch and verify same-chapter statistics**

```powershell
.\build-and-run.ps1
```

Confirm a responsive window. Open `C:\Users\Wight\Downloads\哈利波特1魔法石.epub`, select Page Turn autostart, turn one same-chapter page, and verify before crossing a chapter: progress changes; current character changes; Session/Today change; `statistics.json` changes; diagnostics keep `pageIndex`, `pageCount`, `progress`, and `scrollPosition` consistent.

Also exercise backward movement, continuous mode, adjacent chapters, first/last limits, resize/reflow, reopen, and programmatic chapter/search/highlight/history/internal-link/Sasayaki jumps.

- [ ] **Step 5: Verify the compact panel**

Verify approximately 520x560 content, Session/Today/All Time, start/stop, remaining time, one scroll owner, keyboard, pointer/touch, 200% text scaling, light/dark/high contrast, Japanese characters, and English approximate words.

- [ ] **Step 6: Verify settings and remote safety**

Locally verify that global Sync hides/deactivates statistics Sync while preserving its values. Coordinator tests are required evidence for remote calls. Perform live Drive import/export only after the user explicitly confirms a connected test account/book may be modified; otherwise report that real remote mutation was not exercised.

- [ ] **Step 7: Update maintenance documentation**

Add same-chapter regression and Reader auto-sync checks to `docs/VERIFICATION.md`. Add one `docs/CHANGELOG.md` entry with root cause (`scrolled` vs `moved`) and typed-contract solution; do not add an implementation diary.

- [ ] **Step 8: Commit docs and leave app running**

```powershell
git add docs/VERIFICATION.md docs/CHANGELOG.md
git commit -m "docs: record reader statistics verification"
.\build-and-run.ps1
```

Confirm the final top-level window is responsive and leave it running unless the user asks otherwise.

## Plan Self-Review Results

- Spec coverage: Tasks 1-2 cover typed movement and statistics boundaries; Task 3 covers the compact language-aware panel; Task 4 covers settings interaction; Tasks 5-6 cover open/debounce/single-flight/final sync; Task 7 covers acceptance evidence.
- Scope: the Bookshelf statistics dashboard and TTU schema remain unchanged.
- Type consistency: `ReaderPageNavigationEvent`, `ReaderPageNavigationOutcome`, and `IReaderAutoSyncCoordinator` use identical signatures across producers and consumers.
- Dependency consistency: the coordinator wraps existing services, is transient, and adds no package.
