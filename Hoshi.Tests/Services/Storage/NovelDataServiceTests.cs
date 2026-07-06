using System.Data.Common;
using System.Reflection;
using FluentAssertions;
using Hoshi.Services.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hoshi.Tests.Services.Storage;

public class NovelDataServiceTests
{
    [Fact]
    public async Task Migration003_CreatesNovelTables()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await InvokeMigration003Async(connection, transaction);
        await transaction.CommitAsync(ct);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN ('NovelBooks', 'NovelReadingProgress', 'NovelReaderSettings');
            """;

        var count = (long)(await command.ExecuteScalarAsync(ct))!;
        count.Should().Be(3);
    }

    [Fact]
    public async Task Migration003_PreventsDuplicateFilePaths()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await InvokeMigration003Async(connection, transaction);
        await transaction.CommitAsync(ct);

        var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO NovelBooks
                (Id, Title, FilePath, ImportedAt)
            VALUES
                ('one', 'One', 'D:\Books\a.epub', '2026-05-19T00:00:00Z'),
                ('two', 'Two', 'D:\Books\a.epub', '2026-05-19T00:00:00Z');
            """;

        var act = async () => await insert.ExecuteNonQueryAsync(ct);
        await act.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task DatabaseMigrator_AcceptsExistingVersion8Database()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbPath = Path.Combine(Path.GetTempPath(), $"hoshi-migration-v8-{Guid.NewGuid():N}.db");

        try
        {
            var connectionString = $"Data Source={dbPath};Pooling=False";
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(ct);
                var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version = 8;";
                await command.ExecuteNonQueryAsync(ct);
            }

            var migrator = new DatabaseMigrator(
                NullLogger<DatabaseMigrator>.Instance,
                connectionString);

            var act = async () => await migrator.MigrateAsync();
            await act.Should().NotThrowAsync();
        }
        finally
        {
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task Migration008_CreatesVideoLibraryTable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await InvokeMigrationAsync("Migration_008", connection, transaction);
        await transaction.CommitAsync(ct);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE (type = 'table' AND name = 'VideoItems')
               OR (type = 'index' AND name IN ('IX_VideoItems_Title', 'IX_VideoItems_LastOpenedAt'));
            """;

        var count = (long)(await command.ExecuteScalarAsync(ct))!;
        count.Should().Be(3);
    }

    private static async Task InvokeMigration003Async(
        SqliteConnection connection,
        DbTransaction transaction
    )
    {
        await InvokeMigrationAsync("Migration_003", connection, transaction);
    }

    private static async Task InvokeMigrationAsync(
        string migrationName,
        SqliteConnection connection,
        DbTransaction transaction
    )
    {
        var appAssembly = typeof(Hoshi.Models.NovelBook).Assembly;
        var migrationType = appAssembly.GetType(
            $"Hoshi.Services.Storage.Migrations.{migrationName}",
            throwOnError: true
        )!;
        var migration = Activator.CreateInstance(migrationType, nonPublic: true)!;
        var method = migrationType.GetMethod(
            "UpAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        )!;

        await (Task)method.Invoke(migration, [connection, transaction])!;
    }
}
