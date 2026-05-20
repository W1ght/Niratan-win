using FluentAssertions;
using Hoshi.Services.Dictionary;

namespace Hoshi.Tests.Services.Dictionary;

public class JapaneseDeinflectorTests
{
    [Theory]
    [InlineData("読んだ", "読む", "-た")]
    [InlineData("食べました", "食べる", "-ます")]
    [InlineData("見ている", "見る", "-て")]
    [InlineData("高くなかった", "高い", "negative")]
    public void Deinflect_ReturnsExpectedDictionaryForm(
        string inflected,
        string expected,
        string expectedTrace)
    {
        var results = JapaneseDeinflector.Instance.Deinflect(inflected);

        results.Should().Contain(r =>
            r.Text == expected && r.Trace.Any(t => t.Name == expectedTrace));
    }

    [Fact]
    public void PosToConditions_MapsYomitanVerbRules()
    {
        var conditions = JapaneseDeinflector.PosToConditions("v5 v1 vs adj-i");

        conditions.Should().HaveFlag(JapaneseDeinflectionConditions.V5);
        conditions.Should().HaveFlag(JapaneseDeinflectionConditions.V1);
        conditions.Should().HaveFlag(JapaneseDeinflectionConditions.VS);
        conditions.Should().HaveFlag(JapaneseDeinflectionConditions.ADJ_I);
    }
}
