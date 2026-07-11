using System.Data.Common;
using System.IO;
using System.Reflection;
using FluentAssertions;
using Hoshi.Models;
using Hoshi.Models.Video;
using Hoshi.Services.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hoshi.Tests.Services.Storage;

public class VideoDataServiceTests
{
    [Fact]
    public void VideoDataInterface_DoesNotExposeNovelMembers()
    {
        typeof(IVideoDataService).GetMethods()
            .Select(method => method.Name)
            .Should()
            .OnlyContain(name => !name.Contains("Novel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migration008_CreatesVideoItemsTable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await InvokeMigration008Async(connection, transaction);
        await transaction.CommitAsync(ct);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name = 'VideoItems';
            """;

        var count = (long)(await command.ExecuteScalarAsync(ct))!;
        count.Should().Be(1);
    }

    [Fact]
    public async Task Migration008_PreventsDuplicateFilePaths()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await InvokeMigration008Async(connection, transaction);
        await transaction.CommitAsync(ct);

        var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO VideoItems
                (Id, Title, FilePath, ImportedAt)
            VALUES
                ('one', 'One', 'D:\Video\a.mkv', '2026-07-06T00:00:00Z'),
                ('two', 'Two', 'D:\Video\a.mkv', '2026-07-06T00:00:00Z');
            """;

        var act = async () => await insert.ExecuteNonQueryAsync(ct);
        await act.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Migration009_AddsVideoPlaybackSubtitleStateColumns()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await InvokeMigrationAsync("Migration_008", connection, transaction);
        await InvokeMigrationAsync("Migration_009", connection, transaction);
        await transaction.CommitAsync(ct);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM pragma_table_info('VideoItems')
            WHERE name IN (
                'SubtitleSelectionKind',
                'SubtitleSelectionPath',
                'SubtitleSelectionTrackId',
                'SubtitleSelectionTrackName'
            )
            ORDER BY name;
            """;

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            names.Add(reader.GetString(0));

        names.Should().Equal(
            "SubtitleSelectionKind",
            "SubtitleSelectionPath",
            "SubtitleSelectionTrackId",
            "SubtitleSelectionTrackName");
    }

    [Fact]
    public async Task Migration010_AddsProfileOverrideColumns()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await InvokeMigrationAsync("Migration_003", connection, transaction);
        await InvokeMigrationAsync("Migration_008", connection, transaction);
        await InvokeMigrationAsync("Migration_009", connection, transaction);
        await InvokeMigrationAsync("Migration_010", connection, transaction);
        await transaction.CommitAsync(ct);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tbl_name || '.' || name
            FROM (
                SELECT 'NovelBooks' AS tbl_name, name
                FROM pragma_table_info('NovelBooks')
                UNION ALL
                SELECT 'VideoItems' AS tbl_name, name
                FROM pragma_table_info('VideoItems')
            )
            WHERE name = 'ProfileId'
            ORDER BY tbl_name;
            """;

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            names.Add(reader.GetString(0));

        names.Should().Equal("VideoItems.ProfileId");
    }

    [Fact]
    public async Task Migration011_AddsVideoLibraryManagementColumnsAndIndexes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await InvokeMigrationAsync("Migration_008", connection, transaction);
        await InvokeMigrationAsync("Migration_009", connection, transaction);
        await InvokeMigrationAsync("Migration_010", connection, transaction);
        await InvokeMigrationAsync("Migration_011", connection, transaction);
        await transaction.CommitAsync(ct);

        var columnCommand = connection.CreateCommand();
        columnCommand.CommandText = """
            SELECT name
            FROM pragma_table_info('VideoItems')
            WHERE name IN (
                'SourceFolderPath',
                'PosterPath',
                'Tags',
                'CollectionName',
                'IsWatched'
            )
            ORDER BY name;
            """;

        var columns = new List<string>();
        await using (var reader = await columnCommand.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                columns.Add(reader.GetString(0));
        }

        columns.Should().Equal(
            "CollectionName",
            "IsWatched",
            "PosterPath",
            "SourceFolderPath",
            "Tags");

        var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'index'
              AND name IN (
                  'IX_VideoItems_SourceFolderPath',
                  'IX_VideoItems_CollectionName',
                  'IX_VideoItems_IsWatched'
              )
            ORDER BY name;
            """;

        var indexes = new List<string>();
        await using (var reader = await indexCommand.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                indexes.Add(reader.GetString(0));
        }

        indexes.Should().Equal(
            "IX_VideoItems_CollectionName",
            "IX_VideoItems_IsWatched",
            "IX_VideoItems_SourceFolderPath");
    }

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

    [Fact]
    public async Task DataService_PersistsVideoCollectionsAndMembership()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbPath = Path.Combine(Path.GetTempPath(), $"hoshi-video-{Guid.NewGuid():N}.db");
        try
        {
            var connectionString = $"Data Source={dbPath};Pooling=False";
            await new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance, connectionString).MigrateAsync();
            var service = new VideoDataService(connectionString);

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

    [Fact]
    public async Task UpsertVideoAsync_PreservesExistingFavoriteWhenRescanUsesDefaultFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbPath = Path.Combine(Path.GetTempPath(), $"hoshi-video-{Guid.NewGuid():N}.db");
        try
        {
            var connectionString = $"Data Source={dbPath};Pooling=False";
            await new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance, connectionString).MigrateAsync();
            var service = new VideoDataService(connectionString);
            var filePath = @"D:\Anime\Episode 1.mkv";

            await service.UpsertVideoAsync(new VideoItem
            {
                Id = "video-1",
                Title = "Episode 1",
                FilePath = filePath,
                ImportedAt = DateTime.UtcNow,
                IsFavorite = true,
            }, ct);
            await service.UpsertVideoAsync(new VideoItem
            {
                Id = "video-rescan",
                Title = "Episode 1",
                FilePath = filePath,
                ImportedAt = DateTime.UtcNow,
            }, ct);

            var videos = await service.GetVideosAsync(ct: ct);

            videos.Should().ContainSingle()
                .Which.IsFavorite.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    private static async Task InvokeMigration008Async(
        SqliteConnection connection,
        DbTransaction transaction)
    {
        await InvokeMigrationAsync("Migration_008", connection, transaction);
    }

    private static async Task InvokeMigrationAsync(
        string migrationName,
        SqliteConnection connection,
        DbTransaction transaction)
    {
        var appAssembly = typeof(Hoshi.Models.NovelBook).Assembly;
        var migrationType = appAssembly.GetType(
            $"Hoshi.Services.Storage.Migrations.{migrationName}",
            throwOnError: true)!;
        var migration = Activator.CreateInstance(migrationType, nonPublic: true)!;
        var method = migrationType.GetMethod(
            "UpAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        await (Task)method.Invoke(migration, [connection, transaction])!;
    }
}
