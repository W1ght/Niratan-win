using FluentAssertions;

namespace Niratan.Tests.Views.Dictionary;

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
            "Niratan"));

    [Fact]
    public void PopupHost_UsesOneInitialBatchAndCancelsDeferredWorkOnLifecycleChanges()
    {
        var path = Path.Combine(ProjectRoot, "Views", "Dictionary", "DictionaryLookupPopup.cs");
        var code = File.ReadAllText(path);

        code.Should().Contain("DictionaryPopupBatchPlanner.Create(request.Results.Count)");
        code.Should().Contain("GenerateInjectionScript(initialResults");
        code.Should().Contain("totalResultCount: request.Results.Count");
        code.Should().Contain("AppendDeferredResultsAsync");
        code.Should().Contain("GenerateAppendResultsScript");
        code.Split("CancelDeferredResults();", StringSplitOptions.None)
            .Should().HaveCountGreaterThan(3);
        code.Should().Contain("generation != _displayGeneration");
        code.Should().Contain("_pendingContentCancellationToken.IsCancellationRequested");
        code.Should().Contain("_displayTransaction.CommitInFlightGeneration != readyGeneration");
        code.Should().Contain("CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken)");
        code.Should().Contain("ReferenceEquals(_deferredResultsCts, owner)");
        code.Should().Contain("JsonSerializer.Deserialize<string>(rawResult)");
    }
}
