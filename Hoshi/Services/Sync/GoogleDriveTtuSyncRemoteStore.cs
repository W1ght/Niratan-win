using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Novel;
using Hoshi.Models.Sync;

namespace Hoshi.Services.Sync;

public sealed class GoogleDriveTtuSyncRemoteStore : ITtuSyncRemoteStore
{
    private const string RootFolderName = "ttu-reader-data";
    private const string FolderMimeType = "application/vnd.google-apps.folder";
    private static readonly Uri DriveApiBase = new("https://www.googleapis.com/drive/v3/");
    private static readonly Uri DriveUploadBase = new("https://www.googleapis.com/upload/drive/v3/");

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly IGoogleDriveAuthService _authService;
    private readonly HttpClient _httpClient;
    private readonly IGoogleDriveSyncCache _cache;

    public GoogleDriveTtuSyncRemoteStore(
        IGoogleDriveAuthService authService,
        HttpClient httpClient,
        IGoogleDriveSyncCache cache)
    {
        _authService = authService;
        _httpClient = httpClient;
        _cache = cache;
    }

    public async Task<TtuRemoteBookFiles> ListBookFilesAsync(
        string bookTitle,
        CancellationToken ct = default)
    {
        var bookFolderId = await EnsureBookFolderAsync(bookTitle, ct);
        var files = await ListFilesAsync(
            $"trashed=false and '{EscapeQueryLiteral(bookFolderId)}' in parents and mimeType != '{FolderMimeType}'",
            "files(id,name,parents,thumbnailLink)",
            ct);
        return TtuRemoteBookFiles.FromFiles(files.Select(ToRemoteFile));
    }

    public async Task<IReadOnlyList<TtuRemoteBook>> ListRemoteBooksAsync(
        CancellationToken ct = default)
    {
        var rootFolderId = await EnsureRootFolderAsync(ct);
        var folders = await ListFilesAsync(
            $"trashed=false and '{EscapeQueryLiteral(rootFolderId)}' in parents and mimeType='{FolderMimeType}'",
            "files(id,name)",
            ct);
        if (folders.Count == 0)
            return [];

        var filesByFolder = await ListSyncFilesByParentAsync(folders.Select(folder => folder.Id), ct);
        var remoteBooks = new List<TtuRemoteBook>();
        foreach (var folder in folders)
        {
            filesByFolder.TryGetValue(folder.Id, out var folderFiles);
            var syncFiles = TtuRemoteBookFiles.FromFiles((folderFiles ?? []).Select(ToRemoteFile));
            if (syncFiles.BookData == null)
                continue;

            remoteBooks.Add(new TtuRemoteBook(
                folder.Id,
                TtuSyncFileNames.DesanitizeTtuFilename(folder.Name),
                folder.Name,
                syncFiles,
                ParseProgress(syncFiles.Progress)));
        }

        return remoteBooks
            .OrderBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task TrashRemoteBookAsync(
        TtuRemoteBook remoteBook,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            new Uri(
                DriveApiBase,
                $"files/{Uri.EscapeDataString(remoteBook.Id)}?supportsAllDrives=true"))
        {
            Content = JsonContent(new DriveTrashMetadata(Trashed: true)),
        };
        using var response = await SendAuthorizedAsync(request, ct);
        _cache.RemoveBookFolder(remoteBook.Title);
    }

    public Task<TtuProgress?> GetProgressAsync(
        TtuRemoteFile file,
        CancellationToken ct = default) =>
        DownloadJsonAsync<TtuProgress>(file, ct);

    public async Task<IReadOnlyList<NovelReadingStatistic>?> GetStatisticsAsync(
        TtuRemoteFile file,
        CancellationToken ct = default) =>
        await DownloadJsonAsync<List<NovelReadingStatistic>>(file, ct);

    public Task<TtuAudioBook?> GetAudioBookAsync(
        TtuRemoteFile file,
        CancellationToken ct = default) =>
        DownloadJsonAsync<TtuAudioBook>(file, ct);

    public async Task DownloadBookDataAsync(
        TtuRemoteFile file,
        string destinationFilePath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(DriveApiBase, $"files/{Uri.EscapeDataString(file.Id)}?alt=media"));
        using var response = await SendAuthorizedAsync(request, ct);
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = File.Create(destinationFilePath);
        var buffer = new byte[81920];
        long read = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer, ct);
            if (count == 0)
                break;

            await destination.WriteAsync(buffer.AsMemory(0, count), ct);
            read += count;
            if (total is > 0)
                progress?.Report(Math.Clamp(read / (double)total.Value, 0, 1));
        }

        progress?.Report(1);
    }

    public Task UpsertProgressAsync(
        string bookTitle,
        TtuProgress progress,
        TtuRemoteFile? existingFile,
        CancellationToken ct = default) =>
        UpsertJsonFileAsync(
            bookTitle,
            TtuSyncFileNames.GetProgressFileName(progress),
            progress,
            existingFile,
            ct);

    public Task UpsertStatisticsAsync(
        string bookTitle,
        IReadOnlyList<NovelReadingStatistic> statistics,
        TtuRemoteFile? existingFile,
        CancellationToken ct = default) =>
        UpsertJsonFileAsync(
            bookTitle,
            TtuSyncFileNames.GetStatisticsFileName(statistics),
            statistics,
            existingFile,
            ct);

    public Task UpsertAudioBookAsync(
        string bookTitle,
        TtuAudioBook audioBook,
        TtuRemoteFile? existingFile,
        CancellationToken ct = default) =>
        UpsertJsonFileAsync(
            bookTitle,
            TtuSyncFileNames.GetAudioBookFileName(audioBook),
            audioBook,
            existingFile,
            ct);

    private async Task<T?> DownloadJsonAsync<T>(
        TtuRemoteFile file,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(DriveApiBase, $"files/{Uri.EscapeDataString(file.Id)}?alt=media"));
        using var response = await SendAuthorizedAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private async Task UpsertJsonFileAsync<T>(
        string bookTitle,
        string fileName,
        T payload,
        TtuRemoteFile? existingFile,
        CancellationToken ct)
    {
        var metadata = existingFile == null
            ? new DriveFileMetadata(
                fileName,
                MimeType: null,
                Parents: [await EnsureBookFolderAsync(bookTitle, ct)])
            : new DriveFileMetadata(fileName, MimeType: null, Parents: null);

        var method = existingFile == null ? HttpMethod.Post : HttpMethod.Patch;
        var path = existingFile == null
            ? "files?uploadType=multipart&fields=id,name"
            : $"files/{Uri.EscapeDataString(existingFile.Id)}?uploadType=multipart&fields=id,name";
        using var request = new HttpRequestMessage(method, new Uri(DriveUploadBase, path))
        {
            Content = CreateMultipartJsonContent(metadata, payload),
        };
        using var response = await SendAuthorizedAsync(request, ct);
    }

    private async Task<string> EnsureBookFolderAsync(
        string bookTitle,
        CancellationToken ct)
    {
        if (_cache.TryGetBookFolder(bookTitle, out var cachedFolderId))
            return cachedFolderId;

        var rootFolderId = await EnsureRootFolderAsync(ct);
        var sanitizedTitle = TtuSyncFileNames.SanitizeTtuFilename(bookTitle);
        var folder = await FindFolderAsync(rootFolderId, sanitizedTitle, ct)
            ?? await CreateFolderAsync(rootFolderId, sanitizedTitle, ct);
        _cache.SetBookFolder(bookTitle, folder.Id);
        return folder.Id;
    }

    private async Task<string> EnsureRootFolderAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_cache.RootFolderId))
            return _cache.RootFolderId;

        var folder = await FindFolderAsync("root", RootFolderName, ct)
            ?? await CreateFolderAsync("root", RootFolderName, ct);
        _cache.RootFolderId = folder.Id;
        return folder.Id;
    }

    private async Task<DriveFileDto?> FindFolderAsync(
        string parentId,
        string name,
        CancellationToken ct)
    {
        var files = await ListFilesAsync(
            $"trashed=false and '{EscapeQueryLiteral(parentId)}' in parents and mimeType='{FolderMimeType}' and name = '{EscapeQueryLiteral(name)}'",
            "files(id,name)",
            ct);
        return files.FirstOrDefault();
    }

    private async Task<DriveFileDto> CreateFolderAsync(
        string parentId,
        string name,
        CancellationToken ct)
    {
        var metadata = new DriveFileMetadata(
            name,
            FolderMimeType,
            [parentId]);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(DriveApiBase, "files?fields=id,name"))
        {
            Content = JsonContent(metadata),
        };
        using var response = await SendAuthorizedAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<DriveFileDto>(body, JsonOptions)
            ?? throw new InvalidOperationException("Google Drive did not return the created folder.");
    }

    private async Task<IReadOnlyList<DriveFileDto>> ListFilesAsync(
        string query,
        string fields,
        CancellationToken ct)
    {
        var uri = new Uri(
            DriveApiBase,
            $"files?q={Uri.EscapeDataString(query)}&fields={Uri.EscapeDataString(fields)}&pageSize=1000");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SendAuthorizedAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var list = JsonSerializer.Deserialize<DriveFileListDto>(body, JsonOptions);
        return list?.Files ?? [];
    }

    private async Task<Dictionary<string, List<DriveFileDto>>> ListSyncFilesByParentAsync(
        IEnumerable<string> folderIds,
        CancellationToken ct)
    {
        var ids = folderIds.ToList();
        var grouped = new Dictionary<string, List<DriveFileDto>>(StringComparer.Ordinal);
        for (var index = 0; index < ids.Count; index += 50)
        {
            var chunk = ids.Skip(index).Take(50).ToList();
            var parentQuery = string.Join(
                " or ",
                chunk.Select(id => $"'{EscapeQueryLiteral(id)}' in parents"));
            var files = await ListFilesAsync(
                $"trashed=false and ({parentQuery}) and mimeType != '{FolderMimeType}'",
                "files(id,name,parents,thumbnailLink)",
                ct);
            foreach (var file in files)
            {
                var parentId = file.Parents?.FirstOrDefault();
                if (parentId == null)
                    continue;

                if (!grouped.TryGetValue(parentId, out var list))
                {
                    list = [];
                    grouped[parentId] = list;
                }

                list.Add(file);
            }
        }

        return grouped;
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        var token = await _authService.GetAccessTokenAsync(ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await _httpClient.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return response;

        var body = await response.Content.ReadAsStringAsync(ct);
        response.Dispose();
        throw new InvalidOperationException(string.Create(
            CultureInfo.InvariantCulture,
            $"Google Drive request failed ({(int)response.StatusCode}): {body}"));
    }

    private static MultipartContent CreateMultipartJsonContent<T>(
        DriveFileMetadata metadata,
        T payload)
    {
        var content = new MultipartContent("related");
        content.Add(JsonContent(metadata));
        content.Add(JsonContent(payload));
        return content;
    }

    private static StringContent JsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");

    private static string EscapeQueryLiteral(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

    private static TtuRemoteFile ToRemoteFile(DriveFileDto file) =>
        new(
            file.Id,
            file.Name,
            file.Parents?.FirstOrDefault(),
            file.ThumbnailLink);

    private static double ParseProgress(TtuRemoteFile? progressFile)
    {
        if (progressFile == null)
            return 0;

        var name = Path.GetFileNameWithoutExtension(progressFile.Name);
        var lastPart = name.Split('_').LastOrDefault();
        return double.TryParse(lastPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0, 1)
            : 0;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new UnixMillisecondsDateTimeOffsetConverter());
        return options;
    }

    private sealed record DriveFileDto(
        string Id,
        string Name,
        IReadOnlyList<string>? Parents = null,
        string? ThumbnailLink = null);

    private sealed record DriveFileListDto(
        IReadOnlyList<DriveFileDto> Files);

    private sealed record DriveFileMetadata(
        string Name,
        string? MimeType,
        IReadOnlyList<string>? Parents);

    private sealed record DriveTrashMetadata(bool Trashed);

    private sealed class UnixMillisecondsDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.Number when reader.TryGetInt64(out var value) =>
                    DateTimeOffset.FromUnixTimeMilliseconds(value),
                JsonTokenType.String when DateTimeOffset.TryParse(
                    reader.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var value) => value,
                _ => throw new JsonException("Expected a Unix millisecond timestamp."),
            };
        }

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options) =>
            writer.WriteNumberValue(value.ToUnixTimeMilliseconds());
    }
}
