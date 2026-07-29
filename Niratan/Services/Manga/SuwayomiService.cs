using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Helpers;
using Niratan.Models.Manga;
using Windows.Security.Credentials;

namespace Niratan.Services.Manga;

internal interface ISuwayomiCredentialStore
{
    Task<string?> ReadAsync(string credentialId);
    Task WriteAsync(string credentialId, string secret);
    Task DeleteAsync(string credentialId);
}

internal sealed class WindowsSuwayomiCredentialStore : ISuwayomiCredentialStore
{
    private const string Resource = "Niratan.Suwayomi";

    public Task<string?> ReadAsync(string credentialId)
    {
        try
        {
            var credential = new PasswordVault().Retrieve(Resource, credentialId);
            credential.RetrievePassword();
            return Task.FromResult<string?>(credential.Password);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task WriteAsync(string credentialId, string secret)
    {
        var vault = new PasswordVault();
        try
        {
            vault.Remove(vault.Retrieve(Resource, credentialId));
        }
        catch
        {
        }
        vault.Add(new PasswordCredential(Resource, credentialId, secret));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string credentialId)
    {
        try
        {
            var vault = new PasswordVault();
            vault.Remove(vault.Retrieve(Resource, credentialId));
        }
        catch
        {
        }
        return Task.CompletedTask;
    }
}

internal sealed class SuwayomiService : ISuwayomiService, IDisposable
{
    private const int MaximumJsonBytes = 16 * 1024 * 1024;
    private const int MaximumImageBytes = 256 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly HttpClient _http;
    private readonly ISuwayomiCredentialStore _credentials;
    private readonly string _configurationPath;
    private readonly string _cacheRoot;
    private string? _accessToken;
    private string? _accessTokenIdentity;

    public SuwayomiService()
        : this(
            new HttpClient(),
            new WindowsSuwayomiCredentialStore(),
            AppDataHelper.GetSuwayomiConfigurationPath(),
            Path.Combine(AppDataHelper.GetMangaCachePath(), "Suwayomi"))
    {
    }

    internal SuwayomiService(
        HttpClient http,
        ISuwayomiCredentialStore credentials,
        string configurationPath,
        string cacheRoot)
    {
        _http = http;
        _credentials = credentials;
        _configurationPath = configurationPath;
        _cacheRoot = cacheRoot;
    }

    public async Task<SuwayomiServerConfiguration> LoadConfigurationAsync(
        CancellationToken ct = default)
    {
        if (!File.Exists(_configurationPath))
            return new SuwayomiServerConfiguration();
        try
        {
            await using var input = File.OpenRead(_configurationPath);
            return await JsonSerializer.DeserializeAsync<SuwayomiServerConfiguration>(
                       input,
                       JsonOptions,
                       ct)
                   ?? new SuwayomiServerConfiguration();
        }
        catch (JsonException)
        {
            return new SuwayomiServerConfiguration();
        }
    }

    public async Task SaveConfigurationAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        CancellationToken ct = default)
    {
        var baseUri = NormalizeServerUri(configuration.ServerUrl);
        var previous = await LoadConfigurationAsync(ct);
        var credentialId = CredentialIdentity(baseUri, configuration);
        if (!string.IsNullOrWhiteSpace(previous.CredentialId)
            && previous.CredentialId != credentialId)
        {
            await _credentials.DeleteAsync(previous.CredentialId);
        }

        if (configuration.AuthMode == SuwayomiAuthMode.None)
        {
            await _credentials.DeleteAsync(credentialId);
            configuration.CredentialId = null;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(secret))
                await _credentials.WriteAsync(credentialId, secret);
            configuration.CredentialId = credentialId;
        }

        configuration.ServerUrl = baseUri.AbsoluteUri.TrimEnd('/');
        var directory = Path.GetDirectoryName(_configurationPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var temp = _configurationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                true))
            {
                await JsonSerializer.SerializeAsync(output, configuration, JsonOptions, ct);
            }
            File.Move(temp, _configurationPath, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public async Task<IReadOnlyList<SuwayomiSource>> ConnectAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        CancellationToken ct = default)
    {
        var result = await GetJsonAsync<List<SuwayomiSource>>(
            configuration,
            secret,
            "source/list",
            ct);
        return result
            .OrderBy(source => source.Lang, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public Task<SuwayomiPagedManga> BrowseAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        string sourceId,
        string? query,
        int page,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        var encodedSource = Uri.EscapeDataString(sourceId);
        var path = string.IsNullOrWhiteSpace(query)
            ? $"source/{encodedSource}/popular/{Math.Max(1, page)}"
            : $"source/{encodedSource}/search?searchTerm={Uri.EscapeDataString(query.Trim())}&pageNum={Math.Max(1, page)}";
        return GetJsonAsync<SuwayomiPagedManga>(configuration, secret, path, ct);
    }

    public async Task<IReadOnlyList<SuwayomiManga>> GetLibraryAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        CancellationToken ct = default)
    {
        var categories = await GetJsonAsync<List<SuwayomiCategory>>(
            configuration,
            secret,
            "category",
            ct);
        var mangaById = new Dictionary<int, SuwayomiManga>();
        foreach (var category in categories)
        {
            var items = await GetJsonAsync<List<SuwayomiManga>>(
                configuration,
                secret,
                $"category/{category.Id}",
                ct);
            foreach (var manga in items)
                mangaById[manga.Id] = manga;
        }

        return mangaById.Values
            .OrderBy(manga => manga.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public Task<SuwayomiManga> GetMangaDetailsAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        int mangaId,
        CancellationToken ct = default) =>
        GetJsonAsync<SuwayomiManga>(
            configuration,
            secret,
            $"manga/{mangaId}/full?onlineFetch=true",
            ct);

    public async Task SetLibraryAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        int mangaId,
        bool isInLibrary,
        CancellationToken ct = default)
    {
        var resolvedSecret = await ResolveSecretAsync(configuration, secret);
        using var request = await CreateRequestAsync(
            configuration,
            resolvedSecret,
            isInLibrary ? HttpMethod.Get : HttpMethod.Delete,
            $"manga/{mangaId}/library",
            ct);
        using var response = await SendAsync(request, 1024, ct);
    }

    public async Task<string> GetThumbnailPathAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        int mangaId,
        CancellationToken ct = default)
    {
        var serverId = ServerIdentity(NormalizeServerUri(configuration.ServerUrl));
        var directory = MangaPathUtility.GetCacheDirectory(
            Path.Combine(_cacheRoot, "Covers"),
            serverId);
        Directory.CreateDirectory(directory);
        var existing = Directory.EnumerateFiles(directory, $"{mangaId}.*")
            .FirstOrDefault(path => new FileInfo(path).Length > 0);
        if (existing is not null)
            return existing;

        using var request = await CreateRequestAsync(
            configuration,
            await ResolveSecretAsync(configuration, secret),
            HttpMethod.Get,
            $"manga/{mangaId}/thumbnail",
            ct);
        using var response = await SendAsync(request, MaximumImageBytes, ct);
        var extension = ImageExtension(response.Content.Headers.ContentType?.MediaType);
        var target = Path.Combine(directory, $"{mangaId}{extension}");
        var temp = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using (var output = new FileStream(
                             temp,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             true))
            {
                await CopyBoundedAsync(input, output, MaximumImageBytes, ct);
            }
            File.Move(temp, target, true);
            return target;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public async Task<string?> GetSourceIconPathAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        SuwayomiSource source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source.IconUrl)
            || string.IsNullOrWhiteSpace(source.Id))
        {
            return null;
        }

        var baseUri = NormalizeServerUri(configuration.ServerUrl);
        var apiPath = GetSourceIconApiPath(baseUri, source.IconUrl);
        var serverId = ServerIdentity(baseUri);
        var directory = MangaPathUtility.GetCacheDirectory(
            Path.Combine(_cacheRoot, "SourceIcons"),
            serverId);
        Directory.CreateDirectory(directory);
        var cacheKey = Sha256(source.Id);
        var existing = Directory.EnumerateFiles(directory, $"{cacheKey}.*")
            .FirstOrDefault(path => new FileInfo(path).Length > 0);
        if (existing is not null)
            return existing;

        using var request = await CreateRequestAsync(
            configuration,
            await ResolveSecretAsync(configuration, secret),
            HttpMethod.Get,
            apiPath,
            ct);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        using var response = await SendAsync(request, MaximumImageBytes, ct);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null
            || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(ResourceStringHelper.GetString(
                "SuwayomiInvalidImageResponse",
                "Suwayomi returned a non-image source icon."));
        }

        var target = Path.Combine(directory, cacheKey + ImageExtension(mediaType));
        var temp = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using (var output = new FileStream(
                             temp,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             true))
            {
                await CopyBoundedAsync(input, output, MaximumImageBytes, ct);
            }
            File.Move(temp, target, true);
            return target;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public async Task<IReadOnlyList<SuwayomiChapter>> GetChaptersAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        int mangaId,
        CancellationToken ct = default)
    {
        var chapters = await GetJsonAsync<List<SuwayomiChapter>>(
            configuration,
            secret,
            $"manga/{mangaId}/chapters?onlineFetch=true",
            ct);
        return chapters
            .OrderBy(chapter => chapter.Index)
            .ThenBy(chapter => chapter.Id)
            .ToList();
    }

    public async Task<MangaBook> CreateReaderBookAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        SuwayomiManga manga,
        SuwayomiChapter chapter,
        CancellationToken ct = default)
    {
        await SaveConfigurationAsync(configuration, secret, ct);
        var prepared = await GetJsonAsync<SuwayomiChapter>(
            configuration,
            secret,
            $"manga/{manga.Id}/chapter/{chapter.Index}",
            ct);
        if (prepared.PageCount <= 0)
            throw new InvalidDataException(ResourceStringHelper.GetString(
                "SuwayomiChapterHasNoPages",
                "The selected Suwayomi chapter has no pages."));
        var serverId = ServerIdentity(NormalizeServerUri(configuration.ServerUrl));
        return new MangaBook
        {
            Id = Sha256($"{serverId}\u001f{manga.Id}\u001f{prepared.Id}"),
            Title = $"{manga.Title} — {prepared.Name}",
            OriginalTitle = manga.Title,
            SourcePath = configuration.ServerUrl,
            ContainerKind = MangaContainerKind.Suwayomi,
            CurrentPageIndex = Math.Clamp(prepared.LastPageRead, 0, prepared.PageCount - 1),
            SuwayomiServerId = serverId,
            SuwayomiMangaId = manga.Id,
            SuwayomiChapterId = prepared.Id,
            SuwayomiChapterIndex = prepared.Index,
            Pages = Enumerable.Range(0, prepared.PageCount)
                .Select(index => new MangaPageDescriptor
                {
                    Index = index,
                    Path = index.ToString(),
                })
                .ToList(),
        };
    }

    public async Task<string> GetPagePathAsync(
        MangaBook book,
        int pageIndex,
        CancellationToken ct = default)
    {
        EnsureRemoteBook(book, pageIndex);
        var directory = MangaPathUtility.GetCacheDirectory(_cacheRoot, book.Id);
        Directory.CreateDirectory(directory);
        var existing = Directory.EnumerateFiles(directory, $"{pageIndex:D6}.*")
            .FirstOrDefault(path => new FileInfo(path).Length > 0);
        if (existing is not null)
            return existing;

        var configuration = await LoadConfigurationAsync(ct);
        ValidateServerIdentity(book, configuration);
        var secret = await ResolveSecretAsync(configuration, null);
        using var request = await CreateRequestAsync(
            configuration,
            secret,
            HttpMethod.Get,
            $"manga/{book.SuwayomiMangaId}/chapter/{book.SuwayomiChapterIndex}/page/{pageIndex}",
            ct);
        using var response = await SendAsync(request, MaximumImageBytes, ct);
        var extension = ImageExtension(response.Content.Headers.ContentType?.MediaType);
        var target = Path.Combine(directory, $"{pageIndex:D6}{extension}");
        var temp = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using (var output = new FileStream(
                             temp,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             true))
            {
                await CopyBoundedAsync(input, output, MaximumImageBytes, ct);
            }
            File.Move(temp, target, true);
            return target;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public async Task UpdateProgressAsync(
        MangaBook book,
        int pageIndex,
        bool completed,
        CancellationToken ct = default)
    {
        EnsureRemoteBook(book, Math.Clamp(pageIndex, 0, Math.Max(0, book.PageCount - 1)));
        var configuration = await LoadConfigurationAsync(ct);
        ValidateServerIdentity(book, configuration);
        var secret = await ResolveSecretAsync(configuration, null);
        using var request = await CreateRequestAsync(
            configuration,
            secret,
            HttpMethod.Patch,
            $"manga/{book.SuwayomiMangaId}/chapter/{book.SuwayomiChapterIndex}",
            ct);
        var values = $"lastPageRead={Math.Max(0, pageIndex)}"
                     + (completed ? "&read=true" : string.Empty);
        request.Content = new StringContent(
            values,
            Encoding.UTF8,
            "application/x-www-form-urlencoded");
        using var response = await SendAsync(request, 1024, ct);
        await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        _ = await ReadBoundedAsync(responseStream, 1024, ct);
    }

    private async Task<T> GetJsonAsync<T>(
        SuwayomiServerConfiguration configuration,
        string? suppliedSecret,
        string path,
        CancellationToken ct)
    {
        var secret = await ResolveSecretAsync(configuration, suppliedSecret);
        using var request = await CreateRequestAsync(
            configuration,
            secret,
            HttpMethod.Get,
            path,
            ct);
        using var response = await SendAsync(request, MaximumJsonBytes, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var data = await ReadBoundedAsync(stream, MaximumJsonBytes, ct);
        return JsonSerializer.Deserialize<T>(data, JsonOptions)
               ?? throw new InvalidDataException(ResourceStringHelper.GetString(
                   "SuwayomiEmptyResponse",
                   "Suwayomi returned an empty response."));
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        HttpMethod method,
        string path,
        CancellationToken ct)
    {
        var baseUri = NormalizeServerUri(configuration.ServerUrl);
        var uri = new Uri(
            $"{baseUri.AbsoluteUri.TrimEnd('/')}/api/v1/{path.TrimStart('/')}",
            UriKind.Absolute);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        switch (configuration.AuthMode)
        {
            case SuwayomiAuthMode.None:
                break;
            case SuwayomiAuthMode.Basic:
                RequireCredentials(configuration, secret);
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(
                        $"{configuration.Username}:{secret}")));
                break;
            case SuwayomiAuthMode.Bearer:
                if (string.IsNullOrWhiteSpace(secret))
                    throw new InvalidOperationException(ResourceStringHelper.GetString(
                        "SuwayomiBearerRequired",
                        "Suwayomi bearer token is required."));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                break;
            case SuwayomiAuthMode.UiLogin:
                RequireCredentials(configuration, secret);
                var identity = CredentialIdentity(baseUri, configuration);
                if (_accessToken is null || _accessTokenIdentity != identity)
                {
                    _accessToken = await LoginAsync(baseUri, configuration.Username, secret!, ct);
                    _accessTokenIdentity = identity;
                }
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    _accessToken);
                break;
        }
        return request;
    }

    private async Task<string> LoginAsync(
        Uri baseUri,
        string username,
        string password,
        CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            query = "mutation Login($username: String!, $password: String!) { login(input: { username: $username, password: $password }) { accessToken refreshToken } }",
            variables = new { username, password },
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUri.AbsoluteUri.TrimEnd('/')}/api/graphql")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var response = await SendAsync(request, MaximumJsonBytes, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var responseData = await ReadBoundedAsync(stream, MaximumJsonBytes, ct);
        using var document = JsonDocument.Parse(responseData);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("login", out var login)
            || !login.TryGetProperty("accessToken", out var token)
            || string.IsNullOrWhiteSpace(token.GetString()))
        {
            throw new UnauthorizedAccessException(ResourceStringHelper.GetString(
                "SuwayomiAuthenticationFailed",
                "Suwayomi authentication failed."));
        }
        return token.GetString()!;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        int maximumBytes,
        CancellationToken ct)
    {
        var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            _accessToken = null;
            throw new UnauthorizedAccessException(ResourceStringHelper.GetString(
                "SuwayomiAuthenticationFailed",
                "Suwayomi authentication failed."));
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException(ResourceStringHelper.FormatString(
                "SuwayomiHttpError",
                "Suwayomi Server returned HTTP {0}.",
                status));
        }
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            response.Dispose();
            throw new InvalidDataException(ResourceStringHelper.GetString(
                "SuwayomiResponseTooLarge",
                "The Suwayomi response is too large."));
        }
        return response;
    }

    private async Task<string?> ResolveSecretAsync(
        SuwayomiServerConfiguration configuration,
        string? suppliedSecret)
    {
        if (!string.IsNullOrWhiteSpace(suppliedSecret))
            return suppliedSecret;
        return string.IsNullOrWhiteSpace(configuration.CredentialId)
            ? null
            : await _credentials.ReadAsync(configuration.CredentialId);
    }

    internal static Uri NormalizeServerUri(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException(ResourceStringHelper.GetString(
                "SuwayomiInvalidAddress",
                "The Suwayomi Server address is invalid."));
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException(ResourceStringHelper.GetString(
                "SuwayomiHttpRequired",
                "Suwayomi Server must use HTTP or HTTPS."));
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        var path = builder.Path.TrimEnd('/');
        foreach (var suffix in new[] { "/api/v1", "/api/graphql" })
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                path = path[..^suffix.Length];
        }
        builder.Path = path;
        return builder.Uri;
    }

    internal static string GetSourceIconApiPath(Uri baseUri, string iconUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (string.IsNullOrWhiteSpace(iconUrl))
            throw new ArgumentException(nameof(iconUrl));

        var marker = "/api/v1/";
        if (Uri.TryCreate(iconUrl.Trim(), UriKind.Absolute, out var absolute))
        {
            if (!string.Equals(
                    absolute.Scheme,
                    baseUri.Scheme,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    absolute.Host,
                    baseUri.Host,
                    StringComparison.OrdinalIgnoreCase)
                || absolute.Port != baseUri.Port)
            {
                throw new InvalidDataException(ResourceStringHelper.GetString(
                    "SuwayomiCrossOriginIconError",
                    "Suwayomi returned a source icon from another server."));
            }
            iconUrl = absolute.PathAndQuery;
        }

        var markerIndex = iconUrl.IndexOf(
            marker,
            StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
            return iconUrl[(markerIndex + marker.Length)..];

        if (iconUrl.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidDataException(ResourceStringHelper.GetString(
                "SuwayomiInvalidIconUrlError",
                "Suwayomi returned an invalid source icon URL."));
        }
        return iconUrl;
    }

    private static void RequireCredentials(
        SuwayomiServerConfiguration configuration,
        string? secret)
    {
        if (string.IsNullOrWhiteSpace(configuration.Username)
            || string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(ResourceStringHelper.GetString(
                "SuwayomiCredentialsRequired",
                "Suwayomi credentials are required."));
        }
    }

    private static void EnsureRemoteBook(MangaBook book, int pageIndex)
    {
        if (book.ContainerKind != MangaContainerKind.Suwayomi
            || book.SuwayomiMangaId is null
            || book.SuwayomiChapterIndex is null)
        {
            throw new InvalidOperationException(ResourceStringHelper.GetString(
                "SuwayomiNotRemoteChapter",
                "The manga is not a Suwayomi chapter."));
        }
        if (pageIndex < 0 || pageIndex >= book.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
    }

    private static void ValidateServerIdentity(
        MangaBook book,
        SuwayomiServerConfiguration configuration)
    {
        var actual = ServerIdentity(NormalizeServerUri(configuration.ServerUrl));
        if (book.SuwayomiServerId != actual)
            throw new InvalidOperationException(ResourceStringHelper.GetString(
                "SuwayomiDifferentServer",
                "This chapter belongs to a different Suwayomi Server."));
    }

    private static string CredentialIdentity(
        Uri baseUri,
        SuwayomiServerConfiguration configuration) =>
        Sha256($"{baseUri.AbsoluteUri.TrimEnd('/')}\u001f{configuration.AuthMode}\u001f{configuration.Username}");

    private static string ServerIdentity(Uri baseUri) =>
        Sha256(baseUri.AbsoluteUri.TrimEnd('/'));

    private static string ImageExtension(string? mediaType) =>
        mediaType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            _ => ".jpg",
        };

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static async Task CopyBoundedAsync(
        Stream input,
        Stream output,
        int maximumBytes,
        CancellationToken ct)
    {
        var buffer = new byte[81920];
        var total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct);
            if (read == 0)
                return;
            total = checked(total + read);
            if (total > maximumBytes)
                throw new InvalidDataException(ResourceStringHelper.GetString(
                    "SuwayomiResponseTooLarge",
                    "The Suwayomi response is too large."));
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream input,
        int maximumBytes,
        CancellationToken ct)
    {
        using var output = new MemoryStream();
        await CopyBoundedAsync(input, output, maximumBytes, ct);
        return output.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
