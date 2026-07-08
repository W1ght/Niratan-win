using System.Data.Common;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Hoshi.Services.Storage.Migrations;

internal class Migration_009 : IMigration
{
    public int Version => 9;
    public string Description => "Add video playback subtitle state";

    public async Task UpAsync(SqliteConnection connection, DbTransaction transaction)
    {
        await connection.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS VideoItems (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                FilePath TEXT NOT NULL UNIQUE,
                SubtitlePath TEXT,
                ImportedAt TEXT NOT NULL,
                LastOpenedAt TEXT,
                LastPositionSeconds REAL NOT NULL DEFAULT 0,
                DurationSeconds REAL NOT NULL DEFAULT 0,
                ManualSortOrder INTEGER NOT NULL DEFAULT 0,
                SubtitleSelectionKind INTEGER NOT NULL DEFAULT 0,
                SubtitleSelectionPath TEXT,
                SubtitleSelectionTrackId INTEGER,
                SubtitleSelectionTrackName TEXT
            );

            CREATE INDEX IF NOT EXISTS IX_VideoItems_Title
                ON VideoItems (Title);

            CREATE INDEX IF NOT EXISTS IX_VideoItems_LastOpenedAt
                ON VideoItems (LastOpenedAt);
            """,
            transaction: transaction
        );

        await AddColumnIfMissingAsync(
            connection,
            transaction,
            "SubtitleSelectionKind",
            "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(
            connection,
            transaction,
            "SubtitleSelectionPath",
            "TEXT");
        await AddColumnIfMissingAsync(
            connection,
            transaction,
            "SubtitleSelectionTrackId",
            "INTEGER");
        await AddColumnIfMissingAsync(
            connection,
            transaction,
            "SubtitleSelectionTrackName",
            "TEXT");
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string columnName,
        string columnDefinition)
    {
        var exists = await connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM pragma_table_info('VideoItems')
            WHERE name = @ColumnName;
            """,
            new { ColumnName = columnName },
            transaction);
        if (exists > 0)
            return;

        await connection.ExecuteAsync(
            $"ALTER TABLE VideoItems ADD COLUMN {columnName} {columnDefinition};",
            transaction: transaction);
    }
}
