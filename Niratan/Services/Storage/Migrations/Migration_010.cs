using System.Data.Common;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Niratan.Services.Storage.Migrations;

internal class Migration_010 : IMigration
{
    public int Version => 10;
    public string Description => "Add profile overrides to novels and videos";

    public async Task UpAsync(SqliteConnection connection, DbTransaction transaction)
    {
        await AddColumnIfMissingAsync(connection, transaction, "NovelBooks", "ProfileId", "TEXT");
        await AddColumnIfMissingAsync(connection, transaction, "VideoItems", "ProfileId", "TEXT");
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        var tableExists = await connection.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name = @TableName;
            """,
            new { TableName = tableName },
            transaction);
        if (tableExists == 0)
            return;

        var exists = await connection.ExecuteScalarAsync<long>(
            $"""
            SELECT COUNT(*)
            FROM pragma_table_info('{tableName}')
            WHERE name = @ColumnName;
            """,
            new { ColumnName = columnName },
            transaction);
        if (exists > 0)
            return;

        await connection.ExecuteAsync(
            $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};",
            transaction: transaction);
    }
}
