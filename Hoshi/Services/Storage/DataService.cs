using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Hoshi.Helpers;
using Hoshi.Models;

namespace Hoshi.Services.Storage;

internal class DataService : IDataService
{
    private readonly string _connectionString =
        $"Data Source={Path.Combine(AppDataHelper.GetDataPath(), "hoshi.db")}";

    private async Task<SqliteConnection> GetOpenConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await connection.ExecuteAsync("PRAGMA foreign_keys = ON;");
        return connection;
    }

    public async Task<IReadOnlyList<NovelBook>> GetNovelBooksAsync(
        string? queryText = null,
        CancellationToken ct = default
    )
    {
        using var connection = await GetOpenConnectionAsync();
        const string sql = """
            SELECT Id, Title, Author, FilePath, CoverPath, ImportedAt, LastOpenedAt, Language, UniqueIdentifier, ExtractedPath, ChapterCount, CurrentChapterIndex, Progress, CurrentCharacterCount, TotalCharacterCount, ManualSortOrder
            FROM NovelBooks
            WHERE @QueryText IS NULL
                OR TRIM(@QueryText) = ''
                OR Title LIKE '%' || @QueryText || '%' COLLATE NOCASE
                OR Author LIKE '%' || @QueryText || '%' COLLATE NOCASE
            ORDER BY COALESCE(LastOpenedAt, ImportedAt) DESC, Title ASC;
            """;

        var result = await connection.QueryAsync<NovelBook>(
            new CommandDefinition(
                sql,
                new { QueryText = queryText?.Trim() },
                cancellationToken: ct
            )
        );
        return result.ToList();
    }

    public async Task<NovelBook?> GetNovelBookAsync(string bookId, CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        const string sql = """
            SELECT Id, Title, Author, FilePath, CoverPath, ImportedAt, LastOpenedAt, Language, UniqueIdentifier, ExtractedPath, ChapterCount, CurrentChapterIndex, Progress, CurrentCharacterCount, TotalCharacterCount, ManualSortOrder
            FROM NovelBooks
            WHERE Id = @BookId;
            """;

        return await connection.QueryFirstOrDefaultAsync<NovelBook>(
            new CommandDefinition(sql, new { BookId = bookId }, cancellationToken: ct)
        );
    }

    public async Task UpsertNovelBookAsync(NovelBook book, CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        const string sql = """
            INSERT INTO NovelBooks
                (Id, Title, Author, FilePath, CoverPath, ImportedAt, LastOpenedAt, Language, UniqueIdentifier, ExtractedPath, ChapterCount, CurrentChapterIndex, Progress, CurrentCharacterCount, TotalCharacterCount, ManualSortOrder)
            VALUES
                (@Id, @Title, @Author, @FilePath, @CoverPath, @ImportedAt, @LastOpenedAt, @Language, @UniqueIdentifier, @ExtractedPath, @ChapterCount, @CurrentChapterIndex, @Progress, @CurrentCharacterCount, @TotalCharacterCount, @ManualSortOrder)
            ON CONFLICT(FilePath) DO UPDATE SET
                Title = excluded.Title,
                Author = excluded.Author,
                CoverPath = excluded.CoverPath,
                Language = excluded.Language,
                UniqueIdentifier = excluded.UniqueIdentifier,
                ExtractedPath = excluded.ExtractedPath,
                ChapterCount = excluded.ChapterCount,
                TotalCharacterCount = CASE
                    WHEN excluded.TotalCharacterCount > 0 THEN excluded.TotalCharacterCount
                    ELSE NovelBooks.TotalCharacterCount
                END;
            """;

        await connection.ExecuteAsync(new CommandDefinition(sql, book, cancellationToken: ct));
    }

    public async Task UpdateNovelLastOpenedAsync(
        string bookId,
        DateTime lastOpenedAt,
        CancellationToken ct = default
    )
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE NovelBooks SET LastOpenedAt = @LastOpenedAt WHERE Id = @BookId;",
                new { BookId = bookId, LastOpenedAt = lastOpenedAt },
                cancellationToken: ct
            )
        );
    }

    public async Task DeleteNovelBookAsync(string bookId, CancellationToken ct = default)
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM NovelBooks WHERE Id = @BookId;",
                new { BookId = bookId },
                cancellationToken: ct
            )
        );
    }

    public async Task SaveNovelProgressAsync(
        string bookId,
        int chapterIndex,
        double progress,
        int currentCharacterCount,
        int totalCharacterCount,
        CancellationToken ct = default
    )
    {
        using var connection = await GetOpenConnectionAsync();
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE NovelBooks
                SET CurrentChapterIndex = @ChapterIndex,
                    Progress = @Progress,
                    CurrentCharacterCount = @CurrentCharacterCount,
                    TotalCharacterCount = @TotalCharacterCount
                WHERE Id = @BookId;
                """,
                new
                {
                    BookId = bookId,
                    ChapterIndex = chapterIndex,
                    Progress = progress,
                    CurrentCharacterCount = currentCharacterCount,
                    TotalCharacterCount = totalCharacterCount,
                },
                cancellationToken: ct
            )
        );
    }

    public async Task SaveNovelBookOrderAsync(
        IReadOnlyList<string> orderedBookIds,
        CancellationToken ct = default
    )
    {
        using var connection = await GetOpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(ct);

        for (var index = 0; index < orderedBookIds.Count; index++)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE NovelBooks
                    SET ManualSortOrder = @ManualSortOrder
                    WHERE Id = @BookId;
                    """,
                    new { BookId = orderedBookIds[index], ManualSortOrder = index },
                    transaction,
                    cancellationToken: ct
                )
            );
        }

        await transaction.CommitAsync(ct);
    }
}
