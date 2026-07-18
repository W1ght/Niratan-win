using System.Text.Json;
using FluentAssertions;
using Niratan.Models.Profiles;
using Niratan.Services.Profiles;

namespace Niratan.Tests.Services.Profiles;

public sealed class ProfileServiceTests
{
    [Fact]
    public async Task LoadAsync_CreatesSingleJapaneseBuiltInProfile()
    {
        using var temp = new TemporaryProfileRoot();
        var service = await ProfileService.CreateForTestsAsync(temp.Root);

        service.Profiles.Should().ContainSingle();
        service.Profiles[0].Should().BeEquivalentTo(new NiratanProfile(
            ProfileConstants.DefaultJapaneseProfileId,
            "Japanese",
            "ja",
            IsDefault: true));
    }

    [Fact]
    public async Task Resolve_AlwaysUsesGlobalProfileForLegacyBookAndVideoContexts()
    {
        using var temp = new TemporaryProfileRoot();
        var service = await ProfileService.CreateForTestsAsync(temp.Root);
        var ct = TestContext.Current.CancellationToken;
        var english = await service.CreateProfileAsync("English", "en", ct: ct);
        await service.SetGlobalActiveProfileAsync(english.Id, ct);

        service.Resolve(ProfileContext.Global()).Profile.Id.Should().Be(english.Id);
        service.Resolve(ProfileContext.Book(ProfileConstants.DefaultJapaneseProfileId, "ja-JP"))
            .Profile.Id.Should().Be(english.Id);
        service.Resolve(ProfileContext.Video(ProfileConstants.DefaultJapaneseProfileId))
            .Profile.Id.Should().Be(english.Id);
    }

    [Fact]
    public async Task LoadAsync_RemovesEquivalentLegacyJapaneseVideoProfileFromIndex()
    {
        using var temp = new TemporaryProfileRoot();
        var index = LegacyVideoIndex();
        await WriteIndexAsync(temp.Root, index);
        Directory.CreateDirectory(Path.Combine(
            temp.Root,
            ProfileConstants.DefaultJapaneseVideoProfileId));

        var service = await ProfileService.CreateForTestsAsync(temp.Root);

        service.Profiles.Select(profile => profile.Id)
            .Should().Equal(ProfileConstants.DefaultJapaneseProfileId);
        service.Resolve(ProfileContext.Global()).Profile.Id
            .Should().Be(ProfileConstants.DefaultJapaneseProfileId);
        Directory.Exists(Path.Combine(temp.Root, ProfileConstants.DefaultJapaneseVideoProfileId))
            .Should().BeTrue("Niratan leaves the legacy directory in place");
    }

    [Fact]
    public async Task LoadAsync_PreservesCustomizedLegacyJapaneseVideoProfileAsOrdinaryProfile()
    {
        using var temp = new TemporaryProfileRoot();
        await WriteIndexAsync(temp.Root, LegacyVideoIndex());
        var legacyDirectory = Path.Combine(temp.Root, ProfileConstants.DefaultJapaneseVideoProfileId);
        Directory.CreateDirectory(legacyDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(legacyDirectory, "reader-settings.json"),
            "{\"FontSize\":31}",
            TestContext.Current.CancellationToken);

        var service = await ProfileService.CreateForTestsAsync(temp.Root);

        var migrated = service.Profiles.Single(profile =>
            profile.Id == ProfileConstants.DefaultJapaneseVideoProfileId);
        migrated.IsDefault.Should().BeFalse();
        service.Resolve(ProfileContext.Global()).Profile.Id.Should().Be(migrated.Id);
    }

    [Fact]
    public async Task CreateProfile_RejectsUnsafeProfileIds()
    {
        using var temp = new TemporaryProfileRoot();
        var service = await ProfileService.CreateForTestsAsync(temp.Root);

        var act = async () => await service.CreateProfileAsync(
            "Bad",
            "en",
            "../bad",
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateProfile_CopiesActiveProfileOwnedSettingsLikeNiratan()
    {
        using var temp = new TemporaryProfileRoot();
        var service = await ProfileService.CreateForTestsAsync(temp.Root);
        var ct = TestContext.Current.CancellationToken;
        var sourceDirectory = service.GetProfileDirectory(ProfileConstants.DefaultJapaneseProfileId);
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "dictionaries"));
        await File.WriteAllTextAsync(
            Path.Combine(sourceDirectory, "reader-settings.json"),
            "reader",
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(sourceDirectory, "dictionaries", "dictionary-config.json"),
            "dictionary",
            ct);

        var created = await service.CreateProfileAsync(
            "Copy",
            "ja",
            ct: ct,
            copyFromProfileId: ProfileConstants.DefaultJapaneseProfileId);

        var destinationDirectory = service.GetProfileDirectory(created.Id);
        (await File.ReadAllTextAsync(
                Path.Combine(destinationDirectory, "reader-settings.json"),
                ct))
            .Should()
            .Be("reader");
        (await File.ReadAllTextAsync(
                Path.Combine(destinationDirectory, "dictionaries", "dictionary-config.json"),
                ct))
            .Should()
            .Be("dictionary");
    }

    [Fact]
    public async Task RenameAndDeleteProfile_PersistCrudAndResetActiveProfile()
    {
        using var temp = new TemporaryProfileRoot();
        var service = await ProfileService.CreateForTestsAsync(temp.Root);
        var ct = TestContext.Current.CancellationToken;
        var created = await service.CreateProfileAsync("Study", "ja", ct: ct);
        await service.SetGlobalActiveProfileAsync(created.Id, ct);

        await service.RenameProfileAsync(created.Id, "  Immersion  ", ct);
        service.Profiles.Single(profile => profile.Id == created.Id).Name.Should().Be("Immersion");

        await service.DeleteProfileAsync(created.Id, ct);
        service.Profiles.Should().NotContain(profile => profile.Id == created.Id);
        service.Resolve(ProfileContext.Global()).Profile.Id
            .Should().Be(ProfileConstants.DefaultJapaneseProfileId);
        Directory.Exists(service.GetProfileDirectory(created.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteProfile_RejectsBuiltInDefault()
    {
        using var temp = new TemporaryProfileRoot();
        var service = await ProfileService.CreateForTestsAsync(temp.Root);

        var act = () => service.DeleteProfileAsync(
            ProfileConstants.DefaultJapaneseProfileId,
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void EnglishDisplayUnits_AreApproximateWords()
    {
        ContentLanguageProfile.English.DisplayUnitsFromRawCharacters(11).Should().Be(3);
        ContentLanguageProfile.English.RawCharactersFromDisplayUnits(3).Should().Be(15);
    }

    [Theory]
    [InlineData("ja-JP", "ja")]
    [InlineData("EN_us", "en")]
    [InlineData("", "ja")]
    [InlineData(null, "ja")]
    public void Normalize_HandlesEpubLanguageTags(string? input, string expected)
    {
        ContentLanguageProfile.Normalize(input).Id.Should().Be(expected);
    }

    private static ProfileIndex LegacyVideoIndex() => new()
    {
        Profiles =
        [
            new NiratanProfile(
                ProfileConstants.DefaultJapaneseProfileId,
                "Japanese EPUB",
                "ja",
                IsDefault: true),
            new NiratanProfile(
                ProfileConstants.DefaultJapaneseVideoProfileId,
                "Japanese Video",
                "ja",
                IsDefault: true),
        ],
        DefaultProfileId = ProfileConstants.DefaultJapaneseProfileId,
        GlobalActiveProfileId = ProfileConstants.DefaultJapaneseVideoProfileId,
        PrimaryProfileIdsByLanguage = new Dictionary<string, string>
        {
            ["ja"] = ProfileConstants.DefaultJapaneseProfileId,
        },
    };

    private static async Task WriteIndexAsync(string root, ProfileIndex index)
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "profiles.json"),
            JsonSerializer.Serialize(index),
            TestContext.Current.CancellationToken);
    }

    private sealed class TemporaryProfileRoot : IDisposable
    {
        public TemporaryProfileRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), $"niratan-profiles-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
