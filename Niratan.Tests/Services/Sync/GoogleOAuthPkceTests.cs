using System.Text.RegularExpressions;
using FluentAssertions;
using Niratan.Services.Sync;

namespace Niratan.Tests.Services.Sync;

public sealed class GoogleOAuthPkceTests
{
    [Fact]
    public void CreateCodeVerifier_ReturnsHighEntropyUrlSafeValue()
    {
        var verifier = GoogleOAuthPkce.CreateCodeVerifier();

        verifier.Length.Should().BeInRange(43, 128);
        Regex.IsMatch(verifier, "^[A-Za-z0-9._~-]+$").Should().BeTrue();
    }

    [Fact]
    public void CreateCodeChallenge_UsesBase64UrlEncodedSha256()
    {
        var challenge = GoogleOAuthPkce.CreateCodeChallenge(
            "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");

        challenge.Should().Be("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    }
}
