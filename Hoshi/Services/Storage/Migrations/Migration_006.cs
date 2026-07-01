using System.Data.Common;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Hoshi.Services.Storage.Migrations;

internal class Migration_006 : IMigration
{
    public int Version => 6;
    public string Description => "Add character progress to NovelBooks";

    public async Task UpAsync(SqliteConnection connection, DbTransaction transaction)
    {
        await connection.ExecuteAsync(
            """
            ALTER TABLE NovelBooks ADD COLUMN CurrentCharacterCount INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE NovelBooks ADD COLUMN TotalCharacterCount INTEGER NOT NULL DEFAULT 0;
            """,
            transaction: transaction
        );
    }
}
