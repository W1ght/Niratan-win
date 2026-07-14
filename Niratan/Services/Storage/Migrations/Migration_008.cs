using System.Data.Common;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Niratan.Services.Storage.Migrations;

internal class Migration_008 : IMigration
{
    public int Version => 8;
    public string Description => "Add video library tables";

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
                ManualSortOrder INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS IX_VideoItems_Title
                ON VideoItems (Title);

            CREATE INDEX IF NOT EXISTS IX_VideoItems_LastOpenedAt
                ON VideoItems (LastOpenedAt);
            """,
            transaction: transaction
        );
    }
}
