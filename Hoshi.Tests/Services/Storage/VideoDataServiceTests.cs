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

    private static async Task InvokeMigration008Async(
        SqliteConnection connection,
        DbTransaction transaction)
    {
        var appAssembly = typeof(Hoshi.Models.NovelBook).Assembly;
        var migrationType = appAssembly.GetType(
            "Hoshi.Services.Storage.Migrations.Migration_008",
            throwOnError: true)!;
        var migration = Activator.CreateInstance(migrationType, nonPublic: true)!;
        var method = migrationType.GetMethod(
            "UpAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

        await (Task)method.Invoke(migration, [connection, transaction])!;
    }
}
