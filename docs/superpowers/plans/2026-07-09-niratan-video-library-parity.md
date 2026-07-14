# Niratan Video Library Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Align the Windows video library with Niratan's list/poster browsing, cached thumbnails, smart collections, and expanded library navigation.

**Architecture:** Keep the existing WinUI MVVM layering. `VideoLibraryPage.xaml` renders the secondary navigation, segmented layout switcher, list view, poster view, and smart collection dialog. `VideoLibraryPageViewModel` owns UI state and filtering. `IVideoLibraryService`, `IDataService`, and the new thumbnail service handle persistence, filesystem scanning, collection storage, and generated thumbnails.

**Tech Stack:** WinUI 3 XAML, CommunityToolkit.Mvvm, SQLite/Dapper, xUnit v3, FluentAssertions, Moq, existing libmpv wrapper.

## Global Constraints

- Work in `D:\CODE\Yukari\.worktrees\niratan-video-library-min`.
- Do not modify `native/hoshidicts/`.
- Do not put business logic in XAML code-behind.
- Do not let ViewModels access SQLite directly.
- Do not add network metadata lookup, Python, Node, ML, TMDb, TVDb, AniDB, or bundled anime rules.
- Keep video organization virtual: never move, rename, delete, or rewrite user video files.
- All new visible text needs English and Simplified Chinese resources.
- Thumbnail generation must be asynchronous, single-worker, deduplicated, and suspended while a player window is open.
- Build with `dotnet build -p:Platform=x64`.
- Test with `dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64`.

---

## File Structure

- `Niratan/Models/Video/VideoLibraryLayoutMode.cs`: layout mode enum for List/Posters.
- `Niratan/Models/Video/VideoCollection.cs`: collection, smart rule, and smart rule enum models.
- `Niratan/Models/Video/VideoLibraryView.cs`: expanded Niratan-style library modes.
- `Niratan/Models/VideoItem.cs`: library metadata columns surfaced to the UI.
- `Niratan/Services/Storage/Migrations/Migration_012.cs`: thumbnail, favorite, file metadata, and collection tables.
- `Niratan/Services/Storage/DatabaseMigrator.cs`: register migration 12.
- `Niratan/Services/Storage/IDataService.cs`: storage contract for video metadata and collections.
- `Niratan/Services/Storage/DataService.cs`: Dapper implementation for new schema.
- `Niratan/Services/Video/VideoSmartCollectionMatcher.cs`: pure smart-rule evaluator.
- `Niratan/Services/Video/IVideoThumbnailService.cs`: thumbnail service contract.
- `Niratan/Services/Video/VideoThumbnailService.cs`: cache, scheduler, and persistence coordinator.
- `Niratan/Services/Video/IVideoLibraryService.cs`: library contract for collections and favorite state.
- `Niratan/Services/Video/VideoLibraryService.cs`: collection and metadata commands.
- `Niratan/Services/Video/LibMpvVideoMiningMediaExtractor.cs`: implement screenshot capture for thumbnail generation.
- `Niratan/Services/Video/IVideoPlayerWindowService.cs`: expose player-window open state events.
- `Niratan/Services/Video/VideoPlayerWindowService.cs`: suspend/resume thumbnail work by window state.
- `Niratan/App.xaml.cs`: register `IVideoThumbnailService`.
- `Niratan/ViewModels/Components/VideoItemViewModel.cs`: list/poster display metadata and artwork path.
- `Niratan/ViewModels/Pages/VideoLibraryPageViewModel.cs`: layout state, expanded filters, smart collections, thumbnail queue.
- `Niratan/Views/Pages/VideoLibraryPage.xaml`: list/poster templates, segmented control, smart collection dialog trigger.
- `Niratan/Views/Pages/VideoLibraryPage.xaml.cs`: UI-only click handlers for `ListView` and existing grid buttons.
- `Niratan/Strings/en-US/Resources.resw`: English strings.
- `Niratan/Strings/zh-CN/Resources.resw`: Simplified Chinese strings.
- `Niratan.Tests/Services/Storage/VideoDataServiceTests.cs`: migration and CRUD coverage.
- `Niratan.Tests/Services/Video/VideoLibraryServiceTests.cs`: service delegation and smart collection creation.
- `Niratan.Tests/Services/Video/VideoSmartCollectionMatcherTests.cs`: pure smart rule tests.
- `Niratan.Tests/Services/Video/VideoThumbnailServiceTests.cs`: cache/scheduler behavior.
- `Niratan.Tests/ViewModels/Pages/VideoLibraryPageViewModelTests.cs`: UI state and filtering.
- `Niratan.Tests/Views/Pages/VideoLibraryPageAssetTests.cs`: XAML/localization contract.

---

### Task 1: Domain Models And Storage

**Files:**
- Create: `Niratan/Models/Video/VideoLibraryLayoutMode.cs`
- Create: `Niratan/Models/Video/VideoCollection.cs`
- Create: `Niratan/Services/Storage/Migrations/Migration_012.cs`
- Modify: `Niratan/Models/VideoItem.cs`
- Modify: `Niratan/Models/Video/VideoLibraryView.cs`
- Modify: `Niratan/Services/Storage/DatabaseMigrator.cs`
- Modify: `Niratan/Services/Storage/IDataService.cs`
- Modify: `Niratan/Services/Storage/DataService.cs`
- Test: `Niratan.Tests/Services/Storage/VideoDataServiceTests.cs`

**Interfaces:**
- Produces: `VideoLibraryLayoutMode`, `VideoCollection`, `VideoCollectionKind`, `VideoSmartRule`, `VideoSmartRuleField`, `VideoSmartRuleMatch`.
- Produces: `IDataService.GetVideoCollectionsAsync`, `UpsertVideoCollectionAsync`, `DeleteVideoCollectionAsync`, `SetVideoCollectionItemsAsync`, `UpdateVideoThumbnailPathAsync`, `UpdateVideoFavoriteAsync`.
- Consumes: existing `VideoItem` storage and migrations 8-11.

- [ ] **Step 1: Write failing storage migration and CRUD tests**

Add these tests to `Niratan.Tests/Services/Storage/VideoDataServiceTests.cs`:

```csharp
[Fact]
public async Task Migration012_AddsVideoCollectionAndThumbnailSchema()
{
    var ct = TestContext.Current.CancellationToken;
    await using var connection = new SqliteConnection("Data Source=:memory:");
    await connection.OpenAsync(ct);
    await using var transaction = await connection.BeginTransactionAsync(ct);

    await InvokeMigrationAsync("Migration_008", connection, transaction);
    await InvokeMigrationAsync("Migration_009", connection, transaction);
    await InvokeMigrationAsync("Migration_010", connection, transaction);
    await InvokeMigrationAsync("Migration_011", connection, transaction);
    await InvokeMigrationAsync("Migration_012", connection, transaction);
    await transaction.CommitAsync(ct);

    var command = connection.CreateCommand();
    command.CommandText = """
        SELECT name
        FROM pragma_table_info('VideoItems')
        WHERE name IN ('FileSizeBytes', 'ModifiedAt', 'ThumbnailPath', 'IsFavorite')
        ORDER BY name;
        """;

    var columns = new List<string>();
    await using (var reader = await command.ExecuteReaderAsync(ct))
    {
        while (await reader.ReadAsync(ct))
            columns.Add(reader.GetString(0));
    }

    columns.Should().Equal("FileSizeBytes", "IsFavorite", "ModifiedAt", "ThumbnailPath");

    command.CommandText = """
        SELECT name
        FROM sqlite_master
        WHERE type = 'table'
          AND name IN ('VideoCollections', 'VideoCollectionItems')
        ORDER BY name;
        """;

    var tables = new List<string>();
    await using (var reader = await command.ExecuteReaderAsync(ct))
    {
        while (await reader.ReadAsync(ct))
            tables.Add(reader.GetString(0));
    }

    tables.Should().Equal("VideoCollectionItems", "VideoCollections");
}
```

Add this CRUD test after migration tests:

```csharp
[Fact]
public async Task DataService_PersistsVideoCollectionsAndMembership()
{
    var ct = TestContext.Current.CancellationToken;
    var dbPath = Path.Combine(Path.GetTempPath(), $"niratan-video-{Guid.NewGuid():N}.db");
    try
    {
        var connectionString = $"Data Source={dbPath}";
        await new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance, connectionString).MigrateAsync();
        var service = new DataService(connectionString);

        var video = new VideoItem
        {
            Id = "video-1",
            Title = "Episode 1",
            FilePath = @"D:\Anime\Episode 1.mkv",
            ImportedAt = DateTime.UtcNow,
        };
        await service.UpsertVideoAsync(video, ct);

        var collection = new VideoCollection
        {
            Id = "collection-1",
            Name = "Umaru",
            Kind = VideoCollectionKind.Manual,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await service.UpsertVideoCollectionAsync(collection, ct);
        await service.SetVideoCollectionItemsAsync(collection.Id, ["video-1"], ct);

        var collections = await service.GetVideoCollectionsAsync(ct);

        collections.Should().ContainSingle();
        collections[0].ItemIds.Should().Equal("video-1");
    }
    finally
    {
        if (File.Exists(dbPath))
            File.Delete(dbPath);
    }
}
```

- [ ] **Step 2: Run storage tests to verify they fail**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoDataServiceTests"
```

Expected: fails because `Migration_012`, `VideoCollection`, collection data-service methods, and metadata columns do not exist.

- [ ] **Step 3: Add models and migration**

Create `Niratan/Models/Video/VideoLibraryLayoutMode.cs`:

```csharp
namespace Niratan.Models.Video;

public enum VideoLibraryLayoutMode
{
    List,
    Posters,
}
```

Replace `Niratan/Models/Video/VideoLibraryView.cs` with:

```csharp
namespace Niratan.Models.Video;

public enum VideoLibraryView
{
    ContinueWatching,
    Unwatched,
    Finished,
    Recent,
    All,
    NeedsReview,
    Favorites,
    Series,
    Collections,
    Folders,
    Tags,
}
```

Create `Niratan/Models/Video/VideoCollection.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Niratan.Models.Video;

public enum VideoCollectionKind
{
    Manual = 0,
    Smart = 1,
}

public enum VideoSmartRuleField
{
    FileName,
    ParentFolder,
    Path,
    Tag,
    HasBoundSubtitle,
    PlaybackState,
}

public enum VideoSmartRuleMatch
{
    Contains,
    Equals,
    IsTrue,
}

public sealed class VideoSmartRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public VideoSmartRuleField Field { get; set; }
    public VideoSmartRuleMatch Match { get; set; } = VideoSmartRuleMatch.Contains;
    public string Value { get; set; } = string.Empty;
}

public sealed class VideoCollection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public VideoCollectionKind Kind { get; set; }
    public string? RuleJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int ManualSortOrder { get; set; }
    public IReadOnlyList<string> ItemIds { get; set; } = [];
    public IReadOnlyList<VideoSmartRule> SmartRules { get; set; } = [];
}
```

Add these properties to `Niratan/Models/VideoItem.cs` after `ManualSortOrder`:

```csharp
public long FileSizeBytes { get; set; }
public DateTime? ModifiedAt { get; set; }
public string? ThumbnailPath { get; set; }
public bool IsFavorite { get; set; }
```

Create `Niratan/Services/Storage/Migrations/Migration_012.cs` with version 12:

```csharp
using System.Data.Common;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Niratan.Services.Storage.Migrations;

internal sealed class Migration_012 : IMigration
{
    public int Version => 12;
    public string Description => "Add video collection and thumbnail metadata";

    public async Task UpAsync(SqliteConnection connection, DbTransaction transaction)
    {
        await AddColumnIfMissingAsync(connection, transaction, "VideoItems", "FileSizeBytes", "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(connection, transaction, "VideoItems", "ModifiedAt", "TEXT");
        await AddColumnIfMissingAsync(connection, transaction, "VideoItems", "ThumbnailPath", "TEXT");
        await AddColumnIfMissingAsync(connection, transaction, "VideoItems", "IsFavorite", "INTEGER NOT NULL DEFAULT 0");

        await connection.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS VideoCollections (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Kind INTEGER NOT NULL,
                RuleJson TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                ManualSortOrder INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS VideoCollectionItems (
                CollectionId TEXT NOT NULL,
                VideoId TEXT NOT NULL,
                ManualSortOrder INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (CollectionId, VideoId),
                FOREIGN KEY (CollectionId) REFERENCES VideoCollections(Id) ON DELETE CASCADE,
                FOREIGN KEY (VideoId) REFERENCES VideoItems(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_VideoCollections_Kind
                ON VideoCollections (Kind);

            CREATE INDEX IF NOT EXISTS IX_VideoCollectionItems_VideoId
                ON VideoCollectionItems (VideoId);

            CREATE INDEX IF NOT EXISTS IX_VideoItems_IsFavorite
                ON VideoItems (IsFavorite);
            """,
            transaction: transaction);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        var exists = await connection.ExecuteScalarAsync<long>(
            $"""
            SELECT COUNT(*)
            FROM pragma_table_info('{tableName}')
            WHERE name = @ColumnName;
            """,
            new { ColumnName = columnName },
            transaction);

        if (exists == 0)
            await connection.ExecuteAsync($"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};", transaction: transaction);
    }
}
```

Register `new Migration_012()` after `new Migration_011()` in `DatabaseMigrator.AllMigrations`.

- [ ] **Step 4: Add data-service contract and implementation**

Add to `Niratan/Services/Storage/IDataService.cs`:

```csharp
Task<IReadOnlyList<VideoCollection>> GetVideoCollectionsAsync(CancellationToken ct = default);
Task UpsertVideoCollectionAsync(VideoCollection collection, CancellationToken ct = default);
Task DeleteVideoCollectionAsync(string collectionId, CancellationToken ct = default);
Task SetVideoCollectionItemsAsync(
    string collectionId,
    IReadOnlyList<string> videoIds,
    CancellationToken ct = default);
Task UpdateVideoThumbnailPathAsync(string videoId, string? thumbnailPath, CancellationToken ct = default);
Task UpdateVideoFavoriteAsync(string videoId, bool isFavorite, CancellationToken ct = default);
```

Change the top of `DataService` so tests can use an isolated SQLite file:

```csharp
private readonly string _connectionString;

public DataService()
    : this($"Data Source={Path.Combine(AppDataHelper.GetDataPath(), "niratan.db")}")
{
}

internal DataService(string connectionString)
{
    _connectionString = connectionString;
}
```

Update `DataService.GetVideosAsync`, `GetVideoAsync`, and `UpsertVideoAsync` SQL to include `FileSizeBytes`, `ModifiedAt`, `ThumbnailPath`, and `IsFavorite`. Keep `CollectionName` during this increment so existing data still loads.

Add collection CRUD methods to `DataService.cs`. Use `System.Text.Json` to serialize `VideoCollection.SmartRules` into `RuleJson` and deserialize it in `GetVideoCollectionsAsync`. Load membership with:

```sql
SELECT CollectionId, VideoId
FROM VideoCollectionItems
ORDER BY ManualSortOrder, VideoId;
```

Use a transaction in `SetVideoCollectionItemsAsync`: delete existing rows for the collection, then insert each video id with its list index as `ManualSortOrder`.

- [ ] **Step 5: Run storage tests to verify they pass**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoDataServiceTests"
```

Expected: PASS.

- [ ] **Step 6: Commit Task 1**

```powershell
git add Niratan/Models/Video/VideoLibraryLayoutMode.cs Niratan/Models/Video/VideoCollection.cs Niratan/Models/Video/VideoLibraryView.cs Niratan/Models/VideoItem.cs Niratan/Services/Storage/Migrations/Migration_012.cs Niratan/Services/Storage/DatabaseMigrator.cs Niratan/Services/Storage/IDataService.cs Niratan/Services/Storage/DataService.cs Niratan.Tests/Services/Storage/VideoDataServiceTests.cs
git commit -m "feat: add video collection storage"
```

---

### Task 2: Smart Collection Matching And Library Service

**Files:**
- Create: `Niratan/Services/Video/VideoSmartCollectionMatcher.cs`
- Modify: `Niratan/Services/Video/IVideoLibraryService.cs`
- Modify: `Niratan/Services/Video/VideoLibraryService.cs`
- Test: `Niratan.Tests/Services/Video/VideoSmartCollectionMatcherTests.cs`
- Test: `Niratan.Tests/Services/Video/VideoLibraryServiceTests.cs`

**Interfaces:**
- Consumes: `VideoCollection`, `VideoSmartRule`, `IDataService` collection methods from Task 1.
- Produces: smart rule evaluator and service methods used by `VideoLibraryPageViewModel`.

- [ ] **Step 1: Write failing matcher tests**

Create `Niratan.Tests/Services/Video/VideoSmartCollectionMatcherTests.cs`:

```csharp
using FluentAssertions;
using Niratan.Models;
using Niratan.Models.Video;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public class VideoSmartCollectionMatcherTests
{
    [Fact]
    public void Matches_AllRulesAgainstVideoMetadata()
    {
        var video = new VideoItem
        {
            Title = "Episode 12",
            FilePath = @"D:\Anime\Umaru\Season 1\Episode 12.mkv",
            SourceFolderPath = @"D:\Anime\Umaru\Season 1",
            Tags = "anime, comedy",
            SubtitlePath = @"D:\Anime\Umaru\Season 1\Episode 12.ass",
            LastPositionSeconds = 40,
            DurationSeconds = 100,
        };
        var rules = new[]
        {
            new VideoSmartRule { Field = VideoSmartRuleField.FileName, Match = VideoSmartRuleMatch.Contains, Value = "episode" },
            new VideoSmartRule { Field = VideoSmartRuleField.Tag, Match = VideoSmartRuleMatch.Contains, Value = "anime" },
            new VideoSmartRule { Field = VideoSmartRuleField.HasBoundSubtitle, Match = VideoSmartRuleMatch.IsTrue },
            new VideoSmartRule { Field = VideoSmartRuleField.PlaybackState, Match = VideoSmartRuleMatch.Equals, Value = "inProgress" },
        };

        VideoSmartCollectionMatcher.Matches(video, rules).Should().BeTrue();
    }

    [Fact]
    public void Matches_ReturnsFalseForEmptyRules()
    {
        VideoSmartCollectionMatcher.Matches(new VideoItem(), []).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Write failing service tests**

Add to `Niratan.Tests/Services/Video/VideoLibraryServiceTests.cs`:

```csharp
[Fact]
public async Task CreateSmartCollectionAsync_PersistsNormalizedSmartCollection()
{
    var ct = TestContext.Current.CancellationToken;
    var data = new Mock<IDataService>();
    VideoCollection? saved = null;
    data.Setup(service => service.UpsertVideoCollectionAsync(It.IsAny<VideoCollection>(), It.IsAny<CancellationToken>()))
        .Callback<VideoCollection, CancellationToken>((collection, _) => saved = collection)
        .Returns(Task.CompletedTask);
    var sut = new VideoLibraryService(data.Object, NullLogger<VideoLibraryService>.Instance);

    var result = await sut.CreateSmartCollectionAsync(
        "  Umaru  ",
        [new VideoSmartRule { Field = VideoSmartRuleField.FileName, Value = "umaru" }],
        ct);

    result.IsSuccess.Should().BeTrue();
    saved.Should().NotBeNull();
    saved!.Name.Should().Be("Umaru");
    saved.Kind.Should().Be(VideoCollectionKind.Smart);
    saved.SmartRules.Should().ContainSingle()
        .Which.Value.Should().Be("umaru");
}

[Fact]
public async Task SetFavoriteAsync_DelegatesToDataService()
{
    var ct = TestContext.Current.CancellationToken;
    var data = new Mock<IDataService>();
    data.Setup(service => service.UpdateVideoFavoriteAsync("video-1", true, It.IsAny<CancellationToken>()))
        .Returns(Task.CompletedTask);
    var sut = new VideoLibraryService(data.Object, NullLogger<VideoLibraryService>.Instance);

    var result = await sut.SetFavoriteAsync("video-1", true, ct);

    result.IsSuccess.Should().BeTrue();
    data.Verify(service => service.UpdateVideoFavoriteAsync("video-1", true, It.IsAny<CancellationToken>()), Times.Once);
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSmartCollectionMatcherTests|FullyQualifiedName~VideoLibraryServiceTests"
```

Expected: fails because matcher and service methods do not exist.

- [ ] **Step 4: Implement matcher**

Create `Niratan/Services/Video/VideoSmartCollectionMatcher.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Niratan.Models;
using Niratan.Models.Video;

namespace Niratan.Services.Video;

public static class VideoSmartCollectionMatcher
{
    public static bool Matches(VideoItem video, IReadOnlyList<VideoSmartRule> rules)
    {
        if (rules.Count == 0)
            return false;

        return rules.All(rule => MatchesRule(video, rule));
    }

    private static bool MatchesRule(VideoItem video, VideoSmartRule rule) =>
        rule.Field switch
        {
            VideoSmartRuleField.FileName => MatchesText(rule, video.Title, Path.GetFileNameWithoutExtension(video.FilePath)),
            VideoSmartRuleField.ParentFolder => MatchesText(rule, Path.GetFileName(video.SourceFolderPath ?? ""), video.SourceFolderPath ?? ""),
            VideoSmartRuleField.Path => MatchesText(rule, video.FilePath),
            VideoSmartRuleField.Tag => MatchesText(rule, SplitTags(video.Tags).ToArray()),
            VideoSmartRuleField.HasBoundSubtitle => MatchesBool(rule, !string.IsNullOrWhiteSpace(video.SubtitlePath) || !string.IsNullOrWhiteSpace(video.SubtitleSelectionPath)),
            VideoSmartRuleField.PlaybackState => MatchesText(rule, PlaybackTokens(video)),
            _ => false,
        };

    private static bool MatchesText(VideoSmartRule rule, params string?[] values)
    {
        var needle = rule.Value.Trim();
        if (needle.Length == 0)
            return false;

        return rule.Match switch
        {
            VideoSmartRuleMatch.Contains => values.Any(value => Contains(value, needle)),
            VideoSmartRuleMatch.Equals => values.Any(value => string.Equals(value?.Trim(), needle, StringComparison.CurrentCultureIgnoreCase)),
            _ => false,
        };
    }

    private static bool MatchesBool(VideoSmartRule rule, bool value) =>
        rule.Match switch
        {
            VideoSmartRuleMatch.IsTrue => value,
            VideoSmartRuleMatch.Equals => value == IsTruthy(rule.Value),
            _ => false,
        };

    private static string[] PlaybackTokens(VideoItem video)
    {
        if (video.IsWatched)
            return ["finished", "watched", "played"];
        if (video.DurationSeconds <= 0 || video.LastPositionSeconds < VideoPlaybackState.MinimumPersistablePositionSeconds)
            return ["unwatched"];
        return ["inProgress", "in progress", "resumable", "started"];
    }

    private static bool Contains(string? value, string needle) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(needle, StringComparison.CurrentCultureIgnoreCase);

    private static bool IsTruthy(string value) =>
        value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)
        || value.Trim() == "1";

    private static IReadOnlyList<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
```

- [ ] **Step 5: Extend library service**

Add to `IVideoLibraryService.cs`:

```csharp
Task<Result<IReadOnlyList<VideoCollection>>> GetCollectionsAsync(CancellationToken ct = default);
Task<Result<VideoCollection>> CreateManualCollectionAsync(
    string name,
    IReadOnlyList<string> videoIds,
    CancellationToken ct = default);
Task<Result<VideoCollection>> CreateSmartCollectionAsync(
    string name,
    IReadOnlyList<VideoSmartRule> rules,
    CancellationToken ct = default);
Task<Result> DeleteCollectionAsync(string collectionId, CancellationToken ct = default);
Task<Result> SetFavoriteAsync(string videoId, bool isFavorite, CancellationToken ct = default);
```

Implement these methods in `VideoLibraryService.cs`. Normalize empty collection names to the localized fallback key in the ViewModel, so the service can use the stable English fallback `"Untitled Collection"` when no resource context is present. Trim rule values and drop empty smart rules before saving. Use `ExecuteAsync` for cancellation and error handling.

- [ ] **Step 6: Run service and matcher tests**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoSmartCollectionMatcherTests|FullyQualifiedName~VideoLibraryServiceTests"
```

Expected: PASS.

- [ ] **Step 7: Commit Task 2**

```powershell
git add Niratan/Services/Video/VideoSmartCollectionMatcher.cs Niratan/Services/Video/IVideoLibraryService.cs Niratan/Services/Video/VideoLibraryService.cs Niratan.Tests/Services/Video/VideoSmartCollectionMatcherTests.cs Niratan.Tests/Services/Video/VideoLibraryServiceTests.cs
git commit -m "feat: add video smart collections"
```

---

### Task 3: Thumbnail Service And Playback Suspension

**Files:**
- Create: `Niratan/Services/Video/IVideoThumbnailService.cs`
- Create: `Niratan/Services/Video/VideoThumbnailService.cs`
- Modify: `Niratan/Services/Video/LibMpvVideoMiningMediaExtractor.cs`
- Modify: `Niratan/Services/Video/IVideoPlayerWindowService.cs`
- Modify: `Niratan/Services/Video/VideoPlayerWindowService.cs`
- Modify: `Niratan/App.xaml.cs`
- Test: `Niratan.Tests/Services/Video/VideoThumbnailServiceTests.cs`

**Interfaces:**
- Consumes: `IVideoMiningMediaExtractor.CaptureScreenshotAsync` and `IDataService.UpdateVideoThumbnailPathAsync`.
- Produces: `IVideoThumbnailService.EnsureThumbnailAsync`, `Suspend`, `Resume`.

- [ ] **Step 1: Write failing thumbnail service tests**

Create `Niratan.Tests/Services/Video/VideoThumbnailServiceTests.cs`:

```csharp
using FluentAssertions;
using Niratan.Models;
using Niratan.Services.Storage;
using Niratan.Services.Video;
using Moq;

namespace Niratan.Tests.Services.Video;

public class VideoThumbnailServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"niratan-thumbs-{Guid.NewGuid():N}");

    public VideoThumbnailServiceTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task EnsureThumbnailAsync_ReturnsExistingPosterWithoutGenerating()
    {
        var ct = TestContext.Current.CancellationToken;
        var poster = Touch(Path.Combine(_directory, "poster.jpg"));
        var extractor = new Mock<IVideoMiningMediaExtractor>();
        var data = new Mock<IDataService>();
        var sut = new VideoThumbnailService(extractor.Object, data.Object, _directory);

        var result = await sut.EnsureThumbnailAsync(
            new VideoItem { Id = "video-1", FilePath = @"D:\Video\a.mkv", PosterPath = poster },
            generateIfMissing: true,
            ct);

        result.Should().Be(poster);
        extractor.Verify(
            service => service.CaptureScreenshotAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnsureThumbnailAsync_DeduplicatesConcurrentGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        var video = Touch(Path.Combine(_directory, "episode.mkv"));
        var extractor = new Mock<IVideoMiningMediaExtractor>();
        extractor.Setup(service => service.CaptureScreenshotAsync(video, It.IsAny<string>(), TimeSpan.FromSeconds(5), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string outputPath, TimeSpan _, CancellationToken _) =>
            {
                File.WriteAllText(outputPath, "png");
                return outputPath;
            });
        var data = new Mock<IDataService>();
        data.Setup(service => service.UpdateVideoThumbnailPathAsync("video-1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var sut = new VideoThumbnailService(extractor.Object, data.Object, _directory);

        var item = new VideoItem { Id = "video-1", FilePath = video, FileSizeBytes = 3, ModifiedAt = File.GetLastWriteTimeUtc(video) };
        var results = await Task.WhenAll(
            sut.EnsureThumbnailAsync(item, true, ct),
            sut.EnsureThumbnailAsync(item, true, ct));

        results.Should().OnlyContain(path => path != null && File.Exists(path));
        extractor.Verify(
            service => service.CaptureScreenshotAsync(video, It.IsAny<string>(), TimeSpan.FromSeconds(5), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }
}
```

- [ ] **Step 2: Run thumbnail tests to verify they fail**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoThumbnailServiceTests"
```

Expected: fails because the thumbnail service does not exist.

- [ ] **Step 3: Implement `IVideoThumbnailService`**

Create `Niratan/Services/Video/IVideoThumbnailService.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;

namespace Niratan.Services.Video;

public interface IVideoThumbnailService
{
    Task<string?> EnsureThumbnailAsync(
        VideoItem video,
        bool generateIfMissing,
        CancellationToken ct = default);

    void Suspend();
    void Resume();
}
```

- [ ] **Step 4: Implement cache and single-worker generation**

Create `Niratan/Services/Video/VideoThumbnailService.cs`. The service should:

```csharp
private const int MaximumConcurrentJobs = 1;
private static readonly TimeSpan DefaultCaptureTime = TimeSpan.FromSeconds(5);
```

Use an in-memory `Dictionary<string, Task<string?>>` for deduplication by cache key. Build the cache key from `FilePath`, `FileSizeBytes`, and `ModifiedAt?.Ticks`. Return `PosterPath` first if it exists, then existing `ThumbnailPath`, then cached generated file. When suspended, return cached art only and skip generation.

The generated file path should be:

```csharp
Path.Combine(_cacheDirectory, $"{CacheKey(video)}.png")
```

After successful generation, call:

```csharp
await _dataService.UpdateVideoThumbnailPathAsync(video.Id, outputPath, ct);
```

Use `SemaphoreSlim(1, 1)` around generation so only one mpv capture runs at a time.

- [ ] **Step 5: Implement mpv screenshot extraction**

Update `LibMpvVideoMiningMediaExtractor.CaptureScreenshotAsync` so it no longer returns `null`. Use libmpv with `config=no`, `sid=no`, `audio=no`, `start=<timestamp>`, `pause=yes`, load the file, wait until it is ready, then call:

```csharp
MpvNative.Command(handle, "screenshot-to-file", outputPath, "video");
```

Return `outputPath` only if the file exists and has length greater than zero. On failure, log a warning and return `null`.

- [ ] **Step 6: Suspend thumbnail work while player window is open**

Add to `IVideoPlayerWindowService.cs`:

```csharp
event EventHandler? PlaybackWindowOpened;
event EventHandler? PlaybackWindowClosed;
bool IsPlaybackWindowOpen { get; }
```

Update `VideoPlayerWindowService` to raise `PlaybackWindowOpened` when a window is created and `PlaybackWindowClosed` from the `Closed` handler. Inject `IVideoThumbnailService` into the service constructor and call `Suspend()` on open and `Resume()` on close.

- [ ] **Step 7: Register thumbnail service**

Add to `Niratan/App.xaml.cs` service registration near other video services:

```csharp
services.AddSingleton<IVideoThumbnailService, VideoThumbnailService>();
```

- [ ] **Step 8: Run thumbnail tests**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoThumbnailServiceTests"
```

Expected: PASS.

- [ ] **Step 9: Commit Task 3**

```powershell
git add Niratan/Services/Video/IVideoThumbnailService.cs Niratan/Services/Video/VideoThumbnailService.cs Niratan/Services/Video/LibMpvVideoMiningMediaExtractor.cs Niratan/Services/Video/IVideoPlayerWindowService.cs Niratan/Services/Video/VideoPlayerWindowService.cs Niratan/App.xaml.cs Niratan.Tests/Services/Video/VideoThumbnailServiceTests.cs
git commit -m "feat: generate video library thumbnails"
```

---

### Task 4: ViewModel Layout, Navigation, And Smart Collection Commands

**Files:**
- Modify: `Niratan/ViewModels/Pages/VideoLibraryPageViewModel.cs`
- Modify: `Niratan/ViewModels/Components/VideoItemViewModel.cs`
- Test: `Niratan.Tests/ViewModels/Pages/VideoLibraryPageViewModelTests.cs`
- Test: `Niratan.Tests/ViewModels/Components/VideoItemViewModelTests.cs`

**Interfaces:**
- Consumes: `IVideoLibraryService` collection methods and `IVideoThumbnailService`.
- Produces: XAML-bindable `SelectedLayoutMode`, `IsListLayout`, `IsPosterLayout`, smart collection draft state, expanded navigation filters, and thumbnail-backed `VideoItemViewModel.ArtworkImage`.

- [ ] **Step 1: Write failing ViewModel tests**

Add tests to `VideoLibraryPageViewModelTests.cs`:

```csharp
[Fact]
public async Task SelectedLayoutMode_TogglesListAndPosterFlags()
{
    var sut = CreateSut();
    await sut.InitializeAsync();

    sut.SelectedLayoutMode = VideoLibraryLayoutMode.Posters;

    sut.IsPosterLayout.Should().BeTrue();
    sut.IsListLayout.Should().BeFalse();
}

[Fact]
public async Task SmartCollectionPreview_UsesAllRules()
{
    var service = new RecordingVideoLibraryService
    {
        Videos =
        [
            new VideoItem { Id = "episode", Title = "Umaru 01", FilePath = @"D:\Anime\Umaru 01.mkv", Tags = "anime" },
            new VideoItem { Id = "movie", Title = "Movie", FilePath = @"D:\Movies\Movie.mkv" },
        ],
    };
    var sut = CreateSut(videoService: service);

    await sut.InitializeAsync();
    sut.SmartCollectionNameDraft = "Umaru";
    sut.SelectedSmartRuleField = VideoSmartRuleField.FileName;
    sut.SmartRuleValueDraft = "umaru";

    sut.SmartCollectionPreviewRows.Select(row => row.Video.Id).Should().Equal("episode");
}

[Fact]
public async Task CreateSmartCollectionCommand_CreatesCollectionAndReloadsFilters()
{
    var service = new RecordingVideoLibraryService();
    var sut = CreateSut(videoService: service);
    await sut.InitializeAsync();
    sut.SmartCollectionNameDraft = "Anime";
    sut.SelectedSmartRuleField = VideoSmartRuleField.Tag;
    sut.SmartRuleValueDraft = "anime";

    await sut.CreateSmartCollectionCommand.ExecuteAsync(null);

    service.CreatedSmartCollections.Should().ContainSingle()
        .Which.Name.Should().Be("Anime");
}
```

Add a component test in `Niratan.Tests/ViewModels/Components/VideoItemViewModelTests.cs`:

```csharp
[Fact]
public void ArtworkPath_PrefersPosterThenThumbnail()
{
    new VideoItemViewModel(new VideoItem
    {
        PosterPath = @"D:\poster.jpg",
        ThumbnailPath = @"D:\thumb.png",
    }).ArtworkPath.Should().Be(@"D:\poster.jpg");

    new VideoItemViewModel(new VideoItem
    {
        ThumbnailPath = @"D:\thumb.png",
    }).ArtworkPath.Should().Be(@"D:\thumb.png");
}
```

- [ ] **Step 2: Update the recording service in tests**

Extend `RecordingVideoLibraryService` with:

```csharp
public IReadOnlyList<VideoCollection> Collections { get; init; } = [];
public List<VideoCollection> CreatedSmartCollections { get; } = [];

public Task<Result<IReadOnlyList<VideoCollection>>> GetCollectionsAsync(CancellationToken ct = default) =>
    Task.FromResult(Result<IReadOnlyList<VideoCollection>>.Success(Collections));

public Task<Result<VideoCollection>> CreateSmartCollectionAsync(
    string name,
    IReadOnlyList<VideoSmartRule> rules,
    CancellationToken ct = default)
{
    var collection = new VideoCollection
    {
        Name = name,
        Kind = VideoCollectionKind.Smart,
        SmartRules = rules,
    };
    CreatedSmartCollections.Add(collection);
    return Task.FromResult(Result<VideoCollection>.Success(collection));
}
```

Add no-op implementations for manual collection, delete collection, and favorite service methods.

- [ ] **Step 3: Run ViewModel tests to verify they fail**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoLibraryPageViewModelTests|FullyQualifiedName~VideoItemViewModelTests"
```

Expected: fails because layout mode, smart collection draft state, and artwork path are missing.

- [ ] **Step 4: Update ViewModel constructor dependencies**

Add `IVideoThumbnailService videoThumbnailService` to `VideoLibraryPageViewModel` constructor and keep it in `_thumbnailService`. Update test factory and `App.xaml.cs` DI is already handled by Task 3.

- [ ] **Step 5: Add layout state**

Add to `VideoLibraryPageViewModel`:

```csharp
[ObservableProperty]
public partial VideoLibraryLayoutMode SelectedLayoutMode { get; set; } = VideoLibraryLayoutMode.List;

public bool IsListLayout => SelectedLayoutMode == VideoLibraryLayoutMode.List;
public bool IsPosterLayout => SelectedLayoutMode == VideoLibraryLayoutMode.Posters;

partial void OnSelectedLayoutModeChanged(VideoLibraryLayoutMode value)
{
    OnPropertyChanged(nameof(IsListLayout));
    OnPropertyChanged(nameof(IsPosterLayout));
}

[RelayCommand]
private void SelectLayout(string? layoutName)
{
    if (Enum.TryParse<VideoLibraryLayoutMode>(layoutName, out var layoutMode))
        SelectedLayoutMode = layoutMode;
}
```

- [ ] **Step 6: Load collections and expand navigation filtering**

Add `_collections`, `_activeCollectionId`, and `_activeSeriesName` fields. During `LoadVideosAsync`, call both `GetVideosAsync` and `GetCollectionsAsync`. Update `MatchesSelectedView`:

```csharp
VideoLibraryView.Unwatched => !HasProgress(video) && !video.IsWatched,
VideoLibraryView.Finished => video.IsWatched,
VideoLibraryView.Recent => video.LastOpenedAt.HasValue,
VideoLibraryView.Favorites => video.IsFavorite,
VideoLibraryView.NeedsReview => !IsCoveredByAnyCollection(video),
VideoLibraryView.Series when !string.IsNullOrWhiteSpace(_activeSeriesName) => string.Equals(SeriesName(video), _activeSeriesName, StringComparison.OrdinalIgnoreCase),
VideoLibraryView.Collections when !string.IsNullOrWhiteSpace(_activeCollectionId) => MatchesCollection(video, _activeCollectionId),
```

Keep `VideoLibraryView.Watched` out of new code and migrate existing tests to `Finished`.

- [ ] **Step 7: Add smart collection draft and command state**

Add:

```csharp
[ObservableProperty]
public partial string SmartCollectionNameDraft { get; set; } = "";

[ObservableProperty]
public partial VideoSmartRuleField SelectedSmartRuleField { get; set; } = VideoSmartRuleField.FileName;

[ObservableProperty]
public partial string SmartRuleValueDraft { get; set; } = "";

public IReadOnlyList<VideoItemViewModel> SmartCollectionPreviewRows =>
    BuildSmartRules().Count == 0
        ? []
        : _allVideos
            .Where(video => VideoSmartCollectionMatcher.Matches(video, BuildSmartRules()))
            .Take(5)
            .Select(video => new VideoItemViewModel(video))
            .ToList();
```

Add `CreateSmartCollectionAsync` relay command that calls `IVideoLibraryService.CreateSmartCollectionAsync`, clears drafts, reloads collections, and selects `VideoLibraryView.Collections`.

- [ ] **Step 8: Queue thumbnails for visible rows**

After `ApplyVisibleVideos`, start a guarded background task:

```csharp
_ = GenerateMissingThumbnailsForVisibleVideosAsync(_cts.Token);
```

The method should iterate `Videos.Take(24)`, call `_thumbnailService.EnsureThumbnailAsync(video.Video, generateIfMissing: true, token)`, and call `LoadVideosAsync` once if at least one new thumbnail is saved and the token has not been cancelled.

- [ ] **Step 9: Update `VideoItemViewModel` metadata**

Add:

```csharp
public string? ArtworkPath => File.Exists(Video.PosterPath ?? "") ? Video.PosterPath : Video.ThumbnailPath;
public BitmapImage? ArtworkImage => LoadPoster(ArtworkPath);
public bool HasArtwork => ArtworkImage != null;
public string FileSizeText => Video.FileSizeBytes <= 0 ? "" : FormatByteCount(Video.FileSizeBytes);
public string ModifiedDateText => Video.ModifiedAt?.ToLocalTime().ToString("d") ?? "";
public string RemainingText => Video.IsWatched || Video.DurationSeconds <= 0 ? WatchStatusText : FormatRemaining();
```

Keep `PosterImage` and `HasPoster` as forwarding properties to avoid breaking existing XAML during the same task:

```csharp
public BitmapImage? PosterImage => ArtworkImage;
public bool HasPoster => HasArtwork;
```

- [ ] **Step 10: Run ViewModel tests**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoLibraryPageViewModelTests|FullyQualifiedName~VideoItemViewModelTests"
```

Expected: PASS.

- [ ] **Step 11: Commit Task 4**

```powershell
git add Niratan/ViewModels/Pages/VideoLibraryPageViewModel.cs Niratan/ViewModels/Components/VideoItemViewModel.cs Niratan.Tests/ViewModels/Pages/VideoLibraryPageViewModelTests.cs Niratan.Tests/ViewModels/Components/VideoItemViewModelTests.cs
git commit -m "feat: add video library layout and smart collection state"
```

---

### Task 5: WinUI Library UI And Localization

**Files:**
- Modify: `Niratan/Views/Pages/VideoLibraryPage.xaml`
- Modify: `Niratan/Views/Pages/VideoLibraryPage.xaml.cs`
- Modify: `Niratan/Strings/en-US/Resources.resw`
- Modify: `Niratan/Strings/zh-CN/Resources.resw`
- Test: `Niratan.Tests/Views/Pages/VideoLibraryPageAssetTests.cs`

**Interfaces:**
- Consumes: ViewModel layout flags, collections, smart collection draft fields, and `VideoItemViewModel` artwork metadata.
- Produces: Niratan-style list/poster UI contract and localized visible text.

- [ ] **Step 1: Write failing XAML asset tests**

Update `VideoLibraryPageAssetTests.VideoLibraryPage_DefinesNiratanStyleMinimalLibraryControls` to assert:

```csharp
xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryLayoutSegment\"");
xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoLibraryListView\"");
xaml.Should().Contain("AutomationProperties.AutomationId=\"VideoGridView\"");
xaml.Should().Contain("x:Key=\"VideoListItemTemplate\"");
xaml.Should().Contain("x:Key=\"VideoPosterItemTemplate\"");
xaml.Should().Contain("AutomationProperties.AutomationId=\"CreateSmartCollectionButton\"");
xaml.Should().Contain("Command=\"{x:Bind ViewModel.CreateSmartCollectionCommand}\"");
xaml.Should().Contain("VideoLibraryUnwatchedNavItem");
xaml.Should().Contain("VideoLibraryFinishedNavItem");
xaml.Should().Contain("VideoLibraryRecentNavItem");
xaml.Should().Contain("VideoLibraryNeedsReviewNavItem");
xaml.Should().Contain("VideoLibraryFavoritesNavItem");
xaml.Should().Contain("VideoLibrarySeriesNavItem");
```

Extend the localization key loop with:

```csharp
"VideoLibraryUnwatchedNavItem.Content",
"VideoLibraryFinishedNavItem.Content",
"VideoLibraryRecentNavItem.Content",
"VideoLibraryNeedsReviewNavItem.Content",
"VideoLibraryFavoritesNavItem.Content",
"VideoLibrarySeriesNavItem.Content",
"VideoLibraryLayoutList",
"VideoLibraryLayoutPosters",
"CreateSmartCollectionButton.Label",
"VideoLibrarySmartCollectionName.PlaceholderText",
"VideoLibrarySmartCollectionRuleValue.PlaceholderText",
"VideoLibraryCreateSmartCollectionPrimaryButton",
"VideoLibraryCreateSmartCollectionSecondaryButton",
"VideoLibraryPreviewMatches",
```

- [ ] **Step 2: Run XAML asset tests to verify they fail**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoLibraryPageAssetTests"
```

Expected: fails because XAML controls and localization keys are missing.

- [ ] **Step 3: Expand secondary navigation**

In `VideoLibraryPage.xaml`, add navigation items for `Unwatched`, `Finished`, `Recent`, `NeedsReview`, `Favorites`, and `Series`. Keep `Tag` values identical to the enum names:

```xml
<NavigationViewItem x:Name="VideoLibraryFinishedNavItem"
                    x:Uid="VideoLibraryFinishedNavItem"
                    AutomationProperties.AutomationId="VideoLibraryFinishedNavItem"
                    Content="Finished"
                    Tag="Finished">
    <NavigationViewItem.Icon>
        <FontIcon Glyph="&#xE930;" />
    </NavigationViewItem.Icon>
</NavigationViewItem>
```

Update constructor default selected item to `VideoLibraryContinueWatchingNavItem` if the default ViewModel mode is `ContinueWatching`; otherwise keep the XAML and ViewModel defaults aligned.

- [ ] **Step 4: Add layout segmented control**

In the header grid, reserve a column for a compact two-button segment:

```xml
<Grid x:Name="VideoLibraryLayoutSegment"
      AutomationProperties.AutomationId="VideoLibraryLayoutSegment"
      Grid.Column="3"
      Height="36"
      ColumnDefinitions="*,*">
    <ToggleButton x:Uid="VideoLibraryLayoutList"
                  IsChecked="{x:Bind ViewModel.IsListLayout, Mode=OneWay}"
                  Command="{x:Bind ViewModel.SelectLayoutCommand}"
                  CommandParameter="List">
        <FontIcon Glyph="&#xE8FD;" />
    </ToggleButton>
    <ToggleButton x:Uid="VideoLibraryLayoutPosters"
                  Grid.Column="1"
                  IsChecked="{x:Bind ViewModel.IsPosterLayout, Mode=OneWay}"
                  Command="{x:Bind ViewModel.SelectLayoutCommand}"
                  CommandParameter="Posters">
        <FontIcon Glyph="&#xECA5;" />
    </ToggleButton>
</Grid>
```

Use the `SelectLayoutCommand` added in Task 4.

- [ ] **Step 5: Split list and poster templates**

Rename the current `VideoItemTemplate` to `VideoPosterItemTemplate` and bind image source to `ArtworkImage` and visibility to `HasArtwork`. Add `VideoListItemTemplate`:

```xml
<DataTemplate x:Key="VideoListItemTemplate" x:DataType="vmc:VideoItemViewModel">
    <Button Padding="0"
            HorizontalContentAlignment="Stretch"
            AutomationProperties.AutomationId="{x:Bind AutomationId, Mode=OneTime}"
            Click="VideoButton_Click">
        <Grid Height="96"
              ColumnDefinitions="144,*"
              ColumnSpacing="14"
              Padding="10,8">
            <Border Width="144"
                    Height="81"
                    CornerRadius="8"
                    Background="{ThemeResource CardBackgroundFillColorDefaultBrush}">
                <Grid>
                    <Image Source="{x:Bind ArtworkImage, Mode=OneTime}"
                           Stretch="UniformToFill"
                           Visibility="{x:Bind HasArtwork, Mode=OneTime, Converter={StaticResource BooleanToVisibilityConverter}}" />
                    <FontIcon Glyph="&#xE714;"
                              Opacity="0.35"
                              HorizontalAlignment="Center"
                              VerticalAlignment="Center"
                              Visibility="{x:Bind HasArtwork, Mode=OneTime, Converter={StaticResource BooleanToVisibilityConverter}, ConverterParameter=Invert}" />
                </Grid>
            </Border>
            <Grid Grid.Column="1"
                  RowDefinitions="Auto,Auto,Auto"
                  RowSpacing="6">
                <TextBlock Text="{x:Bind Video.Title, Mode=OneTime}"
                           TextTrimming="CharacterEllipsis"
                           Style="{StaticResource BodyStrongTextBlockStyle}" />
                <TextBlock Grid.Row="1"
                           Text="{x:Bind ListMetadataText, Mode=OneWay}"
                           TextTrimming="CharacterEllipsis"
                           Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                <Grid Grid.Row="2"
                      ColumnDefinitions="120,Auto"
                      ColumnSpacing="10">
                    <ProgressBar Height="4"
                                 Minimum="0"
                                 Maximum="100"
                                 Value="{x:Bind OverallProgressPercent, Mode=OneWay}" />
                    <TextBlock Grid.Column="1"
                               Text="{x:Bind RemainingText, Mode=OneWay}"
                               Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                </Grid>
            </Grid>
        </Grid>
    </Button>
</DataTemplate>
```

Render both controls and bind visibility:

```xml
<ListView x:Name="VideoLibraryListView"
          AutomationProperties.AutomationId="VideoLibraryListView"
          ItemTemplate="{StaticResource VideoListItemTemplate}"
          ItemsSource="{x:Bind ViewModel.Videos, Mode=OneWay}"
          ItemClick="ListView_ItemClick"
          IsItemClickEnabled="True"
          Visibility="{x:Bind ViewModel.IsListLayout, Converter={StaticResource BooleanToVisibilityConverter}, Mode=OneWay}" />

<GridView x:Name="VideoGridView"
          ItemTemplate="{StaticResource VideoPosterItemTemplate}"
          Visibility="{x:Bind ViewModel.IsPosterLayout, Converter={StaticResource BooleanToVisibilityConverter}, Mode=OneWay}" />
```

- [ ] **Step 6: Add smart collection dialog surface**

Add `CreateSmartCollectionButton` to the `CommandBar` and a `ContentDialog` or teaching-tip-style panel bound to ViewModel draft fields. Keep code-behind limited to opening/closing the dialog. The Save action must call `ViewModel.CreateSmartCollectionCommand`.

- [ ] **Step 7: Add localization keys**

Update `Resources.resw` in both `en-US` and `zh-CN`. Use these Simplified Chinese values:

```text
未看
已完成
最近
待整理
收藏
系列
列表
海报
创建智能合集
智能合集名称
规则文本
创建
取消
匹配预览
```

- [ ] **Step 8: Run XAML asset tests**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~VideoLibraryPageAssetTests"
```

Expected: PASS.

- [ ] **Step 9: Commit Task 5**

```powershell
git add Niratan/Views/Pages/VideoLibraryPage.xaml Niratan/Views/Pages/VideoLibraryPage.xaml.cs Niratan/Strings/en-US/Resources.resw Niratan/Strings/zh-CN/Resources.resw Niratan.Tests/Views/Pages/VideoLibraryPageAssetTests.cs
git commit -m "feat: add niratan style video library views"
```

---

### Task 6: Final Verification And Manual Smoke

**Files:**
- Modify only files needed to fix failures found by verification.
- Test: full build/test.

**Interfaces:**
- Consumes all previous tasks.
- Produces verified working video library parity increment.

- [ ] **Step 1: Stop the worktree Niratan process if it is running**

Run:

```powershell
Get-Process Niratan -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like '*\.worktrees\niratan-video-library-min\Niratan\bin*' } |
    ForEach-Object { Stop-Process -Id $_.Id -Force }
```

Expected: no output, or the worktree process stops.

- [ ] **Step 2: Run full build**

Run:

```powershell
dotnet build -p:Platform=x64
```

Expected: build succeeds. Existing NU1903 warnings can remain if they are already present and unrelated.

- [ ] **Step 3: Run full tests**

Run:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64
```

Expected: all tests pass.

- [ ] **Step 4: Launch app for smoke test**

Run:

```powershell
.\build-and-run.ps1
```

Expected: Niratan opens from this worktree.

- [ ] **Step 5: Manual smoke checklist**

Use the app UI:

```text
1. Open Video.
2. Scan a local folder with at least two videos.
3. Switch List and Posters three times.
4. Confirm real thumbnail or fallback art appears without delaying video open.
5. Create a smart collection using File Name contains text.
6. Select Collections and open the smart collection.
7. Open a video, close it, and confirm Continue Watching still refreshes.
```

- [ ] **Step 6: Commit verification fixes**

If verification required fixes:

```powershell
git status --short
git add Niratan/Models Niratan/Services Niratan/ViewModels Niratan/Views Niratan/Strings Niratan.Tests
git commit -m "fix: stabilize niratan video library parity"
```

Before running the `git add`, inspect `git status --short` and avoid staging unrelated pre-existing worktree changes. If no fixes were needed, do not create an empty commit.
