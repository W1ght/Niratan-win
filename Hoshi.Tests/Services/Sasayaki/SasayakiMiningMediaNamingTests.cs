using FluentAssertions;
using Hoshi.Services.Sasayaki;

namespace Hoshi.Tests.Services.Sasayaki;

public sealed class SasayakiMiningMediaNamingTests
{
    [Fact]
    public void CreateAudioClipFilename_UsesStableSafeNames()
    {
        var filename = SasayakiMiningMediaNaming.CreateAudioClipFilename(
            "D:\\Audiobooks\\化物語 上.m4b",
            TimeSpan.FromSeconds(12),
            TimeSpan.FromSeconds(14.5));

        filename.Should().StartWith("hoshi_sasayaki_");
        filename.Should().EndWith("_000012000_000014500.m4a");
    }
}
