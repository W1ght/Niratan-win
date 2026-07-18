using System.Linq.Expressions;
using FluentAssertions;
using Niratan.Models;
using Niratan.Models.Dictionary;
using Niratan.Models.DTO;
using Niratan.Models.Profiles;
using Niratan.Models.Settings;
using Niratan.Services.Dictionary;
using Niratan.Services.Profiles;
using Niratan.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Niratan.Tests.Services.Profiles;

public sealed class ProfileRuntimeServiceTests
{
    [Fact]
    public async Task ActivateForBookAsync_KeepsGlobalProfileRegardlessOfBookLanguage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TemporaryProfileRoot();
        var profiles = await ProfileService.CreateForTestsAsync(temp.Root);
        var settings = new RecordingSettingsService();
        var reader = new RecordingReaderSettingsService();
        var lookup = new RecordingLookupService();
        var runtime = CreateRuntime(profiles, settings, reader, lookup);

        await runtime.ActivateForBookAsync(new NovelBook { Id = "book-1", Language = "en-US" }, ct);

        runtime.ActiveProfileId.Should().Be(ProfileConstants.DefaultJapaneseProfileId);
        runtime.ActiveLanguage.Id.Should().Be(ContentLanguageProfile.Japanese.Id);
        lookup.LanguageIds.Should().ContainSingle().Which.Should().Be(ContentLanguageProfile.Japanese.Id);
        runtime.GetDictionaryConfigRoot(runtime.ActiveProfileId)
            .Should().EndWith(Path.Combine(ProfileConstants.DefaultJapaneseProfileId, "dictionaries"));
        runtime.EnableUnconfiguredDictionariesForProfile(ProfileConstants.DefaultJapaneseProfileId)
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task ActivateForVideoAsync_KeepsGlobalProfileRegardlessOfLegacyOverride()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TemporaryProfileRoot();
        var profiles = await ProfileService.CreateForTestsAsync(temp.Root);
        var lookup = new RecordingLookupService();
        var runtime = CreateRuntime(
            profiles,
            new RecordingSettingsService(),
            new RecordingReaderSettingsService(),
            lookup);

        var english = await profiles.CreateProfileAsync("English", "en", ct: ct);
        await runtime.ActivateProfileAsync(english.Id, ct: ct);

        await runtime.ActivateForVideoAsync(new VideoItem
        {
            Id = "video-1",
            ProfileId = ProfileConstants.DefaultJapaneseProfileId,
        }, ct);

        runtime.ActiveProfileId.Should().Be(english.Id);
        lookup.LanguageIds.Should().ContainSingle().Which.Should().Be(ContentLanguageProfile.English.Id);
    }

    [Fact]
    public async Task SaveActiveSettingsAsync_PersistsProfileOwnedSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TemporaryProfileRoot();
        var profiles = await ProfileService.CreateForTestsAsync(temp.Root);
        var settings = new RecordingSettingsService();
        var reader = new RecordingReaderSettingsService();
        var runtime = CreateRuntime(profiles, settings, reader, new RecordingLookupService());

        var english = await profiles.CreateProfileAsync("English", "en", ct: ct);
        await runtime.ActivateProfileAsync(english.Id, ct: ct);
        settings.Current.DictionaryDisplaySettings = new DictionaryDisplaySettings(MaxResults: 23);
        reader.Current.FontSize = 31;
        await runtime.SaveActiveSettingsAsync(ct);
        await runtime.ActivateProfileAsync(ProfileConstants.DefaultJapaneseProfileId, ct: ct);
        settings.Current.DictionaryDisplaySettings = new DictionaryDisplaySettings(MaxResults: 8);
        reader.Current.FontSize = 18;
        await runtime.ActivateProfileAsync(english.Id, ct: ct);

        settings.Current.DictionaryDisplaySettings.MaxResults.Should().Be(23);
        reader.Current.FontSize.Should().Be(31);
    }

    private static ProfileRuntimeService CreateRuntime(
        IProfileService profiles,
        ISettingsService settings,
        IReaderSettingsService reader,
        RecordingLookupService lookup)
    {
        var store = new ProfileSettingsStore(profiles, settings, reader);
        var services = new ServiceCollection()
            .AddSingleton<IDictionaryLookupService>(lookup)
            .BuildServiceProvider();
        return new ProfileRuntimeService(
            profiles,
            store,
            services,
            NullLogger<ProfileRuntimeService>.Instance);
    }

    private sealed class TemporaryProfileRoot : IDisposable
    {
        public TemporaryProfileRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), $"niratan-profile-runtime-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class RecordingLookupService : IDictionaryLookupService
    {
        public List<string> LanguageIds { get; } = [];

        public Task<List<DictionaryLookupResult>> LookupAsync(
            string text,
            int maxResults = 16,
            int scanLength = 16,
            string? traceId = null) =>
            Task.FromResult(new List<DictionaryLookupResult>());

        public Task<List<DictionaryStyle>> GetStylesAsync() =>
            Task.FromResult(new List<DictionaryStyle>());

        public Task<byte[]?> GetMediaFileAsync(string dictName, string mediaPath) =>
            Task.FromResult<byte[]?>(null);

        public Task RebuildQueryAsync() => Task.CompletedTask;

        public Task SetActiveLanguageAsync(string languageId)
        {
            LanguageIds.Add(languageId);
            return Task.CompletedTask;
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
            SettingChanged?.Invoke(this, new SettingsChangedEventArgs { PropertyName = nameof(Current) });
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
            SettingChanged?.Invoke(this, new SettingsChangedEventArgs { PropertyName = nameof(Current) });
        }

        public Task SaveAsync() => Task.CompletedTask;

        public Task LoadAsync() => Task.CompletedTask;

        public void Reset() => Current = new ReaderSettings();
    }
}
