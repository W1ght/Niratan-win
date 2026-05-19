using System.Data.Common;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Hoshi.Services.Storage.Migrations;

internal class Migration_004 : IMigration
{
    public int Version => 4;
    public string Description => "Add ExtractedPath and ChapterCount to NovelBooks";

    public async Task UpAsync(SqliteConnection connection, DbTransaction transaction)
    {
        await connection.ExecuteAsync(
            """
            ALTER TABLE NovelBooks ADD COLUMN ExtractedPath TEXT;
            ALTER TABLE NovelBooks ADD COLUMN ChapterCount INTEGER NOT NULL DEFAULT 0;
            """,
            transaction: transaction
        );
    }
}
