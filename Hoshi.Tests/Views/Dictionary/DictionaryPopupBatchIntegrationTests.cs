using FluentAssertions;

namespace Hoshi.Tests.Views.Dictionary;

public class DictionaryPopupBatchIntegrationTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "Hoshi"));

    [Fact]
    public void PopupHost_UsesOneInitialBatchAndCancelsDeferredWorkOnLifecycleChanges()
    {
        var path = Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs");
        var code = File.ReadAllText(path);

        code.Should().Contain("DictionaryPopupBatchPlanner.Create(results.Count)");
        code.Should().Contain("GenerateInjectionScript(initialResults");
        code.Should().Contain("totalResultCount: results.Count");
        code.Should().Contain("AppendDeferredResultsAsync");
        code.Should().Contain("GenerateAppendResultsScript");
        code.Split("CancelDeferredResults();", StringSplitOptions.None)
            .Should().HaveCountGreaterThan(3);
        code.Should().Contain("generation != _displayGeneration");
    }
}
