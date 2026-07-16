using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Niratan.Models.Settings;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class Anime4KShaderServiceTests
{
    [Fact]
    public void Presets_KeepOfficialShaderOrder()
    {
        Anime4KShaderService.GetPresetFileNames(VideoShaderPreset.Anime4KFast)
            .Should()
            .Equal(
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_Restore_CNN_M.glsl",
                "Anime4K_Upscale_CNN_x2_M.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K_Upscale_CNN_x2_S.glsl");

        Anime4KShaderService.GetPresetFileNames(VideoShaderPreset.Anime4KHighQuality)
            .Should()
            .Equal(
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_Restore_CNN_VL.glsl",
                "Anime4K_Upscale_CNN_x2_VL.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                "Anime4K_AutoDownscalePre_x4.glsl",
                "Anime4K_Upscale_CNN_x2_M.glsl");
    }

    [Fact]
    public void DownloadUris_ArePinnedAndHaveCdnFallbacks()
    {
        var uris = Anime4KShaderService.BuildDownloadUris(
            "glsl/Restore/Anime4K_Clamp_Highlights.glsl");

        uris.Should().HaveCount(3);
        uris.Should().OnlyContain(uri => uri.AbsoluteUri.Contains("v4.0.1"));
        uris[0].Host.Should().Be("raw.githubusercontent.com");
        uris[1].Host.Should().Be("cdn.jsdelivr.net");
        uris[2].Host.Should().Be("fastly.jsdelivr.net");
    }

    [Fact]
    public void Validation_RequiresPinnedHashAndMpvHookMarkers()
    {
        var bytes = Encoding.UTF8.GetBytes("//!HOOK LUMA\n//!BIND HOOKED\nvec4 hook(){return HOOKED_tex(HOOKED_pos);}\n");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        var action = () => Anime4KShaderService.ValidateDownloadedShader(bytes, hash);
        action.Should().NotThrow();

        var invalidHash = () => Anime4KShaderService.ValidateDownloadedShader(bytes, new string('0', 64));
        invalidHash.Should().Throw<InvalidDataException>();

        var invalidShader = Encoding.UTF8.GetBytes("not a shader");
        var invalidShaderHash = Convert.ToHexString(SHA256.HashData(invalidShader));
        var invalidContent = () => Anime4KShaderService.ValidateDownloadedShader(
            invalidShader,
            invalidShaderHash);
        invalidContent.Should().Throw<InvalidDataException>();
    }
}
