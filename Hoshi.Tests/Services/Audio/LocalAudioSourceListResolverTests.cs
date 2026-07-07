using FluentAssertions;
using Hoshi.Models.Settings;
using Hoshi.Services.Audio;
using Microsoft.Data.Sqlite;

namespace Hoshi.Tests.Services.Audio;

public sealed class LocalAudioSourceListResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hoshi-local-audio-tests", Guid.NewGuid().ToString("N"));

    public LocalAudioSourceListResolverTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task ResolveAsync_WithCanonicalLocalAudioSourceListUrl_ExtractsAudioToCacheFile()
    {
        var dbPath = Path.Combine(_root, "android.db");
        var cacheDir = Path.Combine(_root, "cache");
        var audioBytes = new byte[] { 0x49, 0x44, 0x33, 0x04, 0x20, 0x10, 0x08 };
        CreateLocalAudioDb(dbPath, [
            new("星", "ほし", "nhk16", "audio/hoshi.mp3", audioBytes),
        ]);

        var resolver = new LocalAudioSourceListResolver(() => dbPath, () => cacheDir);

        var result = await resolver.ResolveAsync(
            "http://localhost:18765/localaudio/get/?term=%E6%98%9F&reading=%E3%81%BB%E3%81%97");

        result.Should().NotBeNull();
        result!.Source.Should().Be("nhk16");
        result.File.Should().Be("audio/hoshi.mp3");
        result.AudioUrl.Should().StartWith("file:///");
        File.ReadAllBytes(new Uri(result.AudioUrl).LocalPath).Should().Equal(audioBytes);
    }

    [Fact]
    public async Task ResolveAsync_PrefersExactReadingThenFallsBackToExpression()
    {
        var dbPath = Path.Combine(_root, "android.db");
        var cacheDir = Path.Combine(_root, "cache");
        var exactBytes = new byte[] { 1, 2, 3, 4 };
        var fallbackBytes = new byte[] { 5, 6, 7, 8 };
        CreateLocalAudioDb(dbPath, [
            new("行く", "いく", "nhk16", "audio/iku.mp3", exactBytes),
            new("見る", "みる", "jpod", "audio/miru.mp3", fallbackBytes),
        ]);
        var resolver = new LocalAudioSourceListResolver(() => dbPath, () => cacheDir);

        var exact = await resolver.ResolveAsync(
            "http://localhost:18765/localaudio/get/?term=%E8%A1%8C%E3%81%8F&reading=%E3%81%84%E3%81%8F");
        var fallback = await resolver.ResolveAsync(
            "http://localhost:18765/localaudio/get/?term=%E8%A6%8B%E3%82%8B&reading=%E3%81%BF%E3%81%AA%E3%81%84");

        exact.Should().NotBeNull();
        exact!.File.Should().Be("audio/iku.mp3");
        File.ReadAllBytes(new Uri(exact.AudioUrl).LocalPath).Should().Equal(exactBytes);
        fallback.Should().NotBeNull();
        fallback!.File.Should().Be("audio/miru.mp3");
        File.ReadAllBytes(new Uri(fallback.AudioUrl).LocalPath).Should().Equal(fallbackBytes);
    }

    [Fact]
    public async Task ResolveAsync_AcceptsLegacyLocalAudioSourceListUrl()
    {
        var dbPath = Path.Combine(_root, "android.db");
        var cacheDir = Path.Combine(_root, "cache");
        CreateLocalAudioDb(dbPath, [
            new("星", "ほし", "nhk16", "audio/hoshi.mp3", [1, 2, 3]),
        ]);
        var resolver = new LocalAudioSourceListResolver(() => dbPath, () => cacheDir);

        var result = await resolver.ResolveAsync(
            "http://localhost:8765/localaudio/get/?term=%E6%98%9F&reading=%E3%81%BB%E3%81%97");

        result.Should().NotBeNull();
        result!.AudioUrl.Should().StartWith("file:///");
    }

    [Fact]
    public async Task ResolveAsync_WithExternalEntriesDatabase_ReturnsSourceFileUrl()
    {
        var hoshidbPath = Path.Combine(_root, "missing-android.db");
        var userFilesDir = Path.Combine(_root, "LocalAudioDev", "user_files");
        var entriesDbPath = Path.Combine(userFilesDir, "entries.db");
        var audioPath = Path.Combine(userFilesDir, "nhk16_files", "audio", "hoshi.mp3");
        var audioBytes = new byte[] { 0x49, 0x44, 0x33, 0x09, 0x01 };
        Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
        File.WriteAllBytes(audioPath, audioBytes);
        CreateEntriesOnlyDb(entriesDbPath, [
            new("星", "ほし", "nhk16", "audio/hoshi.mp3"),
        ]);
        var resolver = new LocalAudioSourceListResolver(
            () => hoshidbPath,
            () => Path.Combine(_root, "cache"),
            () => [entriesDbPath]);

        var result = await resolver.ResolveAsync(
            "http://localhost:18765/localaudio/get/?term=%E6%98%9F&reading=%E3%81%BB%E3%81%97");

        result.Should().NotBeNull();
        result!.AudioUrl.Should().Be(new Uri(audioPath).AbsoluteUri);
        File.ReadAllBytes(new Uri(result.AudioUrl).LocalPath).Should().Equal(audioBytes);
    }

    [Fact]
    public void AudioSettings_NormalizedSources_UsesCanonicalLocalAudioPortAndRemovesLegacyLocalUrl()
    {
        var settings = new AudioSettings { EnableLocalAudio = true };
        settings.AudioSources.Add(new AudioSource
        {
            Name = "Legacy Local",
            Url = "http://localhost:8765/localaudio/get/?term={term}&reading={reading}",
            IsEnabled = true,
        });

        var urls = settings.EnabledAudioSourceUrls;

        urls.Should().ContainSingle(u => u.Contains("/localaudio/get/", StringComparison.Ordinal));
        urls[0].Should().Contain("localhost:18765");
        urls.Should().NotContain(u => u.Contains("localhost:8765"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static void CreateLocalAudioDb(string path, IEnumerable<AudioRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE entries (
                    id integer PRIMARY KEY NOT NULL,
                    expression text NOT NULL,
                    reading text,
                    source text NOT NULL,
                    speaker text,
                    display text,
                    file text NOT NULL
                );
                CREATE TABLE android (
                    source text NOT NULL,
                    file text NOT NULL,
                    data blob NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        foreach (var row in rows)
        {
            using var entry = connection.CreateCommand();
            entry.CommandText = """
                INSERT INTO entries(expression, reading, source, file)
                VALUES ($expression, $reading, $source, $file);
                """;
            entry.Parameters.AddWithValue("$expression", row.Expression);
            entry.Parameters.AddWithValue("$reading", row.Reading);
            entry.Parameters.AddWithValue("$source", row.Source);
            entry.Parameters.AddWithValue("$file", row.File);
            entry.ExecuteNonQuery();

            using var audio = connection.CreateCommand();
            audio.CommandText = """
                INSERT INTO android(source, file, data)
                VALUES ($source, $file, $data);
                """;
            audio.Parameters.AddWithValue("$source", row.Source);
            audio.Parameters.AddWithValue("$file", row.File);
            audio.Parameters.Add("$data", SqliteType.Blob).Value = row.Data;
            audio.ExecuteNonQuery();
        }
    }

    private static void CreateEntriesOnlyDb(string path, IEnumerable<EntryRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE entries (
                    id integer PRIMARY KEY NOT NULL,
                    expression text NOT NULL,
                    reading text,
                    source text NOT NULL,
                    speaker text,
                    display text,
                    file text NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        foreach (var row in rows)
        {
            using var entry = connection.CreateCommand();
            entry.CommandText = """
                INSERT INTO entries(expression, reading, source, file)
                VALUES ($expression, $reading, $source, $file);
                """;
            entry.Parameters.AddWithValue("$expression", row.Expression);
            entry.Parameters.AddWithValue("$reading", row.Reading);
            entry.Parameters.AddWithValue("$source", row.Source);
            entry.Parameters.AddWithValue("$file", row.File);
            entry.ExecuteNonQuery();
        }
    }

    private sealed record AudioRow(string Expression, string Reading, string Source, string File, byte[] Data);
    private sealed record EntryRow(string Expression, string Reading, string Source, string File);
}
