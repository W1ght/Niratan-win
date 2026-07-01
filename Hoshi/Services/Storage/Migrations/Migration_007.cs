using System.Data.Common;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Hoshi.Services.Storage.Migrations;

internal class Migration_007 : IMigration
{
    public int Version => 7;
    public string Description => "Add manual novel library sort order";

    public async Task UpAsync(SqliteConnection connection, DbTransaction transaction)
    {
        await connection.ExecuteAsync(
            """
            ALTER TABLE NovelBooks ADD COLUMN ManualSortOrder INTEGER NOT NULL DEFAULT 0;
            """,
            transaction: transaction
        );
    }
}
