using System.Linq.Expressions;
using FluentAssertions;
using Hoshi.Models.DTO;
using Hoshi.Models.Settings;
using Hoshi.Services.Profiles;
using Hoshi.Services.Settings;

namespace Hoshi.Tests.Services.Profiles;

public sealed class ProfileSettingsStoreTests
{
    [Fact]
    public async Task ActivateAsync_PersistsCurrentProfileAndLoadsTargetProfile()
    {
        using var temp = new TemporaryProfileRoot();
        var settings = new RecordingSettingsService
        {
            Current = new AppSettings
            {
                DictionaryDisplaySettings = new DictionaryDisplaySettings(MaxResults: 7),
            },
        };
        var reader = new RecordingReaderSettingsService
        {
            Current = new ReaderSettings { FontSize = 22 },
        };
        var profiles = await ProfileService.CreateForTestsAsync(temp.Root);
        var english = await profiles.CreateProfileAsync(
            "English",
            "en",
            ct: TestContext.Current.CancellationToken);
        var store = new ProfileSettingsStore(profiles, settings, reader);

        await store.ActivateAsync("default-ja", TestContext.Current.CancellationToken);
        settings.Current.DictionaryDisplaySettings = new DictionaryDisplaySettings(
            MaxResults: 9,
            PopupMaxWidth: 1200,
            PopupMaxHeight: 700,
            PopupScale: 1.25,
            PopupActionBar: true,
            PopupFullWidth: true);
        reader.Current.FontSize = 28;
        await store.ActivateAsync(english.Id, TestContext.Current.CancellationToken);

        settings.Current.DictionaryDisplaySettings.MaxResults.Should().Be(16);
        reader.Current.FontSize.Should().Be(new ReaderSettings().FontSize);

        await store.ActivateAsync("default-ja", TestContext.Current.CancellationToken);
        settings.Current.DictionaryDisplaySettings.Should().Match<DictionaryDisplaySettings>(value =>
            value.MaxResults == 9
            && value.PopupMaxWidth == 1200
            && value.PopupMaxHeight == 700
            && value.PopupScale == 1.25
            && value.PopupActionBar
            && value.PopupFullWidth);
        reader.Current.FontSize.Should().Be(28);
    }

    [Fact]
    public async Task ActivateAsync_PreservesGlobalAnkiTransportWhenLoadingProfileMiningSettings()
    {
        using var temp = new TemporaryProfileRoot();
        var settings = new RecordingSettingsService
        {
            Current = new AppSettings
            {
                AnkiSettings = new AnkiSettings
                {
                    AnkiConnectUrl = "http://global",
                    AnkiConnectForceSync = true,
                    AvailableDecks = [new AnkiDeck { Id = 1, Name = "Global Deck" }],
                    AvailableNoteTypes =
                    [
                        new AnkiNoteType { Id = 2, Name = "Global Note", Fields = ["Expression"] },
                    ],
                    SelectedDeckId = 10,
                    SelectedDeckName = "Japanese",
                    SelectedNoteTypeId = 20,
                    SelectedNoteTypeName = "JP Mining",
                    FieldMappings = new Dictionary<string, string> { ["Expression"] = "{expression}" },
                    Tags = "jp",
                },
            },
        };
        var reader = new RecordingReaderSettingsService();
        var profiles = await ProfileService.CreateForTestsAsync(temp.Root);
        var english = await profiles.CreateProfileAsync(
            "English",
            "en",
            ct: TestContext.Current.CancellationToken);
        var store = new ProfileSettingsStore(profiles, settings, reader);

        await store.ActivateAsync("default-ja", TestContext.Current.CancellationToken);
        settings.Current.AnkiSettings.SelectedDeckName = "English";
        settings.Current.AnkiSettings.SelectedNoteTypeName = "EN Mining";
        settings.Current.AnkiSettings.FieldMappings = new Dictionary<string, string> { ["IPA"] = "{ipa}" };
        settings.Current.AnkiSettings.Tags = "en";
        await store.ActivateAsync(english.Id, TestContext.Current.CancellationToken);
        await store.ActivateAsync("default-ja", TestContext.Current.CancellationToken);

        settings.Current.AnkiSettings.AnkiConnectUrl.Should().Be("http://global");
        settings.Current.AnkiSettings.AnkiConnectForceSync.Should().BeTrue();
        settings.Current.AnkiSettings.AvailableDecks.Should().ContainSingle().Which.Name.Should().Be("Global Deck");
        settings.Current.AnkiSettings.SelectedDeckName.Should().Be("English");
        settings.Current.AnkiSettings.SelectedNoteTypeName.Should().Be("EN Mining");
        settings.Current.AnkiSettings.FieldMappings.Should().ContainKey("IPA");
        settings.Current.AnkiSettings.Tags.Should().Be("en");
    }

    private sealed class TemporaryProfileRoot : IDisposable
    {
        public TemporaryProfileRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), $"hoshi-profile-settings-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class RecordingSettingsService : ISettingsService
    {
        public AppSettings Current { get; set; } = new();

        public event EventHandler<SettingsChangedEventArgs>? SettingChanged;

        public void Set<T>(Expression<Func<AppSettings, T>> selector, T value)
        {
        }

        public void ReplaceCurrent(AppSettings settings)
        {
            Current = settings;
            SettingChanged?.Invoke(this, new SettingsChangedEventArgs
            {
                PropertyName = nameof(Current),
            });
        }

        public Task SaveAsync() => Task.CompletedTask;

        public Task LoadAsync() => Task.CompletedTask;

        public void Reset() => Current = new AppSettings();
    }

    private sealed class RecordingReaderSettingsService : IReaderSettingsService
    {
        public ReaderSettings Current { get; set; } = new();

        public event EventHandler<SettingsChangedEventArgs>? SettingChanged;

        public void Set<T>(Expression<Func<ReaderSettings, T>> selector, T value)
        {
        }

        public void ReplaceCurrent(ReaderSettings settings)
        {
            Current = settings;
            SettingChanged?.Invoke(this, new SettingsChangedEventArgs
            {
                PropertyName = nameof(Current),
            });
        }

        public Task SaveAsync() => Task.CompletedTask;

        public Task LoadAsync() => Task.CompletedTask;

        public void Reset() => Current = new ReaderSettings();
    }
}
