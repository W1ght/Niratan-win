using FluentAssertions;
using Niratan.Models.Dictionary;
using Niratan.Models.Settings;
using Niratan.Services.Dictionary;
using Niratan.ViewModels.Windowing;

namespace Niratan.Tests.ViewModels.Windowing;

public class GlobalLookupWindowViewModelTests
{
    [Fact]
    public async Task LookupCommand_WithEmptyInput_ShowsEnterTextStatusWithoutLookup()
    {
        var service = new RecordingPopupRequestService();
        var sut = new GlobalLookupWindowViewModel(service);
        sut.Query = "   ";
        var cleared = false;
        sut.LookupCleared += () => cleared = true;

        await sut.LookupCommand.ExecuteAsync(null);

        sut.StatusText.Should().Be("Enter text to look up.");
        cleared.Should().BeTrue();
        service.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task LookupCommand_SetsInProgressWhileLookupIsRunning()
    {
        var service = new RecordingPopupRequestService
        {
            PendingResult = new TaskCompletionSource<DictionaryPopupRequest?>(),
        };
        var sut = new GlobalLookupWindowViewModel(service) { Query = "星" };

        var lookupTask = sut.LookupCommand.ExecuteAsync(null);

        sut.IsLookupInProgress.Should().BeTrue();
        sut.StatusText.Should().Be("Looking up...");
        service.PendingResult.SetResult(CreateRequest("星"));
        await lookupTask;
        sut.IsLookupInProgress.Should().BeFalse();
    }

    [Fact]
    public async Task LookupCommand_WhenNoResults_ShowsNoResultsStatus()
    {
        var service = new RecordingPopupRequestService();
        var sut = new GlobalLookupWindowViewModel(service) { Query = "星" };
        var cleared = false;
        sut.LookupCleared += () => cleared = true;

        await sut.LookupCommand.ExecuteAsync(null);

        sut.StatusText.Should().Be("No results.");
        cleared.Should().BeTrue();
    }

    [Fact]
    public async Task LookupCommand_WhenRequestCreated_EmitsLookupReady()
    {
        var request = CreateRequest("星");
        var service = new RecordingPopupRequestService { Result = request };
        var sut = new GlobalLookupWindowViewModel(service) { Query = " 星 " };
        DictionaryPopupRequest? emitted = null;
        sut.LookupReady += ready => emitted = ready;

        await sut.LookupCommand.ExecuteAsync(null);

        service.Queries.Should().Equal("星");
        emitted.Should().BeSameAs(request);
        sut.StatusText.Should().Be("Lookup ready.");
    }

    [Fact]
    public async Task InitializeAsync_WithInitialQuery_LooksUpImmediately()
    {
        var request = CreateRequest("学校");
        var service = new RecordingPopupRequestService { Result = request };
        var sut = new GlobalLookupWindowViewModel(service);
        DictionaryPopupRequest? emitted = null;
        sut.LookupReady += ready => emitted = ready;

        await sut.InitializeAsync(" 学校 ", TestContext.Current.CancellationToken);

        sut.Query.Should().Be("学校");
        service.Queries.Should().Equal("学校");
        emitted.Should().BeSameAs(request);
    }

    [Fact]
    public async Task LookupCommand_WhenLookupThrows_ShowsExceptionMessage()
    {
        var service = new RecordingPopupRequestService
        {
            Exception = new InvalidOperationException("lookup failed"),
        };
        var sut = new GlobalLookupWindowViewModel(service) { Query = "星" };
        var cleared = false;
        sut.LookupCleared += () => cleared = true;

        await sut.LookupCommand.ExecuteAsync(null);

        sut.IsLookupInProgress.Should().BeFalse();
        sut.StatusText.Should().Be("lookup failed");
        cleared.Should().BeTrue();
    }

    private static DictionaryPopupRequest CreateRequest(string query) =>
        new(
            query,
            [new DictionaryLookupResult(
                Matched: query,
                Deinflected: query,
                Trace: [],
                Term: new TermResult(
                    Expression: query,
                    Reading: query,
                    Rules: "",
                    Glossaries: [new GlossaryEntry("Test", "definition", "", "")],
                    Frequencies: [],
                    Pitches: []),
                PreprocessorSteps: 0)],
            [],
            new DictionaryDisplaySettings(),
            Niratan.Enums.ThemeMode.System,
            new AudioSettings(),
            new AnkiSettings(),
            null,
            null);

    private sealed class RecordingPopupRequestService : IDictionaryPopupRequestService
    {
        public DictionaryPopupRequest? Result { get; init; }
        public TaskCompletionSource<DictionaryPopupRequest?>? PendingResult { get; init; }
        public Exception? Exception { get; init; }
        public List<string> Queries { get; } = [];

        public Task<DictionaryPopupRequest?> CreateAsync(
            string query,
            Niratan.Models.Anki.AnkiMiningContext? miningContext = null,
            string? traceId = null,
            CancellationToken ct = default)
        {
            Queries.Add(query);
            if (Exception is not null)
                throw Exception;

            return PendingResult?.Task ?? Task.FromResult(Result);
        }
    }
}
