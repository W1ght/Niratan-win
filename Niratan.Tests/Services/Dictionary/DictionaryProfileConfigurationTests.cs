using FluentAssertions;
using Niratan.Models.Dictionary;
using Niratan.Services.Dictionary;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;

namespace Niratan.Tests.Services.Dictionary;

public sealed class DictionaryProfileConfigurationTests
{
    [Fact]
    public void NonDefaultProfile_DisablesUnconfiguredInstalledDictionaries()
    {
        var config = DictionaryConfigurationStore.NormalizeForInstalled(
            DictionaryConfig.Empty,
            [DictionaryType.Term],
            type => type == DictionaryType.Term ? ["SharedDict"] : [],
            enableUnconfigured: false);

        DictionaryConfigurationStore.GetEntries(config, DictionaryType.Term)
            .Should()
            .ContainSingle()
            .Which.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task GetInstalledDictionariesAsync_UsesActiveProfileConfiguration()
    {
        using var temp = new TemporaryProfileDictionaryRoot();
        temp.WriteNativeDictionaryDirectory(DictionaryType.Term, "SharedDict");
        var context = temp.CreateContext("english", "default-ja", "english");
        var importService = new DictionaryImportService(
            NullLogger<DictionaryImportService>.Instance,
            new RecordingLookupService(),
            temp.DictionaryRoot,
            context);

        var dictionaries = await importService.GetInstalledDictionariesAsync(DictionaryType.Term);

        dictionaries.Should().ContainSingle().Which.IsEnabled.Should().BeFalse();
        DictionaryConfigurationStore.GetEntries(
                DictionaryConfigurationStore.Load(temp.ProfileConfigRoot("english")),
                DictionaryType.Term)
            .Should()
            .ContainSingle(entry => entry.FileName == "SharedDict" && !entry.IsEnabled);
    }

    [Fact]
    public async Task DeleteAsync_RemovesDictionaryReferencesFromEveryProfile()
    {
        using var temp = new TemporaryProfileDictionaryRoot();
        temp.WriteNativeDictionaryDirectory(DictionaryType.Term, "SharedDict");
        temp.SaveProfileConfig("default-ja", DictionaryType.Term, "SharedDict");
        temp.SaveProfileConfig("english", DictionaryType.Term, "SharedDict");
        var lookupService = new RecordingLookupService();
        var importService = new DictionaryImportService(
            NullLogger<DictionaryImportService>.Instance,
            lookupService,
            temp.DictionaryRoot,
            temp.CreateContext("default-ja", "default-ja", "english"));

        var deleted = await importService.DeleteAsync(DictionaryType.Term, "SharedDict");

        deleted.Should().BeTrue();
        temp.ReadProfileEntries("default-ja", DictionaryType.Term).Should().BeEmpty();
        temp.ReadProfileEntries("english", DictionaryType.Term).Should().BeEmpty();
        lookupService.RebuildCount.Should().Be(1);
    }

    [Fact]
    public async Task ImportAsync_EnablesImportedDictionaryInActiveProfile()
    {
        using var temp = new TemporaryProfileDictionaryRoot();
        var lookupService = new RecordingLookupService();
        var importService = new DictionaryImportService(
            NullLogger<DictionaryImportService>.Instance,
            lookupService,
            temp.DictionaryRoot,
            temp.CreateContext("english", "default-ja", "english"));
        var zipPath = temp.CreateDictionaryZip("EnglishDict");

        var result = await importService.ImportAsync(zipPath);

        result.Success.Should().BeTrue(string.Join("; ", result.Errors));
        temp.ReadProfileEntries("english", DictionaryType.Term)
            .Should()
            .ContainSingle(entry => entry.FileName == "EnglishDict" && entry.IsEnabled);
        lookupService.RebuildCount.Should().Be(1);
    }

    private sealed class TemporaryProfileDictionaryRoot : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "NiratanTests",
            Guid.NewGuid().ToString("N"));

        public string DictionaryRoot => Path.Combine(_root, "dictionaries");
        public string ProfilesRoot => Path.Combine(_root, "profiles");
        public string InputRoot => Path.Combine(_root, "input");

        public TemporaryProfileDictionaryRoot()
        {
            Directory.CreateDirectory(DictionaryRoot);
            Directory.CreateDirectory(ProfilesRoot);
            Directory.CreateDirectory(InputRoot);
        }

        public string ProfileConfigRoot(string profileId)
        {
            var root = Path.Combine(ProfilesRoot, profileId);
            Directory.CreateDirectory(root);
            return root;
        }

        public RecordingDictionaryProfileContext CreateContext(string activeProfileId, params string[] profileIds) =>
            new(this, activeProfileId, profileIds);

        public void WriteNativeDictionaryDirectory(DictionaryType type, string name)
        {
            var dictDir = Path.Combine(DictionaryRoot, type.ToString(), name);
            Directory.CreateDirectory(dictDir);
            File.WriteAllText(Path.Combine(dictDir, "index.json"), $$"""
            {
              "title": "{{name}}",
              "format": 3,
              "revision": "test"
            }
            """);
            File.WriteAllText(Path.Combine(dictDir, ".hoshidicts_1"), "");
        }

        public void SaveProfileConfig(string profileId, DictionaryType type, string dictName)
        {
            var config = DictionaryConfigurationStore.WithEntries(
                DictionaryConfig.Empty,
                type,
                [new DictionaryConfigEntry(dictName, true, 0)]);
            DictionaryConfigurationStore.Save(ProfileConfigRoot(profileId), config);
        }

        public IReadOnlyList<DictionaryConfigEntry> ReadProfileEntries(
            string profileId,
            DictionaryType type) =>
            DictionaryConfigurationStore.GetEntries(
                DictionaryConfigurationStore.Load(ProfileConfigRoot(profileId)),
                type);

        public string CreateDictionaryZip(string name)
        {
            var inputDir = Path.Combine(InputRoot, name);
            if (Directory.Exists(inputDir))
                Directory.Delete(inputDir, recursive: true);
            Directory.CreateDirectory(inputDir);
            File.WriteAllText(Path.Combine(inputDir, "index.json"), $$"""
            {
              "title": "{{name}}",
              "format": 3,
              "revision": "test"
            }
            """);
            File.WriteAllText(
                Path.Combine(inputDir, "term_bank_1.json"),
                """[["read","","","v",0,["to interpret written text"],0,""]]""");

            var zipPath = Path.Combine(InputRoot, $"{name}.zip");
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            ZipFile.CreateFromDirectory(inputDir, zipPath, CompressionLevel.Optimal, false);
            return zipPath;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingDictionaryProfileContext(
        TemporaryProfileDictionaryRoot root,
        string activeProfileId,
        IReadOnlyList<string> profileIds) : IDictionaryProfileContext
    {
        public string ActiveProfileId => activeProfileId;

        public IReadOnlyList<string> ProfileIds => profileIds;

        public string GetDictionaryConfigRoot(string profileId) =>
            root.ProfileConfigRoot(profileId);

        public bool EnableUnconfiguredDictionariesForProfile(string profileId) =>
            string.Equals(profileId, "default-ja", StringComparison.Ordinal);
    }

    private sealed class RecordingLookupService : IDictionaryLookupService
    {
        public int RebuildCount { get; private set; }

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

        public Task RebuildQueryAsync()
        {
            RebuildCount++;
            return Task.CompletedTask;
        }

        public Task SetActiveLanguageAsync(string languageId) => Task.CompletedTask;
    }
}
