using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Hoshi.Helpers;
using Hoshi.Models.Dictionary;

namespace Hoshi.Services.Dictionary;

public sealed class DictionaryLookupService : IDictionaryLookupService, IDisposable
{
    private readonly ILogger<DictionaryLookupService> _logger;
    private readonly string _dictionaryStorageDir;
    private Dictionary<string, List<IndexedTerm>> _termIndex = new(StringComparer.Ordinal);
    private Dictionary<string, List<IndexedTerm>> _readingIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _styles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DictionaryData> _loadedDicts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _rebuildLock = new(1, 1);
    private bool _indexReady;

    public DictionaryLookupService(ILogger<DictionaryLookupService> logger, string? dictionaryStorageDir = null)
    {
        _logger = logger;
        _dictionaryStorageDir = dictionaryStorageDir ?? GetDictionaryStorageDir();
    }

    public async Task<List<DictionaryLookupResult>> LookupAsync(
        string text, int maxResults = 16, int scanLength = 16)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        await EnsureIndexAsync();

        var termIndex = _termIndex;
        var readingIndex = _readingIndex;
        var resultMap = new Dictionary<string, DictionaryLookupResult>(StringComparer.Ordinal);
        var deinflector = JapaneseDeinflector.Instance;

        foreach (var candidate in EnumerateLookupCandidates(text, scanLength))
        {
            foreach (var deinflection in deinflector.Deinflect(candidate))
            {
                AddMatches(
                    termIndex,
                    deinflection.Text,
                    candidate,
                    deinflection,
                    resultMap);

                AddMatches(
                    readingIndex,
                    deinflection.Text,
                    candidate,
                    deinflection,
                    resultMap);
            }
        }

        return resultMap.Values
            .OrderByDescending(r => r.Matched.EnumerateRunes().Count())
            .ThenBy(r => r.PreprocessorSteps)
            .ThenBy(r => r.Trace.Count)
            .ThenByDescending(r => string.Equals(r.Term.Expression, r.Deinflected, StringComparison.Ordinal))
            .ThenBy(r => r.Term.Glossaries.FirstOrDefault()?.DictName ?? "", StringComparer.Ordinal)
            .ThenByDescending(r => r.Term.Glossaries.Count)
            .Take(maxResults)
            .ToList();
    }

    public static IEnumerable<string> EnumerateLookupCandidates(string text, int scanLength = 16)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        var runes = text.EnumerateRunes()
            .Take(Math.Max(0, scanLength))
            .ToArray();

        for (var length = runes.Length; length > 0; length--)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < length; i++)
                builder.Append(runes[i]);
            yield return builder.ToString();
        }
    }

    public Task<List<DictionaryStyle>> GetStylesAsync()
    {
        var styles = _styles.Select(kv => new DictionaryStyle(kv.Key, kv.Value)).ToList();
        return Task.FromResult(styles);
    }

    public Task<byte[]?> GetMediaFileAsync(string dictName, string mediaPath)
    {
        if (!_loadedDicts.TryGetValue(dictName, out var dictData))
            return Task.FromResult<byte[]?>(null);

        var mediaDir = Path.Combine(dictData.BasePath, "media");
        var fullPath = Path.GetFullPath(Path.Combine(mediaDir, mediaPath));
        if (!fullPath.StartsWith(mediaDir, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<byte[]?>(null);

        if (File.Exists(fullPath))
            return Task.FromResult<byte[]?>(File.ReadAllBytes(fullPath));

        return Task.FromResult<byte[]?>(null);
    }

    public async Task RebuildQueryAsync()
    {
        await _rebuildLock.WaitAsync();
        try
        {
            var state = await Task.Run(BuildIndex);
            _termIndex = state.TermIndex;
            _readingIndex = state.ReadingIndex;

            _styles.Clear();
            foreach (var kv in state.Styles)
                _styles[kv.Key] = kv.Value;

            _loadedDicts.Clear();
            foreach (var kv in state.LoadedDicts)
                _loadedDicts[kv.Key] = kv.Value;

            _indexReady = true;
            _logger.LogInformation(
                "Dictionary index rebuilt: {TermCount} unique expressions from {DictCount} dictionaries",
                _termIndex.Count,
                _loadedDicts.Count);
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    private DictionaryIndexState BuildIndex()
    {
        var state = new DictionaryIndexState();

        var dictDir = _dictionaryStorageDir;
        if (!Directory.Exists(dictDir))
            return state;

        foreach (var (subDir, order) in GetOrderedDictionaryDirectories(dictDir).Select((path, index) => (path, index)))
        {
            try
            {
                LoadDictionary(subDir, order, state);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to load dictionary from {Dir}", subDir);
            }
        }

        return state;
    }

    private static void LoadDictionary(string dirPath, int dictionaryOrder, DictionaryIndexState state)
    {
        var indexFile = Path.Combine(dirPath, "index.json");
        if (!File.Exists(indexFile)) return;

        var indexJson = File.ReadAllText(indexFile);
        using var indexDoc = JsonDocument.Parse(indexJson);
        var title = indexDoc.RootElement.GetProperty("title").GetString() ?? Path.GetFileName(dirPath);

        var dictData = new DictionaryData { Name = title, BasePath = dirPath };

        // Load styles
        var stylesPath = Path.Combine(dirPath, "styles.css");
        if (File.Exists(stylesPath))
            state.Styles[title] = File.ReadAllText(stylesPath);

        // Load term banks
        foreach (var termFile in Directory.EnumerateFiles(dirPath, "term_bank_*.json"))
        {
            LoadTermBank(termFile, title, dictionaryOrder, state);
        }

        state.LoadedDicts[title] = dictData;
    }

    private static void LoadTermBank(string filePath, string dictName, int dictionaryOrder, DictionaryIndexState state)
    {
        var json = File.ReadAllText(filePath);
        using var document = JsonDocument.Parse(json);

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var arr = element.EnumerateArray().ToArray();
            if (arr.Length < 6) continue;

            var expression = arr[0].GetString() ?? "";
            var reading = arr[1].GetString() ?? "";
            var definitionTags = arr[2].GetString() ?? "";
            var rules = arr[3].GetString() ?? "";
            var score = arr.Length > 4 ? arr[4].GetInt32() : 0;
            var glossaryList = arr[5];
            var termTags = arr.Length > 7 ? arr[7].GetString() ?? "" : "";

            var glossaries = ParseGlossaryList(glossaryList, dictName, definitionTags, termTags);

            var term = new IndexedTerm
            {
                Expression = expression,
                Reading = reading,
                Rules = rules,
                Score = score,
                Glossaries = glossaries,
                DictName = dictName,
                DictionaryOrder = dictionaryOrder,
            };

            if (!state.TermIndex.TryGetValue(expression, out var list))
            {
                list = [];
                state.TermIndex[expression] = list;
            }
            list.Add(term);

            if (!string.IsNullOrEmpty(reading))
            {
                if (!state.ReadingIndex.TryGetValue(reading, out var readingList))
                {
                    readingList = [];
                    state.ReadingIndex[reading] = readingList;
                }
                readingList.Add(term);
            }
        }
    }

    private static List<GlossaryEntry> ParseGlossaryList(
        JsonElement glossaryElement, string dictName, string definitionTags, string termTags)
    {
        var entries = new List<GlossaryEntry>();

        if (glossaryElement.ValueKind == JsonValueKind.String)
        {
            entries.Add(new GlossaryEntry(dictName, glossaryElement.GetString() ?? "", definitionTags, termTags));
        }
        else if (glossaryElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in glossaryElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    entries.Add(new GlossaryEntry(dictName, item.GetString() ?? "", definitionTags, termTags));
                }
                else
                {
                    entries.Add(new GlossaryEntry(
                        dictName,
                        item.GetRawText(),
                        definitionTags,
                        termTags));
                }
            }
        }

        return entries;
    }

    private static DictionaryLookupResult TermToResult(
        IndexedTerm term,
        string matched,
        string deinflected,
        List<TransformGroup> trace,
        int preprocessorSteps = 0)
    {
        return new DictionaryLookupResult(
            Matched: matched,
            Deinflected: deinflected,
            Trace: trace,
            Term: new TermResult(
                Expression: term.Expression,
                Reading: term.Reading,
                Rules: term.Rules,
                Glossaries: term.Glossaries,
                Frequencies: [],
                Pitches: []
            ),
            PreprocessorSteps: preprocessorSteps
        );
    }

    private static void AddMatches(
        Dictionary<string, List<IndexedTerm>> index,
        string query,
        string matched,
        JapaneseDeinflectionResult deinflection,
        Dictionary<string, DictionaryLookupResult> resultMap)
    {
        if (!index.TryGetValue(query, out var matches))
            return;

        foreach (var term in matches)
        {
            if (!MatchesPartOfSpeech(term, deinflection))
                continue;

            var key = $"{term.Expression}|{term.Reading}";
            var next = TermToResult(term, matched, deinflection.Text, deinflection.Trace);
            if (!resultMap.TryGetValue(key, out var existing)
                || matched.EnumerateRunes().Count() > existing.Matched.EnumerateRunes().Count()
                || (matched == existing.Matched && next.Trace.Count < existing.Trace.Count))
            {
                resultMap[key] = next;
            }
        }
    }

    private static bool MatchesPartOfSpeech(IndexedTerm term, JapaneseDeinflectionResult deinflection)
    {
        if (deinflection.Conditions == JapaneseDeinflectionConditions.None)
            return true;

        var termConditions = JapaneseDeinflector.PosToConditions(term.Rules);
        if (termConditions == JapaneseDeinflectionConditions.None)
            return true;

        return (termConditions & deinflection.Conditions) != 0;
    }

    private async Task EnsureIndexAsync()
    {
        if (_indexReady) return;
        await RebuildQueryAsync();
    }

    private static string GetDictionaryStorageDir()
    {
        var appData = AppDataHelper.GetAppDataPath();
        return Path.Combine(appData, "dictionaries");
    }

    private static IReadOnlyList<string> GetOrderedDictionaryDirectories(string dictDir)
    {
        var directories = Directory.EnumerateDirectories(dictDir).ToList();
        var byName = directories
            .Select(path => (Name: Path.GetFileName(path), Path: path))
            .Where(item => !string.IsNullOrEmpty(item.Name))
            .ToDictionary(item => item.Name!, item => item.Path, StringComparer.Ordinal);
        var config = DictionaryConfigurationStore.Load(dictDir);
        var enabledTermEntries = DictionaryConfigurationStore
            .MergeWithInstalled(
                DictionaryConfigurationStore.GetEntries(config, DictionaryType.Term),
                byName.Keys.Where(name => name != null).Select(name => name!).ToList())
            .Where(entry => entry.IsEnabled)
            .ToList();

        return enabledTermEntries
            .Select(entry => byName.TryGetValue(entry.FileName, out var path) ? path : null)
            .Where(path => path != null)
            .Select(path => path!)
            .ToList();
    }

    public void Dispose()
    {
        _termIndex.Clear();
        _readingIndex.Clear();
        _loadedDicts.Clear();
        _styles.Clear();
        _rebuildLock.Dispose();
    }

    private sealed class DictionaryIndexState
    {
        public Dictionary<string, List<IndexedTerm>> TermIndex { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<IndexedTerm>> ReadingIndex { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Styles { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, DictionaryData> LoadedDicts { get; } = new(StringComparer.Ordinal);
    }

    private sealed class DictionaryData
    {
        public string Name { get; set; } = "";
        public string BasePath { get; set; } = "";
    }

    private sealed class IndexedTerm
    {
        public string Expression { get; set; } = "";
        public string Reading { get; set; } = "";
        public string Rules { get; set; } = "";
        public int Score { get; set; }
        public List<GlossaryEntry> Glossaries { get; set; } = [];
        public string DictName { get; set; } = "";
        public int DictionaryOrder { get; set; }
    }
}
