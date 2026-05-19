using System.Data.Common;
using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;

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

    private static async Task InvokeMigration003Async(
        SqliteConnection connection,
        DbTransaction transaction
    )
    {
        var appAssembly = typeof(Hoshi.Models.NovelBook).Assembly;
        var migrationType = appAssembly.GetType(
            "Hoshi.Services.Storage.Migrations.Migration_003",
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
