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
        : this(endpoint, new HttpClientHandler())
    {
    }

    internal AnkiConnectClient(string endpoint, HttpMessageHandler handler)
    {
        _endpoint = endpoint.TrimEnd('/');
        _http = new HttpClient(handler)
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

    public async Task<string?> GetMediaDirPathAsync()
    {
        var result = await RequestAsync("getMediaDirPath", null);
        return result.ValueKind == JsonValueKind.String
            ? result.GetString()
            : null;
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

        var results = await MultiRequestWithErrorsAsync(actions);

        var storedNames = new List<string>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            if (i >= results.Count)
            {
                storedNames.Add("");
                continue;
            }

            var result = results[i];
            if (!string.IsNullOrEmpty(result.Error))
            {
                Log.Warning("[AnkiConnect] storeMediaFile batch[{Index}] failed: {Error}", i, result.Error);
                storedNames.Add("");
                continue;
            }

            try
            {
                var name = result.Result.Deserialize<string>() ?? StripDirectory(files[i].filename);
                storedNames.Add(name);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[AnkiConnect] storeMediaFile batch[{Index}] failed: {Filename}", i, files[i].filename);
                storedNames.Add("");
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
        var actionResults = await MultiRequestWithErrorsAsync(actions);
        var results = new List<JsonElement>(actionResults.Count);
        foreach (var result in actionResults)
        {
            if (!string.IsNullOrEmpty(result.Error))
                throw new AnkiConnectException(result.Error);
            results.Add(result.Result);
        }

        return results;
    }

    private async Task<List<MultiActionResult>> MultiRequestWithErrorsAsync(List<(string action, object? parameters)> actions)
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
        var results = new List<MultiActionResult>(actions.Count);
        foreach (var element in resultArray.EnumerateArray())
            results.Add(UnwrapMultiActionResult(element));
        return results;
    }

    private static MultiActionResult UnwrapMultiActionResult(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("result", out var result))
            return new MultiActionResult(element.Clone(), null);

        string? errorMessage = null;
        if (element.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            errorMessage = GetErrorMessage(error);

        return new MultiActionResult(result.Clone(), errorMessage);
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
            var results = await MultiRequestWithErrorsAsync(actions);
            var addResult = results.Count > 0 ? results[0] : new MultiActionResult(DefaultJsonElement(), "Missing addNote result");
            if (!string.IsNullOrEmpty(addResult.Error))
            {
                Log.Warning("[AnkiConnect] addNote failed in batch: {Error}", addResult.Error);
                return false;
            }

            if (sync && results.Count > 1 && !string.IsNullOrEmpty(results[1].Error))
                Log.Warning("[AnkiConnect] sync failed after addNote succeeded: {Error}", results[1].Error);

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

        using (response)
        {
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

            using (doc)
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                    throw new AnkiConnectException(GetErrorMessage(error));

                if (root.TryGetProperty("result", out var result))
                    return result.Clone();

                throw new AnkiConnectException("AnkiConnect response missing 'result' field");
            }
        }
    }

    private static string GetErrorMessage(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
            return error.GetString() ?? "Unknown AnkiConnect error";

        try
        {
            if (error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("content", out var contentElem)
                && contentElem.ValueKind == JsonValueKind.String)
            {
                return contentElem.GetString() ?? "Unknown AnkiConnect error";
            }
        }
        catch (InvalidOperationException)
        {
        }

        return error.GetRawText();
    }

    private static JsonElement DefaultJsonElement()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
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

    private sealed record MultiActionResult(JsonElement Result, string? Error);
}
