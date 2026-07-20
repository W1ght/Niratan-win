using System.Data.Common;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Niratan.Services.Storage.Migrations;

internal sealed class Migration_014 : IMigration
{
    public int Version => 14;
    public string Description => "Add persistent video library sources";

    public async Task UpAsync(SqliteConnection connection, DbTransaction transaction)
    {
        await connection.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS VideoLibrarySources (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                FolderPath TEXT NOT NULL COLLATE NOCASE,
                CreatedAt TEXT NOT NULL,
                LastScannedAt TEXT NULL,
                LastError TEXT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_VideoLibrarySources_FolderPath
                ON VideoLibrarySources (FolderPath COLLATE NOCASE);
            """,
            transaction: transaction);

        if (!await TableExistsAsync(connection, transaction, "VideoItems"))
            return;

        await AddColumnIfMissingAsync(connection, transaction, "SourceId", "TEXT");
        await AddColumnIfMissingAsync(connection, transaction, "LastSeenAt", "TEXT");
        await connection.ExecuteAsync(
            """
            CREATE INDEX IF NOT EXISTS IX_VideoItems_SourceId
                ON VideoItems (SourceId);
            """,
            transaction: transaction);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string tableName) =>
        await connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name = @TableName;
            """,
            new { TableName = tableName },
            transaction) > 0;

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string columnName,
        string definition)
    {
        var exists = await connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*) FROM pragma_table_info('VideoItems')
            WHERE name = @ColumnName;
            """,
            new { ColumnName = columnName },
            transaction);
        if (exists == 0)
        {
            await connection.ExecuteAsync(
                $"ALTER TABLE VideoItems ADD COLUMN {columnName} {definition};",
                transaction: transaction);
        }
    }
}
