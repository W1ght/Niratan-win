using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Helpers;
using Niratan.Models.Manga;
using Niratan.Models.Manga.Protos;
using ContentWarning = Niratan.Models.Manga.Protos.ContentWarning;

namespace Niratan.Services.Manga;

internal sealed class MihonExtensionService : IMihonExtensionService, IDisposable
{
    internal const string BundledRuntimeVersion = "1.0.4";
    internal const string BundledOverlaySha256 =
        "edf198c73f7ffa54e356396833d4c0a34d86366cd59aa0edae9d1559e7960d7c";

    private const int MaximumRepositoryBytes = 8 * 1024 * 1024;
    private const int MaximumBridgeJsonBytes = 16 * 1024 * 1024;
    private const int MaximumApkBytes = 64 * 1024 * 1024;
    private const int MaximumImageBytes = 256 * 1024 * 1024;
    private const int MaximumSourceIconBytes = 4 * 1024 * 1024;
    private const int MaximumArchiveEntries = 10_000;
    private const int MaximumRepositories = 32;
    private const int MaximumLibraryEntries = 10_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly HttpClient _http;
    private readonly string _configurationPath;
    private readonly string _catalogPath;
    private readonly string _extensionRoot;
    private readonly string _cacheRoot;
    private readonly string _bridgeDataRoot;
    private readonly string? _bundledRuntimeRoot;
    private readonly SemaphoreSlim _storeGate = new(1, 1);
    private readonly SemaphoreSlim _bridgeGate = new(1, 1);
    private Process? _bridgeProcess;
    private Uri? _ownedBridgeUri;
    private string? _cachedApkPath;
    private DateTime _cachedApkWriteTimeUtc;
    private long _cachedApkLength;
    private string? _cachedApkBase64;

    public MihonExtensionService()
        : this(
            new HttpClient(),
            AppDataHelper.GetMihonConfigurationPath(),
            AppDataHelper.GetMihonInstalledExtensionsPath(),
            AppDataHelper.GetMihonExtensionsPath(),
            Path.Combine(AppDataHelper.GetMangaCachePath(), "Mihon"),
            AppDataHelper.GetMihonBridgeDataPath(),
            Path.Combine(AppContext.BaseDirectory, "MihonBridge"))
    {
    }

    internal MihonExtensionService(
        HttpClient http,
        string configurationPath,
        string catalogPath,
        string extensionRoot,
        string cacheRoot,
        string bridgeDataRoot,
        string? bundledRuntimeRoot = null)
    {
        _http = http;
        _configurationPath = Path.GetFullPath(configurationPath);
        _catalogPath = Path.GetFullPath(catalogPath);
        _extensionRoot = Path.GetFullPath(extensionRoot);
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _bridgeDataRoot = Path.GetFullPath(bridgeDataRoot);
        _bundledRuntimeRoot = bundledRuntimeRoot is null
            ? null
            : Path.GetFullPath(bundledRuntimeRoot);
    }

    public async Task<MihonExtensionConfiguration> LoadConfigurationAsync(
        CancellationToken ct = default)
    {
        if (!File.Exists(_configurationPath))
            return new MihonExtensionConfiguration();
        try
        {
            await using var input = File.OpenRead(_configurationPath);
            var configuration =
                await JsonSerializer.DeserializeAsync<MihonExtensionConfiguration>(
                    input,
                    JsonOptions,
                    ct)
                ?? new MihonExtensionConfiguration();
            MigrateLegacyRepository(configuration);
            configuration.Library ??= [];
            return configuration;
        }
        catch (JsonException)
        {
            return new MihonExtensionConfiguration();
        }
    }

    public async Task SaveConfigurationAsync(
        MihonExtensionConfiguration configuration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.BridgeUrl = NormalizeBridgeUri(configuration.BridgeUrl)
            .AbsoluteUri.TrimEnd('/');
        configuration.Repositories ??= [];
        configuration.Library ??= [];
        if (configuration.Repositories.Count > MaximumRepositories)
        {
            throw new InvalidOperationException(
                ResourceStringHelper.FormatString(
                    "MihonRepositoryLimitError",
                    "At most {0} Mihon repositories can be configured.",
                    MaximumRepositories));
        }
        var normalizedRepositories = new List<MihonRepositoryConfiguration>();
        var seenRepositoryUrls = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var repository in configuration.Repositories)
        {
            if (repository is null
                || string.IsNullOrWhiteSpace(repository.IndexUrl))
            {
                continue;
            }
            var normalizedUrl = NormalizeRepositoryIndexUri(repository.IndexUrl)
                .AbsoluteUri;
            if (!seenRepositoryUrls.Add(normalizedUrl))
                continue;
            normalizedRepositories.Add(new MihonRepositoryConfiguration
            {
                Id = string.IsNullOrWhiteSpace(repository.Id)
                    ? Guid.NewGuid().ToString("N")
                    : repository.Id.Trim(),
                Name = string.IsNullOrWhiteSpace(repository.Name)
                    ? GetRepositoryDisplayName(normalizedUrl)
                    : repository.Name.Trim(),
                IndexUrl = normalizedUrl,
            });
        }
        configuration.SchemaVersion = 2;
        configuration.Repositories = normalizedRepositories;
        configuration.Library = NormalizeLibrary(configuration.Library);
        configuration.RepositoryUrl = null;
        configuration.JavaExecutablePath =
            configuration.JavaExecutablePath.Trim();
        configuration.ServerJarPath = configuration.ServerJarPath.Trim();
        await WriteAtomicJsonAsync(_configurationPath, configuration, ct);
    }

    private static List<MihonLibraryEntry> NormalizeLibrary(
        IEnumerable<MihonLibraryEntry> entries)
    {
        var result = new List<MihonLibraryEntry>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null
                || entry.Manga is null
                || string.IsNullOrWhiteSpace(entry.SourceId)
                || string.IsNullOrWhiteSpace(entry.PackageName)
                || string.IsNullOrWhiteSpace(entry.Manga.Url)
                || string.IsNullOrWhiteSpace(entry.Manga.Title))
            {
                continue;
            }
            var sourceId = entry.SourceId.Trim();
            var packageName = entry.PackageName.Trim();
            var mangaUrl = entry.Manga.Url.Trim();
            if (!identities.Add(
                    $"{packageName}\u001f{sourceId}\u001f{mangaUrl}"))
            {
                continue;
            }
            if (result.Count >= MaximumLibraryEntries)
            {
                throw new InvalidOperationException(
                    ResourceStringHelper.FormatString(
                        "MihonLibraryLimitError",
                        "At most {0} Mihon manga can be saved.",
                        MaximumLibraryEntries));
            }
            result.Add(new MihonLibraryEntry
            {
                SourceId = sourceId,
                SourceName = entry.SourceName?.Trim() ?? string.Empty,
                SourceLang = entry.SourceLang?.Trim() ?? string.Empty,
                SourceBaseUrl = entry.SourceBaseUrl?.Trim() ?? string.Empty,
                PackageName = packageName,
                AddedAt = entry.AddedAt,
                Manga = new MihonManga
                {
                    Url = mangaUrl,
                    Title = entry.Manga.Title.Trim(),
                    Artist = entry.Manga.Artist?.Trim(),
                    Author = entry.Manga.Author?.Trim(),
                    Description = entry.Manga.Description?.Trim(),
                    Genres = (entry.Manga.Genres ?? [])
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value.Trim())
                        .Distinct(StringComparer.Ordinal)
                        .ToList(),
                    Status = entry.Manga.Status,
                    ThumbnailUrl = entry.Manga.ThumbnailUrl?.Trim(),
                },
            });
        }
        return result;
    }

    public async Task ConnectAsync(
        MihonExtensionConfiguration configuration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (_bundledRuntimeRoot is null)
        {
            await ConnectConfiguredBridgeAsync(configuration, ct);
            return;
        }

        await _bridgeGate.WaitAsync(ct);
        try
        {
            if (_bridgeProcess is not null
                && !_bridgeProcess.HasExited
                && _ownedBridgeUri is not null
                && await IsBridgeAvailableAsync(_ownedBridgeUri, ct))
            {
                return;
            }

            StopOwnedBridge();
            var runtime = ResolveBundledRuntime(_bundledRuntimeRoot);
            var port = ReserveLoopbackPort();
            _ownedBridgeUri = new Uri($"http://127.0.0.1:{port}/");
            await StartOwnedBridgeAsync(
                runtime.JavaExecutablePath,
                runtime.ServerJarPath,
                runtime.OverlayJarPath,
                _ownedBridgeUri,
                ct);
        }
        finally
        {
            _bridgeGate.Release();
        }
    }

    private async Task ConnectConfiguredBridgeAsync(
        MihonExtensionConfiguration configuration,
        CancellationToken ct)
    {
        var bridgeUri = NormalizeBridgeUri(configuration.BridgeUrl);
        if (await IsBridgeAvailableAsync(bridgeUri, ct))
            return;

        if (string.IsNullOrWhiteSpace(configuration.JavaExecutablePath)
            || !File.Exists(configuration.JavaExecutablePath))
        {
            throw new FileNotFoundException(
                ResourceStringHelper.GetString(
                    "MihonJavaMissingError",
                    "The configured Java executable could not be found."),
                configuration.JavaExecutablePath);
        }
        if (string.IsNullOrWhiteSpace(configuration.ServerJarPath)
            || !File.Exists(configuration.ServerJarPath))
        {
            throw new FileNotFoundException(
                ResourceStringHelper.GetString(
                    "MihonServerJarMissingError",
                    "The configured M-Extension-Server JAR could not be found."),
                configuration.ServerJarPath);
        }
        if (bridgeUri.Port <= 0)
            throw new InvalidOperationException(
                ResourceStringHelper.GetString(
                    "MihonBridgePortRequiredError",
                    "The Mihon bridge address must include a fixed local port."));

        await StartOwnedBridgeAsync(
            Path.GetFullPath(configuration.JavaExecutablePath),
            Path.GetFullPath(configuration.ServerJarPath),
            null,
            bridgeUri,
            ct);
    }

    private async Task StartOwnedBridgeAsync(
        string javaExecutablePath,
        string serverJarPath,
        string? overlayJarPath,
        Uri bridgeUri,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_bridgeDataRoot);
        var startInfo = new ProcessStartInfo
        {
            FileName = javaExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = _bridgeDataRoot,
        };
        startInfo.Environment.Remove("CLASSPATH");
        startInfo.Environment.Remove("JAVA_TOOL_OPTIONS");
        startInfo.Environment.Remove("_JAVA_OPTIONS");
        startInfo.Environment.Remove("JDK_JAVA_OPTIONS");
        startInfo.ArgumentList.Add("-Xmx512m");
        startInfo.ArgumentList.Add("-noverify");
        startInfo.ArgumentList.Add(
            "--add-opens=java.base/java.lang=ALL-UNNAMED");
        startInfo.ArgumentList.Add(
            "--add-opens=java.base/java.util=ALL-UNNAMED");
        startInfo.ArgumentList.Add(
            "--add-opens=java.base/java.lang.reflect=ALL-UNNAMED");
        startInfo.ArgumentList.Add(
            "-Dapple.awt.application.name=MExtension Server");
        if (overlayJarPath is null)
        {
            startInfo.ArgumentList.Add("-jar");
            startInfo.ArgumentList.Add(serverJarPath);
        }
        else
        {
            startInfo.ArgumentList.Add("-cp");
            startInfo.ArgumentList.Add(string.Join(
                Path.PathSeparator,
                overlayJarPath,
                serverJarPath));
            startInfo.ArgumentList.Add("mextensionserver.MainKt");
        }
        startInfo.ArgumentList.Add(bridgeUri.Port.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(_bridgeDataRoot);

        _bridgeProcess?.Dispose();
        _bridgeProcess = Process.Start(startInfo)
                         ?? throw new InvalidOperationException(
                             ResourceStringHelper.GetString(
                                 "MihonBridgeStartFailedError",
                                 "M-Extension-Server could not be started."));

        for (var attempt = 0; attempt < 50; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (_bridgeProcess.HasExited)
            {
                StopOwnedBridge();
                throw new InvalidOperationException(
                    ResourceStringHelper.GetString(
                        "MihonBridgeExitedError",
                        "M-Extension-Server exited before it became ready."));
            }
            if (await IsBridgeAvailableAsync(bridgeUri, ct))
                return;
            await Task.Delay(200, ct);
        }

        StopOwnedBridge();
        throw new TimeoutException(
            ResourceStringHelper.GetString(
                "MihonBridgeTimeoutError",
                "M-Extension-Server did not become ready within 10 seconds."));
    }

    internal static MihonBundledRuntime ResolveBundledRuntime(
        string runtimeRoot)
    {
        var root = Path.GetFullPath(runtimeRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var manifestPath = Path.Combine(root, "runtime.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                ResourceStringHelper.GetString(
                    "MihonBundledRuntimeMissingError",
                    "The bundled M-Extension-Server runtime is missing."),
                manifestPath);
        }

        MihonBundledRuntimeManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<MihonBundledRuntimeManifest>(
                           File.ReadAllBytes(manifestPath),
                           JsonOptions)
                       ?? throw new InvalidDataException();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                ResourceStringHelper.GetString(
                    "MihonBundledRuntimeInvalidError",
                    "The bundled M-Extension-Server manifest is invalid."),
                ex);
        }

        if (manifest.SchemaVersion != 2
            || !string.Equals(
                manifest.Version,
                BundledRuntimeVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                ResourceStringHelper.GetString(
                    "MihonBundledRuntimeVersionError",
                    "The bundled M-Extension-Server version is not supported."));
        }

        var javaPath = ResolveBundledRuntimePath(
            root,
            manifest.JavaExecutable);
        var jarPath = ResolveBundledRuntimePath(root, manifest.ServerJar);
        var overlayPath = ResolveBundledRuntimePath(
            root,
            manifest.OverlayJar);
        if (!File.Exists(javaPath)
            || !File.Exists(jarPath)
            || !File.Exists(overlayPath))
        {
            throw new FileNotFoundException(
                ResourceStringHelper.GetString(
                    "MihonBundledRuntimeMissingError",
                "The bundled M-Extension-Server runtime is missing."));
        }
        var overlayHash = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(overlayPath)))
            .ToLowerInvariant();
        if (!string.Equals(
                overlayHash,
                BundledOverlaySha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                ResourceStringHelper.GetString(
                    "MihonBundledRuntimeInvalidError",
                    "The bundled M-Extension-Server manifest is invalid."));
        }
        return new MihonBundledRuntime(
            manifest.Version,
            javaPath,
            jarPath,
            overlayPath);
    }

    private static string ResolveBundledRuntimePath(
        string runtimeRoot,
        string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException(
                ResourceStringHelper.GetString(
                    "MihonBundledRuntimeInvalidError",
                    "The bundled M-Extension-Server manifest is invalid."));
        }
        var fullPath = Path.GetFullPath(Path.Combine(
            runtimeRoot,
            relativePath.Replace(
                '/',
                Path.DirectorySeparatorChar)));
        var requiredPrefix = runtimeRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(
                requiredPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                ResourceStringHelper.GetString(
                    "MihonBundledRuntimeInvalidError",
                    "The bundled M-Extension-Server manifest is invalid."));
        }
        return fullPath;
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task<MihonRepositoryRefreshResult> RefreshRepositoriesAsync(
        MihonExtensionConfiguration configuration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        MigrateLegacyRepository(configuration);
        var result = new MihonRepositoryRefreshResult();
        var installed = await GetInstalledSourcesAsync(ct);
        var installedKeys = installed
            .Select(item => $"{item.PackageName}\u001f{item.SourceId}")
            .ToHashSet(StringComparer.Ordinal);
        var seenSources = new HashSet<string>(StringComparer.Ordinal);

        foreach (var repository in configuration.Repositories)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var sources = await RefreshRepositoryCoreAsync(
                    repository,
                    installedKeys,
                    ct);
                foreach (var source in sources)
                {
                    if (seenSources.Add(
                            $"{source.PackageName}\u001f{source.Id}"))
                    {
                        result.Sources.Add(source);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Failures.Add(new MihonRepositoryRefreshFailure
                {
                    RepositoryId = repository.Id,
                    RepositoryName = string.IsNullOrWhiteSpace(repository.Name)
                        ? GetRepositoryDisplayName(repository.IndexUrl)
                        : repository.Name,
                    Message = ex.Message,
                });
            }
        }

        result.Sources = result.Sources
            .OrderBy(source => source.Lang, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return result;
    }

    private async Task<IReadOnlyList<MihonExtensionSource>>
        RefreshRepositoryCoreAsync(
            MihonRepositoryConfiguration repository,
            HashSet<string> installedKeys,
            CancellationToken ct)
    {
        var indexUri = NormalizeRepositoryIndexUri(repository.IndexUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, indexUri);
        using var response = await SendAsync(request, MaximumRepositoryBytes, ct);
        var bytes = await ReadBoundedAsync(
            await response.Content.ReadAsStreamAsync(ct),
            MaximumRepositoryBytes,
            ct);
        bytes = DecompressIfGzipped(bytes);
        if (bytes.Length > 0 && (bytes[0] == (byte)'[' || bytes[0] == (byte)'{'))
            return ParseJsonRepositoryIndex(
                bytes, indexUri, repository, installedKeys);
        return ParseProtobufRepositoryIndex(
            bytes, repository, installedKeys);
    }

    /// <summary>
    /// If <paramref name="data"/> starts with the gzip magic bytes
    /// (<c>1F 8B</c>), decompress and return the result. Otherwise
    /// return the data unchanged.
    /// </summary>
    private static byte[] DecompressIfGzipped(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0x1F || data[1] != 0x8B)
            return data;

        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static IReadOnlyList<MihonExtensionSource> ParseJsonRepositoryIndex(
        byte[] bytes,
        Uri indexUri,
        MihonRepositoryConfiguration repository,
        HashSet<string> installedKeys)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            MaxDepth = 32,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(ResourceStringHelper.GetString(
                "MihonRepositoryFormatError",
                "The Mihon repository index must be a JSON array."));

        var sources = new List<MihonExtensionSource>();
        foreach (var package in document.RootElement.EnumerateArray())
        {
            if (package.ValueKind != JsonValueKind.Object
                || !TryGetRequiredString(package, "name", out var packageName)
                || !TryGetRequiredString(package, "pkg", out var packageId)
                || !TryGetRequiredString(package, "version", out var version)
                || !TryGetRequiredString(package, "apk", out var apkFileName)
                || !package.TryGetProperty("sources", out var sourceArray)
                || sourceArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            if (packageId.StartsWith(
                    "eu.kanade.tachiyomi.animeextension",
                    StringComparison.OrdinalIgnoreCase)
                || !IsSafeFileName(apkFileName))
            {
                continue;
            }

            var sourceCount = sourceArray.GetArrayLength();
            var apkUri = BuildRepositoryAssetUri(indexUri, "apk", apkFileName);
            var iconUri = BuildRepositoryAssetUri(
                indexUri,
                "icon",
                packageId + ".png");
            var isNsfw = ReadInt(package, "nsfw") == 1;
            foreach (var source in sourceArray.EnumerateArray())
            {
                if (source.ValueKind != JsonValueKind.Object
                    || !TryGetSourceId(source, out var sourceId)
                    || !TryGetRequiredString(source, "name", out var sourceName))
                {
                    continue;
                }
                var language = TryGetRequiredString(source, "lang", out var sourceLang)
                    ? sourceLang
                    : TryGetRequiredString(package, "lang", out var packageLang)
                        ? packageLang
                        : string.Empty;
                var baseUrl = TryGetRequiredString(source, "baseUrl", out var sourceBaseUrl)
                    ? sourceBaseUrl
                    : string.Empty;
                sources.Add(new MihonExtensionSource
                {
                    Id = sourceId,
                    Name = sourceName,
                    Lang = language,
                    BaseUrl = baseUrl,
                    PackageName = packageId,
                    PackageDisplayName = packageName,
                    Version = version,
                    ApkFileName = apkFileName,
                    ApkDownloadUrl = apkUri.AbsoluteUri,
                    IconDownloadUrl = iconUri.AbsoluteUri,
                    RepositoryId = repository.Id,
                    RepositoryName = string.IsNullOrWhiteSpace(repository.Name)
                        ? GetRepositoryDisplayName(repository.IndexUrl)
                        : repository.Name,
                    IsNsfw = isNsfw,
                    IsInstalled = installedKeys.Contains(
                        $"{packageId}{sourceId}"),
                    PackageSourceCount = sourceCount,
                });
            }
        }

        return sources
            .OrderBy(source => source.Lang, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<MihonExtensionSource> ParseProtobufRepositoryIndex(
        byte[] bytes,
        MihonRepositoryConfiguration repository,
        HashSet<string> installedKeys)
    {
        var store = NetworkExtensionStore.Parser.ParseFrom(bytes);

        if (store.ExtensionList?.Extensions is not { Count: > 0 } extensions)
            return Array.Empty<MihonExtensionSource>();

        var repositoryName = string.IsNullOrWhiteSpace(repository.Name)
            ? GetRepositoryDisplayName(repository.IndexUrl)
            : repository.Name;

        var sources = new List<MihonExtensionSource>();
        foreach (var ext in extensions)
        {
            // Skip anime extensions (same filter as JSON path).
            if (ext.PackageName.StartsWith(
                    "eu.kanade.tachiyomi.animeextension",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string apkFileName;
            try
            {
                apkFileName = Path.GetFileName(
                    new Uri(ext.Resources.ApkUrl).AbsolutePath);
            }
            catch
            {
                continue;
            }

            if (!IsSafeFileName(apkFileName))
                continue;

            // Mirror Mihon's ContentWarning threshold: >= MIXED is NSFW.
            var isNsfw = ext.ContentWarning >= ContentWarning.Mixed;
            var sourceCount = ext.Sources.Count;

            foreach (var src in ext.Sources)
            {
                var sourceId = src.Id.ToString();
                sources.Add(new MihonExtensionSource
                {
                    Id = sourceId,
                    Name = src.Name,
                    Lang = src.Language,
                    BaseUrl = src.HomeUrl,
                    PackageName = ext.PackageName,
                    PackageDisplayName = ext.Name,
                    Version = ext.VersionName,
                    ApkFileName = apkFileName,
                    ApkDownloadUrl = ext.Resources.ApkUrl,
                    IconDownloadUrl = ext.Resources.IconUrl,
                    RepositoryId = repository.Id,
                    RepositoryName = repositoryName,
                    IsNsfw = isNsfw,
                    IsInstalled = installedKeys.Contains(
                        $"{ext.PackageName}{sourceId}"),
                    PackageSourceCount = sourceCount,
                });
            }
        }

        return sources
            .OrderBy(source => source.Lang, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void MigrateLegacyRepository(
        MihonExtensionConfiguration configuration)
    {
        configuration.Repositories ??= [];
        if (configuration.Repositories.Count > 0
            || string.IsNullOrWhiteSpace(configuration.RepositoryUrl))
        {
            return;
        }
        configuration.Repositories.Add(new MihonRepositoryConfiguration
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = GetRepositoryDisplayName(configuration.RepositoryUrl!),
            IndexUrl = configuration.RepositoryUrl!,
        });
    }

    internal static string GetRepositoryDisplayName(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri))
            return ResourceStringHelper.GetString(
                "MihonRepositoryFallbackName",
                "Mihon repository");

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.Host.Equals(
                "raw.githubusercontent.com",
                StringComparison.OrdinalIgnoreCase)
            && segments.Length > 0)
        {
            return HumanizeRepositoryName(segments[0]);
        }
        if (segments.Length > 1)
            return HumanizeRepositoryName(segments[^2]);
        return HumanizeRepositoryName(uri.Host.Split('.')[0]);
    }

    private static string HumanizeRepositoryName(string value)
    {
        var words = value
            .Replace('-', ' ')
            .Replace('_', ' ')
            .Trim();
        return string.IsNullOrWhiteSpace(words)
            ? ResourceStringHelper.GetString(
                "MihonRepositoryFallbackName",
                "Mihon repository")
            : System.Globalization.CultureInfo.CurrentCulture.TextInfo
                .ToTitleCase(words.ToLowerInvariant());
    }

    public async Task<IReadOnlyList<MihonInstalledExtension>> GetInstalledSourcesAsync(
        CancellationToken ct = default)
    {
        await _storeGate.WaitAsync(ct);
        try
        {
            var catalog = await LoadInstalledCatalogCoreAsync(ct);
            return catalog.Extensions
                .Where(item => File.Exists(item.ApkPath))
                .OrderBy(item => item.Lang, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.SourceName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        finally
        {
            _storeGate.Release();
        }
    }

    public async Task RemoveAsync(
        string packageName,
        string sourceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            throw new ArgumentException(
                "An extension package name is required.",
                nameof(packageName));
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException(
                "An extension source id is required.",
                nameof(sourceId));

        await _storeGate.WaitAsync(ct);
        try
        {
            var catalog = await LoadInstalledCatalogCoreAsync(ct);
            var removed = catalog.Extensions.FirstOrDefault(item =>
                string.Equals(
                    item.PackageName,
                    packageName,
                    StringComparison.Ordinal)
                && string.Equals(
                    item.SourceId,
                    sourceId,
                    StringComparison.Ordinal));
            if (removed is null)
                return;

            catalog.Extensions.Remove(removed);
            await WriteAtomicJsonAsync(_catalogPath, catalog, ct);

            if (SamePath(_cachedApkPath, removed.ApkPath))
            {
                _cachedApkPath = null;
                _cachedApkWriteTimeUtc = default;
                _cachedApkLength = 0;
                _cachedApkBase64 = null;
            }

            var apkStillReferenced = catalog.Extensions.Any(item =>
                SamePath(item.ApkPath, removed.ApkPath));
            if (!apkStillReferenced
                && IsPathWithinExtensionRoot(removed.ApkPath))
            {
                // SourceIcons is an independent persistent cache. Keep it
                // when an APK is removed so the source row can still render
                // its last known icon without a repository request.
                try
                {
                    File.Delete(removed.ApkPath);
                }
                catch (IOException)
                {
                    // The catalog entry is already removed. An orphaned APK
                    // is safe to clean up on a later install or maintenance pass.
                }
                catch (UnauthorizedAccessException)
                {
                    // Do not turn a logical uninstall into a data-loss retry.
                }
            }
        }
        finally
        {
            _storeGate.Release();
        }
    }

    public async Task<string?> GetRepositorySourceIconPathAsync(
        MihonExtensionConfiguration configuration,
        MihonExtensionSource source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source.PackageName))
            return null;

        var sourceIconRoot = Path.Combine(_cacheRoot, "SourceIcons");
        var directory = MangaPathUtility.GetCacheDirectory(
            sourceIconRoot,
            Sha256($"{source.PackageName}\u001f{source.Id}"));
        Directory.CreateDirectory(directory);
        var existing = FindCachedSourceIcon(directory);
        if (existing is not null)
            return existing;

        // Older builds keyed icons only by package name. Migrate one of those
        // entries into the source-specific cache before going online so an
        // app update does not make an existing icon disappear.
        var legacyDirectory = MangaPathUtility.GetCacheDirectory(
            sourceIconRoot,
            Sha256(source.PackageName));
        var legacy = FindCachedSourceIcon(legacyDirectory);
        if (legacy is not null)
        {
            var migrated = MigrateCachedSourceIcon(legacy, directory);
            if (migrated is not null)
                return migrated;
        }

        if (!string.IsNullOrWhiteSpace(source.IconDownloadUrl))
        {
            var downloaded = await TryDownloadRepositoryIconAsync(
                source.IconDownloadUrl,
                directory,
                ct);
            if (downloaded is not null)
                return downloaded;
        }

        var installedApk = Directory.Exists(_extensionRoot)
            ? Directory
                .EnumerateFiles(
                    _extensionRoot,
                    $"{SafePathSegment(source.PackageName)}-*.apk")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        byte[] apkBytes;
        if (installedApk is not null)
        {
            var info = new FileInfo(installedApk);
            if (info.Length <= 0 || info.Length > MaximumApkBytes)
                return null;
            apkBytes = await File.ReadAllBytesAsync(installedApk, ct);
        }
        else
        {
            var apkUri = NormalizeApkUri(source.ApkDownloadUrl);
            using var request = new HttpRequestMessage(HttpMethod.Get, apkUri);
            using var response = await SendAsync(request, MaximumApkBytes, ct);
            apkBytes = await ReadBoundedAsync(
                await response.Content.ReadAsStreamAsync(ct),
                MaximumApkBytes,
                ct);
        }

        ValidateApk(apkBytes);
        return await ExtractLargestRasterIconAsync(
            apkBytes,
            directory,
            ct);
    }

    public async Task<MihonInstalledExtension> InstallAsync(
        MihonExtensionConfiguration configuration,
        MihonExtensionSource source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(source);
        await ConnectAsync(configuration, ct);

        var apkUri = NormalizeApkUri(source.ApkDownloadUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, apkUri);
        using var response = await SendAsync(request, MaximumApkBytes, ct);
        var apkBytes = await ReadBoundedAsync(
            await response.Content.ReadAsStreamAsync(ct),
            MaximumApkBytes,
            ct);
        ValidateApk(apkBytes);
        var sha256 = Convert.ToHexString(SHA256.HashData(apkBytes))
            .ToLowerInvariant();

        Directory.CreateDirectory(_extensionRoot);
        var target = Path.Combine(
            _extensionRoot,
            $"{SafePathSegment(source.PackageName)}-{sha256[..12]}.apk");
        if (!File.Exists(target))
        {
            var temp = target + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, apkBytes, ct);
                File.Move(temp, target, false);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }

        var installed = new MihonInstalledExtension
        {
            SourceId = source.Id,
            SourceName = source.Name,
            Lang = source.Lang,
            BaseUrl = source.BaseUrl,
            PackageName = source.PackageName,
            Version = source.Version,
            IconDownloadUrl = source.IconDownloadUrl,
            ApkPath = target,
            Sha256 = sha256,
            IsNsfw = source.IsNsfw,
            InstalledAt = DateTimeOffset.UtcNow,
        };
        installed.Headers = await GetExtensionHeadersAsync(
            configuration,
            installed,
            ct);

        await _storeGate.WaitAsync(ct);
        try
        {
            var catalog = await LoadInstalledCatalogCoreAsync(ct);
            catalog.Extensions.RemoveAll(item =>
                string.Equals(
                    item.PackageName,
                    installed.PackageName,
                    StringComparison.Ordinal)
                && string.Equals(
                    item.SourceId,
                    installed.SourceId,
                    StringComparison.Ordinal));
            catalog.Extensions.Add(installed);
            await WriteAtomicJsonAsync(_catalogPath, catalog, ct);
        }
        finally
        {
            _storeGate.Release();
        }
        return installed;
    }

    public async Task<MihonPagedManga> BrowseAsync(
        MihonExtensionConfiguration configuration,
        MihonInstalledExtension source,
        string? query,
        int page,
        CancellationToken ct = default)
    {
        await EnsureInstalledExtensionAsync(source, ct);
        var method = string.IsNullOrWhiteSpace(query)
            ? "getPopularManga"
            : "getSearchManga";
        var payload = new Dictionary<string, object?>
        {
            ["method"] = method,
            ["page"] = Math.Max(1, page),
            ["search"] = query?.Trim() ?? string.Empty,
            ["preferences"] = Array.Empty<object>(),
        };
        return await CallBridgeAsync<MihonPagedManga>(
            configuration,
            source,
            payload,
            ct);
    }

    public async Task<IReadOnlyList<MihonChapter>> GetChaptersAsync(
        MihonExtensionConfiguration configuration,
        MihonInstalledExtension source,
        MihonManga manga,
        CancellationToken ct = default)
    {
        await EnsureInstalledExtensionAsync(source, ct);
        var payload = new Dictionary<string, object?>
        {
            ["method"] = "getChapterList",
            ["mangaData"] = new { url = manga.Url },
            ["preferences"] = Array.Empty<object>(),
        };
        return await CallBridgeAsync<List<MihonChapter>>(
            configuration,
            source,
            payload,
            ct);
    }

    public async Task<MihonManga> GetMangaDetailsAsync(
        MihonExtensionConfiguration configuration,
        MihonInstalledExtension source,
        MihonManga manga,
        CancellationToken ct = default)
    {
        await EnsureInstalledExtensionAsync(source, ct);
        var payload = new Dictionary<string, object?>
        {
            ["method"] = "getDetailsManga",
            ["mangaData"] = new { url = manga.Url },
            ["preferences"] = Array.Empty<object>(),
        };
        return await CallBridgeAsync<MihonManga>(
            configuration,
            source,
            payload,
            ct);
    }

    public async Task<MangaBook> CreateReaderBookAsync(
        MihonExtensionConfiguration configuration,
        MihonInstalledExtension source,
        MihonManga manga,
        MihonChapter chapter,
        CancellationToken ct = default)
    {
        await SaveConfigurationAsync(configuration, ct);
        await EnsureInstalledExtensionAsync(source, ct);
        var payload = new Dictionary<string, object?>
        {
            ["method"] = "getPageList",
            ["chapterData"] = new
            {
                url = chapter.Url,
                name = chapter.Name,
                date_upload = chapter.UploadDate,
                chapter_number = chapter.ChapterNumber,
                scanlator = chapter.Scanlator,
            },
            ["preferences"] = Array.Empty<object>(),
        };
        var pages = await CallBridgeAsync<List<MihonPage>>(
            configuration,
            source,
            payload,
            ct);
        var usablePages = pages
            .Where(page => !string.IsNullOrWhiteSpace(page.ImageUrl))
            .OrderBy(page => page.Index)
            .ToList();
        if (usablePages.Count == 0)
            throw new InvalidDataException(ResourceStringHelper.GetString(
                "MihonChapterHasNoPagesError",
                "The selected Mihon chapter has no pages."));

        var identity = Sha256(
            $"{source.PackageName}\u001f{source.SourceId}\u001f{manga.Url}\u001f{chapter.Url}");
        return new MangaBook
        {
            Id = identity,
            Title = $"{manga.Title} — {chapter.Name}",
            OriginalTitle = manga.Title,
            SourcePath = source.ApkPath,
            ContainerKind = MangaContainerKind.Mihon,
            MihonSourceId = source.SourceId,
            MihonPackageName = source.PackageName,
            MihonExtensionSha256 = source.Sha256,
            MihonMangaUrl = manga.Url,
            MihonChapterUrl = chapter.Url,
            Pages = usablePages
                .Select((page, index) => new MangaPageDescriptor
                {
                    Index = index,
                    Path = page.ImageUrl,
                })
                .ToList(),
        };
    }

    public async Task<string> GetThumbnailPathAsync(
        MihonInstalledExtension source,
        MihonManga manga,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(manga.ThumbnailUrl))
            throw new InvalidDataException(ResourceStringHelper.GetString(
                "MihonCoverMissingError",
                "The Mihon manga has no cover URL."));
        await EnsureInstalledExtensionAsync(source, ct);
        var directory = MangaPathUtility.GetCacheDirectory(
            Path.Combine(_cacheRoot, "Covers"),
            Sha256($"{source.PackageName}\u001f{source.SourceId}"));
        Directory.CreateDirectory(directory);
        var key = Sha256(manga.Url);
        var existing = Directory.EnumerateFiles(directory, $"{key}.*")
            .FirstOrDefault(path => new FileInfo(path).Length > 0);
        if (existing is not null)
            return existing;
        return await DownloadImageAsync(
            manga.ThumbnailUrl,
            source.Headers,
            directory,
            key,
            ct);
    }

    public async Task<string> GetPagePathAsync(
        MangaBook book,
        int pageIndex,
        CancellationToken ct = default)
    {
        if (book.ContainerKind != MangaContainerKind.Mihon
            || string.IsNullOrWhiteSpace(book.MihonSourceId)
            || string.IsNullOrWhiteSpace(book.MihonPackageName)
            || string.IsNullOrWhiteSpace(book.MihonExtensionSha256))
        {
            throw new InvalidOperationException(ResourceStringHelper.GetString(
                "MihonNotRemoteChapterError",
                "The manga is not a Mihon chapter."));
        }
        if (pageIndex < 0 || pageIndex >= book.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        var installed = await GetInstalledSourcesAsync(ct);
        var source = installed.FirstOrDefault(item =>
            string.Equals(item.SourceId, book.MihonSourceId, StringComparison.Ordinal)
            && string.Equals(
                item.PackageName,
                book.MihonPackageName,
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                ResourceStringHelper.GetString(
                    "MihonExtensionNotInstalledError",
                    "The Mihon extension used by this chapter is no longer installed."));
        if (!string.Equals(
                source.Sha256,
                book.MihonExtensionSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                ResourceStringHelper.GetString(
                    "MihonExtensionChangedError",
                    "The Mihon extension changed after this chapter was opened. Reopen the title from Browse."));
        }

        var directory = MangaPathUtility.GetCacheDirectory(_cacheRoot, book.Id);
        Directory.CreateDirectory(directory);
        var existing = Directory.EnumerateFiles(directory, $"{pageIndex:D6}.*")
            .FirstOrDefault(path => new FileInfo(path).Length > 0);
        if (existing is not null)
            return existing;
        return await DownloadImageAsync(
            book.Pages[pageIndex].Path,
            source.Headers,
            directory,
            pageIndex.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
            ct);
    }

    internal static Uri NormalizeBridgeUri(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || (uri.Scheme != Uri.UriSchemeHttp
                && uri.Scheme != Uri.UriSchemeHttps)
            || !IsLoopbackHost(uri.Host)
            || !string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            throw new ArgumentException(
                ResourceStringHelper.GetString(
                    "MihonBridgeLoopbackRequiredError",
                    "The Mihon bridge must be an HTTP(S) loopback address."));
        }
        return new UriBuilder(uri)
        {
            Path = uri.AbsolutePath.TrimEnd('/'),
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty,
        }.Uri;
    }

    internal static Uri NormalizeRepositoryUri(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrWhiteSpace(uri.UserInfo)
            || (uri.Scheme != Uri.UriSchemeHttps
                && !(uri.Scheme == Uri.UriSchemeHttp
                     && IsLoopbackHost(uri.Host))))
        {
            throw new ArgumentException(
                ResourceStringHelper.GetString(
                    "MihonRepositoryHttpsRequiredError",
                    "The Mihon repository must use HTTPS (HTTP is allowed only on loopback)."));
        }
        return new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty,
        }.Uri;
    }

    private static Uri NormalizeRepositoryIndexUri(string value)
    {
        var uri = NormalizeRepositoryUri(value);
        if (!uri.AbsolutePath.EndsWith(
                ".json",
                StringComparison.OrdinalIgnoreCase)
            && !uri.AbsolutePath.EndsWith(
                ".pb",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                ResourceStringHelper.GetString(
                    "MihonRepositoryIndexFormatError",
                    "The Mihon repository URL must end with .json or .pb."));
        }
        return uri;
    }

    private async Task<Dictionary<string, string>> GetExtensionHeadersAsync(
        MihonExtensionConfiguration configuration,
        MihonInstalledExtension source,
        CancellationToken ct)
    {
        List<string> values;
        try
        {
            values = await CallBridgeAsync<List<string>>(
                configuration,
                source,
                new Dictionary<string, object?>
                {
                    ["method"] = "headersManga",
                },
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is HttpRequestException
            or InvalidDataException
            or IOException)
        {
            // Mangayomi treats extension headers as optional. A sidecar or
            // source that cannot expose them must not abort APK installation.
            return new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index + 1 < values.Count; index += 2)
        {
            var name = values[index];
            if (IsAllowedForwardHeader(name))
                headers[name] = values[index + 1];
        }
        return headers;
    }

    private async Task<string?> TryDownloadRepositoryIconAsync(
        string value,
        string directory,
        CancellationToken ct)
    {
        try
        {
            var uri = NormalizeRepositoryUri(value);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength
                    > MaximumSourceIconBytes)
            {
                return null;
            }
            var extension = GetRasterImageExtension(
                response.Content.Headers.ContentType?.MediaType);
            if (extension is null)
            {
                return null;
            }
            var temp = Path.Combine(
                directory,
                $"icon.{Guid.NewGuid():N}.tmp");
            try
            {
                await using var input =
                    await response.Content.ReadAsStreamAsync(ct);
                await using (var output = new FileStream(
                                 temp,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 81920,
                                 true))
                {
                    await CopyBoundedAsync(
                        input,
                        output,
                        MaximumSourceIconBytes,
                        ct);
                }
                var actualExtension = GetDetectedRasterImageExtension(temp);
                if (actualExtension is null)
                    return null;
                var target = Path.Combine(directory, "icon" + actualExtension);
                File.Move(temp, target, true);
                return target;
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static async Task<string?> ExtractLargestRasterIconAsync(
        byte[] apkBytes,
        string directory,
        CancellationToken ct)
    {
        using var input = new MemoryStream(apkBytes, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var icon = archive.Entries
            .Where(entry =>
                entry.FullName.StartsWith("res/", StringComparison.Ordinal)
                && entry.Length > 0
                && entry.Length <= MaximumSourceIconBytes
                && IsRasterImagePath(entry.FullName))
            .OrderByDescending(entry => entry.Length)
            .FirstOrDefault();
        if (icon is null)
            return null;

        var temp = Path.Combine(
            directory,
            $"icon.{Guid.NewGuid():N}.tmp");
        try
        {
            await using var iconStream = icon.Open();
            await using (var output = new FileStream(
                             temp,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             true))
            {
                await CopyBoundedAsync(
                    iconStream,
                    output,
                    MaximumSourceIconBytes,
                    ct);
            }
            var actualExtension = GetDetectedRasterImageExtension(temp);
            if (actualExtension is null)
                return null;
            var target = Path.Combine(directory, "icon" + actualExtension);
            File.Move(temp, target, true);
            return target;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static bool IsRasterImagePath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".bmp";

    private static string? FindCachedSourceIcon(string directory)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "icon.*")
                .Where(IsRasterImagePath)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path)))
            {
                if (new FileInfo(path).Length <= 0)
                    continue;

                if (!HasRasterImageSignature(path))
                    continue;

                var actualExtension = GetDetectedRasterImageExtension(path);
                if (actualExtension is null)
                    continue;

                if (ImageExtensionsMatch(path, actualExtension))
                    return path;

                // Older builds trusted the response content type and could
                // leave a JPEG named icon.png. Normalize that cache entry so
                // it remains reusable without feeding a mislabeled file to
                // WinUI's image decoder.
                var normalized = Path.Combine(directory, "icon" + actualExtension);
                var temp = normalized + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    File.Copy(path, temp, false);
                    File.Move(temp, normalized, true);
                    return normalized;
                }
                finally
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
            }

            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? MigrateCachedSourceIcon(
        string sourcePath,
        string directory)
    {
        if (!IsRasterImagePath(sourcePath))
        {
            return null;
        }

        var extension = GetDetectedRasterImageExtension(sourcePath);
        if (extension is null)
            return null;

        var target = Path.Combine(directory, "icon" + extension);
        var temp = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(sourcePath, temp, false);
            if (GetDetectedRasterImageExtension(temp) is null)
                return null;
            File.Move(temp, target, true);
            return target;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static string? GetRasterImageExtension(string? mediaType) =>
        mediaType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/bmp" or "image/x-ms-bmp" => ".bmp",
            _ => null,
        };

    private static bool HasRasterImageSignature(string path)
        => GetDetectedRasterImageExtension(path) is not null;

    private static string? GetDetectedRasterImageExtension(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[12];
            var read = stream.Read(header);
            if (read < 2)
                return null;

            if (read >= 8
                && header[0] == 0x89
                && header[1] == 0x50
                && header[2] == 0x4E
                && header[3] == 0x47
                && header[4] == 0x0D
                && header[5] == 0x0A
                && header[6] == 0x1A
                && header[7] == 0x0A)
            {
                return ".png";
            }

            if (header[0] == 0xFF && header[1] == 0xD8)
                return ".jpg";

            if (header[0] == (byte)'B' && header[1] == (byte)'M')
                return ".bmp";

            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool ImageExtensionsMatch(
        string path,
        string detectedExtension) =>
        string.Equals(
            Path.GetExtension(path),
            detectedExtension,
            StringComparison.OrdinalIgnoreCase)
        || (detectedExtension == ".jpg"
            && string.Equals(
                Path.GetExtension(path),
                ".jpeg",
                StringComparison.OrdinalIgnoreCase));

    private bool IsPathWithinExtensionRoot(string path)
    {
        try
        {
            var root = Path.GetFullPath(_extensionRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(path);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left)
            || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private async Task<T> CallBridgeAsync<T>(
        MihonExtensionConfiguration configuration,
        MihonInstalledExtension source,
        Dictionary<string, object?> payload,
        CancellationToken ct)
    {
        await ConnectAsync(configuration, ct);
        payload["data"] = await GetApkBase64Async(source.ApkPath, ct);
        payload["sourceId"] = source.SourceId;
        using var content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(GetActiveBridgeUri(configuration), "dalvik"))
        {
            Content = content,
        };
        foreach (var header in source.Headers)
        {
            if (IsAllowedForwardHeader(header.Key))
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        using var response = await SendAsync(
            request,
            MaximumBridgeJsonBytes,
            ct);
        var bytes = await ReadBoundedAsync(
            await response.Content.ReadAsStreamAsync(ct),
            MaximumBridgeJsonBytes,
            ct);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                   ?? throw new InvalidDataException(
                       ResourceStringHelper.GetString(
                           "MihonBridgeEmptyResponseError",
                           "The Mihon bridge returned an empty response."));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                ResourceStringHelper.GetString(
                    "MihonBridgeInvalidJsonError",
                    "The Mihon bridge returned invalid JSON."),
                ex);
        }
    }

    private Uri GetActiveBridgeUri(
        MihonExtensionConfiguration configuration)
    {
        if (_bundledRuntimeRoot is null)
            return NormalizeBridgeUri(configuration.BridgeUrl);
        return _ownedBridgeUri
               ?? throw new InvalidOperationException(
                   ResourceStringHelper.GetString(
                       "MihonBridgeNotRunningError",
                       "The bundled Mihon bridge is not running."));
    }

    private async Task<bool> IsBridgeAvailableAsync(
        Uri bridgeUri,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, bridgeUri);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength > 4096)
            {
                return false;
            }
            var text = Encoding.UTF8.GetString(await ReadBoundedAsync(
                await response.Content.ReadAsStreamAsync(timeout.Token),
                4096,
                timeout.Token));
            return text.Contains(
                "mextensionserver Server Running",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private async Task<string> DownloadImageAsync(
        string value,
        IReadOnlyDictionary<string, string> headers,
        string directory,
        string fileName,
        CancellationToken ct)
    {
        var uri = NormalizeRemoteMediaUri(value);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var header in headers)
        {
            if (IsAllowedForwardHeader(header.Key))
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        using var response = await SendAsync(request, MaximumImageBytes, ct);
        var target = Path.Combine(
            directory,
            fileName + ImageExtension(response.Content.Headers.ContentType?.MediaType));
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

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        int maximumBytes,
        CancellationToken ct)
    {
        var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            response.Dispose();
            throw new InvalidDataException(ResourceStringHelper.GetString(
                "MihonResponseTooLargeError",
                "The Mihon response is too large."));
        }
        if (response.IsSuccessStatusCode)
            return response;

        var status = (int)response.StatusCode;
        var message = string.Empty;
        try
        {
            var bytes = await ReadBoundedAsync(
                await response.Content.ReadAsStreamAsync(ct),
                64 * 1024,
                ct);
            using var error = JsonDocument.Parse(bytes);
            if (error.RootElement.TryGetProperty("error", out var errorValue))
                message = errorValue.GetString() ?? string.Empty;
        }
        catch
        {
        }
        response.Dispose();
        throw new HttpRequestException(
            string.IsNullOrWhiteSpace(message)
                ? ResourceStringHelper.FormatString(
                    "MihonHttpError",
                    "The Mihon endpoint returned HTTP {0}.",
                    status)
                : ResourceStringHelper.FormatString(
                    "MihonHttpDetailedError",
                    "The Mihon endpoint returned HTTP {0}: {1}",
                    status,
                    message));
    }

    private async Task<string> GetApkBase64Async(
        string path,
        CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumApkBytes)
            throw new InvalidDataException(ResourceStringHelper.GetString(
                "MihonInstalledApkInvalidError",
                "The installed Mihon APK is missing or too large."));
        if (string.Equals(_cachedApkPath, fullPath, StringComparison.OrdinalIgnoreCase)
            && _cachedApkWriteTimeUtc == info.LastWriteTimeUtc
            && _cachedApkLength == info.Length
            && _cachedApkBase64 is not null)
        {
            return _cachedApkBase64;
        }
        var bytes = await File.ReadAllBytesAsync(fullPath, ct);
        ValidateApk(bytes);
        _cachedApkPath = fullPath;
        _cachedApkWriteTimeUtc = info.LastWriteTimeUtc;
        _cachedApkLength = info.Length;
        _cachedApkBase64 = Convert.ToBase64String(bytes);
        return _cachedApkBase64;
    }

    private async Task EnsureInstalledExtensionAsync(
        MihonInstalledExtension source,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!File.Exists(source.ApkPath))
            throw new FileNotFoundException(
                ResourceStringHelper.GetString(
                    "MihonInstalledApkMissingError",
                    "The installed Mihon APK could not be found."),
                source.ApkPath);
        await using var input = File.OpenRead(source.ApkPath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct))
            .ToLowerInvariant();
        if (!string.Equals(hash, source.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                ResourceStringHelper.GetString(
                    "MihonInstalledApkHashError",
                    "The installed Mihon APK no longer matches its recorded hash."));
        }
    }

    private async Task<MihonInstalledExtensionCatalog> LoadInstalledCatalogCoreAsync(
        CancellationToken ct)
    {
        if (!File.Exists(_catalogPath))
            return new MihonInstalledExtensionCatalog();
        try
        {
            await using var input = File.OpenRead(_catalogPath);
            var catalog =
                await JsonSerializer.DeserializeAsync<MihonInstalledExtensionCatalog>(
                    input,
                    JsonOptions,
                    ct)
                ?? new MihonInstalledExtensionCatalog();
            catalog.Extensions ??= [];
            foreach (var extension in catalog.Extensions)
            {
                extension.Headers = extension.Headers is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(
                        extension.Headers,
                        StringComparer.OrdinalIgnoreCase);
            }
            return catalog;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                ResourceStringHelper.GetString(
                    "MihonInstalledCatalogInvalidError",
                    "The installed Mihon extension catalog is invalid."),
                ex);
        }
    }

    private static async Task WriteAtomicJsonAsync<T>(
        string path,
        T value,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException(
                            "The Mihon data path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
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
                await JsonSerializer.SerializeAsync(output, value, JsonOptions, ct);
                await output.FlushAsync(ct);
            }
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static Uri BuildRepositoryAssetUri(
        Uri indexUri,
        string folder,
        string fileName)
    {
        var directory = indexUri.AbsoluteUri[..(
            indexUri.AbsoluteUri.LastIndexOf('/') + 1)];
        return new Uri(
            new Uri(directory),
            $"{folder}/{Uri.EscapeDataString(fileName)}");
    }

    private static Uri NormalizeApkUri(string value)
    {
        var uri = NormalizeRepositoryUri(value);
        if (!uri.AbsolutePath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(ResourceStringHelper.GetString(
                "MihonDownloadNotApkError",
                "The extension download is not an APK."));
        return uri;
    }

    private static Uri NormalizeRemoteMediaUri(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrWhiteSpace(uri.UserInfo)
            || (uri.Scheme != Uri.UriSchemeHttp
                && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidDataException(
                ResourceStringHelper.GetString(
                    "MihonInvalidMediaUrlError",
                    "The Mihon extension returned an invalid media URL."));
        }
        if (IsLoopbackHost(uri.Host)
            || IPAddress.TryParse(uri.Host, out var address)
            && IsPrivateAddress(address))
        {
            throw new InvalidDataException(
                ResourceStringHelper.GetString(
                    "MihonPrivateMediaUrlError",
                    "The Mihon extension returned a local or private media URL."));
        }
        return uri;
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host, out var address)
        && IPAddress.IsLoopback(address);

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                   || bytes[0] == 127
                   || bytes[0] == 169 && bytes[1] == 254
                   || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                   || bytes[0] == 192 && bytes[1] == 168;
        }
        return address.IsIPv6LinkLocal
               || address.IsIPv6SiteLocal
               || address.Equals(IPAddress.IPv6Loopback);
    }

    private static bool IsAllowedForwardHeader(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.Equals("Host", StringComparison.OrdinalIgnoreCase)
        && !name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        && !name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        && !name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetRequiredString(
        JsonElement value,
        string name,
        out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        result = property.GetString()?.Trim() ?? string.Empty;
        return result.Length is > 0 and <= 1024;
    }

    private static bool TryGetSourceId(JsonElement value, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty("id", out var property))
            return false;
        result = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty,
        };
        return result.Length is > 0 and <= 128;
    }

    private static int ReadInt(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property))
            return 0;
        if (property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var number))
        {
            return number;
        }
        return property.ValueKind == JsonValueKind.String
               && int.TryParse(property.GetString(), out number)
            ? number
            : 0;
    }

    private static bool IsSafeFileName(string value) =>
        value.Length <= 255
        && string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal)
        && value.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
        && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static string SafePathSegment(string value)
    {
        var safe = new string(value
            .Select(character =>
                char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                    ? character
                    : '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(safe)
            ? "extension"
            : safe[..Math.Min(safe.Length, 120)];
    }

    private static void ValidateApk(byte[] bytes)
    {
        if (bytes.Length is < 4 or > MaximumApkBytes
            || bytes[0] != (byte)'P'
            || bytes[1] != (byte)'K')
        {
            throw new InvalidDataException(ResourceStringHelper.GetString(
                "MihonInvalidApkError",
                "The downloaded extension is not a valid APK."));
        }
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            if (archive.Entries.Count == 0
                || archive.Entries.Count > MaximumArchiveEntries
                || !archive.Entries.Any(entry =>
                    string.Equals(
                        entry.FullName,
                        "AndroidManifest.xml",
                        StringComparison.Ordinal))
                || !archive.Entries.Any(entry =>
                    entry.FullName.StartsWith("classes", StringComparison.Ordinal)
                    && entry.FullName.EndsWith(".dex", StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    ResourceStringHelper.GetString(
                        "MihonApkPayloadMissingError",
                        "The downloaded APK is missing its manifest or DEX payload."));
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                ResourceStringHelper.GetString(
                    "MihonApkUnreadableError",
                    "The downloaded APK cannot be read."),
                ex);
        }
    }

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
                    "MihonResponseTooLargeError",
                    "The Mihon response is too large."));
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

    private void StopOwnedBridge()
    {
        if (_bridgeProcess is null)
        {
            _ownedBridgeUri = null;
            return;
        }
        try
        {
            if (!_bridgeProcess.HasExited)
                _bridgeProcess.Kill(entireProcessTree: true);
        }
        catch
        {
        }
        _bridgeProcess.Dispose();
        _bridgeProcess = null;
        _ownedBridgeUri = null;
    }

    public void Dispose()
    {
        StopOwnedBridge();
        _bridgeGate.Dispose();
        _storeGate.Dispose();
        _http.Dispose();
    }
}

internal sealed record MihonBundledRuntime(
    string Version,
    string JavaExecutablePath,
    string ServerJarPath,
    string OverlayJarPath);

internal sealed class MihonBundledRuntimeManifest
{
    public int SchemaVersion { get; set; }
    public string Version { get; set; } = string.Empty;
    public string JavaExecutable { get; set; } = string.Empty;
    public string ServerJar { get; set; } = string.Empty;
    public string OverlayJar { get; set; } = string.Empty;
}
