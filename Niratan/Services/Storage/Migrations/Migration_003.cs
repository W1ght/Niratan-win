using System.Data.Common;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Niratan.Services.Storage.Migrations;

internal class Migration_003 : IMigration
{
    public int Version => 3;
    public string Description => "Reserve legacy novel schema version";

    public Task UpAsync(SqliteConnection connection, DbTransaction transaction) =>
        Task.CompletedTask;
}
