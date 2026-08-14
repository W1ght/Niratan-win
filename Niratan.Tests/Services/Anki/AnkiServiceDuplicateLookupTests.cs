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

public sealed class AnkiServiceDuplicateLookupTests
{
    [Fact]
    public async Task DuplicateLookupExpressionsAsync_BatchesCandidatesAndReusesCachedResults()
    {
        var settingsService = new MutableSettingsService(CreateAppSettings());
        var scenario = new ExpressionDuplicateScenario("月", 77);
        using var service = CreateService(settingsService, () => new ScenarioHandler(scenario.RespondAsync));

        var first = await service.DuplicateLookupExpressionsAsync(["星", "月", "星"]);
        var second = await service.DuplicateLookupExpressionsAsync(["月", "星"]);

        first["星"].IsDuplicate.Should().BeFalse();
        first["月"].IsDuplicate.Should().BeTrue();
        first["月"].NoteIds.Should().Equal(77);
        second["月"].NoteIds.Should().Equal(77);
        scenario.Actions.Should().Equal("canAddNotesWithErrorDetail", "multi");
        scenario.CanAddBatchSizes.Should().Equal(2);
    }

    [Fact]
    public async Task SettingChanged_ClearsDuplicateCache()
    {
        var settingsService = new MutableSettingsService(CreateAppSettings());
        var scenario = new ExpressionDuplicateScenario("月", 77);
        using var service = CreateService(settingsService, () => new ScenarioHandler(scenario.RespondAsync));

        (await service.DuplicateLookupExpressionAsync("月")).IsDuplicate.Should().BeTrue();
        settingsService.ReplaceCurrent(CreateAppSettings());
        (await service.DuplicateLookupExpressionAsync("月")).IsDuplicate.Should().BeTrue();

        scenario.Actions.Should().Equal(
            "canAddNotesWithErrorDetail",
            "multi",
            "canAddNotesWithErrorDetail",
            "multi");
    }

    [Fact]
    public async Task DuplicateLookup_WhenSettingsChangeDuringRequest_DoesNotCacheOldResult()
    {
        var settingsService = new MutableSettingsService(CreateAppSettings());
        var scenario = new SettingsRaceScenario();
        using var service = CreateService(settingsService, () => new ScenarioHandler(scenario.RespondAsync));

        var oldLookup = service.DuplicateLookupExpressionAsync("星");
        await scenario.FirstCanAddStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        settingsService.ReplaceCurrent(CreateAppSettings());
        scenario.ReleaseFirstCanAdd.TrySetResult();

        (await oldLookup).IsDuplicate.Should().BeFalse();
        var currentLookup = await service.DuplicateLookupExpressionAsync("星");

        currentLookup.IsDuplicate.Should().BeTrue();
        currentLookup.NoteIds.Should().Equal(88);
        scenario.CanAddRequestCount.Should().Be(2);
    }

    [Fact]
    public async Task DuplicateLookup_WhenSettingsChangeWhileWaiting_RevalidatesEveryPartialCacheHit()
    {
        var settingsService = new MutableSettingsService(CreateAppSettings());
        var scenario = new PartialCacheProfileRaceScenario();
        var clientNumber = 0;
        using var service = CreateService(
            settingsService,
            () =>
            {
                var currentClient = Interlocked.Increment(ref clientNumber);
                return new ScenarioHandler(request => scenario.RespondAsync(currentClient, request));
            });

        (await service.DuplicateLookupExpressionAsync("cached")).IsDuplicate.Should().BeTrue();
        var blocker = service.DuplicateLookupExpressionAsync("blocker");
        await scenario.BlockerStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var mixedLookup = service.DuplicateLookupExpressionsAsync(["cached", "fresh"]);
        settingsService.ReplaceCurrent(CreateAppSettings());
        scenario.ReleaseBlocker.TrySetResult();

        await blocker;
        var current = await mixedLookup;

        current["cached"].IsDuplicate.Should().BeFalse();
        current["fresh"].IsDuplicate.Should().BeTrue();
        current["fresh"].NoteIds.Should().Equal(22);
        scenario.NewProfileBatchExpressions.Should().Equal("cached", "fresh");
    }

    [Fact]
    public async Task MineEntryAsync_WhenAddSucceeds_UpdatesDuplicateCacheWithoutRoundTrip()
    {
        var settingsService = new MutableSettingsService(CreateAppSettings(allowDupes: true));
        var scenario = new AddNoteScenario(123456789);
        using var service = CreateService(settingsService, () => new ScenarioHandler(scenario.RespondAsync));

        var noteId = await service.MineEntryAsync(
            """{"expression":"星"}""",
            new AnkiMiningContext());
        var duplicate = await service.DuplicateLookupExpressionAsync("星");

        noteId.Should().Be(123456789);
        duplicate.IsDuplicate.Should().BeTrue();
        duplicate.NoteIds.Should().Equal(123456789);
        scenario.Actions.Should().Equal("multi");
    }

    [Fact]
    public async Task MineEntryAsync_BypassesNegativeCacheBeforeFinalSubmission()
    {
        var settingsService = new MutableSettingsService(CreateAppSettings());
        var scenario = new LateDuplicateScenario();
        using var service = CreateService(settingsService, () => new ScenarioHandler(scenario.RespondAsync));

        (await service.DuplicateLookupExpressionAsync("星")).IsDuplicate.Should().BeFalse();
        var noteId = await service.MineEntryAsync(
            """{"expression":"星"}""",
            new AnkiMiningContext());
        var refreshed = await service.DuplicateLookupExpressionAsync("星");

        noteId.Should().BeNull();
        refreshed.IsDuplicate.Should().BeTrue();
        refreshed.NoteIds.Should().Equal(999);
        scenario.Actions.Should().Equal(
            "canAddNotesWithErrorDetail",
            "canAddNotesWithErrorDetail",
            "multi:findNotes");
    }

    [Fact]
    public async Task MineEntryAsync_ConcurrentSameExpression_AddsOnlyOnce()
    {
        var settingsService = new MutableSettingsService(CreateAppSettings());
        var scenario = new ConcurrentSameExpressionScenario();
        using var service = CreateService(settingsService, () => new ScenarioHandler(scenario.RespondAsync));

        var first = service.MineEntryAsync(
            """{"expression":"星"}""",
            new AnkiMiningContext());
        await scenario.AddStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var second = service.MineEntryAsync(
            """{"expression":"星"}""",
            new AnkiMiningContext());
        scenario.ReleaseAdd.TrySetResult();

        var noteIds = await Task.WhenAll(first, second);

        noteIds.Should().ContainSingle(noteId => noteId == 101);
        noteIds.Should().ContainSingle(noteId => noteId == null);
        scenario.CanAddRequestCount.Should().Be(1);
        scenario.AddRequestCount.Should().Be(1);
    }

    [Fact]
    public async Task MineEntryAsync_ConcurrentDifferentExpressions_DoNotShareSubmissionGate()
    {
        var settingsService = new MutableSettingsService(CreateAppSettings());
        var scenario = new ConcurrentDifferentExpressionsScenario();
        using var service = CreateService(settingsService, () => new ScenarioHandler(scenario.RespondAsync));

        var first = service.MineEntryAsync(
            """{"expression":"星"}""",
            new AnkiMiningContext());
        await scenario.FirstAddStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var second = service.MineEntryAsync(
            """{"expression":"月"}""",
            new AnkiMiningContext());
        var secondReachedAdd = await Task.WhenAny(
            scenario.SecondAddStarted.Task,
            Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        scenario.ReleaseFirstAdd.TrySetResult();

        var noteIds = await Task.WhenAll(first, second);

        secondReachedAdd.Should().Be(scenario.SecondAddStarted.Task);
        noteIds.Should().BeEquivalentTo([101L, 202L]);
        scenario.AddRequestCount.Should().Be(2);
    }

    [Fact]
    public void Dispose_UnsubscribesFromSettingsChanges()
    {
        var settingsService = new MutableSettingsService(CreateAppSettings());
        var scenario = new ExpressionDuplicateScenario("月", 77);
        var service = CreateService(settingsService, () => new ScenarioHandler(scenario.RespondAsync));

        settingsService.SubscriberCount.Should().Be(1);
        service.Dispose();
        service.Dispose();

        settingsService.SubscriberCount.Should().Be(0);
    }

    private static AnkiService CreateService(
        MutableSettingsService settingsService,
        Func<HttpMessageHandler> handlerFactory) =>
        new(
            settingsService,
            Mock.Of<IDictionaryLookupService>(),
            endpoint => new AnkiConnectClient(endpoint, handlerFactory()));

    private static AppSettings CreateAppSettings(bool allowDupes = false) =>
        new()
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
                        Fields = ["Front"],
                    },
                ],
                FieldMappings = new Dictionary<string, string>
                {
                    ["Front"] = "{expression}",
                },
                EmbedMedia = false,
                AllowDupes = allowDupes,
            },
        };

    private static HttpResponseMessage JsonResponse(object? result) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { result, error = (string?)null }),
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class ExpressionDuplicateScenario(string duplicateExpression, long noteId)
    {
        public ConcurrentQueue<string> Actions { get; } = new();
        public ConcurrentQueue<int> CanAddBatchSizes { get; } = new();

        public Task<HttpResponseMessage> RespondAsync(JsonElement request)
        {
            var action = request.GetProperty("action").GetString()!;
            Actions.Enqueue(action);
            if (action == "canAddNotesWithErrorDetail")
            {
                var notes = request.GetProperty("params").GetProperty("notes");
                CanAddBatchSizes.Enqueue(notes.GetArrayLength());
                var result = notes.EnumerateArray()
                    .Select(note => new
                    {
                        canAdd = note.GetProperty("fields").GetProperty("Front").GetString()
                            != duplicateExpression,
                    })
                    .ToArray();
                return Task.FromResult(JsonResponse(result));
            }

            var actionResults = request.GetProperty("params").GetProperty("actions")
                .EnumerateArray()
                .Select(_ => new { result = new[] { noteId }, error = (string?)null })
                .ToArray();
            return Task.FromResult(JsonResponse(actionResults));
        }
    }

    private sealed class SettingsRaceScenario
    {
        private int _canAddRequestCount;

        public TaskCompletionSource FirstCanAddStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstCanAdd { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CanAddRequestCount => Volatile.Read(ref _canAddRequestCount);

        public async Task<HttpResponseMessage> RespondAsync(JsonElement request)
        {
            var action = request.GetProperty("action").GetString();
            if (action == "canAddNotesWithErrorDetail")
            {
                var requestNumber = Interlocked.Increment(ref _canAddRequestCount);
                if (requestNumber == 1)
                {
                    FirstCanAddStarted.TrySetResult();
                    await ReleaseFirstCanAdd.Task;
                }

                return JsonResponse(new[] { new { canAdd = requestNumber == 1 } });
            }

            return JsonResponse(new[]
            {
                new { result = new[] { 88L }, error = (string?)null },
            });
        }
    }

    private sealed class PartialCacheProfileRaceScenario
    {
        public TaskCompletionSource BlockerStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBlocker { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> NewProfileBatchExpressions { get; } = new();

        public async Task<HttpResponseMessage> RespondAsync(
            int clientNumber,
            JsonElement request)
        {
            var action = request.GetProperty("action").GetString();
            if (action == "canAddNotesWithErrorDetail")
            {
                var expressions = request
                    .GetProperty("params")
                    .GetProperty("notes")
                    .EnumerateArray()
                    .Select(note => note
                        .GetProperty("fields")
                        .GetProperty("Front")
                        .GetString() ?? "")
                    .ToArray();

                if (clientNumber == 1 && expressions.Contains("blocker", StringComparer.Ordinal))
                {
                    BlockerStarted.TrySetResult();
                    await ReleaseBlocker.Task;
                    return JsonResponse(new[] { new { canAdd = true } });
                }

                if (clientNumber > 1)
                {
                    foreach (var expression in expressions)
                        NewProfileBatchExpressions.Enqueue(expression);
                    return JsonResponse(expressions
                        .Select(expression => new { canAdd = expression == "cached" })
                        .ToArray());
                }

                return JsonResponse(expressions
                    .Select(expression => new { canAdd = expression != "cached" })
                    .ToArray());
            }

            var noteId = clientNumber > 1 ? 22L : 11L;
            var actions = request.GetProperty("params").GetProperty("actions");
            return JsonResponse(actions.EnumerateArray()
                .Select(_ => new { result = new[] { noteId }, error = (string?)null })
                .ToArray());
        }
    }

    private sealed class AddNoteScenario(long noteId)
    {
        public ConcurrentQueue<string> Actions { get; } = new();

        public Task<HttpResponseMessage> RespondAsync(JsonElement request)
        {
            var action = request.GetProperty("action").GetString()!;
            Actions.Enqueue(action);
            return Task.FromResult(JsonResponse(new[]
            {
                new { result = (object)noteId, error = (string?)null },
            }));
        }
    }

    private sealed class LateDuplicateScenario
    {
        private int _canAddRequests;

        public ConcurrentQueue<string> Actions { get; } = new();

        public Task<HttpResponseMessage> RespondAsync(JsonElement request)
        {
            var action = request.GetProperty("action").GetString()!;
            if (action == "canAddNotesWithErrorDetail")
            {
                Actions.Enqueue(action);
                var canAdd = Interlocked.Increment(ref _canAddRequests) == 1;
                return Task.FromResult(JsonResponse(new[] { new { canAdd } }));
            }

            var nestedAction = request
                .GetProperty("params")
                .GetProperty("actions")[0]
                .GetProperty("action")
                .GetString()!;
            Actions.Enqueue($"multi:{nestedAction}");
            return Task.FromResult(nestedAction == "addNote"
                ? JsonResponse(new[]
                {
                    new { result = (object?)null, error = "cannot create note because it is a duplicate" },
                })
                : JsonResponse(new[]
                {
                    new { result = (object)new[] { 999L }, error = (string?)null },
                }));
        }
    }

    private sealed class ConcurrentSameExpressionScenario
    {
        private int _canAddRequestCount;
        private int _addRequestCount;

        public TaskCompletionSource AddStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAdd { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CanAddRequestCount => Volatile.Read(ref _canAddRequestCount);
        public int AddRequestCount => Volatile.Read(ref _addRequestCount);

        public async Task<HttpResponseMessage> RespondAsync(JsonElement request)
        {
            var action = request.GetProperty("action").GetString();
            if (action == "canAddNotesWithErrorDetail")
            {
                Interlocked.Increment(ref _canAddRequestCount);
                return JsonResponse(new[] { new { canAdd = true } });
            }

            Interlocked.Increment(ref _addRequestCount);
            AddStarted.TrySetResult();
            await ReleaseAdd.Task;
            return JsonResponse(new[]
            {
                new { result = (object)101L, error = (string?)null },
            });
        }
    }

    private sealed class ConcurrentDifferentExpressionsScenario
    {
        private int _addRequestCount;

        public TaskCompletionSource FirstAddStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondAddStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstAdd { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AddRequestCount => Volatile.Read(ref _addRequestCount);

        public async Task<HttpResponseMessage> RespondAsync(JsonElement request)
        {
            var action = request.GetProperty("action").GetString();
            if (action == "canAddNotesWithErrorDetail")
            {
                return JsonResponse(new[] { new { canAdd = true } });
            }

            var expression = request
                .GetProperty("params")
                .GetProperty("actions")[0]
                .GetProperty("params")
                .GetProperty("note")
                .GetProperty("fields")
                .GetProperty("Front")
                .GetString();
            Interlocked.Increment(ref _addRequestCount);
            if (expression == "星")
            {
                FirstAddStarted.TrySetResult();
                await ReleaseFirstAdd.Task;
                return JsonResponse(new[]
                {
                    new { result = (object)101L, error = (string?)null },
                });
            }

            SecondAddStarted.TrySetResult();
            return JsonResponse(new[]
            {
                new { result = (object)202L, error = (string?)null },
            });
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

    private sealed class MutableSettingsService(AppSettings current) : ISettingsService
    {
        private EventHandler<SettingsChangedEventArgs>? _settingChanged;

        public AppSettings Current { get; private set; } = current;
        public int SubscriberCount { get; private set; }

        public event EventHandler<SettingsChangedEventArgs>? SettingChanged
        {
            add
            {
                _settingChanged += value;
                SubscriberCount++;
            }
            remove
            {
                _settingChanged -= value;
                SubscriberCount--;
            }
        }

        public void Set<T>(Expression<Func<AppSettings, T>> selector, T value) =>
            throw new NotSupportedException();

        public void ReplaceCurrent(AppSettings settings)
        {
            var previous = Current;
            Current = settings;
            _settingChanged?.Invoke(
                this,
                new SettingsChangedEventArgs
                {
                    PropertyName = nameof(Current),
                    OldValue = previous,
                    NewValue = settings,
                });
        }

        public Task SaveAsync() => Task.CompletedTask;
        public Task LoadAsync() => Task.CompletedTask;
        public void Reset()
        {
        }
    }
}
