using System.Data.Common;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Niratan.Services.Storage.Migrations;

internal class Migration_006 : IMigration
{
    public int Version => 6;
    public string Description => "Add character progress to NovelBooks";

    public async Task UpAsync(SqliteConnection connection, DbTransaction transaction)
    {
        if (!await LegacyTableExistsAsync(connection, transaction))
            return;

        await connection.ExecuteAsync(
            """
            ALTER TABLE NovelBooks ADD COLUMN CurrentCharacterCount INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE NovelBooks ADD COLUMN TotalCharacterCount INTEGER NOT NULL DEFAULT 0;
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
