using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Niratan.Helpers;
using Niratan.Models.Video;

namespace Niratan.Services.Video;

internal sealed class AniDbCatalogStore : IAniDbCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan UnrecognizedRetryDelay = TimeSpan.FromDays(30);
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public AniDbCatalogStore() : this(Path.Combine(AppDataHelper.GetDataPath(), "anidb.sqlite3")) { }
    internal AniDbCatalogStore(string path) => _path = Path.GetFullPath(path);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await InitializeCoreAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        if (_initialized) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var connection = await OpenAsync(ct);
        await connection.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS asset_state(
                asset_id TEXT PRIMARY KEY NOT NULL,
                identity_key TEXT NOT NULL,
                ed2k TEXT,
                crc32 TEXT,
                md5 TEXT,
                sha1 TEXT,
                file_size INTEGER NOT NULL DEFAULT 0,
                modified_at TEXT,
                hashed_at TEXT,
                file_id INTEGER,
                anime_id INTEGER,
                file_match_json TEXT,
                mylist_json TEXT,
                last_error TEXT,
                updated_at TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_anidb_asset_identity ON asset_state(identity_key);
            CREATE INDEX IF NOT EXISTS idx_anidb_asset_file ON asset_state(file_id);
            CREATE INDEX IF NOT EXISTS idx_anidb_asset_anime ON asset_state(anime_id);
            CREATE TABLE IF NOT EXISTS anime(
                anime_id INTEGER PRIMARY KEY NOT NULL,
                title TEXT NOT NULL,
                anime_json TEXT NOT NULL,
                fetched_at TEXT NOT NULL,
                expires_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS episode(
                episode_id INTEGER PRIMARY KEY NOT NULL,
                anime_id INTEGER NOT NULL,
                episode_type INTEGER NOT NULL,
                episode_number INTEGER NOT NULL,
                episode_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_anidb_episode_anime ON episode(anime_id,episode_type,episode_number);
            CREATE TABLE IF NOT EXISTS relation(
                anime_id INTEGER NOT NULL,
                related_anime_id INTEGER NOT NULL,
                relation_type TEXT NOT NULL,
                title TEXT,
                verified INTEGER,
                fetched_at TEXT,
                PRIMARY KEY(anime_id,related_anime_id,relation_type)
            );
            CREATE TABLE IF NOT EXISTS stored_release(
                ed2k TEXT NOT NULL,
                file_size INTEGER NOT NULL,
                file_id INTEGER,
                anime_id INTEGER,
                release_json TEXT,
                matched_at TEXT NOT NULL,
                last_error TEXT,
                status TEXT NOT NULL DEFAULT 'never',
                next_retry_at TEXT,
                prevent_rescan INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(ed2k,file_size)
            );
            CREATE INDEX IF NOT EXISTS idx_anidb_release_file ON stored_release(file_id);
            CREATE INDEX IF NOT EXISTS idx_anidb_release_anime ON stored_release(anime_id);
            CREATE TABLE IF NOT EXISTS file_episode_link(
                ed2k TEXT NOT NULL,
                file_size INTEGER NOT NULL,
                anime_id INTEGER NOT NULL,
                episode_id INTEGER NOT NULL,
                percentage INTEGER NOT NULL,
                is_other INTEGER NOT NULL,
                ordinal INTEGER NOT NULL,
                is_manual INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(ed2k,file_size,episode_id,ordinal)
            );
            CREATE INDEX IF NOT EXISTS idx_anidb_file_episode_eid ON file_episode_link(episode_id);
            CREATE TABLE IF NOT EXISTS release_match_attempt(
                id TEXT PRIMARY KEY NOT NULL,
                asset_id TEXT NOT NULL,
                ed2k TEXT,
                file_size INTEGER,
                provider_id TEXT NOT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT NOT NULL,
                result TEXT NOT NULL,
                error TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_anidb_match_attempt_asset
                ON release_match_attempt(asset_id,completed_at DESC);
            CREATE TABLE IF NOT EXISTS anime_group(
                group_id TEXT PRIMARY KEY NOT NULL,
                main_anime_id INTEGER NOT NULL,
                is_manual INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS anime_group_member(
                group_id TEXT NOT NULL,
                anime_id INTEGER NOT NULL UNIQUE,
                ordinal INTEGER NOT NULL,
                is_manual INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(group_id,anime_id)
            );
            CREATE INDEX IF NOT EXISTS idx_anidb_group_member_group
                ON anime_group_member(group_id,ordinal);
            CREATE TABLE IF NOT EXISTS import_job(
                asset_id TEXT PRIMARY KEY NOT NULL,
                stage TEXT NOT NULL,
                state TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                scheduled_at TEXT NOT NULL,
                last_error TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_anidb_import_job_due
                ON import_job(state,scheduled_at,created_at);
            CREATE TABLE IF NOT EXISTS mylist_job(
                asset_id TEXT PRIMARY KEY NOT NULL,
                watched INTEGER NOT NULL,
                state TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                scheduled_at TEXT NOT NULL,
                last_error TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_anidb_mylist_job_due
                ON mylist_job(state,scheduled_at,created_at);
            CREATE TABLE IF NOT EXISTS remote_mylist(
                mylist_id INTEGER PRIMARY KEY NOT NULL,
                file_id INTEGER,
                entry_json TEXT NOT NULL,
                fetched_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_anidb_remote_mylist_file
                ON remote_mylist(file_id);
            CREATE TABLE IF NOT EXISTS mylist_snapshot_state(
                singleton_id INTEGER PRIMARY KEY NOT NULL CHECK(singleton_id=1),
                fetched_at TEXT NOT NULL,
                item_count INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS anime_title(
                anime_id INTEGER NOT NULL,
                language TEXT NOT NULL,
                title_type TEXT NOT NULL,
                value TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                PRIMARY KEY(anime_id,language,title_type,value)
            );
            CREATE TABLE IF NOT EXISTS episode_title(
                episode_id INTEGER NOT NULL,
                anime_id INTEGER NOT NULL,
                language TEXT NOT NULL,
                value TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                PRIMARY KEY(episode_id,language,value)
            );
            CREATE TABLE IF NOT EXISTS tag(
                tag_id INTEGER PRIMARY KEY NOT NULL,
                parent_tag_id INTEGER,
                name TEXT NOT NULL,
                description TEXT,
                verified INTEGER NOT NULL,
                updated_at TEXT
            );
            CREATE TABLE IF NOT EXISTS anime_tag(
                anime_id INTEGER NOT NULL,
                tag_id INTEGER NOT NULL,
                weight INTEGER NOT NULL,
                local_spoiler INTEGER NOT NULL,
                global_spoiler INTEGER NOT NULL,
                PRIMARY KEY(anime_id,tag_id)
            );
            CREATE TABLE IF NOT EXISTS creator(
                creator_id INTEGER PRIMARY KEY NOT NULL,
                name TEXT NOT NULL,
                picture TEXT
            );
            CREATE TABLE IF NOT EXISTS anime_creator(
                anime_id INTEGER NOT NULL,
                creator_id INTEGER NOT NULL,
                role TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                PRIMARY KEY(anime_id,creator_id,role)
            );
            CREATE TABLE IF NOT EXISTS character(
                character_id INTEGER PRIMARY KEY NOT NULL,
                name TEXT NOT NULL,
                character_type TEXT,
                appearance_type TEXT,
                gender TEXT,
                description TEXT,
                picture TEXT
            );
            CREATE TABLE IF NOT EXISTS anime_character(
                anime_id INTEGER NOT NULL,
                character_id INTEGER NOT NULL,
                ordinal INTEGER NOT NULL,
                PRIMARY KEY(anime_id,character_id)
            );
            CREATE TABLE IF NOT EXISTS character_voice_actor(
                character_id INTEGER NOT NULL,
                creator_id INTEGER NOT NULL,
                ordinal INTEGER NOT NULL,
                PRIMARY KEY(character_id,creator_id)
            );
            CREATE TABLE IF NOT EXISTS anime_resource(
                anime_id INTEGER NOT NULL,
                resource_type INTEGER NOT NULL,
                identifier TEXT NOT NULL,
                PRIMARY KEY(anime_id,resource_type,identifier)
            );
            CREATE TABLE IF NOT EXISTS similar_anime(
                anime_id INTEGER NOT NULL,
                related_anime_id INTEGER NOT NULL,
                approval INTEGER NOT NULL,
                total INTEGER NOT NULL,
                PRIMARY KEY(anime_id,related_anime_id)
            );
            """);
        await EnsureAssetHashColumnsAsync(connection);
        await EnsureRelationColumnsAsync(connection);
        await EnsureReleaseStateColumnsAsync(connection);
        await EnsureFileEpisodeLinkColumnsAsync(connection);
        await EnsureMatchAttemptIdentityColumnsAsync(connection);
        await connection.ExecuteAsync("""
            CREATE INDEX IF NOT EXISTS idx_anidb_match_attempt_release
                ON release_match_attempt(ed2k,file_size,completed_at DESC);
            UPDATE release_match_attempt
            SET ed2k=(SELECT lower(asset.ed2k) FROM asset_state asset
                      WHERE asset.asset_id=release_match_attempt.asset_id),
                file_size=(SELECT asset.file_size FROM asset_state asset
                           WHERE asset.asset_id=release_match_attempt.asset_id)
            WHERE ed2k IS NULL
              AND EXISTS(SELECT 1 FROM asset_state asset
                         WHERE asset.asset_id=release_match_attempt.asset_id
                           AND asset.ed2k IS NOT NULL);
            """);
        await connection.ExecuteAsync(
            "UPDATE import_job SET state='queued',scheduled_at=@Now,updated_at=@Now WHERE state='running';",
            new { Now = Format(DateTimeOffset.UtcNow) });
        await connection.ExecuteAsync("""
            UPDATE import_job
            SET stage='anime_metadata',state='queued',attempts=0,
                scheduled_at=@Now,last_error=NULL,updated_at=@Now
            WHERE state='completed'
              AND EXISTS(
                  SELECT 1 FROM asset_state asset
                  WHERE asset.asset_id=import_job.asset_id
                    AND asset.anime_id IS NOT NULL
                    AND asset.file_match_json IS NOT NULL
                    AND NOT EXISTS(
                        SELECT 1 FROM anime
                        WHERE anime.anime_id=asset.anime_id));
            """, new { Now = Format(DateTimeOffset.UtcNow) });
        await connection.ExecuteAsync("""
            UPDATE import_job
            SET stage='anime_metadata',state='queued',attempts=0,
                scheduled_at=@Now,last_error=NULL,updated_at=@Now
            WHERE state='failed'
              AND stage='anime_metadata'
              AND last_error LIKE 'AniDB rejected the HTTP API client ID/version.%'
              AND EXISTS(
                  SELECT 1 FROM asset_state asset
                  WHERE asset.asset_id=import_job.asset_id
                    AND asset.anime_id IS NOT NULL
                    AND asset.file_match_json IS NOT NULL);
            """, new { Now = Format(DateTimeOffset.UtcNow) });
        await connection.ExecuteAsync(
            "UPDATE mylist_job SET state='queued',scheduled_at=@Now,updated_at=@Now WHERE state='running';",
            new { Now = Format(DateTimeOffset.UtcNow) });
        _initialized = true;
    }

    public Task<AniDbAssetSnapshot?> GetAssetAsync(Guid assetId, CancellationToken ct = default) =>
        WithLockAsync(async connection => Map(await connection.QuerySingleOrDefaultAsync<Row>(
            "SELECT * FROM asset_state WHERE asset_id=@Id", new { Id = assetId.ToString("D") })), ct);

    public Task<ImmutableArray<AniDbAssetSnapshot>> GetAssetsAsync(CancellationToken ct = default) =>
        WithLockAsync(async connection => (await connection.QueryAsync<Row>("SELECT * FROM asset_state"))
            .Select(Map).Where(item => item != null).Cast<AniDbAssetSnapshot>().ToImmutableArray(), ct);

    public Task UpsertHashAsync(Guid assetId, string identityKey, AniDbEd2kHash hash, CancellationToken ct = default) =>
        ExecuteAsync("""
            INSERT INTO asset_state(asset_id,identity_key,ed2k,crc32,md5,sha1,file_size,modified_at,hashed_at,updated_at)
            VALUES(@Id,@Identity,@Hash,@Crc32,@Md5,@Sha1,@Size,@Modified,@Hashed,@Updated)
            ON CONFLICT(asset_id) DO UPDATE SET identity_key=excluded.identity_key,ed2k=excluded.ed2k,
              crc32=excluded.crc32,md5=excluded.md5,sha1=excluded.sha1,
              file_size=excluded.file_size,modified_at=excluded.modified_at,hashed_at=excluded.hashed_at,
              file_id=NULL,anime_id=NULL,file_match_json=NULL,last_error=NULL,updated_at=excluded.updated_at;
            """, new { Id = assetId.ToString("D"), Identity = identityKey, Hash = NormalizeEd2k(hash.Value),
                hash.Crc32, hash.Md5, hash.Sha1,
                Size = hash.FileSize, Modified = Format(hash.ModifiedAt), Hashed = Format(hash.HashedAt),
                Updated = Format(DateTimeOffset.UtcNow) }, ct);

    public Task UpsertFileMatchAsync(
        Guid assetId,
        AniDbFileMatch? match,
        string? error,
        CancellationToken ct = default) => WithLockAsync(async connection =>
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var identity = await connection.QuerySingleOrDefaultAsync<ReleaseIdentityRow>(
            "SELECT ed2k,file_size FROM asset_state WHERE asset_id=@Id;",
            new { Id = assetId.ToString("D") }, transaction);
        if (identity is { ed2k.Length: > 0 })
        {
            var ed2k = NormalizeEd2k(identity.ed2k);
            var existing = await ReadReleaseStateCoreAsync(
                connection,
                transaction,
                ed2k,
                identity.file_size);
            if (existing is { PreventRescan: true, Status: not AniDbReleaseStatus.Never })
            {
                await BindReleaseToAssetCoreAsync(
                    connection,
                    transaction,
                    assetId,
                    existing.Match,
                    existing.LastError,
                    existing.UpdatedAt ?? DateTimeOffset.UtcNow);
                await transaction.CommitAsync(ct);
                return true;
            }

            var now = DateTimeOffset.UtcNow;
            var state = new AniDbReleaseState(
                ed2k,
                identity.file_size,
                match == null ? AniDbReleaseStatus.Unrecognized : AniDbReleaseStatus.Matched,
                match,
                match == null ? now.Add(UnrecognizedRetryDelay) : null,
                false,
                error,
                now);
            await WriteReleaseStateCoreAsync(connection, transaction, state);
            await BindReleaseToAssetCoreAsync(connection, transaction, assetId, match, error, now);
        }
        else
        {
            await BindReleaseToAssetCoreAsync(
                connection,
                transaction,
                assetId,
                match,
                error,
                DateTimeOffset.UtcNow);
        }
        await transaction.CommitAsync(ct);
        return true;
    }, ct);

    public Task<AniDbFileMatch?> GetFileMatchByHashAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct = default) => WithLockAsync(async connection =>
            (await ReadReleaseStateCoreAsync(
                connection,
                null,
                NormalizeEd2k(ed2k),
                fileSize))?.Match, ct);

    public Task<AniDbReleaseState> GetReleaseStateAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct = default) => WithLockAsync(async connection =>
            await ReadReleaseStateCoreAsync(
                connection,
                null,
                NormalizeEd2k(ed2k),
                fileSize)
            ?? NeverRelease(ed2k, fileSize), ct);

    public Task LinkManualReleaseAsync(
        string ed2k,
        long fileSize,
        AniDbManualReleaseLink link,
        CancellationToken ct = default) => WithLockAsync(async connection =>
    {
        ValidateReleaseKey(ed2k, fileSize);
        ValidateManualLink(link);
        var normalized = NormalizeEd2k(ed2k);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var prior = await ReadReleaseStateCoreAsync(connection, transaction, normalized, fileSize);
        var priorMatch = prior?.Match;
        var episodes = link.Episodes
            .OrderBy(item => item.Ordinal)
            .ThenBy(item => item.EpisodeId)
            .Select(item => item with { IsManual = true })
            .ToImmutableArray();
        var match = new AniDbFileMatch(
            link.FileId,
            link.AnimeId,
            priorMatch?.GroupId,
            priorMatch?.GroupName,
            priorMatch?.GroupShortName,
            priorMatch?.Deprecated ?? false,
            Math.Max(1, priorMatch?.Version ?? 1),
            priorMatch?.Censored,
            priorMatch?.CrcMatches,
            priorMatch?.Chaptered ?? false,
            priorMatch?.Quality,
            priorMatch?.Source ?? "Manual",
            priorMatch?.AudioLanguages ?? [],
            priorMatch?.SubtitleLanguages ?? [],
            priorMatch?.Description,
            priorMatch?.FileName,
            priorMatch?.ReleasedAt,
            episodes);
        var state = new AniDbReleaseState(
            normalized,
            fileSize,
            AniDbReleaseStatus.Manual,
            match,
            null,
            true,
            null,
            DateTimeOffset.UtcNow);
        await WriteReleaseStateCoreAsync(connection, transaction, state);
        await transaction.CommitAsync(ct);
        return true;
    }, ct);

    public Task UnlinkReleaseAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct = default) => WriteEmptyReleaseStateAsync(
            ed2k,
            fileSize,
            AniDbReleaseStatus.Unrecognized,
            preventRescan: true,
            ct);

    public Task IgnoreReleaseAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct = default) => WriteEmptyReleaseStateAsync(
            ed2k,
            fileSize,
            AniDbReleaseStatus.Ignored,
            preventRescan: true,
            ct);

    public Task ClearReleaseAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct = default) => WithLockAsync(async connection =>
    {
        ValidateReleaseKey(ed2k, fileSize);
        var normalized = NormalizeEd2k(ed2k);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("""
            DELETE FROM file_episode_link WHERE lower(ed2k)=@Ed2k AND file_size=@FileSize;
            DELETE FROM stored_release WHERE lower(ed2k)=@Ed2k AND file_size=@FileSize;
            UPDATE asset_state SET file_id=NULL,anime_id=NULL,file_match_json=NULL,last_error=NULL,
                updated_at=@Updated
            WHERE lower(ed2k)=@Ed2k AND file_size=@FileSize;
            """, new
        {
            Ed2k = normalized,
            FileSize = fileSize,
            Updated = Format(DateTimeOffset.UtcNow),
        }, transaction);
        await transaction.CommitAsync(ct);
        return true;
    }, ct);

    public Task ResetReleaseForRescanAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct = default) => ClearReleaseAsync(ed2k, fileSize, ct);

    public Task UpsertMyListAsync(Guid assetId, AniDbMyListEntry? entry, string? error, CancellationToken ct = default) =>
        ExecuteAsync("UPDATE asset_state SET mylist_json=@Json,last_error=@Error,updated_at=@Updated WHERE asset_id=@Id",
            new { Id = assetId.ToString("D"), Json = entry == null ? null : JsonSerializer.Serialize(entry, JsonOptions),
                Error = error, Updated = Format(DateTimeOffset.UtcNow) }, ct);

    public Task ReplaceRemoteMyListAsync(
        ImmutableArray<AniDbMyListEntry> entries,
        DateTimeOffset fetchedAt,
        CancellationToken ct = default) => WithLockAsync(async connection =>
    {
        var snapshot = entries.IsDefault ? ImmutableArray<AniDbMyListEntry>.Empty : entries;
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("DELETE FROM remote_mylist;", transaction: transaction);
        foreach (var entry in snapshot.Where(item => item.MyListId is > 0))
        {
            await connection.ExecuteAsync("""
                INSERT INTO remote_mylist(mylist_id,file_id,entry_json,fetched_at)
                VALUES(@MyListId,@FileId,@Json,@FetchedAt);
                """, new
            {
                MyListId = entry.MyListId!.Value,
                entry.FileId,
                Json = JsonSerializer.Serialize(entry, JsonOptions),
                FetchedAt = Format(fetchedAt),
            }, transaction);
        }
        await connection.ExecuteAsync("""
            INSERT INTO mylist_snapshot_state(singleton_id,fetched_at,item_count)
            VALUES(1,@FetchedAt,@ItemCount)
            ON CONFLICT(singleton_id) DO UPDATE SET
                fetched_at=excluded.fetched_at,item_count=excluded.item_count;
            """, new
        {
            FetchedAt = Format(fetchedAt),
            ItemCount = snapshot.Count(item => item.MyListId is > 0),
        }, transaction);
        await transaction.CommitAsync(ct);
        return true;
    }, ct);

    public Task<ImmutableArray<AniDbMyListEntry>> GetRemoteMyListAsync(CancellationToken ct = default) =>
        WithLockAsync(async connection => (await connection.QueryAsync<string>(
                "SELECT entry_json FROM remote_mylist ORDER BY mylist_id;"))
            .Select(Deserialize<AniDbMyListEntry>)
            .Where(item => item != null)
            .Cast<AniDbMyListEntry>()
            .ToImmutableArray(), ct);

    public Task UpsertAnimeAsync(AniDbAnime anime, CancellationToken ct = default) => WithLockAsync(async connection =>
    {
        var titles = anime.Titles.IsDefault ? [] : anime.Titles;
        var episodes = anime.Episodes.IsDefault ? [] : anime.Episodes;
        var relations = anime.Relations.IsDefault ? [] : anime.Relations;
        var tags = anime.Tags.IsDefault ? [] : anime.Tags;
        var creators = anime.Creators.IsDefault ? [] : anime.Creators;
        var characters = anime.Characters.IsDefault ? [] : anime.Characters;
        var resources = anime.Resources.IsDefault ? [] : anime.Resources;
        var similar = anime.SimilarAnime.IsDefault ? [] : anime.SimilarAnime;
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("""
            INSERT INTO anime(anime_id,title,anime_json,fetched_at,expires_at) VALUES(@Id,@Title,@Json,@Fetched,@Expires)
            ON CONFLICT(anime_id) DO UPDATE SET title=excluded.title,anime_json=excluded.anime_json,
              fetched_at=excluded.fetched_at,expires_at=excluded.expires_at;
            DELETE FROM episode WHERE anime_id=@Id;
            DELETE FROM relation WHERE anime_id=@Id;
            DELETE FROM anime_title WHERE anime_id=@Id;
            DELETE FROM episode_title WHERE anime_id=@Id;
            DELETE FROM anime_tag WHERE anime_id=@Id;
            DELETE FROM anime_creator WHERE anime_id=@Id;
            DELETE FROM anime_character WHERE anime_id=@Id;
            DELETE FROM anime_resource WHERE anime_id=@Id;
            DELETE FROM similar_anime WHERE anime_id=@Id;
            """, new { Id = anime.AnimeId, anime.Title, Json = JsonSerializer.Serialize(anime, JsonOptions),
                Fetched = Format(anime.FetchedAt), Expires = Format(anime.ExpiresAt) }, transaction);
        for (var index = 0; index < titles.Length; index++)
        {
            var title = titles[index];
            await connection.ExecuteAsync("""
                INSERT OR REPLACE INTO anime_title(
                    anime_id,language,title_type,value,ordinal)
                VALUES(@AnimeId,@Language,@Type,@Value,@Ordinal);
                """, new
            {
                AnimeId = anime.AnimeId,
                title.Language,
                Type = title.Type,
                title.Value,
                Ordinal = index,
            }, transaction);
        }
        foreach (var episode in episodes)
        {
            await connection.ExecuteAsync("INSERT OR REPLACE INTO episode VALUES(@EpisodeId,@AnimeId,@Type,@Number,@Json)",
                new { episode.EpisodeId, episode.AnimeId, Type = (int)episode.Type, episode.Number,
                    Json = JsonSerializer.Serialize(episode, JsonOptions) }, transaction);
            var episodeTitles = episode.Titles.IsDefault ? [] : episode.Titles;
            for (var index = 0; index < episodeTitles.Length; index++)
            {
                var title = episodeTitles[index];
                await connection.ExecuteAsync("""
                    INSERT OR REPLACE INTO episode_title(
                        episode_id,anime_id,language,value,ordinal)
                    VALUES(@EpisodeId,@AnimeId,@Language,@Value,@Ordinal);
                    """, new
                {
                    episode.EpisodeId,
                    episode.AnimeId,
                    title.Language,
                    title.Value,
                    Ordinal = index,
                }, transaction);
            }
        }
        foreach (var relation in relations)
            await connection.ExecuteAsync("""
                INSERT OR REPLACE INTO relation(
                    anime_id,related_anime_id,relation_type,title,verified,fetched_at)
                VALUES(@AnimeId,@RelatedAnimeId,@Type,@Title,@Verified,@FetchedAt);
                """, new
            {
                relation.AnimeId,
                relation.RelatedAnimeId,
                relation.Type,
                relation.Title,
                Verified = relation.Verified.HasValue ? (int?)(relation.Verified.Value ? 1 : 0) : null,
                FetchedAt = Format(anime.FetchedAt),
            }, transaction);
        foreach (var tag in tags)
        {
            await connection.ExecuteAsync("""
                INSERT INTO tag(tag_id,parent_tag_id,name,description,verified,updated_at)
                VALUES(@TagId,@ParentTagId,@Name,@Description,@Verified,@UpdatedAt)
                ON CONFLICT(tag_id) DO UPDATE SET parent_tag_id=excluded.parent_tag_id,
                    name=excluded.name,description=excluded.description,
                    verified=excluded.verified,updated_at=excluded.updated_at;
                INSERT INTO anime_tag(anime_id,tag_id,weight,local_spoiler,global_spoiler)
                VALUES(@AnimeId,@TagId,@Weight,@LocalSpoiler,@GlobalSpoiler);
                """, new
            {
                tag.TagId,
                tag.ParentTagId,
                tag.Name,
                tag.Description,
                Verified = tag.Verified ? 1 : 0,
                UpdatedAt = tag.UpdatedAt.HasValue ? Format(tag.UpdatedAt.Value) : null,
                AnimeId = anime.AnimeId,
                tag.Weight,
                LocalSpoiler = tag.LocalSpoiler ? 1 : 0,
                GlobalSpoiler = tag.GlobalSpoiler ? 1 : 0,
            }, transaction);
        }
        for (var index = 0; index < creators.Length; index++)
        {
            var creator = creators[index];
            await connection.ExecuteAsync("""
                INSERT INTO creator(creator_id,name) VALUES(@CreatorId,@Name)
                ON CONFLICT(creator_id) DO UPDATE SET name=excluded.name;
                INSERT INTO anime_creator(anime_id,creator_id,role,ordinal)
                VALUES(@AnimeId,@CreatorId,@Role,@Ordinal);
                """, new
            {
                creator.CreatorId,
                creator.Name,
                AnimeId = anime.AnimeId,
                creator.Role,
                Ordinal = index,
            }, transaction);
        }
        for (var index = 0; index < characters.Length; index++)
        {
            var character = characters[index];
            await connection.ExecuteAsync("""
                INSERT INTO character(
                    character_id,name,character_type,appearance_type,gender,description,picture)
                VALUES(@CharacterId,@Name,@CharacterType,@AppearanceType,@Gender,@Description,@Picture)
                ON CONFLICT(character_id) DO UPDATE SET name=excluded.name,
                    character_type=excluded.character_type,appearance_type=excluded.appearance_type,
                    gender=excluded.gender,description=excluded.description,picture=excluded.picture;
                INSERT INTO anime_character(anime_id,character_id,ordinal)
                VALUES(@AnimeId,@CharacterId,@Ordinal);
                DELETE FROM character_voice_actor WHERE character_id=@CharacterId;
                """, new
            {
                character.CharacterId,
                character.Name,
                CharacterType = character.Type,
                character.AppearanceType,
                character.Gender,
                character.Description,
                character.Picture,
                AnimeId = anime.AnimeId,
                Ordinal = index,
            }, transaction);
            var voiceActors = character.VoiceActors.IsDefault ? [] : character.VoiceActors;
            for (var voiceIndex = 0; voiceIndex < voiceActors.Length; voiceIndex++)
            {
                var actor = voiceActors[voiceIndex];
                await connection.ExecuteAsync("""
                    INSERT INTO creator(creator_id,name,picture) VALUES(@CreatorId,@Name,@Picture)
                    ON CONFLICT(creator_id) DO UPDATE SET name=excluded.name,
                        picture=COALESCE(excluded.picture,creator.picture);
                    INSERT INTO character_voice_actor(character_id,creator_id,ordinal)
                    VALUES(@CharacterId,@CreatorId,@Ordinal);
                    """, new
                {
                    actor.CreatorId,
                    actor.Name,
                    actor.Picture,
                    character.CharacterId,
                    Ordinal = voiceIndex,
                }, transaction);
            }
        }
        foreach (var resource in resources)
            await connection.ExecuteAsync("""
                INSERT OR REPLACE INTO anime_resource(anime_id,resource_type,identifier)
                VALUES(@AnimeId,@Type,@Identifier);
                """, new { AnimeId = anime.AnimeId, resource.Type, resource.Identifier }, transaction);
        foreach (var item in similar)
            await connection.ExecuteAsync("""
                INSERT OR REPLACE INTO similar_anime(anime_id,related_anime_id,approval,total)
                VALUES(@AnimeId,@RelatedAnimeId,@Approval,@Total);
                """, new
            {
                AnimeId = anime.AnimeId,
                RelatedAnimeId = item.AnimeId,
                item.Approval,
                item.Total,
            }, transaction);
        await RecalculateGroupsCoreAsync(connection, (SqliteTransaction)transaction);
        await transaction.CommitAsync(ct);
        return true;
    }, ct);

    public Task<AniDbAnime?> GetAnimeAsync(int animeId, CancellationToken ct = default) => WithLockAsync(async connection =>
    {
        var json = await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT anime_json FROM anime WHERE anime_id=@Id", new { Id = animeId });
        return json == null ? null : JsonSerializer.Deserialize<AniDbAnime>(json, JsonOptions);
    }, ct);

    public Task<AniDbAnime?> GetAnimeByEpisodeAsync(int episodeId, CancellationToken ct = default) =>
        WithLockAsync(async connection =>
        {
            var json = await connection.QuerySingleOrDefaultAsync<string?>("""
                SELECT a.anime_json
                FROM episode e
                JOIN anime a ON a.anime_id=e.anime_id
                WHERE e.episode_id=@EpisodeId
                LIMIT 1;
                """, new { EpisodeId = episodeId });
            return json == null ? null : JsonSerializer.Deserialize<AniDbAnime>(json, JsonOptions);
        }, ct);

    public Task<AniDbAnimeGroup> MaterializeGroupAsync(
        int animeId,
        CancellationToken ct = default) => WithLockAsync(async connection =>
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await RecalculateGroupsCoreAsync(connection, (SqliteTransaction)transaction);
        var group = await ReadGroupByAnimeIdAsync(connection, (SqliteTransaction)transaction, animeId)
            ?? throw new KeyNotFoundException($"AniDB anime {animeId} is not cached.");
        await transaction.CommitAsync(ct);
        return group;
    }, ct);

    public Task EnqueueImportJobAsync(Guid assetId, CancellationToken ct = default) =>
        ExecuteAsync("""
            INSERT INTO import_job(
                asset_id,stage,state,attempts,scheduled_at,last_error,created_at,updated_at)
            VALUES(@Asset,'queued','queued',0,@Now,NULL,@Now,@Now)
            ON CONFLICT(asset_id) DO UPDATE SET
                stage=CASE WHEN import_job.state='running' THEN import_job.stage ELSE 'queued' END,
                state=CASE WHEN import_job.state='running' THEN import_job.state ELSE 'queued' END,
                attempts=CASE WHEN import_job.state='running' THEN import_job.attempts ELSE 0 END,
                scheduled_at=CASE WHEN import_job.state='running' THEN import_job.scheduled_at ELSE @Now END,
                last_error=CASE WHEN import_job.state='running' THEN import_job.last_error ELSE NULL END,
                updated_at=@Now;
            """, new { Asset = assetId.ToString("D"), Now = Format(DateTimeOffset.UtcNow) }, ct);

    public Task<AniDbImportJob?> ClaimImportJobAsync(
        DateTimeOffset now,
        CancellationToken ct = default) => WithLockAsync(async connection =>
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<ImportJobRow>("""
            SELECT * FROM import_job
            WHERE state IN ('queued','retry') AND scheduled_at<=@Now
            ORDER BY scheduled_at,created_at,asset_id
            LIMIT 1;
            """, new { Now = Format(now) }, transaction);
        if (row == null)
        {
            await transaction.CommitAsync(ct);
            return null;
        }
        await connection.ExecuteAsync("""
            UPDATE import_job SET state='running',updated_at=@Now WHERE asset_id=@Asset;
            """, new { Asset = row.asset_id, Now = Format(now) }, transaction);
        await transaction.CommitAsync(ct);
        row.state = "running";
        row.updated_at = Format(now);
        return MapImportJob(row);
    }, ct);

    public Task AdvanceImportJobAsync(
        Guid assetId,
        AniDbImportJobStage stage,
        CancellationToken ct = default) => ExecuteAsync("""
            UPDATE import_job SET stage=@Stage,state='running',last_error=NULL,updated_at=@Now
            WHERE asset_id=@Asset;
            """, new
        {
            Asset = assetId.ToString("D"),
            Stage = ToDb(stage),
            Now = Format(DateTimeOffset.UtcNow),
        }, ct);

    public Task RetryImportJobAsync(
        Guid assetId,
        AniDbImportJobStage stage,
        int attempts,
        DateTimeOffset scheduledAt,
        string error,
        bool terminal,
        CancellationToken ct = default) => ExecuteAsync("""
            UPDATE import_job SET stage=@Stage,state=@State,attempts=@Attempts,
                scheduled_at=@Scheduled,last_error=@Error,updated_at=@Now
            WHERE asset_id=@Asset;
            """, new
        {
            Asset = assetId.ToString("D"),
            Stage = ToDb(stage),
            State = terminal ? "failed" : "retry",
            Attempts = Math.Max(0, attempts),
            Scheduled = Format(scheduledAt),
            Error = string.IsNullOrWhiteSpace(error) ? "AniDB import failed." : error,
            Now = Format(DateTimeOffset.UtcNow),
        }, ct);

    public Task CompleteImportJobAsync(Guid assetId, CancellationToken ct = default) =>
        ExecuteAsync("""
            UPDATE import_job SET stage='completed',state='completed',last_error=NULL,updated_at=@Now
            WHERE asset_id=@Asset;
            """, new { Asset = assetId.ToString("D"), Now = Format(DateTimeOffset.UtcNow) }, ct);

    public Task<ImmutableArray<AniDbImportJob>> GetImportJobsAsync(CancellationToken ct = default) =>
        WithLockAsync(async connection => (await connection.QueryAsync<ImportJobRow>(
                "SELECT * FROM import_job ORDER BY created_at,asset_id;"))
            .Select(MapImportJob).ToImmutableArray(), ct);

    public Task RecordMatchAttemptAsync(
        AniDbReleaseMatchAttempt attempt,
        CancellationToken ct = default) => WithLockAsync(async connection =>
    {
        var ed2k = string.IsNullOrWhiteSpace(attempt.Ed2k)
            ? null
            : NormalizeEd2k(attempt.Ed2k);
        var fileSize = attempt.FileSize;
        if (ed2k == null)
        {
            var identity = await connection.QuerySingleOrDefaultAsync<ReleaseIdentityRow>(
                "SELECT ed2k,file_size FROM asset_state WHERE asset_id=@Asset;",
                new { Asset = attempt.AssetId.ToString("D") });
            if (identity is { ed2k.Length: > 0 })
            {
                ed2k = NormalizeEd2k(identity.ed2k);
                fileSize = identity.file_size;
            }
        }
        await connection.ExecuteAsync("""
            INSERT OR REPLACE INTO release_match_attempt(
                id,asset_id,ed2k,file_size,provider_id,started_at,completed_at,result,error)
            VALUES(@Id,@Asset,@Ed2k,@FileSize,@Provider,@Started,@Completed,@Result,@Error);
            """, new
        {
            Id = attempt.Id.ToString("D"),
            Asset = attempt.AssetId.ToString("D"),
            Ed2k = ed2k,
            FileSize = ed2k == null ? (long?)null : fileSize,
            Provider = attempt.ProviderId,
            Started = Format(attempt.StartedAt),
            Completed = Format(attempt.CompletedAt),
            attempt.Result,
            attempt.Error,
        });
        return true;
    }, ct);

    public Task<ImmutableArray<AniDbReleaseMatchAttempt>> GetMatchAttemptsAsync(
        Guid assetId,
        CancellationToken ct = default) => WithLockAsync(async connection =>
            (await connection.QueryAsync<MatchAttemptRow>("""
                SELECT * FROM release_match_attempt WHERE asset_id=@Asset
                ORDER BY completed_at,id;
                """, new { Asset = assetId.ToString("D") }))
            .Select(row => new AniDbReleaseMatchAttempt(
                Guid.Parse(row.id),
                Guid.Parse(row.asset_id),
                row.provider_id,
                Parse(row.started_at) ?? DateTimeOffset.MinValue,
                Parse(row.completed_at) ?? DateTimeOffset.MinValue,
                row.result,
                row.error)
            {
                Ed2k = row.ed2k,
                FileSize = row.file_size ?? 0,
            })
            .ToImmutableArray(), ct);

    public Task<ImmutableArray<AniDbReleaseMatchAttempt>> GetMatchAttemptsAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct = default) => WithLockAsync(async connection =>
            (await connection.QueryAsync<MatchAttemptRow>("""
                SELECT * FROM release_match_attempt
                WHERE lower(ed2k)=@Ed2k AND file_size=@FileSize
                ORDER BY completed_at,id;
                """, new { Ed2k = NormalizeEd2k(ed2k), FileSize = fileSize }))
            .Select(row => new AniDbReleaseMatchAttempt(
                Guid.Parse(row.id),
                Guid.Parse(row.asset_id),
                row.provider_id,
                Parse(row.started_at) ?? DateTimeOffset.MinValue,
                Parse(row.completed_at) ?? DateTimeOffset.MinValue,
                row.result,
                row.error)
            {
                Ed2k = row.ed2k,
                FileSize = row.file_size ?? 0,
            })
            .ToImmutableArray(), ct);

    public Task EnqueueMyListJobAsync(
        Guid assetId,
        bool watched,
        CancellationToken ct = default) => ExecuteAsync("""
            INSERT INTO mylist_job(
                asset_id,watched,state,attempts,scheduled_at,last_error,created_at,updated_at)
            VALUES(@Asset,@Watched,'queued',0,@Now,NULL,@Now,@Now)
            ON CONFLICT(asset_id) DO UPDATE SET watched=excluded.watched,state='queued',
                attempts=0,scheduled_at=excluded.scheduled_at,last_error=NULL,updated_at=excluded.updated_at;
            """, new
        {
            Asset = assetId.ToString("D"),
            Watched = watched ? 1 : 0,
            Now = Format(DateTimeOffset.UtcNow),
        }, ct);

    public Task<AniDbMyListJob?> ClaimMyListJobAsync(
        DateTimeOffset now,
        CancellationToken ct = default) => WithLockAsync(async connection =>
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<MyListJobRow>("""
            SELECT * FROM mylist_job
            WHERE state IN ('queued','retry') AND scheduled_at<=@Now
            ORDER BY scheduled_at,created_at,asset_id LIMIT 1;
            """, new { Now = Format(now) }, transaction);
        if (row == null)
        {
            await transaction.CommitAsync(ct);
            return null;
        }
        await connection.ExecuteAsync("""
            UPDATE mylist_job SET state='running',updated_at=@Now WHERE asset_id=@Asset;
            """, new { Asset = row.asset_id, Now = Format(now) }, transaction);
        await transaction.CommitAsync(ct);
        row.state = "running";
        row.updated_at = Format(now);
        return MapMyListJob(row);
    }, ct);

    public Task RetryMyListJobAsync(
        Guid assetId,
        int attempts,
        DateTimeOffset scheduledAt,
        string error,
        bool terminal,
        CancellationToken ct = default) => ExecuteAsync("""
            UPDATE mylist_job SET state=@State,attempts=@Attempts,scheduled_at=@Scheduled,
                last_error=@Error,updated_at=@Now WHERE asset_id=@Asset;
            """, new
        {
            Asset = assetId.ToString("D"),
            State = terminal ? "failed" : "retry",
            Attempts = Math.Max(0, attempts),
            Scheduled = Format(scheduledAt),
            Error = string.IsNullOrWhiteSpace(error) ? "AniDB MyList update failed." : error,
            Now = Format(DateTimeOffset.UtcNow),
        }, ct);

    public Task CompleteMyListJobAsync(Guid assetId, CancellationToken ct = default) =>
        ExecuteAsync("DELETE FROM mylist_job WHERE asset_id=@Asset;",
            new { Asset = assetId.ToString("D") }, ct);

    public Task<ImmutableArray<AniDbMyListJob>> GetMyListJobsAsync(CancellationToken ct = default) =>
        WithLockAsync(async connection => (await connection.QueryAsync<MyListJobRow>(
                "SELECT * FROM mylist_job ORDER BY created_at,asset_id;"))
            .Select(MapMyListJob).ToImmutableArray(), ct);

    public Task<IReadOnlyCollection<VideoManualAniDbIdentity>> GetManualCatalogIdentitiesAsync(
        CancellationToken ct = default) =>
        WithLockAsync(async connection =>
        {
            var rows = (await connection.QueryAsync<ManualCatalogIdentityRow>(
                """
                SELECT asset.asset_id,release.anime_id AS release_anime_id,
                       link.anime_id AS episode_anime_id,link.episode_id
                FROM asset_state asset
                JOIN stored_release release
                  ON lower(release.ed2k)=lower(asset.ed2k)
                 AND release.file_size=asset.file_size
                LEFT JOIN file_episode_link link
                  ON lower(link.ed2k)=lower(release.ed2k)
                 AND link.file_size=release.file_size
                 AND link.is_manual=1
                WHERE release.status='manual'
                ORDER BY asset.asset_id,link.ordinal,link.episode_id;
                """)).ToArray();
            return (IReadOnlyCollection<VideoManualAniDbIdentity>)rows
                .GroupBy(row => row.asset_id, StringComparer.OrdinalIgnoreCase)
                .Select(group => new VideoManualAniDbIdentity(
                    Guid.Parse(group.Key),
                    group.SelectMany(row => new[]
                        {
                            row.release_anime_id,
                            row.episode_anime_id,
                        })
                        .Where(value => value is > 0)
                        .Select(value => value!.Value)
                        .ToImmutableHashSet(),
                    group.Where(row => row.episode_id is > 0)
                        .Select(row => row.episode_id!.Value)
                        .ToImmutableHashSet()))
                .ToImmutableArray();
        }, ct);

    public Task ClearScrapingRecordsAsync(CancellationToken ct = default) =>
        WithLockAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(
                """
                DELETE FROM import_job;
                DELETE FROM release_match_attempt;

                DELETE FROM file_episode_link WHERE is_manual=0;
                DELETE FROM stored_release
                WHERE status NOT IN ('manual','ignored') AND prevent_rescan=0;

                UPDATE asset_state SET last_error=NULL;
                UPDATE asset_state
                SET file_id=NULL,
                    anime_id=NULL,
                    file_match_json=NULL,
                    updated_at=@Now
                WHERE NOT EXISTS(
                    SELECT 1 FROM stored_release release
                    WHERE lower(release.ed2k)=lower(asset_state.ed2k)
                      AND release.file_size=asset_state.file_size);

                DELETE FROM anime_group_member WHERE is_manual=0;
                DELETE FROM anime_group WHERE is_manual=0;

                DELETE FROM character_voice_actor;
                DELETE FROM anime_character;
                DELETE FROM character;
                DELETE FROM anime_creator;
                DELETE FROM creator;
                DELETE FROM anime_tag;
                DELETE FROM tag;
                DELETE FROM similar_anime;
                DELETE FROM anime_resource;
                DELETE FROM episode_title;
                DELETE FROM anime_title;
                DELETE FROM relation;
                DELETE FROM episode;
                DELETE FROM anime;
                """,
                new { Now = Format(DateTimeOffset.UtcNow) },
                transaction);
            await transaction.CommitAsync(ct);
            return true;
        }, ct);

    private Task WriteEmptyReleaseStateAsync(
        string ed2k,
        long fileSize,
        AniDbReleaseStatus status,
        bool preventRescan,
        CancellationToken ct) => WithLockAsync(async connection =>
    {
        ValidateReleaseKey(ed2k, fileSize);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await WriteReleaseStateCoreAsync(connection, transaction, new AniDbReleaseState(
            NormalizeEd2k(ed2k),
            fileSize,
            status,
            null,
            null,
            preventRescan,
            null,
            DateTimeOffset.UtcNow));
        await transaction.CommitAsync(ct);
        return true;
    }, ct);

    private static async Task<AniDbReleaseState?> ReadReleaseStateCoreAsync(
        SqliteConnection connection,
        System.Data.IDbTransaction? transaction,
        string ed2k,
        long fileSize)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ReleaseStateRow>("""
            SELECT * FROM stored_release
            WHERE lower(ed2k)=@Ed2k AND file_size=@FileSize
            ORDER BY matched_at DESC LIMIT 1;
            """, new { Ed2k = NormalizeEd2k(ed2k), FileSize = fileSize }, transaction);
        if (row == null)
            return null;
        var match = Deserialize<AniDbFileMatch>(row.release_json);
        return new AniDbReleaseState(
            NormalizeEd2k(row.ed2k),
            row.file_size,
            ParseReleaseStatus(row.status, match, row.prevent_rescan != 0),
            match,
            Parse(row.next_retry_at),
            row.prevent_rescan != 0,
            row.last_error,
            Parse(row.matched_at));
    }

    private static async Task WriteReleaseStateCoreAsync(
        SqliteConnection connection,
        System.Data.IDbTransaction transaction,
        AniDbReleaseState state)
    {
        var ed2k = NormalizeEd2k(state.Ed2k);
        var updatedAt = state.UpdatedAt ?? DateTimeOffset.UtcNow;
        var json = state.Match == null ? null : JsonSerializer.Serialize(state.Match, JsonOptions);
        await connection.ExecuteAsync("""
            DELETE FROM file_episode_link
            WHERE lower(ed2k)=@Ed2k AND file_size=@FileSize;
            DELETE FROM stored_release
            WHERE lower(ed2k)=@Ed2k AND ed2k<>@Ed2k AND file_size=@FileSize;
            INSERT INTO stored_release(
                ed2k,file_size,file_id,anime_id,release_json,matched_at,last_error,
                status,next_retry_at,prevent_rescan)
            VALUES(@Ed2k,@FileSize,@FileId,@AnimeId,@Json,@Updated,@Error,
                @Status,@NextRetryAt,@PreventRescan)
            ON CONFLICT(ed2k,file_size) DO UPDATE SET
                file_id=excluded.file_id,anime_id=excluded.anime_id,
                release_json=excluded.release_json,matched_at=excluded.matched_at,
                last_error=excluded.last_error,status=excluded.status,
                next_retry_at=excluded.next_retry_at,prevent_rescan=excluded.prevent_rescan;
            UPDATE asset_state SET file_id=@FileId,anime_id=@AnimeId,file_match_json=@Json,
                last_error=@Error,updated_at=@Updated
            WHERE lower(ed2k)=@Ed2k AND file_size=@FileSize;
            """, new
        {
            Ed2k = ed2k,
            state.FileSize,
            FileId = state.Match?.FileId,
            AnimeId = state.Match?.AnimeId,
            Json = json,
            Updated = Format(updatedAt),
            Error = state.LastError,
            Status = ToDb(state.Status),
            NextRetryAt = state.NextRetryAt is { } retryAt ? Format(retryAt) : null,
            PreventRescan = state.PreventRescan ? 1 : 0,
        }, transaction);
        if (state.Match == null)
            return;
        var links = state.Match.Episodes.IsDefault
            ? ImmutableArray<AniDbFileEpisodeLink>.Empty
            : state.Match.Episodes;
        foreach (var link in links.OrderBy(item => item.Ordinal).ThenBy(item => item.EpisodeId))
        {
            await connection.ExecuteAsync("""
                INSERT INTO file_episode_link(
                    ed2k,file_size,anime_id,episode_id,percentage,is_other,ordinal,is_manual)
                VALUES(@Ed2k,@FileSize,@AnimeId,@EpisodeId,@Percentage,@IsOther,@Ordinal,@IsManual);
                """, new
            {
                Ed2k = ed2k,
                state.FileSize,
                AnimeId = link.AnimeId > 0 ? link.AnimeId : state.Match.AnimeId,
                link.EpisodeId,
                Percentage = Math.Clamp((int)link.Percentage, 0, 100),
                IsOther = link.IsOther ? 1 : 0,
                link.Ordinal,
                IsManual = state.Status == AniDbReleaseStatus.Manual || link.IsManual ? 1 : 0,
            }, transaction);
        }
    }

    private static Task BindReleaseToAssetCoreAsync(
        SqliteConnection connection,
        System.Data.IDbTransaction transaction,
        Guid assetId,
        AniDbFileMatch? match,
        string? error,
        DateTimeOffset updatedAt) => connection.ExecuteAsync("""
            UPDATE asset_state SET file_id=@FileId,anime_id=@AnimeId,file_match_json=@Json,
              last_error=@Error,updated_at=@Updated WHERE asset_id=@Id;
            """, new
        {
            Id = assetId.ToString("D"),
            FileId = match?.FileId,
            AnimeId = match?.AnimeId,
            Json = match == null ? null : JsonSerializer.Serialize(match, JsonOptions),
            Error = error,
            Updated = Format(updatedAt),
        }, transaction);

    private static AniDbReleaseState NeverRelease(string ed2k, long fileSize) => new(
        NormalizeEd2k(ed2k),
        fileSize,
        AniDbReleaseStatus.Never,
        null,
        null,
        false,
        null,
        null);

    private static void ValidateReleaseKey(string ed2k, long fileSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ed2k);
        if (ed2k.Trim().Length != 32 || ed2k.Trim().Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("AniDB release ED2K must be a 32-character hexadecimal hash.", nameof(ed2k));
        if (fileSize < 0)
            throw new ArgumentOutOfRangeException(nameof(fileSize));
    }

    private static void ValidateManualLink(AniDbManualReleaseLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (link.FileId <= 0)
            throw new ArgumentOutOfRangeException(nameof(link), "AniDB FID must be positive.");
        if (link.AnimeId <= 0)
            throw new ArgumentOutOfRangeException(nameof(link), "AniDB AID must be positive.");
        if (link.Episodes.IsDefaultOrEmpty)
            throw new ArgumentException("A manual AniDB release needs at least one EID.", nameof(link));
        if (link.Episodes.Any(item => item.EpisodeId <= 0
                                      || item.AnimeId <= 0
                                      || item.Percentage is 0 or > 100
                                      || item.Ordinal < 0))
            throw new ArgumentException(
                "Every manual EID needs a positive owner AID, a 1-100 percentage, and a non-negative order.",
                nameof(link));
        if (link.Episodes
            .GroupBy(item => (item.EpisodeId, item.Ordinal))
            .Any(group => group.Count() > 1))
            throw new ArgumentException("Manual AniDB EID/order pairs must be unique.", nameof(link));
    }

    private static async Task RecalculateGroupsCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var animeRows = (await connection.QueryAsync<AnimeGraphRow>(
            "SELECT anime_id,anime_json FROM anime ORDER BY anime_id;",
            transaction: transaction)).ToList();
        if (animeRows.Count == 0)
            return;

        var animeIds = animeRows.Select(row => row.anime_id).ToHashSet();
        var adjacency = animeIds.ToDictionary(id => id, _ => new HashSet<int>());
        var relations = await connection.QueryAsync<RelationRow>(
            "SELECT anime_id,related_anime_id,relation_type,verified FROM relation;",
            transaction: transaction);
        foreach (var relation in relations)
        {
            if (!animeIds.Contains(relation.anime_id)
                || !animeIds.Contains(relation.related_anime_id)
                || relation.verified == 0
                || !IsGroupingRelation(relation.relation_type))
                continue;
            adjacency[relation.anime_id].Add(relation.related_anime_id);
            adjacency[relation.related_anime_id].Add(relation.anime_id);
        }

        var components = new List<HashSet<int>>();
        var visited = new HashSet<int>();
        foreach (var animeId in animeIds.Order())
        {
            if (!visited.Add(animeId))
                continue;
            var component = new HashSet<int> { animeId };
            var queue = new Queue<int>();
            queue.Enqueue(animeId);
            while (queue.TryDequeue(out var current))
            {
                foreach (var related in adjacency[current])
                {
                    if (!visited.Add(related))
                        continue;
                    component.Add(related);
                    queue.Enqueue(related);
                }
            }
            components.Add(component);
        }

        var existingGroups = (await connection.QueryAsync<GroupRow>(
            "SELECT * FROM anime_group ORDER BY created_at,group_id;",
            transaction: transaction)).ToDictionary(row => row.group_id, StringComparer.OrdinalIgnoreCase);
        var existingMembers = (await connection.QueryAsync<GroupMemberRow>(
            "SELECT * FROM anime_group_member ORDER BY ordinal,anime_id;",
            transaction: transaction)).ToList();
        var groupIdsByAnime = existingMembers
            .GroupBy(row => row.anime_id)
            .ToDictionary(group => group.Key, group => group.Select(row => row.group_id).ToArray());
        var usedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = Format(DateTimeOffset.UtcNow);

        // Automatic memberships are a projection of the current verified relation graph.
        // Manual memberships remain untouched and win when a component contains one.
        await connection.ExecuteAsync(
            "DELETE FROM anime_group_member WHERE is_manual=0;",
            transaction: transaction);

        foreach (var component in components
                     .OrderBy(values => values.Min()))
        {
            var candidateIds = component
                .SelectMany(id => groupIdsByAnime.GetValueOrDefault(id) ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(existingGroups.ContainsKey)
                .ToList();
            var selected = candidateIds
                .Select(id => existingGroups[id])
                .Where(row => row.is_manual != 0 && !usedGroups.Contains(row.group_id))
                .OrderBy(row => row.created_at, StringComparer.Ordinal)
                .FirstOrDefault()
                ?? candidateIds
                    .Select(id => existingGroups[id])
                    .Where(row => component.Contains(row.main_anime_id)
                                  && !usedGroups.Contains(row.group_id))
                    .OrderBy(row => row.created_at, StringComparer.Ordinal)
                    .FirstOrDefault()
                ?? candidateIds
                    .Select(id => existingGroups[id])
                    .Where(row => !usedGroups.Contains(row.group_id))
                    .OrderBy(row => row.created_at, StringComparer.Ordinal)
                    .FirstOrDefault();
            var groupId = selected?.group_id ?? Guid.NewGuid().ToString("D");
            usedGroups.Add(groupId);
            var isManual = selected?.is_manual != 0;
            var mainAnimeId = isManual && selected != null && component.Contains(selected.main_anime_id)
                ? selected.main_anime_id
                : SelectMainAnimeId(component, animeRows);
            var createdAt = selected?.created_at ?? now;
            await connection.ExecuteAsync("""
                INSERT INTO anime_group(group_id,main_anime_id,is_manual,created_at,updated_at)
                VALUES(@GroupId,@MainAnimeId,@Manual,@Created,@Updated)
                ON CONFLICT(group_id) DO UPDATE SET
                    main_anime_id=CASE WHEN anime_group.is_manual=1
                        THEN anime_group.main_anime_id ELSE excluded.main_anime_id END,
                    updated_at=excluded.updated_at;
                """, new
            {
                GroupId = groupId,
                MainAnimeId = mainAnimeId,
                Manual = isManual ? 1 : 0,
                Created = createdAt,
                Updated = now,
            }, transaction);
            var ordinal = 0;
            foreach (var memberAnimeId in component
                         .OrderBy(id => id == mainAnimeId ? 0 : 1)
                         .ThenBy(id => id))
            {
                var hasManualMembership = existingMembers.Any(row =>
                    row.anime_id == memberAnimeId && row.is_manual != 0);
                if (hasManualMembership && !existingMembers.Any(row =>
                        row.anime_id == memberAnimeId
                        && row.is_manual != 0
                        && row.group_id.Equals(groupId, StringComparison.OrdinalIgnoreCase)))
                    continue;
                await connection.ExecuteAsync("""
                    INSERT OR REPLACE INTO anime_group_member(
                        group_id,anime_id,ordinal,is_manual)
                    VALUES(@GroupId,@AnimeId,@Ordinal,@Manual);
                    """, new
                {
                    GroupId = groupId,
                    AnimeId = memberAnimeId,
                    Ordinal = ordinal++,
                    Manual = hasManualMembership ? 1 : 0,
                }, transaction);
            }
        }

        await connection.ExecuteAsync("""
            DELETE FROM anime_group
            WHERE is_manual=0
              AND NOT EXISTS(
                  SELECT 1 FROM anime_group_member member
                  WHERE member.group_id=anime_group.group_id);
            """, transaction: transaction);
    }

    private static int SelectMainAnimeId(
        IReadOnlySet<int> component,
        IReadOnlyList<AnimeGraphRow> animeRows) => animeRows
        .Where(row => component.Contains(row.anime_id))
        .Select(row =>
        {
            var anime = Deserialize<AniDbAnime>(row.anime_json);
            return (row.anime_id, StartDate: ParseDateOnly(anime?.StartDate));
        })
        .OrderBy(row => row.StartDate ?? DateOnly.MaxValue)
        .ThenBy(row => row.anime_id)
        .First().anime_id;

    private static bool IsGroupingRelation(string relationType)
    {
        var normalized = new string((relationType ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return normalized is "prequel" or "sequel" or "parentstory" or "sidestory"
            or "fullstory" or "summary";
    }

    private static async Task<AniDbAnimeGroup?> ReadGroupByAnimeIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int animeId)
    {
        var group = await connection.QuerySingleOrDefaultAsync<GroupRow>("""
            SELECT g.* FROM anime_group g
            JOIN anime_group_member member ON member.group_id=g.group_id
            WHERE member.anime_id=@AnimeId LIMIT 1;
            """, new { AnimeId = animeId }, transaction);
        if (group == null)
            return null;
        var members = (await connection.QueryAsync<int>("""
            SELECT anime_id FROM anime_group_member
            WHERE group_id=@GroupId ORDER BY ordinal,anime_id;
            """, new { GroupId = group.group_id }, transaction)).ToImmutableArray();
        return new AniDbAnimeGroup(
            Guid.Parse(group.group_id),
            group.main_anime_id,
            members,
            group.is_manual != 0,
            Parse(group.created_at) ?? DateTimeOffset.MinValue,
            Parse(group.updated_at) ?? DateTimeOffset.MinValue);
    }

    private static AniDbImportJob MapImportJob(ImportJobRow row) => new(
        Guid.Parse(row.asset_id),
        ParseImportStage(row.stage),
        ParseImportState(row.state),
        row.attempts,
        Parse(row.scheduled_at) ?? DateTimeOffset.MinValue,
        Parse(row.created_at) ?? DateTimeOffset.MinValue,
        Parse(row.updated_at) ?? DateTimeOffset.MinValue,
        row.last_error);

    private static AniDbMyListJob MapMyListJob(MyListJobRow row) => new(
        Guid.Parse(row.asset_id),
        row.watched != 0,
        ParseImportState(row.state),
        row.attempts,
        Parse(row.scheduled_at) ?? DateTimeOffset.MinValue,
        Parse(row.updated_at) ?? DateTimeOffset.MinValue,
        row.last_error);

    private static string ToDb(AniDbReleaseStatus status) => status switch
    {
        AniDbReleaseStatus.Matched => "matched",
        AniDbReleaseStatus.Unrecognized => "unrecognized",
        AniDbReleaseStatus.Ignored => "ignored",
        AniDbReleaseStatus.Manual => "manual",
        _ => "never",
    };

    private static AniDbReleaseStatus ParseReleaseStatus(
        string? value,
        AniDbFileMatch? match,
        bool preventRescan) => value?.Trim().ToLowerInvariant() switch
    {
        "matched" => AniDbReleaseStatus.Matched,
        "unrecognized" => AniDbReleaseStatus.Unrecognized,
        "ignored" => AniDbReleaseStatus.Ignored,
        "manual" => AniDbReleaseStatus.Manual,
        _ when match != null => AniDbReleaseStatus.Matched,
        _ when preventRescan => AniDbReleaseStatus.Ignored,
        _ => AniDbReleaseStatus.Unrecognized,
    };

    private static string ToDb(AniDbImportJobStage stage) => stage switch
    {
        AniDbImportJobStage.Hashing => "hashing",
        AniDbImportJobStage.FileLookup => "file_lookup",
        AniDbImportJobStage.AnimeMetadata => "anime_metadata",
        AniDbImportJobStage.Grouping => "grouping",
        AniDbImportJobStage.CatalogProjection => "catalog_projection",
        AniDbImportJobStage.MyList => "mylist",
        AniDbImportJobStage.Completed => "completed",
        _ => "queued",
    };

    private static AniDbImportJobStage ParseImportStage(string value) => value switch
    {
        "hashing" => AniDbImportJobStage.Hashing,
        "file_lookup" => AniDbImportJobStage.FileLookup,
        "anime_metadata" => AniDbImportJobStage.AnimeMetadata,
        "grouping" => AniDbImportJobStage.Grouping,
        "catalog_projection" => AniDbImportJobStage.CatalogProjection,
        "mylist" => AniDbImportJobStage.MyList,
        "completed" => AniDbImportJobStage.Completed,
        _ => AniDbImportJobStage.Queued,
    };

    private static AniDbImportJobState ParseImportState(string value) => value switch
    {
        "running" => AniDbImportJobState.Running,
        "retry" => AniDbImportJobState.Retry,
        "completed" => AniDbImportJobState.Completed,
        "failed" => AniDbImportJobState.Failed,
        _ => AniDbImportJobState.Queued,
    };

    private Task ExecuteAsync(string sql, object parameters, CancellationToken ct) => WithLockAsync(async connection =>
    { await connection.ExecuteAsync(sql, parameters); return true; }, ct);

    private async Task<T> WithLockAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await InitializeCoreAsync(ct);
            await using var connection = await OpenAsync(ct);
            return await action(connection);
        }
        finally { _gate.Release(); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = false }.ToString());
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        return connection;
    }

    private static async Task EnsureAssetHashColumnsAsync(SqliteConnection connection)
    {
        var columns = (await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('asset_state')"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!columns.Contains("crc32"))
            await connection.ExecuteAsync("ALTER TABLE asset_state ADD COLUMN crc32 TEXT");
        if (!columns.Contains("md5"))
            await connection.ExecuteAsync("ALTER TABLE asset_state ADD COLUMN md5 TEXT");
        if (!columns.Contains("sha1"))
            await connection.ExecuteAsync("ALTER TABLE asset_state ADD COLUMN sha1 TEXT");
    }

    private static async Task EnsureRelationColumnsAsync(SqliteConnection connection)
    {
        var columns = (await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('relation')"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!columns.Contains("verified"))
            await connection.ExecuteAsync("ALTER TABLE relation ADD COLUMN verified INTEGER");
        if (!columns.Contains("fetched_at"))
            await connection.ExecuteAsync("ALTER TABLE relation ADD COLUMN fetched_at TEXT");
    }

    private static async Task EnsureReleaseStateColumnsAsync(SqliteConnection connection)
    {
        var columns = (await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('stored_release')"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!columns.Contains("status"))
            await connection.ExecuteAsync(
                "ALTER TABLE stored_release ADD COLUMN status TEXT NOT NULL DEFAULT 'never'");
        if (!columns.Contains("next_retry_at"))
            await connection.ExecuteAsync("ALTER TABLE stored_release ADD COLUMN next_retry_at TEXT");
        if (!columns.Contains("prevent_rescan"))
            await connection.ExecuteAsync(
                "ALTER TABLE stored_release ADD COLUMN prevent_rescan INTEGER NOT NULL DEFAULT 0");
        await connection.ExecuteAsync("""
            UPDATE stored_release
            SET status=CASE
                WHEN release_json IS NOT NULL THEN 'matched'
                WHEN prevent_rescan<>0 THEN 'ignored'
                ELSE 'unrecognized'
            END
            WHERE status IS NULL OR lower(status) NOT IN ('matched','unrecognized','ignored','manual')
               OR lower(status)='never';
            UPDATE stored_release SET next_retry_at=@RetryAt
            WHERE status='unrecognized' AND prevent_rescan=0 AND next_retry_at IS NULL;
            CREATE INDEX IF NOT EXISTS idx_anidb_release_retry
                ON stored_release(status,prevent_rescan,next_retry_at);
            """, new { RetryAt = Format(DateTimeOffset.UtcNow.Add(UnrecognizedRetryDelay)) });
    }

    private static async Task EnsureFileEpisodeLinkColumnsAsync(SqliteConnection connection)
    {
        var columns = (await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('file_episode_link')"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!columns.Contains("is_manual"))
            await connection.ExecuteAsync(
                "ALTER TABLE file_episode_link ADD COLUMN is_manual INTEGER NOT NULL DEFAULT 0");
    }

    private static async Task EnsureMatchAttemptIdentityColumnsAsync(SqliteConnection connection)
    {
        var columns = (await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('release_match_attempt')"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!columns.Contains("ed2k"))
            await connection.ExecuteAsync("ALTER TABLE release_match_attempt ADD COLUMN ed2k TEXT");
        if (!columns.Contains("file_size"))
            await connection.ExecuteAsync("ALTER TABLE release_match_attempt ADD COLUMN file_size INTEGER");
    }

    private static AniDbAssetSnapshot? Map(Row? row) => row == null ? null : new AniDbAssetSnapshot(
        Guid.Parse(row.asset_id), row.ed2k, row.file_size, Parse(row.modified_at), Parse(row.hashed_at),
        Deserialize<AniDbFileMatch>(row.file_match_json), Deserialize<AniDbMyListEntry>(row.mylist_json), row.last_error)
    {
        Crc32 = row.crc32,
        Md5 = row.md5,
        Sha1 = row.sha1,
    };
    private static T? Deserialize<T>(string? json) where T : class => json == null ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);
    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset? Parse(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
    private static DateOnly? ParseDateOnly(string? value) => DateOnly.TryParse(
        value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    private static string NormalizeEd2k(string value) => value.Trim().ToLowerInvariant();

    private sealed class Row
    {
        public string asset_id { get; set; } = "";
        public string? ed2k { get; set; }
        public string? crc32 { get; set; }
        public string? md5 { get; set; }
        public string? sha1 { get; set; }
        public long file_size { get; set; }
        public string? modified_at { get; set; }
        public string? hashed_at { get; set; }
        public string? file_match_json { get; set; }
        public string? mylist_json { get; set; }
        public string? last_error { get; set; }
    }

    private sealed class ReleaseIdentityRow
    {
        public string? ed2k { get; set; }
        public long file_size { get; set; }
    }

    private sealed class ManualCatalogIdentityRow
    {
        public string asset_id { get; set; } = "";
        public int? release_anime_id { get; set; }
        public int? episode_anime_id { get; set; }
        public int? episode_id { get; set; }
    }

    private sealed class ReleaseStateRow
    {
        public string ed2k { get; set; } = "";
        public long file_size { get; set; }
        public string? release_json { get; set; }
        public string matched_at { get; set; } = "";
        public string? last_error { get; set; }
        public string? status { get; set; }
        public string? next_retry_at { get; set; }
        public int prevent_rescan { get; set; }
    }

    private sealed class ImportJobRow
    {
        public string asset_id { get; set; } = "";
        public string stage { get; set; } = "queued";
        public string state { get; set; } = "queued";
        public int attempts { get; set; }
        public string scheduled_at { get; set; } = "";
        public string? last_error { get; set; }
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";
    }

    private sealed class MyListJobRow
    {
        public string asset_id { get; set; } = "";
        public int watched { get; set; }
        public string state { get; set; } = "queued";
        public int attempts { get; set; }
        public string scheduled_at { get; set; } = "";
        public string? last_error { get; set; }
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";
    }

    private sealed class MatchAttemptRow
    {
        public string id { get; set; } = "";
        public string asset_id { get; set; } = "";
        public string? ed2k { get; set; }
        public long? file_size { get; set; }
        public string provider_id { get; set; } = "";
        public string started_at { get; set; } = "";
        public string completed_at { get; set; } = "";
        public string result { get; set; } = "";
        public string? error { get; set; }
    }

    private sealed class AnimeGraphRow
    {
        public int anime_id { get; set; }
        public string anime_json { get; set; } = "";
    }

    private sealed class RelationRow
    {
        public int anime_id { get; set; }
        public int related_anime_id { get; set; }
        public string relation_type { get; set; } = "";
        public int? verified { get; set; }
    }

    private sealed class GroupRow
    {
        public string group_id { get; set; } = "";
        public int main_anime_id { get; set; }
        public int is_manual { get; set; }
        public string created_at { get; set; } = "";
        public string updated_at { get; set; } = "";
    }

    private sealed class GroupMemberRow
    {
        public string group_id { get; set; } = "";
        public int anime_id { get; set; }
        public int ordinal { get; set; }
        public int is_manual { get; set; }
    }
}
