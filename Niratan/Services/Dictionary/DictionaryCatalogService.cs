using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Niratan.Models.Dictionary;
using Niratan.Services.Profiles;
using Niratan.Services.Settings;

namespace Niratan.Services.Dictionary;

public sealed partial class DictionaryCatalogService : IDictionaryCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly IReadOnlyList<DictionaryRecommendation> Recommendations =
    [
        new("jmdict", "JMdict", DictionaryType.Term,
            "https://github.com/yomidevs/jmdict-yomitan/releases/latest/download/JMdict_english_without_proper_names.json", null, "ja"),
        new("jmnedict", "JMnedict", DictionaryType.Term,
            "https://github.com/yomidevs/jmdict-yomitan/releases/latest/download/JMnedict.json", null, "ja"),
        new("jiten", "Jiten", DictionaryType.Frequency,
            "https://api.jiten.moe/api/frequency-list/index", null, "ja"),
        new("jitendex", "Jitendex", DictionaryType.Term,
            "https://jitendex.org/static/yomitan.json", null, "ja"),
        new("wty-en-en", "Wiktionary English-English", DictionaryType.Term,
            "https://huggingface.co/datasets/daxida/wty-release/resolve/main/latest/index/wty-en-en-index.json?download=true", null, "en"),
        new("wty-en-en-ipa", "Wiktionary English-English IPA", DictionaryType.Pitch,
            "https://huggingface.co/datasets/daxida/wty-release/resolve/main/latest/index/wty-en-en-ipa-index.json?download=true", null, "en"),
        new("wty-simple-simple", "Wiktionary Simple English-Simple English", DictionaryType.Term,
            "https://huggingface.co/datasets/daxida/wty-release/resolve/main/latest/index/wty-simple-simple-index.json?download=true", null, "en"),
        new("wty-en-ja", "Wiktionary English-Japanese", DictionaryType.Term,
            "https://huggingface.co/datasets/daxida/wty-release/resolve/main/latest/index/wty-en-ja-index.json?download=true", null, "en"),
        new("wty-en-ja-gloss", "Wiktionary English-Japanese Glossary", DictionaryType.Term,
            "https://huggingface.co/datasets/daxida/wty-release/resolve/main/latest/index/wty-en-ja-gloss-index.json?download=true", null, "en"),
        new("leipzig-english-web-rank", "Leipzig English Web", DictionaryType.Frequency, null,
            "https://github.com/StefanVukovic99/leipzig-to-yomitan/releases/latest/download/Leipzig.English.Web.Rank.zip", "en"),
        new("leipzig-english-wikipedia-rank", "Leipzig English Wikipedia", DictionaryType.Frequency, null,
            "https://github.com/StefanVukovic99/leipzig-to-yomitan/releases/latest/download/Leipzig.English.Wikipedia.Rank.zip", "en"),
    ];

    private readonly HttpClient _httpClient;
    private readonly IDictionaryImportService _importService;
    private readonly IProfileRuntimeService _profileRuntime;
    private readonly ISettingsService _settings;
    private readonly ILogger<DictionaryCatalogService> _logger;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public DictionaryCatalogService(
        HttpClient httpClient,
        IDictionaryImportService importService,
        IProfileRuntimeService profileRuntime,
        ISettingsService settings,
        ILogger<DictionaryCatalogService> logger)
    {
        _httpClient = httpClient;
        _importService = importService;
        _profileRuntime = profileRuntime;
        _settings = settings;
        _logger = logger;
    }

    public IReadOnlyList<DictionaryRecommendation> GetRecommendations() =>
        Recommendations
            .Where(item => string.Equals(
                item.LanguageId,
                _profileRuntime.ActiveLanguage.Id,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

    public async Task<DictionaryUpdateCheckResult> CheckForUpdatesAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var installed = await _importService.GetInstalledDictionariesAsync();
        var candidates = new List<DictionaryUpdateCandidate>();
        var failures = new List<string>();
        var checkedCount = 0;

        foreach (var dictionary in installed)
        {
            ct.ThrowIfCancellationRequested();
            var indexUrl = ResolveIndexUrl(dictionary);
            if (string.IsNullOrWhiteSpace(indexUrl))
                continue;

            checkedCount++;
            progress?.Report($"Checking {dictionary.DisplayTitle}...");
            try
            {
                var remote = await GetRemoteIndexAsync(indexUrl, ct);
                if (!string.Equals(dictionary.Revision, remote.Revision, StringComparison.Ordinal))
                {
                    candidates.Add(new DictionaryUpdateCandidate(
                        dictionary,
                        indexUrl,
                        remote.Revision,
                        RequireHttpsUrl(remote.DownloadUrl)));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{dictionary.DisplayTitle}: {ex.Message}");
                _logger.LogWarning(ex, "[DictionaryCatalog] Failed to check {Dictionary}", dictionary.DisplayTitle);
            }
        }

        return new DictionaryUpdateCheckResult(candidates, failures, checkedCount);
    }

    public Task<DictionaryBatchOperationResult> DownloadRecommendationsAsync(
        IReadOnlyList<DictionaryRecommendation> recommendations,
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        RunBatchAsync(
            recommendations,
            item => item.Name,
            async (item, token) =>
            {
                var downloadUrl = item.DownloadUrl;
                if (!string.IsNullOrWhiteSpace(item.IndexUrl))
                    downloadUrl = (await GetRemoteIndexAsync(item.IndexUrl, token)).DownloadUrl;
                await DownloadAndImportAsync(item.Name, RequireHttpsUrl(downloadUrl), progress, token);
            },
            ct);

    public Task<DictionaryBatchOperationResult> UpdateDictionariesAsync(
        IReadOnlyList<DictionaryUpdateCandidate> updates,
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        RunBatchAsync(
            updates,
            item => item.Dictionary.DisplayTitle,
            async (item, token) =>
            {
                var result = await DownloadAndImportAsync(
                    item.Dictionary.DisplayTitle,
                    RequireHttpsUrl(item.DownloadUrl),
                    progress,
                    token);
                if (!string.Equals(result.Title, item.Dictionary.DisplayTitle, StringComparison.Ordinal)
                    && !string.Equals(result.Title, item.Dictionary.Name, StringComparison.Ordinal))
                {
                    await _importService.MigrateDictionaryNameAsync(
                        item.Dictionary.Type,
                        item.Dictionary.Name,
                        result.Title);
                    ReplaceCollapsedDictionaryName(item.Dictionary.DisplayTitle, result.Title);
                }
            },
            ct,
            recordLastUpdate: true);

    public async Task TryAutoUpdateAsync(CancellationToken ct = default)
    {
        var updateSettings = _settings.Current.DictionaryUpdateSettings;
        if (!updateSettings.UpdateAutomatically)
            return;
        if (updateSettings.LastUpdate is { } last
            && DateTimeOffset.Now - last < updateSettings.GetInterval())
        {
            return;
        }

        if (!await _operationLock.WaitAsync(0, ct))
            return;
        try
        {
            var check = await CheckForUpdatesAsync(ct: ct);
            if (check.Updates.Count > 0)
                await UpdateDictionariesCoreAsync(check.Updates, ct);
            else if (check.CheckedCount > check.Failures.Count)
                await RecordLastUpdateAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[DictionaryCatalog] Automatic dictionary update failed");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task UpdateDictionariesCoreAsync(
        IReadOnlyList<DictionaryUpdateCandidate> updates,
        CancellationToken ct)
    {
        await RunBatchCoreAsync(
            updates,
            item => item.Dictionary.DisplayTitle,
            async (item, token) =>
            {
                var result = await DownloadAndImportAsync(
                    item.Dictionary.DisplayTitle,
                    RequireHttpsUrl(item.DownloadUrl),
                    progress: null,
                    token);
                if (!string.Equals(result.Title, item.Dictionary.DisplayTitle, StringComparison.Ordinal)
                    && !string.Equals(result.Title, item.Dictionary.Name, StringComparison.Ordinal))
                {
                    await _importService.MigrateDictionaryNameAsync(
                        item.Dictionary.Type,
                        item.Dictionary.Name,
                        result.Title);
                    ReplaceCollapsedDictionaryName(item.Dictionary.DisplayTitle, result.Title);
                }
            },
            ct,
            recordLastUpdate: true);
    }

    private async Task<DictionaryBatchOperationResult> RunBatchAsync<T>(
        IReadOnlyList<T> items,
        Func<T, string> getName,
        Func<T, CancellationToken, Task> operation,
        CancellationToken ct,
        bool recordLastUpdate = false)
    {
        await _operationLock.WaitAsync(ct);
        try
        {
            return await RunBatchCoreAsync(items, getName, operation, ct, recordLastUpdate);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<DictionaryBatchOperationResult> RunBatchCoreAsync<T>(
        IReadOnlyList<T> items,
        Func<T, string> getName,
        Func<T, CancellationToken, Task> operation,
        CancellationToken ct,
        bool recordLastUpdate)
    {
        var succeeded = new List<string>();
        var failures = new List<string>();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var name = getName(item);
            try
            {
                await operation(item, ct);
                succeeded.Add(name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{name}: {ex.Message}");
                _logger.LogWarning(ex, "[DictionaryCatalog] Operation failed for {Dictionary}", name);
            }
        }

        if (recordLastUpdate && succeeded.Count > 0)
            await RecordLastUpdateAsync();

        return new DictionaryBatchOperationResult(succeeded, failures);
    }

    private async Task RecordLastUpdateAsync()
    {
        _settings.Current.DictionaryUpdateSettings.LastUpdate = DateTimeOffset.Now;
        _settings.Set(
            settings => settings.DictionaryUpdateSettings,
            _settings.Current.DictionaryUpdateSettings);
        await _settings.SaveAsync();
    }

    private async Task<DictionaryImportResult> DownloadAndImportAsync(
        string name,
        string downloadUrl,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report($"Downloading {name}...");
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "Niratan",
            "dictionary-downloads",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var zipPath = Path.Combine(tempDirectory, "dictionary.zip");
        try
        {
            using var request = CreateRequest(downloadUrl);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(zipPath))
                await input.CopyToAsync(output, ct);

            progress?.Report($"Importing {name}...");
            var result = await _importService.ImportAsync(zipPath);
            if (!result.Success)
            {
                var message = result.Errors.Count > 0
                    ? string.Join("; ", result.Errors)
                    : "Dictionary import failed.";
                throw new InvalidDataException(message);
            }

            return result;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                    Directory.Delete(tempDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[DictionaryCatalog] Failed to clean temporary download");
            }
        }
    }

    private async Task<RemoteDictionaryIndex> GetRemoteIndexAsync(
        string indexUrl,
        CancellationToken ct)
    {
        using var request = CreateRequest(RequireHttpsUrl(indexUrl));
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var index = await JsonSerializer.DeserializeAsync<RemoteDictionaryIndex>(stream, JsonOptions, ct)
                    ?? throw new InvalidDataException("Dictionary index response was empty.");
        if (string.IsNullOrWhiteSpace(index.Revision)
            || string.IsNullOrWhiteSpace(index.DownloadUrl))
        {
            throw new InvalidDataException("Dictionary index is missing revision or downloadUrl.");
        }

        return index;
    }

    private string? ResolveIndexUrl(InstalledDictionary dictionary)
    {
        if (!string.IsNullOrWhiteSpace(dictionary.IndexUrl))
            return dictionary.IndexUrl;

        return Recommendations.FirstOrDefault(item =>
            item.Type == dictionary.Type
            && !string.IsNullOrWhiteSpace(item.IndexUrl)
            && DictionaryTitlesMatch(dictionary.DisplayTitle, item.Name))?.IndexUrl;
    }

    private void ReplaceCollapsedDictionaryName(string oldName, string newName)
    {
        var display = _settings.Current.DictionaryDisplaySettings;
        if (!display.CollapsedDictionariesOrDefault.Contains(oldName))
            return;

        var collapsed = new HashSet<string>(display.CollapsedDictionariesOrDefault);
        collapsed.Remove(oldName);
        collapsed.Add(newName);
        _settings.Set(
            settings => settings.DictionaryDisplaySettings,
            display with { CollapsedDictionaries = collapsed });
        _ = _settings.SaveAsync();
    }

    internal static bool DictionaryTitlesMatch(string title, string recommendationName)
    {
        var normalizedTitle = NormalizeTitle(title);
        var withoutBracketSuffix = BracketSuffixRegex().Replace(normalizedTitle, "").Trim();
        var normalizedRecommendation = NormalizeTitle(recommendationName);
        return string.Equals(normalizedTitle, normalizedRecommendation, StringComparison.OrdinalIgnoreCase)
               || string.Equals(withoutBracketSuffix, normalizedRecommendation, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTitle(string title) =>
        WhitespaceRegex().Replace(title.Trim(), " ");

    private static string RequireHttpsUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Dictionary source must be an HTTPS URL.");
        }

        return uri.AbsoluteUri;
    }

    private static HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Niratan-Windows/0.6");
        return request;
    }

    [GeneratedRegex(@"\s*\[[^\]]+\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex BracketSuffixRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    private sealed record RemoteDictionaryIndex(
        string Title,
        string Revision,
        string DownloadUrl,
        string IndexUrl = "");
}
