using System.Data.Common;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Niratan.Services.Storage.Migrations;

internal sealed class Migration_013 : IMigration
{
    public int Version => 13;
    public string Description => "Add durable remote video identity";

    public async Task UpAsync(SqliteConnection connection, DbTransaction transaction)
    {
        if (!await TableExistsAsync(connection, transaction, "VideoItems"))
            return;

        await AddColumnIfMissingAsync(connection, transaction, "ProviderId", "TEXT");
        await AddColumnIfMissingAsync(connection, transaction, "RemoteId", "TEXT");
        await AddColumnIfMissingAsync(connection, transaction, "OriginalUrl", "TEXT");
        await AddColumnIfMissingAsync(connection, transaction, "CanonicalUrl", "TEXT");
        await AddColumnIfMissingAsync(connection, transaction, "RemoteThumbnailUrl", "TEXT");
        await AddColumnIfMissingAsync(connection, transaction, "RemoteSubtitleLanguage", "TEXT");

        await connection.ExecuteAsync(
            """
            CREATE UNIQUE INDEX IF NOT EXISTS IX_VideoItems_RemoteIdentity
                ON VideoItems (ProviderId, RemoteId)
                WHERE ProviderId IS NOT NULL AND TRIM(ProviderId) <> ''
                  AND RemoteId IS NOT NULL AND TRIM(RemoteId) <> '';
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
