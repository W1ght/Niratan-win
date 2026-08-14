using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Niratan.Models.Anki;
using Niratan.Models.DTO;
using Niratan.Models.Settings;
using Niratan.Services.Anki;
using Niratan.Services.Dictionary;
using Niratan.Services.Settings;

namespace Niratan.Tests.Services.Anki;

public sealed class AnkiServiceMediaPipelineTests : IDisposable
{
    private readonly string _fixtureDirectory = Path.Combine(
        Path.GetTempPath(),
        "Niratan.Tests",
        nameof(AnkiServiceMediaPipelineTests),
        Guid.NewGuid().ToString("N"));

    public AnkiServiceMediaPipelineTests()
    {
        Directory.CreateDirectory(_fixtureDirectory);
    }

    [Fact]
    public async Task MineEntryAsync_WhenCollectionMediaIsWritable_DirectWritesBeforeAddNote()
    {
        var mediaDirectory = Path.Combine(_fixtureDirectory, "collection.media");
        Directory.CreateDirectory(mediaDirectory);
        var coverPath = Path.Combine(_fixtureDirectory, "cover.jpg");
        var coverBytes = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(coverPath, coverBytes, TestContext.Current.CancellationToken);

        var scenario = new DirectMediaScenario(mediaDirectory, 4242);
        using var service = CreateService(scenario);

        var noteId = await service.MineEntryAsync(
            """{"expression":"星"}""",
            new AnkiMiningContext { CoverPath = coverPath });

        noteId.Should().Be(4242);
        scenario.Actions.Should().Equal(
            "getMediaDirPath",
            "canAddNotesWithErrorDetail",
            "multi");
        scenario.MultiActions.Should().Equal("addNote");
        var storedFilename = AnkiService.CreateCoverMediaFilename(coverPath, coverBytes);
        (await File.ReadAllBytesAsync(
                Path.Combine(mediaDirectory, storedFilename),
                TestContext.Current.CancellationToken))
            .Should().Equal(coverBytes);
        scenario.PictureField.Should().Be($"<img src=\"{storedFilename}\">");
    }

    [Fact]
    public async Task MineEntryAsync_WhenRequiredGeneratedMediaIsMissing_DoesNotSubmitNote()
    {
        var scenario = new DirectMediaScenario(_fixtureDirectory, 4242);
        using var service = CreateService(
            scenario,
            new Dictionary<string, string>
            {
                ["Front"] = "{expression}",
                ["Picture"] = "{video-screenshot}",
            });

        var noteId = await service.MineEntryAsync(
            """{"expression":"星"}""",
            new AnkiMiningContext { VideoFileName = "episode.mkv" });

        noteId.Should().BeNull();
        scenario.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task MineEntryAsync_WhenMappedCoverCannotBeStored_DoesNotSubmitNote()
    {
        var coverPath = Path.Combine(_fixtureDirectory, "cover.webp");
        await File.WriteAllBytesAsync(
            coverPath,
            [7, 8, 9],
            TestContext.Current.CancellationToken);
        var scenario = new DirectMediaScenario(
            Path.Combine(_fixtureDirectory, "missing-media-directory"),
            4242,
            failMediaStore: true);
        using var service = CreateService(scenario);

        var noteId = await service.MineEntryAsync(
            """{"expression":"星"}""",
            new AnkiMiningContext { CoverPath = coverPath });

        noteId.Should().BeNull();
        scenario.Actions.Should().Equal("getMediaDirPath", "multi");
        scenario.MultiActions.Should().Equal("storeMediaFile");
    }

    private static AnkiService CreateService(
        DirectMediaScenario scenario,
        Dictionary<string, string>? fieldMappings = null)
    {
        var settings = new AppSettings
        {
            AnkiSettings = new AnkiSettings
            {
                AnkiConnectUrl = "http://anki.test",
                SelectedDeckId = 1,
                SelectedDeckName = "Mining",
                SelectedNoteTypeId = 2,
                SelectedNoteTypeName = "Basic",
                AvailableDecks = [new AnkiDeck { Id = 1, Name = "Mining" }],
                AvailableNoteTypes =
                [
                    new AnkiNoteType
                    {
                        Id = 2,
                        Name = "Basic",
                        Fields = ["Front", "Picture"],
                    },
                ],
                FieldMappings = fieldMappings ?? new Dictionary<string, string>
                {
                    ["Front"] = "{expression}",
                    ["Picture"] = "{book-cover}",
                },
            },
        };

        return new AnkiService(
            new FakeSettingsService(settings),
            Mock.Of<IDictionaryLookupService>(),
            endpoint => new AnkiConnectClient(endpoint, new ScenarioHandler(scenario.RespondAsync)));
    }

    public void Dispose()
    {
        var fullPath = Path.GetFullPath(_fixtureDirectory);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private sealed class DirectMediaScenario(
        string mediaDirectory,
        long noteId,
        bool failMediaStore = false)
    {
        public ConcurrentQueue<string> Actions { get; } = new();
        public ConcurrentQueue<string> MultiActions { get; } = new();
        public string? PictureField { get; private set; }

        public Task<HttpResponseMessage> RespondAsync(JsonElement request)
        {
            var action = request.GetProperty("action").GetString()!;
            Actions.Enqueue(action);
            if (action == "getMediaDirPath")
                return Task.FromResult(JsonResponse(mediaDirectory));
            if (action == "canAddNotesWithErrorDetail")
            {
                return Task.FromResult(JsonResponse(
                    new[] { new { canAdd = true, error = (string?)null } }));
            }

            var actions = request.GetProperty("params").GetProperty("actions");
            var results = new List<object>();
            foreach (var item in actions.EnumerateArray())
            {
                var itemAction = item.GetProperty("action").GetString()!;
                MultiActions.Enqueue(itemAction);
                if (itemAction == "addNote")
                {
                    PictureField = item
                        .GetProperty("params")
                        .GetProperty("note")
                        .GetProperty("fields")
                        .GetProperty("Picture")
                        .GetString();
                    results.Add(new { result = (object)noteId, error = (string?)null });
                }
                else if (itemAction == "storeMediaFile" && failMediaStore)
                    results.Add(new { result = (object?)null, error = "media write failed" });
                else
                    results.Add(new { result = (object)"stored-media", error = (string?)null });
            }

            return Task.FromResult(JsonResponse(results));
        }
    }

    private sealed class ScenarioHandler(
        Func<JsonElement, Task<HttpResponseMessage>> respondAsync) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            return await respondAsync(document.RootElement.Clone());
        }
    }

    private static HttpResponseMessage JsonResponse(object? result) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { result, error = (string?)null }),
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class FakeSettingsService(AppSettings current) : ISettingsService
    {
        public AppSettings Current { get; private set; } = current;
        public event EventHandler<SettingsChangedEventArgs>? SettingChanged;

        public void Set<T>(Expression<Func<AppSettings, T>> selector, T value)
        {
        }

        public void ReplaceCurrent(AppSettings settings)
        {
            Current = settings;
            SettingChanged?.Invoke(
                this,
                new SettingsChangedEventArgs { PropertyName = nameof(Current) });
        }

        public Task SaveAsync() => Task.CompletedTask;
        public Task LoadAsync() => Task.CompletedTask;
        public void Reset() { }
    }
}
