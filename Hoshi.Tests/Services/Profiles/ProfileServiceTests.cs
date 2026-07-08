using FluentAssertions;
using Hoshi.Models.Profiles;
using Hoshi.Services.Profiles;

namespace Hoshi.Tests.Services.Profiles;

public sealed class ProfileServiceTests
{
    [Fact]
    public async Task ResolveBook_UsesExplicitThenLanguagePrimaryThenGlobalFallback()
    {
        using var temp = new TemporaryProfileRoot();
        var service = await ProfileService.CreateForTestsAsync(temp.Root);
        var ct = TestContext.Current.CancellationToken;
        var english = await service.CreateProfileAsync("English EPUB", "en", ct: ct);
        await service.SetPrimaryProfileForLanguageAsync("en", english.Id, ct);
        await service.SetGlobalActiveProfileAsync("default-ja-video", ct);

        service.Resolve(ProfileContext.Book(english.Id, "ja-JP")).Profile.Id.Should().Be(english.Id);
        service.Resolve(ProfileContext.Book(null, "en-US")).Profile.Id.Should().Be(english.Id);
        service.Resolve(ProfileContext.Book(null, "fr")).Profile.Id.Should().Be("default-ja-video");
    }

    [Fact]
    public async Task LoadAsync_CreatesEnglishBuiltInPrimaryProfile()
    {
        using var temp = new TemporaryProfileRoot();
        var service = await ProfileService.CreateForTestsAsync(temp.Root);

        service.Resolve(ProfileContext.Book(null, "en-US")).Profile.Id
            .Should()
            .Be(ProfileConstants.DefaultEnglishProfileId);
        service.GetPrimaryProfileIdForLanguage("en")
            .Should()
            .Be(ProfileConstants.DefaultEnglishProfileId);
    }

    [Fact]
    public async Task ResolveVideo_UsesExplicitThenFallback()
    {
        using var temp = new TemporaryProfileRoot();
        var service = await ProfileService.CreateForTestsAsync(temp.Root);
        var english = await service.CreateProfileAsync(
            "English Video",
            "en",
            ct: TestContext.Current.CancellationToken);

        service.Resolve(ProfileContext.Video(english.Id)).Profile.Id.Should().Be(english.Id);
        service.Resolve(ProfileContext.Video("missing")).Profile.Id.Should().Be("default-ja");
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

    private sealed class TemporaryProfileRoot : IDisposable
    {
        public TemporaryProfileRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), $"hoshi-profiles-{Guid.NewGuid():N}");
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
