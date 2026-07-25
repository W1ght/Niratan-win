using System.Data.Common;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Niratan.Services.Storage.Migrations;

internal sealed class Migration_015 : IMigration
{
    public int Version => 15;
    public string Description => "Persist per-video inspector playback state";

    public async Task UpAsync(SqliteConnection connection, DbTransaction transaction)
    {
        var tableExists =
            await connection.ExecuteScalarAsync<long>(
                """
                SELECT COUNT(*) FROM sqlite_master
                WHERE type = 'table' AND name = 'VideoItems';
                """,
                transaction: transaction
            ) > 0;
        if (!tableExists)
            return;

        await AddColumnIfMissingAsync(
            connection,
            transaction,
            "SubtitleDelayMilliseconds",
            "INTEGER NOT NULL DEFAULT 0"
        );
        await AddColumnIfMissingAsync(
            connection,
            transaction,
            "PlaybackSpeed",
            "REAL NOT NULL DEFAULT 1"
        );
        await AddColumnIfMissingAsync(
            connection,
            transaction,
            "AudioDelaySeconds",
            "REAL NOT NULL DEFAULT 0"
        );
        await AddColumnIfMissingAsync(
            connection,
            transaction,
            "AudioSelectionKind",
            "INTEGER NOT NULL DEFAULT 0"
        );
        await AddColumnIfMissingAsync(connection, transaction, "AudioSelectionTrackId", "INTEGER");
        await AddColumnIfMissingAsync(connection, transaction, "AudioSelectionFfIndex", "INTEGER");
        await AddColumnIfMissingAsync(connection, transaction, "AudioSelectionTitle", "TEXT");
        await AddColumnIfMissingAsync(connection, transaction, "AudioSelectionLanguage", "TEXT");
        await AddColumnIfMissingAsync(connection, transaction, "AudioSelectionCodec", "TEXT");
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string columnName,
        string definition
    )
    {
        var columnExists =
            await connection.ExecuteScalarAsync<long>(
                """
                SELECT COUNT(*) FROM pragma_table_info('VideoItems')
                WHERE name = @ColumnName;
                """,
                new { ColumnName = columnName },
                transaction
            ) > 0;
        if (!columnExists)
        {
            await connection.ExecuteAsync(
                $"ALTER TABLE VideoItems ADD COLUMN {columnName} {definition};",
                transaction: transaction
            );
        }
    }
}
