using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Niratan.Services.Novels;

internal enum NovelJsonReadStatus
{
    Missing,
    Success,
    Invalid,
}

internal sealed record NovelJsonReadResult<T>(
    NovelJsonReadStatus Status,
    T? Value,
    string? Error = null);

internal interface INiratanJsonFileStore
{
    Task<NovelJsonReadResult<T>> ReadAsync<T>(
        string path,
        CancellationToken ct = default);

    Task WriteAsync<T>(
        string path,
        T value,
        CancellationToken ct = default);
}

internal sealed class NiratanJsonFileStore : INiratanJsonFileStore
{
    private static readonly DateTimeOffset MacAbsoluteDateReference =
        new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<NovelJsonReadResult<T>> ReadAsync<T>(
        string path,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
            return new NovelJsonReadResult<T>(NovelJsonReadStatus.Missing, default);

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
            return new NovelJsonReadResult<T>(NovelJsonReadStatus.Success, value);
        }
        catch (JsonException ex)
        {
            return new NovelJsonReadResult<T>(NovelJsonReadStatus.Invalid, default, ex.Message);
        }
        catch (IOException ex)
        {
            return new NovelJsonReadResult<T>(NovelJsonReadStatus.Invalid, default, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new NovelJsonReadResult<T>(NovelJsonReadStatus.Invalid, default, ex.Message);
        }
    }

    public async Task WriteAsync<T>(
        string path,
        T value,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(value);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var operationId = Guid.NewGuid().ToString("N");
        var tempPath = path + "." + operationId + ".tmp";
        var backupPath = path + "." + operationId + ".backup.tmp";
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct);
            }

            if (File.Exists(path))
            {
                File.Replace(
                    tempPath,
                    path,
                    backupPath,
                    ignoreMetadataErrors: true);
                File.Delete(backupPath);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };
        options.Converters.Add(new MacAbsoluteDateTimeOffsetJsonConverter());
        return options;
    }

    private sealed class MacAbsoluteDateTimeOffsetJsonConverter
        : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
                return MacAbsoluteDateReference.AddSeconds(reader.GetDouble());

            if (reader.TokenType == JsonTokenType.String
                && DateTimeOffset.TryParse(
                    reader.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return parsed.ToUniversalTime();
            }

            throw new JsonException("Invalid Mac absolute date value.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options)
        {
            var seconds = (value.ToUniversalTime() - MacAbsoluteDateReference).TotalSeconds;
            writer.WriteNumberValue(seconds);
        }
    }
}
