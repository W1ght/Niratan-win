using FluentAssertions;
using Niratan.Models.Profiles;
using Niratan.Services.Dictionary;

namespace Niratan.Tests.Services.Dictionary;

public sealed class TextSelectionResolverTests
{
    [Fact]
    public void LookupCandidate_Japanese_StartsAtClickedUtf16Offset()
    {
        var candidate = TextSelectionResolver.LookupCandidate(
            "夜空の星を見ています。",
            3,
            6,
            ContentLanguageProfile.Japanese);

        candidate.Should().Be(new TextLookupCandidate("星を見ていま", 3));
    }

    [Fact]
    public void LookupCandidate_LeadingWhitespace_PreservesAdjustedOffset()
    {
        var candidate = TextSelectionResolver.LookupCandidate(
            "😀  星を見ます。",
            2,
            8,
            ContentLanguageProfile.Japanese);

        candidate.Should().Be(new TextLookupCandidate("星を見ます。", 4));
    }

    [Fact]
    public void LookupCandidate_English_RewindsToWordStart()
    {
        var candidate = TextSelectionResolver.LookupCandidate(
            "We watched don't panic together.",
            14,
            16,
            ContentLanguageProfile.English);

        candidate.Should().Be(new TextLookupCandidate("don't panic toge", 11));
    }
}
