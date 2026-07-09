using System.Data.Common;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Hoshi.Services.Storage.Migrations;

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
