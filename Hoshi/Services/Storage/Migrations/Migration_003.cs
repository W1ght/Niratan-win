using System.Data.Common;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Hoshi.Services.Storage.Migrations;

internal class Migration_003 : IMigration
{
    public int Version => 3;
    public string Description => "Add novel EPUB library tables";

    public async Task UpAsync(SqliteConnection connection, DbTransaction transaction)
    {
        await connection.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS NovelBooks (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                Author TEXT,
                FilePath TEXT NOT NULL UNIQUE,
                CoverPath TEXT,
                ImportedAt TEXT NOT NULL,
                LastOpenedAt TEXT,
                Language TEXT,
                UniqueIdentifier TEXT
            );

            CREATE TABLE IF NOT EXISTS NovelReadingProgress (
                BookId TEXT PRIMARY KEY,
                LocationJson TEXT NOT NULL,
                Progression REAL,
                ChapterHref TEXT,
                UpdatedAt TEXT NOT NULL,
                FOREIGN KEY (BookId) REFERENCES NovelBooks(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS NovelReaderSettings (
                Scope TEXT NOT NULL,
                ScopeId TEXT NOT NULL,
                SettingsJson TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (Scope, ScopeId)
            );

            CREATE INDEX IF NOT EXISTS IX_NovelBooks_Title
                ON NovelBooks (Title);
            """,
            transaction: transaction
        );
    }
}
