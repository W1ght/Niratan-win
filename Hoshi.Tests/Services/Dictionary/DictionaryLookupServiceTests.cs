using FluentAssertions;
using Hoshi.Models.Dictionary;
using Hoshi.Services.Dictionary;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Hoshi.Tests.Services.Dictionary;

public class DictionaryLookupServiceTests
{
    [Fact]
    public void EnumerateLookupCandidates_ReturnsLongestPrefixesFirst()
    {
        var candidates = DictionaryLookupService
            .EnumerateLookupCandidates("abcdef", scanLength: 4)
            .ToList();

        candidates.Should().Equal("abcd", "abc", "ab", "a");
    }

    [Fact]
    public void EnumerateLookupCandidates_StopsAtTextLength()
    {
        var candidates = DictionaryLookupService
            .EnumerateLookupCandidates("go", scanLength: 16)
            .ToList();

        candidates.Should().Equal("go", "g");
    }

    [Fact]
    public void PopupHtmlGenerator_AutoRendersLookupEntriesAfterShellLoads()
    {
        var html = new PopupHtmlGenerator().GenerateHtml(
            [
                new DictionaryLookupResult(
                    Matched: "test",
                    Deinflected: "test",
                    Trace: [],
                    Term: new TermResult(
                        Expression: "test",
                        Reading: "test",
                        Rules: "",
                        Glossaries: [new GlossaryEntry("TestDict", "definition", "", "")],
                        Frequencies: [],
                        Pitches: []),
                    PreprocessorSteps: 0)
            ],
            []);

        html.Should().Contain("window.lookupEntries = ");
        html.Should().Contain("window.entryCount = 1;");
        html.Should().Contain("window.hoshiPopupObserveContentReady");
        html.Should().Contain("window.renderPopup();");
        html.Should().Contain("popupDiagnostic");
        html.Should().Contain("contentReady");
    }

    [Fact]
    public async Task LookupAsync_FindsDeinflectedVerbForms()
    {
        using var temp = new TemporaryDictionaryRoot();
        temp.WriteTermDictionary("TestDict",
            new object[] { "読む", "よむ", "", "v5", 10, new[] { "to read" } });

        var service = new DictionaryLookupService(
            NullLogger<DictionaryLookupService>.Instance,
            temp.DictionaryRoot);

        var results = await service.LookupAsync("読んだ");

        results.Should().ContainSingle();
        results[0].Matched.Should().Be("読んだ");
        results[0].Deinflected.Should().Be("読む");
        results[0].Trace.Should().Contain(t => t.Name == "-た");
    }

    [Fact]
    public async Task LookupAsync_FiltersDeinflectionByYomitanRules()
    {
        using var temp = new TemporaryDictionaryRoot();
        temp.WriteTermDictionary("TestDict",
            new object[] { "読み", "よみ", "", "n", 10, new[] { "reading as a noun" } },
            new object[] { "読む", "よむ", "", "v5", 10, new[] { "to read" } });

        var service = new DictionaryLookupService(
            NullLogger<DictionaryLookupService>.Instance,
            temp.DictionaryRoot);

        var results = await service.LookupAsync("読んだ");

        results.Select(r => r.Term.Expression).Should().Contain("読む");
        results.Select(r => r.Term.Expression).Should().NotContain("読み");
    }

    private sealed class TemporaryDictionaryRoot : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "HoshiTests", Guid.NewGuid().ToString("N"));

        public string DictionaryRoot => Path.Combine(_root, "dictionaries");

        public TemporaryDictionaryRoot()
        {
            Directory.CreateDirectory(DictionaryRoot);
        }

        public void WriteTermDictionary(string name, params object[][] terms)
        {
            var dir = Path.Combine(DictionaryRoot, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "index.json"), $$"""
            {
              "title": "{{name}}",
              "format": 3,
              "revision": "test"
            }
            """);
            File.WriteAllText(Path.Combine(dir, "term_bank_1.json"), JsonSerializer.Serialize(terms));
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
