using System.Linq.Expressions;
using FluentAssertions;
using Hoshi.Enums;
using Hoshi.Models.Anki;
using Hoshi.Models.Dictionary;
using Hoshi.Models.DTO;
using Hoshi.Models.Settings;
using Hoshi.Services.Dictionary;
using Hoshi.Services.Settings;

namespace Hoshi.Tests.Services.Dictionary;

public class DictionaryPopupRequestServiceTests
{
    [Fact]
    public async Task CreateAsync_BlankQuery_ReturnsNullWithoutLookup()
    {
        var lookup = new RecordingDictionaryLookupService();
        var sut = new DictionaryPopupRequestService(lookup, new RecordingSettingsService());

        var request = await sut.CreateAsync("   ", ct: TestContext.Current.CancellationToken);

        request.Should().BeNull();
        lookup.LookupCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_NoResults_ReturnsNullWithoutLoadingStyles()
    {
        var lookup = new RecordingDictionaryLookupService();
        var sut = new DictionaryPopupRequestService(lookup, new RecordingSettingsService());

        var request = await sut.CreateAsync("星", ct: TestContext.Current.CancellationToken);

        request.Should().BeNull();
        lookup.LookupCalls.Should().ContainSingle().Which.Text.Should().Be("星");
        lookup.GetStylesCallCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_UsesDictionaryDisplayLookupLimitsAndTraceId()
    {
        var lookup = new RecordingDictionaryLookupService
        {
            Results = [CreateResult("星")],
        };
        var settings = new RecordingSettingsService
        {
            Current = new AppSettings
            {
                DictionaryDisplaySettings = new DictionaryDisplaySettings(MaxResults: 7, ScanLength: 9),
            },
        };
        var sut = new DictionaryPopupRequestService(lookup, settings);

        await sut.CreateAsync(" 星 ", traceId: "global-1", ct: TestContext.Current.CancellationToken);

        lookup.LookupCalls.Should().ContainSingle().Which.Should().Be(
            new LookupCall("星", 7, 9, "global-1"));
    }

    [Fact]
    public async Task CreateAsync_MapsStylesByDictionaryName()
    {
        var lookup = new RecordingDictionaryLookupService
        {
            Results = [CreateResult("星")],
            Styles =
            [
                new DictionaryStyle("A", ".a{}"),
                new DictionaryStyle("B", ".b{}"),
            ],
        };
        var sut = new DictionaryPopupRequestService(lookup, new RecordingSettingsService());

        var request = await sut.CreateAsync("星", ct: TestContext.Current.CancellationToken);

        request.Should().NotBeNull();
        request!.Styles.Should().Equal(
            new KeyValuePair<string, string>("A", ".a{}"),
            new KeyValuePair<string, string>("B", ".b{}"));
    }

    [Fact]
    public async Task CreateAsync_CapturesSettingsSnapshotForPopupDisplay()
    {
        var lookup = new RecordingDictionaryLookupService
        {
            Results = [CreateResult("星")],
        };
        var settings = new RecordingSettingsService
        {
            Current = new AppSettings
            {
                Theme = ThemeMode.Dark,
                DictionaryDisplaySettings = new DictionaryDisplaySettings(
                    CollapsedDictionaries: ["A"],
                    CustomCSS: ".term{}",
                    MaxResults: 5,
                    PopupMaxWidth: 1200,
                    PopupMaxHeight: 700,
                    PopupScale: 1.25,
                    PopupActionBar: true,
                    PopupFullWidth: true),
                AudioSettings = new AudioSettings
                {
                    EnableAutoplay = true,
                    PlaybackMode = AudioPlaybackMode.Duck,
                    AudioSources =
                    [
                        new AudioSource { Name = "Custom", Url = "https://audio.test/{term}", IsEnabled = true },
                    ],
                },
                AnkiSettings = new AnkiSettings
                {
                    AnkiConnectUrl = "http://anki.test",
                    SelectedDeckId = 10,
                    SelectedDeckName = "Japanese",
                    SelectedNoteTypeId = 20,
                    SelectedNoteTypeName = "Mining",
                    Tags = "hoshi",
                    AllowDupes = true,
                    FieldMappings = new Dictionary<string, string> { ["Expression"] = "{expression}" },
                    AvailableDecks = [new AnkiDeck { Id = 10, Name = "Japanese" }],
                    AvailableNoteTypes = [new AnkiNoteType { Id = 20, Name = "Mining", Fields = ["Expression"] }],
                },
            },
        };
        var sut = new DictionaryPopupRequestService(lookup, settings);

        var request = await sut.CreateAsync("星", ct: TestContext.Current.CancellationToken);
        settings.Current.Theme = ThemeMode.Light;
        settings.Current.DictionaryDisplaySettings.CollapsedDictionaries!.Add("B");
        settings.Current.DictionaryDisplaySettings = new DictionaryDisplaySettings();
        settings.Current.AudioSettings.AudioSources[0].Name = "Changed";
        settings.Current.AnkiSettings.Tags = "changed";
        settings.Current.AnkiSettings.FieldMappings["Expression"] = "changed";

        request.Should().NotBeNull();
        request!.Theme.Should().Be(ThemeMode.Dark);
        request.DisplaySettings.CustomCSS.Should().Be(".term{}");
        request.DisplaySettings.CollapsedDictionariesOrDefault.Should().Equal("A");
        request.DisplaySettings.PopupMaxWidth.Should().Be(1200);
        request.DisplaySettings.PopupMaxHeight.Should().Be(700);
        request.DisplaySettings.PopupScale.Should().Be(1.25);
        request.DisplaySettings.PopupActionBar.Should().BeTrue();
        request.DisplaySettings.PopupFullWidth.Should().BeTrue();
        request.AudioSettings.EnableAutoplay.Should().BeTrue();
        request.AudioSettings.PlaybackMode.Should().Be(AudioPlaybackMode.Duck);
        request.AudioSettings.AudioSources.Should().ContainSingle().Which.Name.Should().Be("Custom");
        request.AnkiSettings.Tags.Should().Be("hoshi");
        request.AnkiSettings.FieldMappings["Expression"].Should().Be("{expression}");
        request.AnkiSettings.AvailableDecks.Should().ContainSingle().Which.Name.Should().Be("Japanese");
        request.AnkiSettings.AvailableNoteTypes.Should().ContainSingle().Which.Fields.Should().Equal("Expression");
    }

    [Fact]
    public async Task CreateAsync_IncludesOptionalMiningContext()
    {
        var lookup = new RecordingDictionaryLookupService
        {
            Results = [CreateResult("星")],
        };
        var sut = new DictionaryPopupRequestService(lookup, new RecordingSettingsService());
        var miningContext = new AnkiMiningContext
        {
            Sentence = "星を見た。",
            SentenceOffset = 0,
        };

        var request = await sut.CreateAsync(
            "星",
            miningContext,
            traceId: "lookup-ctx",
            ct: TestContext.Current.CancellationToken);

        request.Should().NotBeNull();
        request!.Query.Should().Be("星");
        request.TraceId.Should().Be("lookup-ctx");
        request.MiningContext.Should().BeSameAs(miningContext);
    }

    private static DictionaryLookupResult CreateResult(string matched) =>
        new(
            Matched: matched,
            Deinflected: matched,
            Trace: [],
            Term: new TermResult(
                Expression: matched,
                Reading: matched,
                Rules: "",
                Glossaries: [new GlossaryEntry("Test", "star", "", "")],
                Frequencies: [],
                Pitches: []),
            PreprocessorSteps: 0);

    private sealed record LookupCall(string Text, int MaxResults, int ScanLength, string? TraceId);

    private sealed class RecordingDictionaryLookupService : IDictionaryLookupService
    {
        public List<DictionaryLookupResult> Results { get; init; } = [];
        public List<DictionaryStyle> Styles { get; init; } = [];
        public List<LookupCall> LookupCalls { get; } = [];
        public int GetStylesCallCount { get; private set; }

        public Task<List<DictionaryLookupResult>> LookupAsync(
            string text,
            int maxResults = 16,
            int scanLength = 16,
            string? traceId = null)
        {
            LookupCalls.Add(new LookupCall(text, maxResults, scanLength, traceId));
            return Task.FromResult(Results);
        }

        public Task<List<DictionaryStyle>> GetStylesAsync()
        {
            GetStylesCallCount++;
            return Task.FromResult(Styles);
        }

        public Task<byte[]?> GetMediaFileAsync(string dictName, string mediaPath) =>
            Task.FromResult<byte[]?>(null);

        public Task RebuildQueryAsync() => Task.CompletedTask;

        public Task SetActiveLanguageAsync(string languageId) => Task.CompletedTask;
    }

    private sealed class RecordingSettingsService : ISettingsService
    {
        public AppSettings Current { get; init; } = new();

        public event EventHandler<SettingsChangedEventArgs>? SettingChanged;

        public void Set<T>(Expression<Func<AppSettings, T>> selector, T value) =>
            SettingChanged?.Invoke(this, new SettingsChangedEventArgs
            {
                PropertyName = selector.ToString() ?? "",
                NewValue = value,
            });

        public void ReplaceCurrent(AppSettings settings) =>
            SettingChanged?.Invoke(this, new SettingsChangedEventArgs
            {
                PropertyName = nameof(Current),
                NewValue = settings,
            });

        public Task SaveAsync() => Task.CompletedTask;

        public Task LoadAsync() => Task.CompletedTask;

        public void Reset()
        {
        }
    }
}
