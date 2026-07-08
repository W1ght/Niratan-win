using System.Linq.Expressions;
using FluentAssertions;
using Hoshi.Models.Anki;
using Hoshi.Models.Dictionary;
using Hoshi.Models.DTO;
using Hoshi.Models.Settings;
using Hoshi.Services.Anki;
using Hoshi.Services.Dictionary;
using Hoshi.Services.Settings;

namespace Hoshi.Tests.Services.Anki;

public class AnkiServiceDictionaryMediaTests
{
    [Fact]
    public async Task ResolveDictionaryMediaAsync_LoadsYomitanMediaFromDictionaryService()
    {
        var lookup = new RecordingLookupService([0x3c, 0x73, 0x76, 0x67, 0x3e]);
        var service = new AnkiService(new FakeSettingsService(), lookup);

        var bytes = await service.ResolveDictionaryMediaAsync(new DictionaryMedia
        {
            Dictionary = "明鏡国語辞典 第三版",
            Path = "gaiji/00001.svg",
            Filename = "hoshi_dict_0.svg",
        });

        bytes.Should().Equal([0x3c, 0x73, 0x76, 0x67, 0x3e]);
        lookup.Requests.Should().ContainSingle().Which.Should().Be(("明鏡国語辞典 第三版", "gaiji/00001.svg"));
    }

    [Fact]
    public async Task ResolveDictionaryMediaAsync_FallsBackToLocalFilePathForLegacyPayloads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hoshi-anki-media-{Guid.NewGuid():N}.svg");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        try
        {
            var lookup = new RecordingLookupService(null);
            var service = new AnkiService(new FakeSettingsService(), lookup);

            var bytes = await service.ResolveDictionaryMediaAsync(new DictionaryMedia
            {
                Path = path,
                Filename = "legacy.svg",
            });

            bytes.Should().Equal([1, 2, 3, 4]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public event EventHandler<SettingsChangedEventArgs>? SettingChanged;

        public void Set<T>(Expression<Func<AppSettings, T>> selector, T value) =>
            SettingChanged?.Invoke(this, new SettingsChangedEventArgs { PropertyName = "" });

        public void ReplaceCurrent(AppSettings settings) =>
            SettingChanged?.Invoke(this, new SettingsChangedEventArgs { PropertyName = nameof(Current) });

        public Task SaveAsync() => Task.CompletedTask;

        public Task LoadAsync() => Task.CompletedTask;

        public void Reset()
        {
        }
    }

    private sealed class RecordingLookupService(byte[]? bytes) : IDictionaryLookupService
    {
        public List<(string Dictionary, string Path)> Requests { get; } = [];

        public Task<List<DictionaryLookupResult>> LookupAsync(
            string text,
            int maxResults = 16,
            int scanLength = 16,
            string? traceId = null) =>
            Task.FromResult(new List<DictionaryLookupResult>());

        public Task<List<DictionaryStyle>> GetStylesAsync() =>
            Task.FromResult(new List<DictionaryStyle>());

        public Task<byte[]?> GetMediaFileAsync(string dictName, string mediaPath)
        {
            Requests.Add((dictName, mediaPath));
            return Task.FromResult(bytes);
        }

        public Task RebuildQueryAsync() => Task.CompletedTask;

        public Task SetActiveLanguageAsync(string languageId) => Task.CompletedTask;
    }
}
