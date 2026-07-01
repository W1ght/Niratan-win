using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Hoshi.Models.Settings;
using Serilog;

namespace Hoshi.Services.Anki;

public class AnkiConnectException : Exception
{
    public AnkiConnectException(string message) : base(message) { }
    public AnkiConnectException(string message, Exception inner) : base(message, inner) { }
}

public sealed class AnkiConnectClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private const int Version = 6;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public AnkiConnectClient(string endpoint)
    {
        _endpoint = endpoint.TrimEnd('/');
        _http = new HttpClient
        {
            Timeout = DefaultTimeout,
        };
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            await RequestAsync("version", null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<AnkiDeck>> FetchDecksAsync()
    {
        var result = await RequestAsync("deckNames", null);
        var names = result.Deserialize<List<string>>() ?? [];
        return names.Select(name => new AnkiDeck
        {
            Name = name,
            Id = AnkiDeck.ComputeStableId(name),
        }).ToList();
    }

    public async Task<List<AnkiNoteType>> FetchNoteTypesAsync()
    {
        var result = await RequestAsync("modelNames", null);
        var names = result.Deserialize<List<string>>() ?? [];

        var noteTypes = new List<AnkiNoteType>();
        foreach (var name in names)
        {
            var fields = await FetchModelFieldNamesAsync(name);
            noteTypes.Add(new AnkiNoteType
            {
                Name = name,
                Id = AnkiDeck.ComputeStableId(name),
                Fields = fields,
            });
        }

        return noteTypes;
    }

    public async Task<List<string>> FetchModelFieldNamesAsync(string modelName)
    {
        var result = await RequestAsync("modelFieldNames", new { modelName });
        return result.Deserialize<List<string>>() ?? [];
    }

    public async Task<bool> CanAddNotesAsync(
        AnkiDeck deck,
        AnkiNoteType noteType,
        Dictionary<string, string> fields,
        AnkiSettings settings)
    {
        var notes = new[]
        {
            BuildNoteObject(deck, noteType, fields, settings),
        };

        var result = await RequestAsync("canAddNotesWithErrorDetail", new { notes });
        var results = result.Deserialize<List<CanAddResult>>();
        return results?.FirstOrDefault()?.CanAdd ?? true;
    }

    public async Task<bool> AddNoteAsync(
        AnkiDeck deck,
        AnkiNoteType noteType,
        Dictionary<string, string> fields,
        AnkiSettings settings)
    {
        try
        {
            await RequestAsync("addNote", new { note = BuildNoteObject(deck, noteType, fields, settings) });
            return true;
        }
        catch (AnkiConnectException ex)
        {
            Log.Warning(ex, "[AnkiConnect] addNote failed");
            return false;
        }
    }

    public async Task<string> StoreMediaFileAsync(string filename, byte[] data)
    {
        var base64 = Convert.ToBase64String(data);
        var result = await RequestAsync("storeMediaFile", new
        {
            filename = StripDirectory(filename),
            data = base64,
        });
        return result.Deserialize<string>() ?? filename;
    }

    /// <summary>
    /// Uploads multiple media files in a single batched <c>multi</c> request.
    /// Returns stored filenames in the same order as <paramref name="files"/>.
    /// </summary>
    public async Task<List<string>> StoreMediaFilesAsync(List<(string filename, byte[] data)> files)
    {
        if (files.Count == 0) return [];

        var actions = files.Select(f => (
            action: "storeMediaFile",
            parameters: (object?)new
            {
                filename = StripDirectory(f.filename),
                data = Convert.ToBase64String(f.data),
            }
        )).ToList();

        var results = await MultiRequestAsync(actions);

        var storedNames = new List<string>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            try
            {
                var name = results[i].Deserialize<string>() ?? files[i].filename;
                storedNames.Add(name);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[AnkiConnect] storeMediaFile batch[{Index}] failed: {Filename}", i, files[i].filename);
                storedNames.Add(files[i].filename);
            }
        }
        return storedNames;
    }

    /// <summary>
    /// Sends multiple actions in one <c>multi</c> HTTP request.
    /// Returns one <see cref="JsonElement"/> per action, in order.
    /// </summary>
    public async Task<List<JsonElement>> MultiRequestAsync(List<(string action, object? parameters)> actions)
    {
        if (actions.Count == 0) return [];

        var actionList = actions.Select(a =>
        {
            var dict = new Dictionary<string, object>
            {
                ["action"] = a.action,
            };
            if (a.parameters != null)
                dict["params"] = a.parameters;
            return (object)dict;
        }).ToList();

        var resultArray = await RequestAsync("multi", new { actions = actionList });
        var results = new List<JsonElement>(actions.Count);
        foreach (var element in resultArray.EnumerateArray())
            results.Add(element.Clone());
        return results;
    }

    public async Task<bool> AddNoteWithOptionalSyncAsync(
        AnkiDeck deck,
        AnkiNoteType noteType,
        Dictionary<string, string> fields,
        AnkiSettings settings,
        bool sync)
    {
        var actions = new List<(string, object?)>
        {
            ("addNote", new { note = BuildNoteObject(deck, noteType, fields, settings) }),
        };
        if (sync)
            actions.Add(("sync", null));

        try
        {
            var results = await MultiRequestAsync(actions);
            // addNote result is the first element
            var addResult = results[0];
            if (addResult.ValueKind == JsonValueKind.Object && addResult.TryGetProperty("error", out _))
            {
                Log.Warning("[AnkiConnect] addNote failed in batch: {Result}", addResult.GetRawText());
                return false;
            }
            return true;
        }
        catch (AnkiConnectException ex)
        {
            Log.Warning(ex, "[AnkiConnect] addNote+sync batch failed");
            return false;
        }
    }

    public async Task SyncAsync()
    {
        try
        {
            await RequestAsync("sync", null);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AnkiConnect] sync failed");
        }
    }

    private static object BuildNoteObject(
        AnkiDeck deck,
        AnkiNoteType noteType,
        Dictionary<string, string> fields,
        AnkiSettings settings)
    {
        var options = new Dictionary<string, object>
        {
            ["allowDuplicate"] = settings.AllowDupes,
            ["duplicateScope"] = settings.DuplicateScope switch
            {
                AnkiDuplicateScope.Collection => "collection",
                AnkiDuplicateScope.Deck => "deck",
                AnkiDuplicateScope.DeckRoot => "deck",
                _ => "collection",
            },
        };

        if (settings.DuplicateScope == AnkiDuplicateScope.DeckRoot)
        {
            var rootName = deck.Name.Split("::")[0];
            options["duplicateScopeOptions"] = new Dictionary<string, object>
            {
                ["deckName"] = rootName,
                ["checkChildren"] = true,
            };
        }

        if (settings.CheckDuplicatesAcrossAllModels)
        {
            if (!options.ContainsKey("duplicateScopeOptions"))
                options["duplicateScopeOptions"] = new Dictionary<string, object>();
            ((Dictionary<string, object>)options["duplicateScopeOptions"])["checkAllModels"] = true;
        }

        var note = new Dictionary<string, object>
        {
            ["deckName"] = deck.Name,
            ["modelName"] = noteType.Name,
            ["fields"] = fields,
            ["options"] = options,
        };

        var tags = ParseTags(settings.Tags);
        if (tags.Count > 0)
            note["tags"] = tags;

        return note;
    }

    private static List<string> ParseTags(string tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return [];
        return tags.Split(' ', ',', ';', '\n')
                   .Select(t => t.Trim())
                   .Where(t => t.Length > 0)
                   .ToList();
    }

    private async Task<JsonElement> RequestAsync(string action, object? parameters)
    {
        var requestBody = new Dictionary<string, object>
        {
            ["action"] = action,
            ["version"] = Version,
        };

        if (parameters != null)
            requestBody["params"] = parameters;

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(_endpoint, content);
        }
        catch (Exception ex)
        {
            throw new AnkiConnectException($"Failed to connect to AnkiConnect at {_endpoint}: {ex.Message}", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(responseBody);
        }
        catch (Exception ex)
        {
            throw new AnkiConnectException($"Invalid JSON response from AnkiConnect: {ex.Message}", ex);
        }

        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            var errorMsg = error.TryGetProperty("content", out var contentElem)
                ? contentElem.GetString()
                : error.GetRawText();
            throw new AnkiConnectException(errorMsg ?? "Unknown AnkiConnect error");
        }

        if (root.TryGetProperty("result", out var result))
            return result.Clone();

        throw new AnkiConnectException("AnkiConnect response missing 'result' field");
    }

    private static string StripDirectory(string path)
    {
        var idx = path.LastIndexOfAny(['/', '\\']);
        return idx >= 0 ? path[(idx + 1)..] : path;
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    private sealed class CanAddResult
    {
        [JsonPropertyName("canAdd")]
        public bool CanAdd { get; set; }
    }
}
