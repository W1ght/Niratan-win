using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Niratan.Helpers;
using Microsoft.Data.Sqlite;
using Serilog;

namespace Niratan.Services.Audio;

internal sealed record LocalAudioSourceListResult(string AudioUrl, string Source, string File);

internal sealed class LocalAudioSourceListResolver
{
    private static readonly string[] s_sourcePriority =
    [
        "nhk16",
        "daijisen",
        "shinmeikai8",
        "jpod",
        "jpod_alternate",
        "taas",
        "ozk5",
        "forvo",
        "forvo_ext",
        "forvo_ext2",
    ];

    private static readonly string[] s_supportedExtensions =
    [
        ".mp3",
        ".opus",
        ".ogg",
        ".m4a",
        ".aac",
        ".wav",
        ".flac",
    ];

    private readonly Func<string> _databasePath;
    private readonly Func<string> _cacheDirectory;
    private readonly Func<IEnumerable<string>> _externalEntriesDatabasePaths;
    private readonly ConcurrentDictionary<string, Lazy<Task<LocalAudioSourceListResult?>>> _inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LocalAudioSourceListResult> _cache = new(StringComparer.Ordinal);
    private static readonly Lazy<string[]> s_discoveredExternalEntriesDatabases = new(DiscoverExternalEntriesDatabases);

    public LocalAudioSourceListResolver()
        : this(
            () => Path.Combine(AppDataHelper.GetDataPath(), "Audio", "android.db"),
            () => Path.Combine(AppDataHelper.GetDataPath(), "Audio", "Cache"),
            () => s_discoveredExternalEntriesDatabases.Value)
    {
    }

    internal LocalAudioSourceListResolver(Func<string> databasePath, Func<string> cacheDirectory)
        : this(databasePath, cacheDirectory, () => [])
    {
    }

    internal LocalAudioSourceListResolver(
        Func<string> databasePath,
        Func<string> cacheDirectory,
        Func<IEnumerable<string>> externalEntriesDatabasePaths)
    {
        _databasePath = databasePath;
        _cacheDirectory = cacheDirectory;
        _externalEntriesDatabasePaths = externalEntriesDatabasePaths;
    }

    public async Task<LocalAudioSourceListResult?> ResolveAsync(string url)
    {
        if (!TryParseLocalAudioSourceListUrl(url, out var expression, out var reading))
            return null;

        var sw = Stopwatch.StartNew();
        var cacheKey = $"{expression}\n{reading}\n{_databasePath()}\n{string.Join("|", _externalEntriesDatabasePaths())}";
        if (_cache.TryGetValue(cacheKey, out var cached) && File.Exists(new Uri(cached.AudioUrl).LocalPath))
        {
            Log.Information(
                "[AudioTrace] local-audio cache hit in {Ms}ms expression='{Expression}' reading='{Reading}'",
                sw.ElapsedMilliseconds,
                expression,
                reading);
            return cached;
        }

        var operation = _inflight.GetOrAdd(
            cacheKey,
            static (key, self) => new Lazy<Task<LocalAudioSourceListResult?>>(
                () => self.ResolveAndCacheAsync(key)),
            this);

        try
        {
            var result = await operation.Value;
            Log.Information(
                "[AudioTrace] local-audio resolve completed in {Ms}ms expression='{Expression}' reading='{Reading}' hit={Hit}",
                sw.ElapsedMilliseconds,
                expression,
                reading,
                result != null);
            return result;
        }
        finally
        {
            if (_inflight.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, operation))
                _inflight.TryRemove(cacheKey, out _);
        }
    }

    public static bool IsLocalAudioSourceListUrl(string url) =>
        TryParseLocalAudioSourceListUrl(url, out _, out _);

    private async Task<LocalAudioSourceListResult?> ResolveAndCacheAsync(string cacheKey)
    {
        var parts = cacheKey.Split('\n', 3);
        var expression = parts[0];
        var reading = parts.Length > 1 ? parts[1] : "";
        try
        {
            var dbPath = _databasePath();
            if (File.Exists(dbPath))
            {
                var androidResult = await ResolveFromAndroidDatabaseAsync(dbPath, expression, reading);
                if (androidResult != null)
                {
                    _cache[cacheKey] = androidResult;
                    return androidResult;
                }
            }

            foreach (var entriesDbPath in _externalEntriesDatabasePaths())
            {
                if (!File.Exists(entriesDbPath))
                    continue;

                var fileResult = await ResolveFromFileDatabaseAsync(entriesDbPath, expression, reading);
                if (fileResult != null)
                {
                    _cache[cacheKey] = fileResult;
                    return fileResult;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Audio] Local audio sourceList resolution failed for {Expression}/{Reading}", expression, reading);
        }

        return null;
    }

    private async Task<LocalAudioSourceListResult?> ResolveFromAndroidDatabaseAsync(
        string dbPath,
        string expression,
        string reading)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();

        var match = await FindAudioEntryAsync(connection, expression, reading);
        if (match == null)
            return null;

        var bytes = await ReadAudioBytesAsync(connection, match.Value.Source, match.Value.File);
        if (bytes is not { Length: > 0 })
            return null;

        var audioUrl = await WriteCacheFileAsync(match.Value.Source, match.Value.File, bytes);
        return new LocalAudioSourceListResult(audioUrl, match.Value.Source, match.Value.File);
    }

    private static async Task<LocalAudioSourceListResult?> ResolveFromFileDatabaseAsync(
        string entriesDbPath,
        string expression,
        string reading)
    {
        await using var connection = new SqliteConnection($"Data Source={entriesDbPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();

        var match = await FindAudioEntryAsync(connection, expression, reading);
        if (match == null)
            return null;

        var audioPath = ResolveExternalAudioPath(entriesDbPath, match.Value.Source, match.Value.File);
        if (audioPath == null)
            return null;

        return new LocalAudioSourceListResult(new Uri(audioPath).AbsoluteUri, match.Value.Source, match.Value.File);
    }

    private static async Task<(string Source, string File)?> FindAudioEntryAsync(
        SqliteConnection connection,
        string expression,
        string reading)
    {
        if (!string.IsNullOrWhiteSpace(reading))
        {
            var exact = await QueryAudioEntryAsync(
                connection,
                "expression = $expression AND reading = $reading",
                new Dictionary<string, object?>
                {
                    ["$expression"] = expression,
                    ["$reading"] = KatakanaToHiragana(reading),
                });
            if (exact != null)
                return exact;
        }

        return await QueryAudioEntryAsync(
            connection,
            "expression = $expression",
            new Dictionary<string, object?>
            {
                ["$expression"] = expression,
            });
    }

    private static async Task<(string Source, string File)?> QueryAudioEntryAsync(
        SqliteConnection connection,
        string where,
        IReadOnlyDictionary<string, object?> parameters)
    {
        var sw = Stopwatch.StartNew();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT source, file
            FROM entries
            WHERE {where}
              AND ({SupportedFileFilter()})
            ORDER BY {SourceOrderSql()}
            LIMIT 1;
            """;

        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        for (var i = 0; i < s_supportedExtensions.Length; i++)
            command.Parameters.AddWithValue($"$ext{i}", "%" + s_supportedExtensions[i].ToLowerInvariant());

        for (var i = 0; i < s_sourcePriority.Length; i++)
            command.Parameters.AddWithValue($"$source{i}", s_sourcePriority[i]);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            Log.Information(
                "[AudioTrace] local-audio sql entry query completed in {Ms}ms hit=false where='{Where}'",
                sw.ElapsedMilliseconds,
                where);
            return null;
        }

        var result = (reader.GetString(0), reader.GetString(1));
        Log.Information(
            "[AudioTrace] local-audio sql entry query completed in {Ms}ms hit=true where='{Where}' source='{Source}' file='{File}'",
            sw.ElapsedMilliseconds,
            where,
            result.Item1,
            result.Item2);
        return result;
    }

    private static async Task<byte[]?> ReadAudioBytesAsync(SqliteConnection connection, string source, string file)
    {
        var sw = Stopwatch.StartNew();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT data
            FROM android
            WHERE source = $source AND file = $file
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$file", file);

        var value = await command.ExecuteScalarAsync();
        var bytes = value as byte[];
        Log.Information(
            "[AudioTrace] local-audio sql blob query completed in {Ms}ms source='{Source}' file='{File}' bytes={Bytes}",
            sw.ElapsedMilliseconds,
            source,
            file,
            bytes?.Length ?? 0);
        return bytes;
    }

    private static string? ResolveExternalAudioPath(string entriesDbPath, string source, string file)
    {
        var userFilesDir = Path.GetDirectoryName(entriesDbPath);
        if (string.IsNullOrWhiteSpace(userFilesDir))
            return null;

        var sourceDir = Path.GetFullPath(Path.Combine(userFilesDir, $"{source}_files"));
        var audioPath = Path.GetFullPath(Path.Combine(sourceDir, file.Replace('\\', Path.DirectorySeparatorChar)));
        if (!audioPath.StartsWith(sourceDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;

        return File.Exists(audioPath) ? audioPath : null;
    }

    private async Task<string> WriteCacheFileAsync(string source, string file, byte[] bytes)
    {
        var extension = Path.GetExtension(file);
        if (!s_supportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            extension = ".mp3";

        var cacheNameBytes = System.Text.Encoding.UTF8.GetBytes($"{source}\n{file}");
        var hash = Convert.ToHexString(SHA256.HashData(cacheNameBytes)).ToLowerInvariant();
        var directory = _cacheDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"local_audio_{hash}{extension.ToLowerInvariant()}");

        if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
            await File.WriteAllBytesAsync(path, bytes);

        return new Uri(path).AbsoluteUri;
    }

    private static bool TryParseLocalAudioSourceListUrl(string url, out string expression, out string reading)
    {
        expression = "";
        reading = "";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp
            || !IsLocalHost(uri.Host)
            || (uri.Port != 18765 && uri.Port != 8765)
            || !string.Equals(uri.AbsolutePath.TrimEnd('/'), "/localaudio/get", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = ParseQuery(uri.Query);
        expression = GetQueryValue(query, "term");
        if (string.IsNullOrEmpty(expression))
            expression = GetQueryValue(query, "expression");

        reading = GetQueryValue(query, "reading");
        return !string.IsNullOrWhiteSpace(expression);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace("+", " ", StringComparison.Ordinal));
            var value = parts.Length > 1
                ? Uri.UnescapeDataString(parts[1].Replace("+", " ", StringComparison.Ordinal))
                : "";
            values[key] = value;
        }

        return values;
    }

    private static string GetQueryValue(IReadOnlyDictionary<string, string> query, string key) =>
        query.TryGetValue(key, out var value) ? value : "";

    private static bool IsLocalHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase);

    private static string SupportedFileFilter() =>
        string.Join(" OR ", s_supportedExtensions.Select((ext, i) => $"LOWER(file) LIKE $ext{i}"));

    private static string SourceOrderSql() =>
        "CASE source "
        + string.Join(" ", s_sourcePriority.Select((_, i) => $"WHEN $source{i} THEN {i}"))
        + " ELSE 999 END";

    private static string KatakanaToHiragana(string text)
    {
        return string.Concat(text.Select(ch =>
        {
            if (ch >= '\u30A1' && ch <= '\u30F6')
                return (char)(ch - 0x60);
            return ch;
        }));
    }

    private static string[] DiscoverExternalEntriesDatabases()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var addonsDir = Path.Combine(appData, "Anki2", "addons21");
            if (!Directory.Exists(addonsDir))
                return [];

            return Directory.EnumerateFiles(addonsDir, "entries.db", SearchOption.AllDirectories)
                .Where(path => path.Contains($"{Path.DirectorySeparatorChar}user_files{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Audio] Failed to discover external local audio entries databases");
            return [];
        }
    }
}
