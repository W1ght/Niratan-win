using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Niratan.Models.Settings;
using Niratan.Models.Video;
using Niratan.Services.Settings;
using Niratan.Services.Storage;
using Niratan.Services.Video;
using Xunit;

namespace Niratan.Tests.Services.Video;

public sealed class AniDbClientTests
{
    [Fact]
    public async Task Ed2kHasher_UsesRfc1320Md4ForSingleChunk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"niratan-anidb-{Guid.NewGuid():N}.bin");
        try
        {
            await File.WriteAllBytesAsync(path, [], TestContext.Current.CancellationToken);
            var result = await new AniDbEd2kHasher().HashAsync(path, TestContext.Current.CancellationToken);
            result.Value.Should().Be("31d6cfe0d16ae931b73c59d7e0c089c0");
            result.Crc32.Should().Be("00000000");
            result.Md5.Should().Be("d41d8cd98f00b204e9800998ecf8427e");
            result.Sha1.Should().Be("da39a3ee5e6b4b0d3255bfef95601890afd80709");
            result.FileSize.Should().Be(0);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Ed2kHasher_ComputesShokoHashesInOnePass()
    {
        var path = Path.Combine(Path.GetTempPath(), $"niratan-anidb-{Guid.NewGuid():N}.bin");
        try
        {
            await File.WriteAllBytesAsync(path, "123456789"u8.ToArray(), TestContext.Current.CancellationToken);
            var result = await new AniDbEd2kHasher().HashAsync(path, TestContext.Current.CancellationToken);
            result.Value.Should().Be("2ae523785d0caf4d2fb557c12016185c");
            result.Crc32.Should().Be("cbf43926");
            result.Md5.Should().Be("25f9e794323b453885f5181f1b624d0b");
            result.Sha1.Should().Be("f7c3bc1d808e04732adf679965ccc34ca7ae3441");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void HttpAnimeParser_PreservesEpisodesRelationsAndTitles()
    {
        var xml = XDocument.Parse("""
            <anime id="123">
              <type>TV Series</type><episodecount>1</episodecount><startdate>2024-01-02</startdate>
              <enddate>2024-03-01</enddate><url>https://anidb.net/anime/123</url><picture>123.jpg</picture>
              <titles><title xml:lang="en" type="main">Example</title><title xml:lang="ja" type="official">例</title></titles>
              <episodes><episode id="456"><epno type="1">1</epno><length>24</length><airdate>2024-01-02</airdate><title xml:lang="en">Start</title></episode></episodes>
              <relatedanime><anime id="124" type="Sequel" verified="true">Example 2</anime></relatedanime>
              <similaranime><anime id="125" approval="10" total="12" /></similaranime>
              <tags><tag id="8" weight="400" verified="true" update="2024-01-01"><name>fantasy</name></tag></tags>
              <creators><name id="9" type="Animation Work">Studio</name></creators>
              <characters><character id="10" type="main character in"><name>Hero</name><charactertype>Character</charactertype><picture>char.jpg</picture><seiyuu id="11" picture="actor.jpg">Actor</seiyuu></character></characters>
              <resources><resource type="2"><externalentity><identifier>999</identifier></externalentity></resource><resource type="38"><externalentity><identifier>888</identifier></externalentity></resource></resources>
            </anime>
            """);
        var anime = AniDbHttpClient.ParseAnime(xml);
        anime.AnimeId.Should().Be(123);
        anime.Title.Should().Be("Example");
        anime.Episodes.Should().ContainSingle().Which.EpisodeId.Should().Be(456);
        anime.Relations.Should().ContainSingle().Which.RelatedAnimeId.Should().Be(124);
        anime.Tags.Should().ContainSingle().Which.Name.Should().Be("fantasy");
        anime.Characters.Should().ContainSingle().Which.VoiceActors.Should().ContainSingle();
        anime.Resources.Should().Contain(resource => resource.Type == 2 && resource.Identifier == "999");
        anime.SimilarAnime.Should().ContainSingle().Which.AnimeId.Should().Be(125);
        var details = AniDbImportService.ToDetails(anime);
        details.ExternalIds.Should().Contain("mal", "999").And.Contain("bangumi", "888");
        details.Studios.Should().ContainSingle("Studio");
        details.People.Should().Contain(person => person.Name == "Actor" && person.Role == "Hero");
        details.RelatedItems.Select(item => item.ProviderItemId).Should().Contain(["124", "125"]);
        AniDbTitleIndexProvider.AniDbImageUrl(anime.Picture).Should()
            .Be("https://cdn.anidb.net/images/main/123.jpg");
    }

    [Fact]
    public void ToDetails_PreservesAniDbSpecialTypePrefixes()
    {
        var xml = XDocument.Parse("""
            <anime id="123">
              <titles><title xml:lang="en" type="main">Example</title></titles>
              <episodes>
                <episode id="101"><epno type="2">S1</epno><title xml:lang="en">Special</title></episode>
                <episode id="102"><epno type="3">C1</epno><title xml:lang="en">Credits</title></episode>
                <episode id="103"><epno type="4">T1</epno><title xml:lang="en">Trailer</title></episode>
                <episode id="104"><epno type="5">P1</epno><title xml:lang="en">Parody</title></episode>
                <episode id="105"><epno type="6">O1</epno><title xml:lang="en">Other</title></episode>
              </episodes>
            </anime>
            """);

        var details = AniDbImportService.ToDetails(AniDbHttpClient.ParseAnime(xml));

        var specials = details.Seasons.Single(season => season.SeasonNumber == 0);
        specials.Episodes.Should().HaveCount(5);
        specials.Episodes.Select(episode => episode.EpisodeNumber).Should().OnlyContain(number => number == 1);
        specials.Episodes.Select(episode => episode.DisplayNumber)
            .Should().Equal("S1", "C1", "T1", "P1", "O1");
    }

    [Fact]
    public async Task CatalogStore_PersistsHashMatchAnimeAndMyListWithoutMediaWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), $"niratan-anidb-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new AniDbCatalogStore(Path.Combine(root, "anidb.sqlite3"));
            var assetId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await store.UpsertHashAsync(assetId, "C:\\Anime\\episode.mkv",
                new AniDbEd2kHash("31d6cfe0d16ae931b73c59d7e0c089c0", 0, now, now)
                {
                    Crc32 = "00000000",
                    Md5 = "d41d8cd98f00b204e9800998ecf8427e",
                    Sha1 = "da39a3ee5e6b4b0d3255bfef95601890afd80709",
                },
                TestContext.Current.CancellationToken);
            var match = new AniDbFileMatch(1, 123, null, null, null, false, 1, null, true, false,
                "HD", "Blu-ray", [], [], null, "episode.mkv", null, []);
            await store.UpsertFileMatchAsync(assetId, match, null, TestContext.Current.CancellationToken);
            await store.UpsertMyListAsync(assetId,
                new AniDbMyListEntry(2, 1, 456, 123, AniDbMyListState.OnHdd, true, now, now),
                null, TestContext.Current.CancellationToken);
            var restored = await store.GetAssetAsync(assetId, TestContext.Current.CancellationToken);
            restored.Should().NotBeNull();
            restored!.Ed2k.Should().Be("31d6cfe0d16ae931b73c59d7e0c089c0");
            restored.Crc32.Should().Be("00000000");
            restored.Md5.Should().Be("d41d8cd98f00b204e9800998ecf8427e");
            restored.Sha1.Should().Be("da39a3ee5e6b4b0d3255bfef95601890afd80709");
            restored.FileMatch!.AnimeId.Should().Be(123);
            restored.MyList!.Watched.Should().BeTrue();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CatalogStore_AddsHashColumnsWithoutReplacingLegacyAssetRows()
    {
        var root = Path.Combine(Path.GetTempPath(), $"niratan-anidb-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "anidb.sqlite3");
            var assetId = Guid.NewGuid();
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE asset_state(
                        asset_id TEXT PRIMARY KEY NOT NULL,
                        identity_key TEXT NOT NULL,
                        ed2k TEXT,
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
                    INSERT INTO asset_state(asset_id,identity_key,ed2k,file_size,updated_at)
                    VALUES($assetId,'C:\\Anime\\legacy.mkv','legacy-ed2k',42,'2024-01-01T00:00:00+00:00');
                    """;
                command.Parameters.AddWithValue("$assetId", assetId.ToString("D"));
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var store = new AniDbCatalogStore(path);
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            var restored = await store.GetAssetAsync(assetId, TestContext.Current.CancellationToken);
            restored.Should().NotBeNull();
            restored!.Ed2k.Should().Be("legacy-ed2k");
            restored.FileSize.Should().Be(42);
            restored.Crc32.Should().BeNull();
            restored.Md5.Should().BeNull();
            restored.Sha1.Should().BeNull();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CatalogStore_PersistsReleaseEpisodeGraphAndReusesContentMatch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"niratan-anidb-release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "anidb.sqlite3");
            var store = new AniDbCatalogStore(path);
            var assetId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            const string ed2k = "31d6cfe0d16ae931b73c59d7e0c089c0";
            await store.UpsertHashAsync(assetId, "C:\\Anime\\combined.mkv",
                new AniDbEd2kHash(ed2k, 100, now, now)
                {
                    Crc32 = "00000000",
                    Md5 = "d41d8cd98f00b204e9800998ecf8427e",
                    Sha1 = "da39a3ee5e6b4b0d3255bfef95601890afd80709",
                }, TestContext.Current.CancellationToken);
            var match = new AniDbFileMatch(
                9, 123, 7, "Group", "GRP", false, 1, null, true, false,
                "HD", "Blu-ray", ["japanese"], ["english"], null, "combined.mkv", null,
                [
                    new AniDbFileEpisodeLink(1001, 50, false, 0) { AnimeId = 123 },
                    new AniDbFileEpisodeLink(1002, 50, true, 1) { AnimeId = 456 },
                ]);

            await store.UpsertFileMatchAsync(assetId, match, null, TestContext.Current.CancellationToken);

            var cached = await store.GetFileMatchByHashAsync(ed2k, 100, TestContext.Current.CancellationToken);
            cached.Should().BeEquivalentTo(match);
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM file_episode_link WHERE ed2k=$ed2k AND file_size=100;";
            command.Parameters.AddWithValue("$ed2k", ed2k);
            (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).Should().Be(2L);
            command.CommandText = "SELECT COUNT(DISTINCT anime_id) FROM file_episode_link WHERE ed2k=$ed2k AND file_size=100;";
            (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).Should().Be(2L);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CatalogStore_TracksNegativeManualIgnoredAndNeverReleaseStatesByContentKey()
    {
        var root = Path.Combine(Path.GetTempPath(), $"niratan-anidb-release-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "anidb.sqlite3");
            var store = new AniDbCatalogStore(path);
            var assetId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var automatic = new AniDbFileMatch(
                99, 999, null, null, null, false, 1, null, true, false,
                "HD", "Web", [], [], null, null, null,
                [new AniDbFileEpisodeLink(9991, 100, false, 0) { AnimeId = 999 }]);
            await store.UpsertHashAsync(assetId, "C:\\Anime\\manual.mkv",
                new AniDbEd2kHash(Hash, 123, now, now),
                TestContext.Current.CancellationToken);

            var never = await store.GetReleaseStateAsync(Hash, 123, TestContext.Current.CancellationToken);
            never.Status.Should().Be(AniDbReleaseStatus.Never);
            never.IsAutomaticLookupDue(now).Should().BeTrue();

            await store.UpsertFileMatchAsync(assetId, null, null, TestContext.Current.CancellationToken);
            var unrecognized = await store.GetReleaseStateAsync(Hash, 123, TestContext.Current.CancellationToken);
            unrecognized.Status.Should().Be(AniDbReleaseStatus.Unrecognized);
            unrecognized.NextRetryAt.Should().BeAfter(now.AddDays(29));
            unrecognized.PreventRescan.Should().BeFalse();
            unrecognized.IsAutomaticLookupDue(DateTimeOffset.UtcNow).Should().BeFalse();

            var manual = new AniDbManualReleaseLink(7, 100,
            [
                new AniDbFileEpisodeLink(1001, 60, false, 0) { AnimeId = 100 },
                new AniDbFileEpisodeLink(2001, 40, true, 1) { AnimeId = 200 },
            ]);
            await store.LinkManualReleaseAsync(Hash.ToUpperInvariant(), 123, manual,
                TestContext.Current.CancellationToken);
            await store.UpsertFileMatchAsync(assetId, automatic, null,
                TestContext.Current.CancellationToken);

            var linked = await store.GetReleaseStateAsync(Hash, 123, TestContext.Current.CancellationToken);
            linked.Status.Should().Be(AniDbReleaseStatus.Manual);
            linked.PreventRescan.Should().BeTrue();
            linked.Match!.FileId.Should().Be(7);
            linked.Match.AnimeId.Should().Be(100);
            linked.Match.Episodes.Select(item => (item.EpisodeId, item.AnimeId, item.Percentage, item.Ordinal))
                .Should().Equal((1001, 100, (byte)60, 0), (2001, 200, (byte)40, 1));
            linked.Match.Episodes.Should().OnlyContain(item => item.IsManual);

            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                             $"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT SUM(is_manual) FROM file_episode_link WHERE ed2k=$ed2k AND file_size=123;";
                command.Parameters.AddWithValue("$ed2k", Hash);
                (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).Should().Be(2L);
            }

            await store.UnlinkReleaseAsync(Hash, 123, TestContext.Current.CancellationToken);
            var unlinked = await store.GetReleaseStateAsync(Hash, 123, TestContext.Current.CancellationToken);
            unlinked.Status.Should().Be(AniDbReleaseStatus.Unrecognized);
            unlinked.PreventRescan.Should().BeTrue();
            unlinked.Match.Should().BeNull();

            await store.LinkManualReleaseAsync(Hash, 123, manual,
                TestContext.Current.CancellationToken);
            await store.IgnoreReleaseAsync(Hash, 123, TestContext.Current.CancellationToken);
            await store.UpsertFileMatchAsync(assetId, automatic, null,
                TestContext.Current.CancellationToken);
            var ignored = await store.GetReleaseStateAsync(Hash, 123, TestContext.Current.CancellationToken);
            ignored.Status.Should().Be(AniDbReleaseStatus.Ignored);
            ignored.Match.Should().BeNull();
            ignored.PreventRescan.Should().BeTrue();

            await store.ClearReleaseAsync(Hash, 123, TestContext.Current.CancellationToken);
            (await store.GetReleaseStateAsync(Hash, 123, TestContext.Current.CancellationToken))
                .Status.Should().Be(AniDbReleaseStatus.Never);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CatalogStore_MigratesLegacyNegativeReleaseAndAttemptWithoutReplacingRows()
    {
        var root = Path.Combine(Path.GetTempPath(), $"niratan-anidb-release-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "anidb.sqlite3");
            var assetId = Guid.NewGuid();
            var attemptId = Guid.NewGuid();
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                             $"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE asset_state(
                        asset_id TEXT PRIMARY KEY NOT NULL, identity_key TEXT NOT NULL, ed2k TEXT,
                        file_size INTEGER NOT NULL DEFAULT 0, modified_at TEXT, hashed_at TEXT,
                        file_id INTEGER, anime_id INTEGER, file_match_json TEXT, mylist_json TEXT,
                        last_error TEXT, updated_at TEXT NOT NULL);
                    CREATE TABLE stored_release(
                        ed2k TEXT NOT NULL, file_size INTEGER NOT NULL, file_id INTEGER, anime_id INTEGER,
                        release_json TEXT, matched_at TEXT NOT NULL, last_error TEXT,
                        PRIMARY KEY(ed2k,file_size));
                    CREATE TABLE file_episode_link(
                        ed2k TEXT NOT NULL, file_size INTEGER NOT NULL, anime_id INTEGER NOT NULL,
                        episode_id INTEGER NOT NULL, percentage INTEGER NOT NULL, is_other INTEGER NOT NULL,
                        ordinal INTEGER NOT NULL, PRIMARY KEY(ed2k,file_size,episode_id,ordinal));
                    CREATE TABLE release_match_attempt(
                        id TEXT PRIMARY KEY NOT NULL, asset_id TEXT NOT NULL, provider_id TEXT NOT NULL,
                        started_at TEXT NOT NULL, completed_at TEXT NOT NULL, result TEXT NOT NULL, error TEXT);
                    INSERT INTO asset_state(asset_id,identity_key,ed2k,file_size,updated_at)
                    VALUES($asset,'C:\Anime\legacy.mkv',$ed2k,456,'2026-08-01T00:00:00+00:00');
                    INSERT INTO stored_release(ed2k,file_size,release_json,matched_at)
                    VALUES($ed2k,456,NULL,'2026-08-01T00:00:00+00:00');
                    INSERT INTO release_match_attempt(
                        id,asset_id,provider_id,started_at,completed_at,result,error)
                    VALUES($attempt,$asset,'anidb','2026-08-01T00:00:00+00:00',
                        '2026-08-01T00:00:01+00:00','unrecognized',NULL);
                    """;
                command.Parameters.AddWithValue("$asset", assetId.ToString("D"));
                command.Parameters.AddWithValue("$attempt", attemptId.ToString("D"));
                command.Parameters.AddWithValue("$ed2k", Hash);
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var migrated = new AniDbCatalogStore(path);
            await migrated.InitializeAsync(TestContext.Current.CancellationToken);
            var release = await migrated.GetReleaseStateAsync(Hash, 456,
                TestContext.Current.CancellationToken);
            release.Status.Should().Be(AniDbReleaseStatus.Unrecognized);
            release.NextRetryAt.Should().NotBeNull();
            release.IsAutomaticLookupDue(DateTimeOffset.UtcNow).Should().BeFalse();
            var attempts = await migrated.GetMatchAttemptsAsync(Hash, 456,
                TestContext.Current.CancellationToken);
            attempts.Should().ContainSingle();
            attempts[0].Id.Should().Be(attemptId);
            attempts[0].AssetId.Should().Be(assetId);
            attempts[0].Ed2k.Should().Be(Hash);
            attempts[0].FileSize.Should().Be(456);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CatalogStore_MaterializesStableVerifiedRelationGroupWithoutSameSettingMerge()
    {
        var root = Path.Combine(Path.GetTempPath(), $"niratan-anidb-group-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new AniDbCatalogStore(Path.Combine(root, "anidb.sqlite3"));
            var first = Anime(100, "First", "2020-01-01",
                [new AniDbRelation(100, 200, "Sequel", "Second") { Verified = true }]);
            var second = Anime(200, "Second", "2021-01-01",
                [new AniDbRelation(200, 100, "Prequel", "First") { Verified = true }]);
            var sameSetting = Anime(300, "Same setting", "2019-01-01",
                [new AniDbRelation(300, 100, "Same setting", "First") { Verified = true }]);
            await store.UpsertAnimeAsync(first, TestContext.Current.CancellationToken);
            await store.UpsertAnimeAsync(second, TestContext.Current.CancellationToken);
            await store.UpsertAnimeAsync(sameSetting, TestContext.Current.CancellationToken);

            var firstGroup = await store.MaterializeGroupAsync(100, TestContext.Current.CancellationToken);
            var secondGroup = await store.MaterializeGroupAsync(200, TestContext.Current.CancellationToken);
            var otherGroup = await store.MaterializeGroupAsync(300, TestContext.Current.CancellationToken);

            secondGroup.GroupId.Should().Be(firstGroup.GroupId);
            firstGroup.AnimeIds.Should().Equal([100, 200]);
            firstGroup.MainAnimeId.Should().Be(100);
            otherGroup.GroupId.Should().NotBe(firstGroup.GroupId);
            otherGroup.AnimeIds.Should().Equal([300]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CatalogStore_RecoversRunningImportJobAfterRestartAndPersistsAttempts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"niratan-anidb-job-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "anidb.sqlite3");
            var assetId = Guid.NewGuid();
            var firstStore = new AniDbCatalogStore(path);
            await firstStore.EnqueueImportJobAsync(assetId, TestContext.Current.CancellationToken);
            var claimed = await firstStore.ClaimImportJobAsync(
                DateTimeOffset.UtcNow.AddSeconds(1), TestContext.Current.CancellationToken);
            claimed.Should().NotBeNull();
            claimed!.State.Should().Be(AniDbImportJobState.Running);

            var restarted = new AniDbCatalogStore(path);
            await restarted.InitializeAsync(TestContext.Current.CancellationToken);
            var recovered = await restarted.GetImportJobsAsync(TestContext.Current.CancellationToken);
            recovered.Should().ContainSingle().Which.State.Should().Be(AniDbImportJobState.Queued);
            var attempt = new AniDbReleaseMatchAttempt(
                Guid.NewGuid(), assetId, "anidb", DateTimeOffset.UtcNow.AddSeconds(-1),
                DateTimeOffset.UtcNow, "failed", "temporary");
            await restarted.RecordMatchAttemptAsync(attempt, TestContext.Current.CancellationToken);
            (await restarted.GetMatchAttemptsAsync(assetId, TestContext.Current.CancellationToken))
                .Should().ContainSingle().Which.Should().BeEquivalentTo(attempt);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CatalogStore_RecoversLegacyCompletedMatchedJobWhenAnimeEntityIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"niratan-anidb-legacy-completed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "anidb.sqlite3");
            var assetId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var firstStore = new AniDbCatalogStore(path);
            await firstStore.UpsertHashAsync(
                assetId,
                "asset-key",
                new AniDbEd2kHash(Hash, 123, now, now),
                TestContext.Current.CancellationToken);
            await firstStore.UpsertFileMatchAsync(
                assetId,
                new AniDbFileMatch(
                    301, 19242, null, null, null, false, 1, null, null, false,
                    null, null, [], [], null, null, null,
                    [new AniDbFileEpisodeLink(1001, 100, false, 0) { AnimeId = 19242 }]),
                null,
                TestContext.Current.CancellationToken);
            await firstStore.EnqueueImportJobAsync(assetId, TestContext.Current.CancellationToken);
            await firstStore.CompleteImportJobAsync(assetId, TestContext.Current.CancellationToken);

            var restarted = new AniDbCatalogStore(path);
            await restarted.InitializeAsync(TestContext.Current.CancellationToken);

            var recovered = (await restarted.GetImportJobsAsync(TestContext.Current.CancellationToken))
                .Should().ContainSingle().Which;
            recovered.State.Should().Be(AniDbImportJobState.Queued);
            recovered.Stage.Should().Be(AniDbImportJobStage.AnimeMetadata);
            recovered.Attempts.Should().Be(0);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CatalogStore_RequeuesHttpClientRejectionForUdpMetadataFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"niratan-anidb-http-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "anidb.sqlite3");
            var assetId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var firstStore = new AniDbCatalogStore(path);
            await firstStore.UpsertHashAsync(
                assetId,
                "asset-key",
                new AniDbEd2kHash(Hash, 123, now, now),
                TestContext.Current.CancellationToken);
            await firstStore.UpsertFileMatchAsync(
                assetId,
                new AniDbFileMatch(
                    301, 19242, null, null, null, false, 1, null, null, false,
                    null, null, [], [], null, null, null,
                    [new AniDbFileEpisodeLink(1001, 100, false, 0) { AnimeId = 19242 }]),
                null,
                TestContext.Current.CancellationToken);
            await firstStore.EnqueueImportJobAsync(assetId, TestContext.Current.CancellationToken);
            await firstStore.RetryImportJobAsync(
                assetId,
                AniDbImportJobStage.AnimeMetadata,
                0,
                now,
                "AniDB rejected the HTTP API client ID/version. Configure a client and version registered for AniDB's HTTP API, then retry.",
                terminal: true,
                TestContext.Current.CancellationToken);

            var restarted = new AniDbCatalogStore(path);
            await restarted.InitializeAsync(TestContext.Current.CancellationToken);

            var recovered = (await restarted.GetImportJobsAsync(TestContext.Current.CancellationToken))
                .Should().ContainSingle().Which;
            recovered.State.Should().Be(AniDbImportJobState.Queued);
            recovered.Stage.Should().Be(AniDbImportJobStage.AnimeMetadata);
            recovered.Attempts.Should().Be(0);
            recovered.LastError.Should().BeNull();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ImportService_DoesNotRepeatFileLookupForNegativeCacheBeforeRetryAt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"niratan-anidb-negative-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var mediaPath = Path.Combine(root, "episode.mkv");
            await File.WriteAllBytesAsync(mediaPath, [1, 2, 3], TestContext.Current.CancellationToken);
            var modifiedAt = File.GetLastWriteTimeUtc(mediaPath);
            await using var repository = new SQLiteVideoCatalogRepository(
                Path.Combine(root, "video.sqlite3"),
                Path.Combine(root, "legacy.json"));
            await repository.InitializeAsync(TestContext.Current.CancellationToken);
            await repository.UpsertAssetAsync(new VideoCatalogAssetUpsert(
                mediaPath,
                VideoMediaAssetKind.LocalFile,
                mediaPath,
                "Episode",
                "Anime",
                3,
                modifiedAt,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                VideoMediaAvailability.Available),
                TestContext.Current.CancellationToken);
            var assetId = (await repository.GetSnapshotAsync(TestContext.Current.CancellationToken))
                .Assets.Single().Id;
            var store = new AniDbCatalogStore(Path.Combine(root, "anidb.sqlite3"));
            var udp = new CountingUnrecognizedUdpClient();
            var configuration = new StaticConfiguration(new AniDbClientConfiguration(
                "niratan_test", 1, "user", "password", 45500, true, false, false,
                AniDbMyListState.OnHdd, 0));
            var history = new VideoPlaybackHistoryStore(Path.Combine(root, "history.json"));
            await using var service = new AniDbImportService(
                repository,
                store,
                configuration,
                new FixedHasher(new AniDbEd2kHash(Hash, 3, modifiedAt, DateTimeOffset.UtcNow)
                {
                    Crc32 = "55bc801d",
                    Md5 = "5289df737df57326fcdd22597afb1fac",
                    Sha1 = "7037807198c22a7d2b0807371d763779a84fdfcf",
                }),
                udp,
                new NullAniDbHttpClient(),
                history,
                NullLogger<AniDbImportService>.Instance);

            await service.QueueAssetAsync(assetId, TestContext.Current.CancellationToken);
            var first = await WaitForCompletedImportAsync(
                store, assetId, DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
            udp.FileLookupCount.Should().Be(1);

            await service.QueueAssetAsync(assetId, TestContext.Current.CancellationToken);
            await WaitForCompletedImportAsync(
                store, assetId, first.UpdatedAt, TestContext.Current.CancellationToken);

            udp.FileLookupCount.Should().Be(1);
            var release = await service.GetReleaseStateAsync(Hash, 3,
                TestContext.Current.CancellationToken);
            release.Status.Should().Be(AniDbReleaseStatus.Unrecognized);
            release.NextRetryAt.Should().BeAfter(DateTimeOffset.UtcNow.AddDays(29));
            (await store.GetMatchAttemptsAsync(Hash, 3, TestContext.Current.CancellationToken))
                .Should().ContainSingle(item => item.Result == "unrecognized");

            await service.RescanReleaseAsync(Hash, 3, TestContext.Current.CancellationToken);
            await WaitForCompletedImportAsync(
                store, assetId, first.UpdatedAt, TestContext.Current.CancellationToken);
            udp.FileLookupCount.Should().Be(2);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UdpClient_AuthenticatesWithRegisteredIdentityAndParsesMultiEpisodeFile()
    {
        var transport = new FakeTransport(
            "200 session-token LOGIN ACCEPTED",
            "220 FILE\n1|123|456|7|457'50'458'50|0|1|HD|Blu-ray|japanese|english|desc|1700000000|file.mkv|Group|GRP");
        var configuration = new StaticConfiguration(new AniDbClientConfiguration(
            "niratan_test", 1, "user", "password", 45500, true, true, true,
            AniDbMyListState.OnHdd, 1));
        await using var client = new AniDbUdpClient(transport, configuration,
            NullLogger<AniDbUdpClient>.Instance, TimeSpan.Zero);
        var match = await client.GetFileAsync("31d6cfe0d16ae931b73c59d7e0c089c0", 12,
            TestContext.Current.CancellationToken);
        transport.Commands[0].Should().Contain("client=niratan_test").And.Contain("clientver=1");
        transport.Commands[1].Should().Contain("FILE size=12").And.Contain("s=session-token");
        match!.Episodes.Should().HaveCount(3);
        match.Episodes[1].Percentage.Should().Be(50);
        match.Episodes[1].AnimeId.Should().Be(0);
    }

    [Fact]
    public async Task UdpClient_UsesConfiguredServerAndLocalBindWithoutEndpointFailover()
    {
        var transport = new FakeTransport("200 session-token LOGIN ACCEPTED");
        var configuration = new StaticConfiguration(new AniDbClientConfiguration(
            "niratan_test", 1, "user", "password", 45500, true, true, true,
            AniDbMyListState.OnHdd, 1)
        {
            UdpServerHost = "94.130.237.200",
            UdpServerPort = 9001,
            UdpBindAddress = "192.168.1.88",
        });
        await using var client = new AniDbUdpClient(
            transport, configuration, NullLogger<AniDbUdpClient>.Instance, TimeSpan.Zero);

        (await client.TestLoginAsync(TestContext.Current.CancellationToken)).Should().BeTrue();

        transport.Requests.Should().ContainSingle().Which.Should().Be(
            ("94.130.237.200", 9001, 45500, "192.168.1.88"));
    }

    [Fact]
    public async Task ConfigurationProvider_PropagatesAdvancedUdpEndpointSettings()
    {
        var appSettings = new AppSettings
        {
            VideoSettings = new Niratan.Models.Settings.VideoSettings
            {
                Metadata = new VideoMetadataSettings
                {
                    OnlineConsentAccepted = true,
                    AniDbEnabled = true,
                    AniDbClientId = "niratan_test",
                    AniDbClientVersion = 1,
                    AniDbUdpServerHost = "  94.130.237.200  ",
                    AniDbUdpServerPort = 9001,
                    AniDbUdpBindAddress = "  192.168.1.88  ",
                    AniDbUdpLocalPort = 45501,
                },
            },
        };
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(appSettings);
        var credentials = new Mock<IVideoMetadataCredentialStore>();
        credentials.Setup(store => store.ReadAsync(
                "anidb", "username", It.IsAny<CancellationToken>()))
            .ReturnsAsync("user");
        credentials.Setup(store => store.ReadAsync(
                "anidb", "password", It.IsAny<CancellationToken>()))
            .ReturnsAsync("password");

        var configuration = await new AniDbConfigurationProvider(
            settings.Object, credentials.Object).GetAsync(TestContext.Current.CancellationToken);

        configuration.Should().NotBeNull();
        configuration!.UdpServerHost.Should().Be("94.130.237.200");
        configuration.UdpServerPort.Should().Be(9001);
        configuration.UdpBindAddress.Should().Be("192.168.1.88");
        configuration.UdpLocalPort.Should().Be(45501);
    }

    [Fact]
    public async Task UdpSocketTransport_BindsConfiguredIpv4BeforeSending()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var serverPort = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        await using var transport = new AniDbUdpSocketTransport();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        var responseTask = transport.SendAsync(
            "127.0.0.1", serverPort, 0, "127.0.0.1", "PING", timeout.Token);
        var request = await server.ReceiveAsync(timeout.Token);
        request.RemoteEndPoint.Address.Should().Be(IPAddress.Loopback);
        request.Buffer.Should().Equal("PING"u8.ToArray());
        await server.SendAsync("300 PONG"u8.ToArray(), request.RemoteEndPoint, timeout.Token);

        (await responseTask).Should().Be("300 PONG");
    }

    [Fact]
    public async Task UdpSocketTransport_RejectsNonIpv4BindBeforeDnsOrSend()
    {
        await using var transport = new AniDbUdpSocketTransport();

        var action = async () => await transport.SendAsync(
            "unused.invalid", 9000, 45500, "not-an-ip", "PING",
            TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*IPv4*");
    }

    [Fact]
    public async Task UdpClient_ResolvesOtherEpisodeToItsOwningAnime()
    {
        var transport = new FakeTransport(
            "200 session-token LOGIN ACCEPTED",
            "240 EPISODE\n458|999|24|800|100|2|English|Romaji|Kanji|1700000000|1");
        await using var client = new AniDbUdpClient(
            transport, Configuration(), NullLogger<AniDbUdpClient>.Instance, TimeSpan.Zero);

        var identity = await client.GetEpisodeIdentityAsync(
            458, TestContext.Current.CancellationToken);

        identity.Should().Be(new AniDbEpisodeIdentity(458, 999));
        transport.Commands[1].Should().Contain("EPISODE eid=458");
    }

    [Fact]
    public async Task UdpClient_ParsesBoundedAnimeAndEpisodeMetadataFallback()
    {
        var animeFields = new string[39];
        Array.Fill(animeFields, string.Empty);
        animeFields[0] = "19242";
        animeFields[1] = "16";
        animeFields[2] = "2026-2026";
        animeFields[3] = "TV Series";
        animeFields[4] = "11370'19243";
        animeFields[5] = "2'1";
        animeFields[6] = "Re Zero kara Hajimeru Isekai Seikatsu 4th Season";
        animeFields[7] = "Re：ゼロから始める異世界生活 4th season";
        animeFields[8] = "Re:ZERO Season 4";
        animeFields[10] = "ReZero 4";
        animeFields[12] = "16";
        animeFields[13] = "16";
        animeFields[15] = "1767225600";
        animeFields[17] = "https://anidb.net/anime/19242";
        animeFields[18] = "19242.jpg";
        animeFields[19] = "845";
        animeFields[20] = "100";
        animeFields[26] = "0";
        animeFields[27] = "99999";
        animeFields[30] = "fantasy'adventure";
        animeFields[31] = "10'11";
        animeFields[32] = "600'500";
        animeFields[33] = "1767225600";
        var transport = new FakeTransport(
            "200 session-token LOGIN ACCEPTED",
            "230 ANIME\n" + string.Join('|', animeFields),
            "240 EPISODE\n458|19242|24|800|100|01|The Beginning|Hajimari|始まり|1767225600|1");
        await using var client = new AniDbUdpClient(
            transport, Configuration(), NullLogger<AniDbUdpClient>.Instance, TimeSpan.Zero);

        var anime = await client.GetAnimeMetadataAsync(
            19242, TestContext.Current.CancellationToken);
        var episode = await client.GetEpisodeMetadataAsync(
            458, TestContext.Current.CancellationToken);

        anime.Should().NotBeNull();
        anime!.IsDegraded.Should().BeTrue();
        anime.Title.Should().Contain("Re Zero");
        anime.OriginalTitle.Should().Contain("異世界生活");
        anime.Picture.Should().Be("19242.jpg");
        anime.Rating.Should().Be(8.45);
        anime.Relations.Should().HaveCount(2).And.OnlyContain(relation => relation.Verified == true);
        anime.Tags.Select(tag => tag.Name).Should().Equal("fantasy", "adventure");
        anime.Resources.Should().Contain(resource => resource.Type == 1 && resource.Identifier == "99999");
        episode.Should().NotBeNull();
        episode!.AnimeId.Should().Be(19242);
        episode.Number.Should().Be(1);
        episode.Titles.Should().Contain(title => title.Language == "ja" && title.Value == "始まり");
        episode.Rating.Should().Be(8);
        transport.Commands[1].Should().Contain("ANIME aid=19242").And.Contain("amask=FCFCFEFF7F00F8");
        transport.Commands[2].Should().Contain("EPISODE eid=458");
    }

    [Fact]
    public async Task UdpClient_LocalTimeoutRetriesOnceWithoutOpeningProviderBackoff()
    {
        var timing = new ManualTiming();
        var transport = new ScriptedTransport(
            () => "200 session-token LOGIN ACCEPTED",
            () => throw new TimeoutException("first ANIME datagram was lost"),
            () => AnimeUdpResponse(19242));
        await using var client = new AniDbUdpClient(
            transport,
            Configuration(),
            NullLogger<AniDbUdpClient>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(90),
            timing.UtcNow,
            timing.DelayAsync);

        var anime = await client.GetAnimeMetadataAsync(
            19242, TestContext.Current.CancellationToken);

        anime.Should().NotBeNull();
        transport.Commands.Should().HaveCount(3);
        transport.Commands.Skip(1).Should().OnlyContain(command =>
            command.Contains("ANIME aid=19242", StringComparison.Ordinal));
        client.Status.State.Should().Be(AniDbClientConnectionState.Connected);
        client.Status.RetryAt.Should().BeNull();
    }

    [Fact]
    public async Task UdpClient_RepeatedLocalTimeoutUsesCallerRetryWithoutProviderBackoff()
    {
        var timing = new ManualTiming();
        var transport = new ScriptedTransport(
            () => "200 session-token LOGIN ACCEPTED",
            () => throw new TimeoutException("first ANIME datagram was lost"),
            () => throw new TimeoutException("second ANIME datagram was lost"));
        await using var client = new AniDbUdpClient(
            transport,
            Configuration(),
            NullLogger<AniDbUdpClient>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(90),
            timing.UtcNow,
            timing.DelayAsync);

        var request = async () => await client.GetAnimeMetadataAsync(
            19242, TestContext.Current.CancellationToken);

        await request.Should().ThrowAsync<TimeoutException>();
        transport.Commands.Should().HaveCount(3);
        client.Status.State.Should().Be(AniDbClientConnectionState.Connected);
        client.Status.RetryAt.Should().BeNull();
    }

    [Fact]
    public async Task UdpClient_UsesSlowRateDuringSustainedActivityAndResetsAfterIdle()
    {
        var timing = new ManualTiming();
        var transport = new FakeTransport(Enumerable.Repeat("200 session-token LOGIN ACCEPTED", 10).ToArray());
        await using var client = new AniDbUdpClient(
            transport,
            Configuration(),
            NullLogger<AniDbUdpClient>.Instance,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(90),
            timing.UtcNow,
            timing.DelayAsync);

        for (var index = 0; index < 6; index++)
            (await client.TestLoginAsync(TestContext.Current.CancellationToken)).Should().BeTrue();

        timing.Delays.Should().EndWith(TimeSpan.FromMilliseconds(30));
        timing.Advance(TimeSpan.FromMilliseconds(101));
        var delayCount = timing.Delays.Count;
        (await client.TestLoginAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
        timing.Delays.Should().HaveCount(delayCount);
        (await client.TestLoginAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
        timing.Delays.Should().EndWith(TimeSpan.FromMilliseconds(10));
    }

    [Theory]
    [InlineData(600)]
    [InlineData(601)]
    [InlineData(602)]
    [InlineData(604)]
    public async Task UdpClient_OverloadResponseBlocksRequestsUntilRetryAt(int responseCode)
    {
        var timing = new ManualTiming();
        var transport = new FakeTransport(
            "200 session-token LOGIN ACCEPTED",
            $"{responseCode} TEMPORARILY UNAVAILABLE",
            "220 FILE\n1|123|456|7||0|1|HD|Blu-ray|japanese|english|desc|1700000000|file.mkv|Group|GRP");
        await using var client = new AniDbUdpClient(
            transport,
            Configuration(),
            NullLogger<AniDbUdpClient>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(90),
            timing.UtcNow,
            timing.DelayAsync);

        var first = async () => await client.GetFileAsync(Hash, 12, TestContext.Current.CancellationToken);
        await first.Should().ThrowAsync<InvalidOperationException>();
        client.Status.State.Should().Be(AniDbClientConnectionState.BackingOff);
        client.Status.RetryAt.Should().Be(timing.Now.AddMilliseconds(50));
        transport.Commands.Should().HaveCount(2);

        var blocked = async () => await client.GetFileAsync(Hash, 12, TestContext.Current.CancellationToken);
        await blocked.Should().ThrowAsync<InvalidOperationException>();
        transport.Commands.Should().HaveCount(2);

        timing.Advance(TimeSpan.FromMilliseconds(51));
        (await client.GetFileAsync(Hash, 12, TestContext.Current.CancellationToken)).Should().NotBeNull();
        transport.Commands.Should().HaveCount(3);
    }

    [Fact]
    public async Task UdpClient_BannedResponseAppliesNinetyMinuteGate()
    {
        var transport = new FakeTransport(
            "200 session-token LOGIN ACCEPTED",
            "555 BANNED");
        await using var client = new AniDbUdpClient(
            transport,
            Configuration(),
            NullLogger<AniDbUdpClient>.Instance,
            TimeSpan.Zero);

        var first = async () => await client.GetFileAsync(Hash, 12, TestContext.Current.CancellationToken);
        await first.Should().ThrowAsync<InvalidOperationException>();
        client.Status.State.Should().Be(AniDbClientConnectionState.Banned);
        (client.Status.RetryAt!.Value - client.Status.UpdatedAt).Should()
            .BeCloseTo(TimeSpan.FromMinutes(90), TimeSpan.FromMilliseconds(50));

        var blocked = async () => await client.GetFileAsync(Hash, 12, TestContext.Current.CancellationToken);
        await blocked.Should().ThrowAsync<InvalidOperationException>();
        transport.Commands.Should().HaveCount(2);
    }

    [Fact]
    public async Task HttpClient_DecompressesGzipAnimeResponseBeforeSecureXmlParsing()
    {
        var xml = "<anime id=\"123\"><type>TV Series</type><episodecount>1</episodecount><titles><title xml:lang=\"en\" type=\"main\">Example</title></titles></anime>";
        byte[] payload;
        await using (var output = new MemoryStream())
        {
            await using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
                await gzip.WriteAsync(System.Text.Encoding.UTF8.GetBytes(xml), TestContext.Current.CancellationToken);
            payload = output.ToArray();
        }
        using var http = new HttpClient(new GzipHandler(payload));
        using var client = new AniDbHttpClient(
            new StaticConfiguration(new AniDbClientConfiguration(
                "niratantest", 1, "user", "password", 45500, true, true, true,
                AniDbMyListState.OnHdd, 1)), http);
        var anime = await client.GetAnimeAsync(123, TestContext.Current.CancellationToken);
        anime.Should().NotBeNull();
        anime!.AnimeId.Should().Be(123);
        anime.Title.Should().Be("Example");
    }

    [Fact]
    public async Task HttpClient_RejectsAndCachesInvalidHttpClientVersionInsteadOfReturningMissingAnime()
    {
        var handler = new FactoryHttpHandler(_ => XmlResponse(
            "<error code=\"302\">client version missing or invalid</error>"));
        using var http = new HttpClient(handler);
        using var client = HttpClient(http, new ManualTiming());

        var first = async () => await client.GetAnimeAsync(
            19242, TestContext.Current.CancellationToken);
        var failure = await first.Should().ThrowAsync<AniDbHttpApiException>();
        failure.Which.Code.Should().Be(302);
        failure.Which.IsClientConfigurationError.Should().BeTrue();

        var cached = async () => await client.GetAnimeAsync(
            18727, TestContext.Current.CancellationToken);
        await cached.Should().ThrowAsync<AniDbHttpApiException>()
            .WithMessage("*HTTP API client ID/version*");
        handler.RequestCount.Should().Be(1,
            "the same permanently rejected HTTP identity must not hammer AniDB for every matched file");
    }

    [Fact]
    public async Task HttpClient_ExplicitProbeRetriesSameRejectedIdentityAndClearsTheRejection()
    {
        const string anime = "<anime id=\"1\"><type>TV Series</type><episodecount>1</episodecount>" +
                             "<titles><title xml:lang=\"en\" type=\"main\">Probe</title></titles></anime>";
        var responseIndex = 0;
        var handler = new FactoryHttpHandler(_ => responseIndex++ == 0
            ? XmlResponse("<error code=\"302\">client version missing or invalid</error>")
            : XmlResponse(anime));
        using var http = new HttpClient(handler);
        using var client = HttpClient(http, new ManualTiming());

        var rejected = async () => await client.GetAnimeAsync(
            1, TestContext.Current.CancellationToken);
        await rejected.Should().ThrowAsync<AniDbHttpApiException>();

        var probe = await client.ProbeAnimeAsync(1, TestContext.Current.CancellationToken);
        probe.Should().NotBeNull();
        probe!.Title.Should().Be("Probe");

        var ordinary = await client.GetAnimeAsync(1, TestContext.Current.CancellationToken);
        ordinary.Should().NotBeNull();
        handler.RequestCount.Should().Be(3);
    }

    [Fact]
    public async Task HttpClient_FailedExplicitProbeKeepsSameIdentityRejectedForBackgroundRequests()
    {
        var responseIndex = 0;
        var handler = new FactoryHttpHandler(_ => responseIndex++ == 0
            ? XmlResponse("<error code=\"302\">client version missing or invalid</error>")
            : XmlResponse("not xml"));
        using var http = new HttpClient(handler);
        using var client = HttpClient(http, new ManualTiming());

        var rejected = async () => await client.GetAnimeAsync(
            1, TestContext.Current.CancellationToken);
        await rejected.Should().ThrowAsync<AniDbHttpApiException>();

        var failedProbe = async () => await client.ProbeAnimeAsync(
            1, TestContext.Current.CancellationToken);
        await failedProbe.Should().ThrowAsync<System.Xml.XmlException>();

        var background = async () => await client.GetAnimeAsync(
            1, TestContext.Current.CancellationToken);
        await background.Should().ThrowAsync<AniDbHttpApiException>();
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task HttpClient_TreatsMyList330CodeAsValidEmptySnapshot()
    {
        var handler = new FactoryHttpHandler(_ =>
            XmlResponse("<error code=\"330\">no such mylist</error>"));
        using var http = new HttpClient(handler);
        using var client = HttpClient(http, new ManualTiming());

        (await client.GetMyListAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task HttpClient_DownloadsAndParsesCompleteMyListSnapshot()
    {
        const string xml = """
            <mylist>
              <mylistitem id="11" aid="101" eid="201" fid="301" updated="2026-08-22T12:00:00Z" viewdate="2026-08-23T01:02:03Z">
                <state>1</state><filestate>0</filestate>
              </mylistitem>
              <mylistitem id="12" aid="102" eid="202" fid="302" updated="2026-08-22T13:00:00Z">
                <state>2</state><filestate>4</filestate>
              </mylistitem>
            </mylist>
            """;
        var handler = new CapturingHttpHandler(XmlResponse(xml));
        using var http = new System.Net.Http.HttpClient(handler);
        using var client = new AniDbHttpClient(
            new StaticConfiguration(new AniDbClientConfiguration(
                "niratantest", 1, "user name", "p&ss", 45500, true, true, true,
                AniDbMyListState.OnHdd, 1)
            {
                HttpClientId = "niratanhttp",
                HttpClientVersion = 7,
            }), http);

        var entries = await client.GetMyListAsync(TestContext.Current.CancellationToken);

        entries.Should().HaveCount(2);
        entries[0].Should().BeEquivalentTo(new AniDbMyListEntry(
            11, 301, 201, 101, AniDbMyListState.OnHdd, true,
            new DateTimeOffset(2026, 8, 23, 1, 2, 3, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero)));
        entries[1].FileState.Should().Be(4);
        entries[1].Watched.Should().BeFalse();
        handler.RequestUri!.Query.Should().Contain("request=mylist")
            .And.Contain("client=niratanhttp")
            .And.Contain("clientver=7")
            .And.Contain("user=user%20name")
            .And.Contain("pass=p%26ss");
    }

    [Fact]
    public async Task CatalogStore_ReplacesAndPersistsCompleteRemoteMyListSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"niratan-anidb-mylist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "anidb.sqlite3");
            var entries = ImmutableArray.Create(
                new AniDbMyListEntry(11, 301, 201, 101, AniDbMyListState.OnHdd,
                    true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new AniDbMyListEntry(12, 302, 202, 102, AniDbMyListState.OnCd,
                    false, null, DateTimeOffset.UtcNow) { FileState = 4 });
            var store = new AniDbCatalogStore(path);
            await store.ReplaceRemoteMyListAsync(entries, DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);

            var restarted = new AniDbCatalogStore(path);
            (await restarted.GetRemoteMyListAsync(TestContext.Current.CancellationToken))
                .Should().BeEquivalentTo(entries, options => options.WithStrictOrdering());

            await restarted.ReplaceRemoteMyListAsync([], DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);
            (await restarted.GetRemoteMyListAsync(TestContext.Current.CancellationToken))
                .Should().BeEmpty();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CatalogStore_ClearScrapingRecords_RemovesAutomaticStateAndPreservesHashesMyListManualAndIgnoredReleases()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"niratan-anidb-clear-scrape-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "anidb.sqlite3");
            var store = new AniDbCatalogStore(path);
            await store.InitializeAsync(ct);
            var automaticAssetId = Guid.NewGuid();
            var manualAssetId = Guid.NewGuid();
            var ignoredAssetId = Guid.NewGuid();
            const string automaticHash = Hash;
            const string manualHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string ignoredHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
            var automaticMyList = new AniDbMyListEntry(
                11, 301, 1001, 101, AniDbMyListState.OnHdd, true, now, now);

            await store.UpsertHashAsync(
                automaticAssetId,
                "C:\\Anime\\automatic.mkv",
                new AniDbEd2kHash(automaticHash, 100, now, now)
                {
                    Crc32 = "11223344",
                    Md5 = "d41d8cd98f00b204e9800998ecf8427e",
                    Sha1 = "da39a3ee5e6b4b0d3255bfef95601890afd80709",
                }, ct);
            await store.UpsertHashAsync(
                manualAssetId,
                "C:\\Anime\\manual.mkv",
                new AniDbEd2kHash(manualHash, 200, now, now), ct);
            await store.UpsertHashAsync(
                ignoredAssetId,
                "C:\\Anime\\ignored.mkv",
                new AniDbEd2kHash(ignoredHash, 300, now, now), ct);
            await store.UpsertMyListAsync(automaticAssetId, automaticMyList, null, ct);

            var automaticMatch = new AniDbFileMatch(
                301, 101, null, null, null, false, 1, null, true, false,
                "HD", "Blu-ray", ["japanese"], ["english"], null,
                "automatic.mkv", null,
                [new AniDbFileEpisodeLink(1001, 100, false, 0) { AnimeId = 101 }]);
            await store.UpsertFileMatchAsync(automaticAssetId, automaticMatch, null, ct);
            await store.UpsertAnimeAsync(Anime(101, "Automatic", "2026-01-01", []), ct);
            await store.MaterializeGroupAsync(101, ct);
            await store.EnqueueImportJobAsync(automaticAssetId, ct);
            await store.RecordMatchAttemptAsync(new AniDbReleaseMatchAttempt(
                Guid.NewGuid(),
                automaticAssetId,
                "anidb",
                now,
                now.AddSeconds(1),
                "matched",
                null)
            {
                Ed2k = automaticHash,
                FileSize = 100,
            }, ct);

            var manualLink = new AniDbManualReleaseLink(
                401,
                201,
                [
                    new AniDbFileEpisodeLink(2001, 50, false, 0) { AnimeId = 201 },
                    new AniDbFileEpisodeLink(2002, 50, false, 1) { AnimeId = 202 },
                ]);
            await store.LinkManualReleaseAsync(manualHash, 200, manualLink, ct);
            await store.UpsertFileMatchAsync(manualAssetId, automaticMatch, null, ct);
            await store.IgnoreReleaseAsync(ignoredHash, 300, ct);
            await store.UpsertFileMatchAsync(ignoredAssetId, automaticMatch, null, ct);

            var manualCatalogIdentity = (await store.GetManualCatalogIdentitiesAsync(ct))
                .Should().ContainSingle().Subject;
            manualCatalogIdentity.AssetId.Should().Be(manualAssetId);
            manualCatalogIdentity.AnimeIds.Should().BeEquivalentTo([201, 202]);
            manualCatalogIdentity.EpisodeIds.Should().BeEquivalentTo([2001, 2002]);

            await store.EnqueueMyListJobAsync(automaticAssetId, watched: true, ct);
            await store.ReplaceRemoteMyListAsync([automaticMyList], now, ct);
            var manualGroupId = Guid.NewGuid();
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                             $"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO anime_group(
                        group_id,main_anime_id,is_manual,created_at,updated_at)
                    VALUES($group,9999,1,$now,$now);
                    INSERT INTO anime_group_member(
                        group_id,anime_id,ordinal,is_manual)
                    VALUES($group,9999,0,1);
                    """;
                command.Parameters.AddWithValue("$group", manualGroupId.ToString("D"));
                command.Parameters.AddWithValue("$now", now.ToString("O"));
                await command.ExecuteNonQueryAsync(ct);
            }

            await store.ClearScrapingRecordsAsync(ct);

            var preservedManualCatalogIdentity = (await store.GetManualCatalogIdentitiesAsync(ct))
                .Should().ContainSingle().Subject;
            preservedManualCatalogIdentity.AssetId.Should().Be(manualAssetId);
            preservedManualCatalogIdentity.AnimeIds.Should().BeEquivalentTo([201, 202]);
            preservedManualCatalogIdentity.EpisodeIds.Should().BeEquivalentTo([2001, 2002]);

            var automaticAsset = await store.GetAssetAsync(automaticAssetId, ct);
            automaticAsset.Should().NotBeNull();
            automaticAsset!.Ed2k.Should().Be(automaticHash);
            automaticAsset.FileSize.Should().Be(100);
            automaticAsset.Crc32.Should().Be("11223344");
            automaticAsset.Md5.Should().Be("d41d8cd98f00b204e9800998ecf8427e");
            automaticAsset.Sha1.Should().Be("da39a3ee5e6b4b0d3255bfef95601890afd80709");
            automaticAsset.FileMatch.Should().BeNull();
            automaticAsset.MyList.Should().BeEquivalentTo(automaticMyList);

            (await store.GetReleaseStateAsync(automaticHash, 100, ct))
                .Status.Should().Be(AniDbReleaseStatus.Never);
            var manual = await store.GetReleaseStateAsync(manualHash, 200, ct);
            manual.Status.Should().Be(AniDbReleaseStatus.Manual);
            manual.PreventRescan.Should().BeTrue();
            manual.Match.Should().NotBeNull();
            manual.Match!.FileId.Should().Be(401);
            manual.Match.Episodes.Should().BeEquivalentTo(
                [
                    new AniDbFileEpisodeLink(2001, 50, false, 0) { AnimeId = 201, IsManual = true },
                    new AniDbFileEpisodeLink(2002, 50, false, 1) { AnimeId = 202, IsManual = true },
                ]);
            var ignored = await store.GetReleaseStateAsync(ignoredHash, 300, ct);
            ignored.Status.Should().Be(AniDbReleaseStatus.Ignored);
            ignored.PreventRescan.Should().BeTrue();

            (await store.GetImportJobsAsync(ct)).Should().BeEmpty();
            (await store.GetMatchAttemptsAsync(automaticAssetId, ct)).Should().BeEmpty();
            (await store.GetAnimeAsync(101, ct)).Should().BeNull();
            (await store.GetMyListJobsAsync(ct)).Should().ContainSingle(job =>
                job.AssetId == automaticAssetId && job.Watched);
            (await store.GetRemoteMyListAsync(ct)).Should().ContainSingle()
                .Which.Should().BeEquivalentTo(automaticMyList);

            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                             $"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM asset_state;";
                (await command.ExecuteScalarAsync(ct)).Should().Be(3L);
                command.CommandText = "SELECT COUNT(*) FROM stored_release WHERE status IN ('matched','unrecognized');";
                (await command.ExecuteScalarAsync(ct)).Should().Be(0L);
                command.CommandText = "SELECT COUNT(*) FROM stored_release WHERE status IN ('manual','ignored');";
                (await command.ExecuteScalarAsync(ct)).Should().Be(2L);
                command.CommandText = "SELECT COUNT(*) FROM file_episode_link WHERE is_manual=0;";
                (await command.ExecuteScalarAsync(ct)).Should().Be(0L);
                command.CommandText = "SELECT COUNT(*) FROM file_episode_link WHERE is_manual=1;";
                (await command.ExecuteScalarAsync(ct)).Should().Be(2L);
                command.CommandText = "SELECT COUNT(*) FROM anime;";
                (await command.ExecuteScalarAsync(ct)).Should().Be(0L);
                command.CommandText = "SELECT COUNT(*) FROM anime_group WHERE is_manual=0;";
                (await command.ExecuteScalarAsync(ct)).Should().Be(0L);
                command.CommandText = "SELECT COUNT(*) FROM anime_group WHERE group_id=$group AND is_manual=1;";
                command.Parameters.AddWithValue("$group", manualGroupId.ToString("D"));
                (await command.ExecuteScalarAsync(ct)).Should().Be(1L);
                command.CommandText = "SELECT COUNT(*) FROM anime_group_member WHERE group_id=$group AND is_manual=1;";
                (await command.ExecuteScalarAsync(ct)).Should().Be(1L);
                command.CommandText = "SELECT COUNT(*) FROM mylist_job;";
                (await command.ExecuteScalarAsync(ct)).Should().Be(1L);
                command.CommandText = "SELECT COUNT(*) FROM remote_mylist;";
                (await command.ExecuteScalarAsync(ct)).Should().Be(1L);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HttpClient_UsesSlowRateDuringSustainedActivityAndResetsAfterIdle()
    {
        var timing = new ManualTiming();
        var handler = new FactoryHttpHandler(_ => AnimeResponse());
        using var http = new HttpClient(handler);
        using var client = HttpClient(http, timing,
            shortInterval: TimeSpan.FromMilliseconds(10),
            longInterval: TimeSpan.FromMilliseconds(30),
            sustainedPeriod: TimeSpan.FromMilliseconds(40),
            idleResetPeriod: TimeSpan.FromMilliseconds(100));

        for (var index = 0; index < 6; index++)
            (await client.GetAnimeAsync(123, TestContext.Current.CancellationToken)).Should().NotBeNull();

        timing.Delays.Should().EndWith(TimeSpan.FromMilliseconds(30));
        timing.Advance(TimeSpan.FromMilliseconds(101));
        var delayCount = timing.Delays.Count;
        (await client.GetAnimeAsync(123, TestContext.Current.CancellationToken)).Should().NotBeNull();
        timing.Delays.Should().HaveCount(delayCount);
        (await client.GetAnimeAsync(123, TestContext.Current.CancellationToken)).Should().NotBeNull();
        timing.Delays.Should().EndWith(TimeSpan.FromMilliseconds(10));
    }

    [Theory]
    [InlineData("<banned />")]
    [InlineData("<error>banned</error>")]
    public async Task HttpClient_BannedXmlBlocksRequestsUntilBanExpires(string bannedXml)
    {
        var timing = new ManualTiming();
        var handler = new SequenceHttpHandler(
            () => XmlResponse(bannedXml),
            AnimeResponse);
        using var http = new HttpClient(handler);
        using var client = HttpClient(http, timing, banPeriod: TimeSpan.FromMilliseconds(75));

        var first = async () => await client.GetAnimeAsync(123, TestContext.Current.CancellationToken);
        await first.Should().ThrowAsync<InvalidOperationException>();
        var blocked = async () => await client.GetAnimeAsync(123, TestContext.Current.CancellationToken);
        await blocked.Should().ThrowAsync<InvalidOperationException>();
        handler.RequestCount.Should().Be(1);

        timing.Advance(TimeSpan.FromMilliseconds(76));
        (await client.GetAnimeAsync(123, TestContext.Current.CancellationToken)).Should().NotBeNull();
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task HttpClient_RetriesTemporaryHttpAndNetworkFailuresOnlyThreeTimes()
    {
        var timing = new ManualTiming();
        var handler = new SequenceHttpHandler(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => throw new HttpRequestException("temporary network failure"),
            AnimeResponse);
        using var http = new HttpClient(handler);
        using var client = HttpClient(http, timing);

        (await client.GetAnimeAsync(123, TestContext.Current.CancellationToken)).Should().NotBeNull();
        handler.RequestCount.Should().Be(3);

        var exhausted = new FactoryHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var exhaustedHttp = new HttpClient(exhausted);
        using var exhaustedClient = HttpClient(exhaustedHttp, new ManualTiming());
        var action = async () => await exhaustedClient.GetAnimeAsync(123, TestContext.Current.CancellationToken);
        await action.Should().ThrowAsync<HttpRequestException>();
        exhausted.RequestCount.Should().Be(3);
    }

    [Fact]
    public async Task HttpClient_DoesNotRetryResponseThatExceedsSizeLimit()
    {
        var timing = new ManualTiming();
        var handler = new FactoryHttpHandler(_ =>
        {
            var response = XmlResponse("<anime id=\"123\" />");
            response.Content.Headers.ContentLength = 8_388_609;
            return response;
        });
        using var http = new HttpClient(handler);
        using var client = HttpClient(http, timing);

        var action = async () => await client.GetAnimeAsync(123, TestContext.Current.CancellationToken);
        await action.Should().ThrowAsync<InvalidDataException>();
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task HttpClient_DoesNotRetryPermanentHttpStatus()
    {
        var handler = new FactoryHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var http = new System.Net.Http.HttpClient(handler);
        using var client = HttpClient(http, new ManualTiming());

        var action = async () => await client.GetAnimeAsync(123, TestContext.Current.CancellationToken);
        await action.Should().ThrowAsync<HttpRequestException>();
        handler.RequestCount.Should().Be(1);
    }

    private const string Hash = "31d6cfe0d16ae931b73c59d7e0c089c0";

    private static StaticConfiguration Configuration() => new(new AniDbClientConfiguration(
        "niratan_test", 1, "user", "password", 45500, true, true, true,
        AniDbMyListState.OnHdd, 1));

    private static AniDbHttpClient HttpClient(
        System.Net.Http.HttpClient http,
        ManualTiming timing,
        TimeSpan? shortInterval = null,
        TimeSpan? longInterval = null,
        TimeSpan? sustainedPeriod = null,
        TimeSpan? idleResetPeriod = null,
        TimeSpan? banPeriod = null) => new(
            Configuration(),
            http,
            false,
            shortInterval ?? TimeSpan.Zero,
            longInterval ?? TimeSpan.Zero,
            sustainedPeriod ?? TimeSpan.FromSeconds(1),
            idleResetPeriod ?? TimeSpan.FromSeconds(2),
            banPeriod ?? TimeSpan.FromMilliseconds(75),
            TimeSpan.Zero,
            3,
            timing.UtcNow,
            timing.DelayAsync);

    private static HttpResponseMessage AnimeResponse() => XmlResponse(
        "<anime id=\"123\"><type>TV Series</type><episodecount>1</episodecount>" +
        "<titles><title xml:lang=\"en\" type=\"main\">Example</title></titles></anime>");

    private static HttpResponseMessage XmlResponse(string xml) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(xml),
    };

    private sealed class StaticConfiguration(AniDbClientConfiguration configuration) : IAniDbConfigurationProvider
    {
        public Task<AniDbClientConfiguration?> GetAsync(CancellationToken ct = default) =>
            Task.FromResult<AniDbClientConfiguration?>(configuration);
    }

    private static AniDbAnime Anime(
        int animeId,
        string title,
        string startDate,
        ImmutableArray<AniDbRelation> relations)
    {
        var now = DateTimeOffset.UtcNow;
        return new AniDbAnime(
            animeId, "TV Series", title, null, null, startDate, null, null, 0, false, null,
            [new AniDbTitle("en", "main", title)], [], relations, [], [], now, now.AddDays(7));
    }

    private static async Task<AniDbImportJob> WaitForCompletedImportAsync(
        IAniDbCatalogStore store,
        Guid assetId,
        DateTimeOffset after,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 250; attempt++)
        {
            var job = (await store.GetImportJobsAsync(ct))
                .FirstOrDefault(item => item.AssetId == assetId);
            if (job is { State: AniDbImportJobState.Completed } && job.UpdatedAt > after)
                return job;
            await Task.Delay(20, ct);
        }
        throw new TimeoutException("AniDB import did not complete within the offline test timeout.");
    }

    private sealed class FixedHasher(AniDbEd2kHash hash) : IAniDbEd2kHasher
    {
        public Task<AniDbEd2kHash> HashAsync(string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(hash);
        }
    }

    private sealed class CountingUnrecognizedUdpClient : IAniDbUdpClient
    {
        public event EventHandler<AniDbClientStatus>? StatusChanged
        {
            add { }
            remove { }
        }
        public int FileLookupCount { get; private set; }
        public AniDbClientStatus Status { get; } = new(
            AniDbClientConnectionState.Ready,
            null,
            DateTimeOffset.UtcNow);

        public Task<bool> TestLoginAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<AniDbFileMatch?> GetFileAsync(
            string ed2k,
            long fileSize,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            FileLookupCount++;
            return Task.FromResult<AniDbFileMatch?>(null);
        }

        public Task<AniDbEpisodeIdentity?> GetEpisodeIdentityAsync(
            int episodeId,
            CancellationToken ct = default) => Task.FromResult<AniDbEpisodeIdentity?>(null);

        public Task<AniDbAnime?> GetAnimeMetadataAsync(
            int animeId,
            CancellationToken ct = default) => Task.FromResult<AniDbAnime?>(null);

        public Task<AniDbEpisode?> GetEpisodeMetadataAsync(
            int episodeId,
            CancellationToken ct = default) => Task.FromResult<AniDbEpisode?>(null);

        public Task<AniDbMyListEntry?> GetMyListAsync(
            string ed2k,
            long fileSize,
            CancellationToken ct = default) => Task.FromResult<AniDbMyListEntry?>(null);

        public Task<AniDbMyListEntry?> AddOrUpdateMyListAsync(
            string ed2k,
            long fileSize,
            AniDbMyListState state,
            bool watched,
            DateTimeOffset? watchedAt,
            CancellationToken ct = default) => Task.FromResult<AniDbMyListEntry?>(null);

        public Task DeleteMyListAsync(
            string ed2k,
            long fileSize,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullAniDbHttpClient : IAniDbHttpClient
    {
        public DateTimeOffset? RetryAt => null;
        public Task<AniDbAnime?> GetAnimeAsync(int animeId, CancellationToken ct = default) =>
            Task.FromResult<AniDbAnime?>(null);
        public Task<AniDbAnime?> ProbeAnimeAsync(int animeId, CancellationToken ct = default) =>
            GetAnimeAsync(animeId, ct);
        public Task<ImmutableArray<AniDbMyListEntry>> GetMyListAsync(CancellationToken ct = default) =>
            Task.FromResult(ImmutableArray<AniDbMyListEntry>.Empty);
    }

    private sealed class FakeTransport(params string[] responses) : IAniDbUdpTransport
    {
        private readonly Queue<string> _responses = new(responses);
        public List<string> Commands { get; } = [];
        public List<(string Host, int ServerPort, int LocalPort, string? BindAddress)> Requests { get; } = [];
        public Task<string> SendAsync(
            string host,
            int serverPort,
            int localPort,
            string? bindAddress,
            string command,
            CancellationToken ct = default)
        {
            Requests.Add((host, serverPort, localPort, bindAddress));
            Commands.Add(command);
            return Task.FromResult(_responses.Dequeue());
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedTransport(params Func<string>[] responses) : IAniDbUdpTransport
    {
        private readonly Queue<Func<string>> _responses = new(responses);
        public List<string> Commands { get; } = [];
        public Task<string> SendAsync(
            string host,
            int serverPort,
            int localPort,
            string? bindAddress,
            string command,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(command);
            try
            {
                return Task.FromResult(_responses.Dequeue()());
            }
            catch (Exception ex)
            {
                return Task.FromException<string>(ex);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string AnimeUdpResponse(int animeId)
    {
        var fields = new string[39];
        Array.Fill(fields, string.Empty);
        fields[0] = animeId.ToString();
        fields[3] = "TV Series";
        fields[6] = $"AniDB {animeId}";
        fields[12] = "1";
        fields[26] = "0";
        return "230 ANIME\n" + string.Join('|', fields);
    }

    private sealed class ManualTiming
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);
        public List<TimeSpan> Delays { get; } = [];
        public DateTimeOffset UtcNow() => Now;
        public void Advance(TimeSpan duration) => Now = Now.Add(duration);
        public Task DelayAsync(TimeSpan duration, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Delays.Add(duration);
            Advance(duration);
            return Task.CompletedTask;
        }
    }

    private sealed class FactoryHttpHandler(Func<int, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(responseFactory(RequestCount));
        }
    }

    private sealed class SequenceHttpHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new(responses);
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RequestCount++;
            try
            {
                return Task.FromResult(_responses.Dequeue()());
            }
            catch (Exception exception)
            {
                return Task.FromException<HttpResponseMessage>(exception);
            }
        }
    }

    private sealed class GzipHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class CapturingHttpHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RequestUri = request.RequestUri;
            return Task.FromResult(response);
        }
    }
}
