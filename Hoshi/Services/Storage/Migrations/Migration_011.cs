using System.Data.Common;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Hoshi.Services.Storage.Migrations;

internal class Migration_011 : IMigration
{
    public int Version => 11;
    public string Description => "Add video library management metadata";

    public async Task UpAsync(SqliteConnection connection, DbTransaction transaction)
    {
        if (!await TableExistsAsync(connection, transaction, "VideoItems"))
            return;

        await AddColumnIfMissingAsync(connection, transaction, "VideoItems", "SourceFolderPath", "TEXT");
        await AddColumnIfMissingAsync(connection, transaction, "VideoItems", "PosterPath", "TEXT");
        await AddColumnIfMissingAsync(connection, transaction, "VideoItems", "Tags", "TEXT");
        await AddColumnIfMissingAsync(connection, transaction, "VideoItems", "CollectionName", "TEXT");
        await AddColumnIfMissingAsync(connection, transaction, "VideoItems", "IsWatched", "INTEGER NOT NULL DEFAULT 0");

        await connection.ExecuteAsync(
            """
            CREATE INDEX IF NOT EXISTS IX_VideoItems_SourceFolderPath
                ON VideoItems (SourceFolderPath);

            CREATE INDEX IF NOT EXISTS IX_VideoItems_CollectionName
                ON VideoItems (CollectionName);

            CREATE INDEX IF NOT EXISTS IX_VideoItems_IsWatched
                ON VideoItems (IsWatched);
            """,
            transaction: transaction);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string tableName)
    {
        var count = await connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name = @TableName;
            """,
            new { TableName = tableName },
            transaction);

        return count > 0;
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        var tableExists = await connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name = @TableName;
            """,
            new { TableName = tableName },
            transaction);
        if (tableExists == 0)
            return;

        var exists = await connection.ExecuteScalarAsync<long>(
            $"""
            SELECT COUNT(*)
            FROM pragma_table_info('{tableName}')
            WHERE name = @ColumnName;
            """,
            new { ColumnName = columnName },
            transaction);
        if (exists > 0)
            return;

        await connection.ExecuteAsync(
            $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};",
            transaction: transaction);
    }
}
