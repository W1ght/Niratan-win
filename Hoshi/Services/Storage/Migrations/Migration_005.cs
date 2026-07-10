using System.Data.Common;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Hoshi.Services.Storage.Migrations;

internal class Migration_005 : IMigration
{
    public int Version => 5;
    public string Description => "Add CurrentChapterIndex and Progress to NovelBooks";

    public async Task UpAsync(SqliteConnection connection, DbTransaction transaction)
    {
        if (!await LegacyTableExistsAsync(connection, transaction))
            return;

        await connection.ExecuteAsync(
            """
            ALTER TABLE NovelBooks ADD COLUMN CurrentChapterIndex INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE NovelBooks ADD COLUMN Progress REAL NOT NULL DEFAULT 0.0;
            """,
            transaction: transaction
        );
    }

    private static async Task<bool> LegacyTableExistsAsync(
        SqliteConnection connection,
        DbTransaction transaction) =>
        await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'NovelBooks';",
            transaction: transaction) > 0;
}
