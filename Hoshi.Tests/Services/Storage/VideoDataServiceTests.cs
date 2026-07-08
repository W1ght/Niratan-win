using System.Data.Common;
using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Hoshi.Tests.Services.Storage;

public class VideoDataServiceTests
{
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

        names.Should().Equal("NovelBooks.ProfileId", "VideoItems.ProfileId");
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
