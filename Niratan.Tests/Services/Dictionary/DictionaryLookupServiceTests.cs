using System.IO.Compression;
using FluentAssertions;
using Niratan.Enums;
using Niratan.Models.Dictionary;
using Niratan.Models.Settings;
using Niratan.Services.Dictionary;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Collections.Concurrent;

namespace Niratan.Tests.Services.Dictionary;

[Collection(NativeDictionaryTestCollection.Name)]
public class DictionaryLookupServiceTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));

    private static DictionaryLookupResult CreateLookupResult(string expression) =>
        new(
            Matched: expression,
            Deinflected: expression,
            Trace: [],
            Term: new TermResult(
                Expression: expression,
                Reading: expression,
                Rules: "",
                Glossaries: [new GlossaryEntry("TestDict", $"{expression} definition", "", "")],
                Frequencies: [],
                Pitches: []),
            PreprocessorSteps: 0);

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
        html.Should().NotContain("window.niratanPopupObserveContentReady");
        html.Should().Contain("window.renderPopup();");
        html.Should().Contain("window.popupRenderGeneration");
        html.Should().Contain("var generation = window.popupRenderGeneration || 0;");
        html.Should().Contain("style=\"visibility:visible\"");
        html.Should().Contain("window.dictionaryMediaRequestEndpoint = 'https://niratan-dictionary-media.local/image';");
        html.Should().Contain("window.audioRequestEndpoint = 'https://niratan-audio-resolver.local/resolve';");
        html.Should().Contain("popupDiagnostic");
        html.Should().Contain("contentReady");
    }

    [Fact]
    public void PopupHtmlGenerator_SeparatesInitialAndDeferredResultScripts()
    {
        var generator = new PopupHtmlGenerator();
        var first = CreateLookupResult("first");
        var deferred = CreateLookupResult("deferred");

        var initial = generator.GenerateInjectionScript(
            [first], [], renderGeneration: 7, totalResultCount: 2);
        var append = generator.GenerateAppendResultsScript([deferred], 2, 7);

        initial.Should().Contain("entryCount: 2,");
        initial.Should().Contain("window.niratanStagePopupRender({");
        initial.Should().Contain("first");
        initial.Should().NotContain("deferred");
        append.Should().Contain("window.niratanAppendResults");
        append.Should().Contain("deferred");
        append.Should().Contain(", 2, 0, 7)");
        append.Should().Contain("bridge-missing");
        append.Should().Contain("appended");
        append.Should().Contain("stale");
    }

    [Fact]
    public void PopupScript_AppendsOnlyToTheCurrentGeneration()
    {
        var script = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Web", "DictionaryPopup", "popup.js"));

        script.Should().Contain("window.niratanAppendResults = function");
        script.Should().Contain("expectedGeneration !== generation");
        script.Should().Contain("Array.prototype.push.apply(lookupEntries, entries)");
        script.Should().Contain("renderAvailableEntries()");
    }

    [Fact]
    public void PopupScript_CommitsCompleteFirstEntryBeforeSingleContentReady()
    {
        var script = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Web", "DictionaryPopup", "popup.js"));
        const string readyMessage = "postPopupMessage('contentReady', {";

        script.Should().Contain("function commitFirstFrame(generation, entryDiv)");
        script.Should().Contain("function renderAvailableEntries()");
        script.Split(readyMessage, StringSplitOptions.None).Should().HaveCount(2);
        script.Should().Contain("renderEntry(0, generation");
        script.Should().Contain("commitFirstFrame(generation, firstEntryDiv)");
        script.Should().Contain("renderAvailableEntries()");

        var commitStart = script.IndexOf(
            "function commitFirstFrame(generation, entryDiv)",
            StringComparison.Ordinal);
        var commitEnd = script.IndexOf(
            "function renderAvailableEntries",
            commitStart,
            StringComparison.Ordinal);
        var commit = script[commitStart..commitEnd];

        commit.IndexOf("applyConfiguredStyles()", StringComparison.Ordinal)
            .Should().BeLessThan(commit.IndexOf("layoutDictionaryColumns()", StringComparison.Ordinal));
        commit.IndexOf("layoutDictionaryColumns()", StringComparison.Ordinal)
            .Should().BeLessThan(commit.IndexOf(
                "document.documentElement.style.visibility = 'visible'",
                StringComparison.Ordinal));
        commit.IndexOf(
                "document.documentElement.style.visibility = 'visible'",
                StringComparison.Ordinal)
            .Should().BeLessThan(commit.IndexOf(readyMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void PopupHtmlGenerator_UsesOpaqueThemeBackgroundColors()
    {
        var generator = new PopupHtmlGenerator();

        var darkHtml = generator.GenerateHtml([], [], themeMode: ThemeMode.Dark);
        var darkInjection = generator.GenerateInjectionScript([], [], themeMode: ThemeMode.Dark);
        var lightHtml = generator.GenerateHtml([], [], themeMode: ThemeMode.Light);

        darkHtml.Should().Contain("--background-color: #000000;");
        darkHtml.Should().Contain("background-color: var(--background-color);");
        darkHtml.Should().NotContain("--background-color: #2b2b2b;");
        darkInjection.Should().Contain("backgroundColor: '#000000'");
        darkInjection.Should().NotContain("document.documentElement.style.setProperty");
        lightHtml.Should().Contain("--background-color: #FFFFFF;");
        lightHtml.Should().NotContain("--background-color: #f3f3f3;");
    }

    [Fact]
    public void PopupHtmlGenerator_SystemThemeUsesCurrentDarkAppearance()
    {
        var generator = new PopupHtmlGenerator(isSystemThemeDark: () => true);

        var html = generator.GenerateHtml([], [], themeMode: ThemeMode.System);
        var injection = generator.GenerateInjectionScript([], [], themeMode: ThemeMode.System);

        html.Should().Contain("data-niratan-color-scheme=\"dark\"");
        html.Should().Contain("--background-color: #000000;");
        injection.Should().Contain("colorScheme: 'dark'");
        injection.Should().Contain("backgroundColor: '#000000'");
    }

    [Fact]
    public void PopupHtmlGenerator_UsesChromiumCaretFallbackForNestedLookupSelection()
    {
        var html = new PopupHtmlGenerator().GenerateHtml(
            [
                new DictionaryLookupResult(
                    Matched: "食べる",
                    Deinflected: "食べる",
                    Trace: [],
                    Term: new TermResult(
                        Expression: "食べる",
                        Reading: "たべる",
                        Rules: "v1",
                        Glossaries: [new GlossaryEntry("TestDict", "to eat", "", "")],
                        Frequencies: [],
                        Pitches: []),
                    PreprocessorSteps: 0)
            ],
            []);

        html.Should().Contain("getCaretRange");
        html.Should().Contain("getCharacterAtPoint");
        html.Should().Contain("closest('rt, rp')");
        html.Should().Contain("NodeFilter.FILTER_REJECT");
        html.Should().Contain("getSelectionRect");
        html.Should().Contain("document.caretRangeFromPoint");
        html.Should().Contain("document.createTreeWalker");
    }

    [Fact]
    public void PopupHtmlGenerator_AllowsNonJapaneseLookupWhenSettingIsEnabled()
    {
        var html = new PopupHtmlGenerator().GenerateHtml(
            [
                new DictionaryLookupResult(
                    Matched: "test",
                    Deinflected: "test",
                    Trace: [],
                    Term: new TermResult(
                        Expression: "test",
                        Reading: "",
                        Rules: "",
                        Glossaries: [new GlossaryEntry("TestDict", "an examination", "", "")],
                        Frequencies: [],
                        Pitches: []),
                    PreprocessorSteps: 0)
            ],
            [],
            new DictionaryDisplaySettings(ScanNonJapaneseText: true));

        html.Should().Contain("window.scanNonJapaneseText = true");
        html.Should().Contain("window.scanNonJapaneseText === false");
        html.Should().NotContain("|| !isCodePointJapanese(ch.codePointAt(0));");
    }

    [Fact]
    public void PopupHtmlGenerator_OnlyEnablesTwoColumnLayoutWhenConfigured()
    {
        var enabled = new PopupHtmlGenerator().GenerateShellHtml(
            settings: new DictionaryDisplaySettings(TwoColumnLayout: true));
        var disabled = new PopupHtmlGenerator().GenerateShellHtml(
            settings: new DictionaryDisplaySettings(TwoColumnLayout: false));

        enabled.Should().Contain("window.twoColumnLayout = true");
        disabled.Should().Contain("window.twoColumnLayout = false");
    }

    [Fact]
    public void PopupAssets_KeepSingleColumnDictionaryCardsInNormalFlow()
    {
        var stylesheet = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Web",
            "DictionaryPopup",
            "popup.css"));
        var script = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Web",
            "DictionaryPopup",
            "popup.js"));
        const string selector = ".glossary-sections:not(.single-section) > .glossary-group {";
        var ruleStart = stylesheet.IndexOf(selector, StringComparison.Ordinal);
        var ruleEnd = stylesheet.IndexOf('}', ruleStart);

        ruleStart.Should().BeGreaterThanOrEqualTo(0);
        ruleEnd.Should().BeGreaterThan(ruleStart);
        var defaultRule = stylesheet[ruleStart..ruleEnd];
        defaultRule.Should().NotContain("position: absolute");
        defaultRule.Should().NotContain("visibility: hidden");
        script.Should().Contain("window.twoColumnLayout === true");
        script.Should().Contain("item.style.position = 'absolute'");
    }

    [Fact]
    public void PopupScript_AppliesConfiguredShiftHoverDelayInsidePopup()
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "DictionaryPopup", "popup.js"));

        script.Should().Contain("mousemove");
        script.Should().Contain("e.shiftKey");
        script.Should().Contain("window.desktopLookupHoverDelayMs");
        script.Should().Contain("scheduleShiftHoverLookup");
        script.Should().Contain("lookupAtPopupPoint");
        script.Should().Contain("postPopupMessage('lookupRedirect'");
    }

    [Fact]
    public void PopupScript_ResolvesAudioBeforeMiningWhenAudioIsRequired()
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "DictionaryPopup", "popup.js"));

        script.Should().Contain("if (!audioUrls[entryIndex] && (window.audioSources || []).length && window.needsAudio)");
        script.Should().Contain("audioUrls[entryIndex] = await fetchAudioUrl(expression, reading || expression");
        script.Should().Contain("var audio = audioUrls[entryIndex] || '';");
    }

    [Fact]
    public void PopupScript_AcceptsSpaceSeparatedPartOfSpeechRulesDuringMining()
    {
        var script = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Web", "DictionaryPopup", "popup.js"));

        script.Should().Contain("Array.isArray(rules)");
        script.Should().Contain("String(rules || '').split(/\\s+/).filter(Boolean)");
    }

    [Fact]
    public void PopupScript_AlignsDuplicateStateContextMiningAndResultCallbacks()
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "DictionaryPopup", "popup.js"));
        var shellHtml = new PopupHtmlGenerator().GenerateShellHtml();

        script.Should().Contain("el('button'");
        script.Should().Contain("className: 'button-slot inline-action-button'");
        script.Should().Contain("slot.onclick = function (event)");
        script.Should().Contain("createButtonSlot('context', idx)");
        script.Should().Contain("function inlineButtonIcon(kind, state)");
        script.Should().Contain("M12 8v8m-4-4h8");
        script.Should().Contain("requestRenderedDuplicateChecks(liveContainer)");
        script.Should().Contain("postPopupMessage('duplicateCheck'");
        script.Should().Contain("postPopupMessage('prepareContextMining'");
        script.Should().Contain("status === 'added' || status === 'duplicate'");
        script.Should().Contain("window.onMineComplete = function (entryIndex, result)");
        script.Should().Contain("createButtonSlot('viewNote', idx, false)");
        script.Should().Contain("showAnkiNoteButton(entryIndex, result.noteID)");
        script.Should().Contain("requestDuplicateCheck(idx, expression || '', mineSlot)");
        script.Should().Contain("function applyAnkiDuplicateLookup(entryIndex, duplicateLookup, slots)");
        script.Should().Contain("noteIDs: noteIDs.map(Number)");
        script.Should().Contain("postPopupMessage('openAnkiNote'");
        script.Should().Contain("window.onOpenAnkiNoteComplete = function (entryIndex)");
        shellHtml.Should().Contain("window.viewAnkiNoteLabel =");
    }

    [Fact]
    public void PopupHost_BindsContextAndDuplicateChecksToTheCommittingGeneration()
    {
        var popup = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "Views",
            "Dictionary",
            "DictionaryLookupPopup.cs"));

        popup.Should().Contain("ResolveMiningContext(miningPayload.RenderGeneration)");
        popup.Should().Contain("staged.Generation == renderGeneration");
        popup.Should().Contain("_displayTransaction.CommitInFlightGeneration != renderGeneration");
        popup.Should().Contain("dialog.ShowAsync(ContentDialogPlacement.InPlace)");
        popup.Should().Contain("dialog.ShowAsync(ContentDialogPlacement.Popup)");
        popup.Should().Contain("_openableAnkiNotes.TryGetValue");
        popup.Should().Contain("allowedNoteIds.SequenceEqual(noteIds)");
        popup.Should().Contain("_ankiService.OpenNotesInAnkiAsync(noteIds)");
        popup.Should().Contain("_ankiService.DuplicateLookupExpressionAsync(expression)");
        popup.Should().Contain("noteIDs = noteIds");
        popup.Should().Contain("noteID = result.NoteId");
    }

    [Fact]
    public void PopupScript_ExportsGlossariesInYomitanCompatibleLapisShape()
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "DictionaryPopup", "popup.js"));

        script.Should().Contain("COMPACT_GLOSSARIES_ANKI");
        script.Should().Contain("class=\"yomitan-glossary\"");
        script.Should().Contain("<ol>");
        script.Should().Contain("<li data-dictionary=\"");
        script.Should().Contain("applyTableStyles(tempDiv.innerHTML)");
        script.Should().Contain("constructDictCss(css, dictName)");
        script.Should().Contain("window.compactGlossariesAnki");
    }

    [Fact]
    public void PopupScript_ExportsDictionaryImagesAsAnkiMediaInsteadOfPrivateWebViewUrls()
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "DictionaryPopup", "popup.js"));

        script.Should().Contain("currentDictionaryMedia = new Map();");
        script.Should().Contain("currentDictionaryMedia = null;");
        script.Should().Contain("getMediaFilename(dictionary, path)");
        script.Should().Contain("window.useAnkiConnect || window.embedMedia");
        script.Should().Contain("image.src = filename;");
        script.Should().Contain("dictionaryMedia: JSON.stringify(dictMedia)");
        script.Should().Contain("getDictionaryMediaUrl(dictionary, path)");
        script.Should().Contain("img.src = imageUrl;");
    }

    [Fact]
    public void PopupScript_EmitsLookupAndRenderTimingDiagnostics()
    {
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "DictionaryPopup", "popup.js"));

        script.Should().Contain("function postPopupTrace(stage, details)");
        script.Should().Contain("postPopupTrace('render-start'");
        script.Should().Contain("postPopupTrace('render-finished'");
        script.Should().Contain("postPopupTrace('lookup-click-hit'");
        script.Should().Contain("selectMs");
        script.Should().Contain("rectMs");
        script.Should().Contain("clientNow");
    }

    [Fact]
    public void DictionaryImportService_DetectsFrequencyAndPitchMetadataBanks()
    {
        DictionaryImportService
            .DetectMetadataBankKind("""[["word","freq",{"value":1}]]""")
            .Should()
            .Be(DictionaryImportService.DictionaryBankKind.Frequency);

        DictionaryImportService
            .DetectMetadataBankKind("""[["ことば","pitch",{"position":0}]]""")
            .Should()
            .Be(DictionaryImportService.DictionaryBankKind.Pitch);
    }

    [Fact]
    public void DictionaryImportService_CreatesNiratanCompatibleAsciiImportZip()
    {
        using var temp = new TemporaryDictionaryRoot();
        var sourceZip = temp.CreateCustomDictionaryZip(
            "明鏡国語辞典 第三版",
            [
                ("term_bank_1.json", """[["学校","がっこう","","n",0,["school"],0,""]]"""),
                ("term_meta_bank_1.json", """[["学校","freq",{"reading":"がっこう","value":42,"displayValue":"42"}],["学校","pitch",{"reading":"がっこう","pitches":[{"position":0}]}]]"""),
                ("tag_bank_1.json", """[]"""),
                ("styles.css", """.tag{color:red}"""),
                ("images/pic.png", "not copied"),
            ]);
        var compatZip = Path.Combine(temp.InputRoot, "compat.zip");

        var result = DictionaryImportService.CreateCompatibilityImportZip(sourceZip, compatZip, "abc123");

        result.OriginalTitle.Should().Be("明鏡国語辞典 第三版");
        using var archive = ZipFile.OpenRead(result.Path);
        archive.Entries.Select(entry => entry.FullName)
            .Should()
            .BeEquivalentTo("index.json", "term_bank_1.json", "term_meta_bank_1.json", "tag_bank_1.json", "styles.css");
        archive.GetEntry("images/pic.png").Should().BeNull("compat import keeps only lookup files");

        using var reader = new StreamReader(archive.GetEntry("index.json")!.Open());
        var indexJson = reader.ReadToEnd();
        indexJson.Should().Contain("niratan-import-abc123");
        indexJson.Should().NotContain("明鏡国語辞典 第三版");
    }

    [Fact]
    public void DictionaryImportService_KeepsCompatibilityImportDirectoryAsciiAfterNormalize()
    {
        using var temp = new TemporaryDictionaryRoot();
        var typeDir = Path.Combine(temp.DictionaryRoot, DictionaryType.Term.ToString());
        var compatDir = temp.WriteNativeDictionaryDirectory(typeDir, "niratan-import-abc123");
        File.WriteAllText(Path.Combine(compatDir, "index.json"), """
        {
          "title": "明鏡国語辞典 第三版",
          "format": 3,
          "revision": "test"
        }
        """);

        DictionaryImportService.NormalizeConfig(temp.DictionaryRoot);

        Directory.Exists(Path.Combine(typeDir, "niratan-import-abc123")).Should().BeTrue();
        Directory.Exists(Path.Combine(typeDir, "明鏡国語辞典 第三版")).Should().BeFalse();
    }

    [Fact]
    public void DictionaryLookupService_UsesAndroidStyleTypedDictionaryDirectories()
    {
        using var temp = new TemporaryDictionaryRoot();
        temp.WriteNativeDictionaryDirectory(Path.Combine(temp.DictionaryRoot, "Term"), "Mixed");
        temp.WriteNativeDictionaryDirectory(Path.Combine(temp.DictionaryRoot, "Frequency"), "Mixed");
        temp.WriteNativeDictionaryDirectory(Path.Combine(temp.DictionaryRoot, "Pitch"), "Mixed");
        temp.WriteNativeDictionaryDirectory(temp.DictionaryRoot, "LegacyFlat");
        DictionaryConfigurationStore.Save(temp.DictionaryRoot, new DictionaryConfig(
            [new DictionaryConfigEntry("Mixed", true, 0)],
            [new DictionaryConfigEntry("Mixed", true, 0)],
            [new DictionaryConfigEntry("Mixed", true, 0)]));

        // NormalizeConfig migrates LegacyFlat into Term/ and syncs config with disk.
        DictionaryImportService.NormalizeConfig(temp.DictionaryRoot);

        DictionaryLookupService
            .GetOrderedDictionaryDirectories(temp.DictionaryRoot, DictionaryType.Term)
            .Should()
            .Equal(
                Path.Combine(temp.DictionaryRoot, "Term", "Mixed"),
                Path.Combine(temp.DictionaryRoot, "Term", "LegacyFlat"));
        DictionaryLookupService
            .GetOrderedDictionaryDirectories(temp.DictionaryRoot, DictionaryType.Frequency)
            .Should()
            .Equal(Path.Combine(temp.DictionaryRoot, "Frequency", "Mixed"));
        DictionaryLookupService
            .GetOrderedDictionaryDirectories(temp.DictionaryRoot, DictionaryType.Pitch)
            .Should()
            .Equal(Path.Combine(temp.DictionaryRoot, "Pitch", "Mixed"));
    }

    [Fact]
    public void DictionaryDisplaySettings_DefaultsMatchNiratan()
    {
        var settings = new DictionaryDisplaySettings();

        settings.CollapseMode.Should().Be(DictionaryCollapseMode.ExpandAll);
        settings.ExpandFirstDictionary.Should().BeFalse();
        settings.ScanNonJapaneseText.Should().BeTrue();
        settings.MaxResults.Should().Be(16);
        settings.ScanLength.Should().Be(16);
        settings.PopupMaxWidth.Should().Be(320);
        settings.PopupMaxHeight.Should().Be(250);
        settings.PopupScale.Should().Be(1.0);
        settings.PopupActionBar.Should().BeFalse();
        settings.PopupFullWidth.Should().BeFalse();
        settings.CompactGlossaries.Should().BeTrue();
        settings.CompactPitchAccents.Should().BeTrue();
        settings.DeduplicatePitchAccents.Should().BeFalse();
        settings.HarmonicFrequency.Should().BeFalse();
        settings.ShowExpressionTags.Should().BeFalse();
        settings.DictionaryTabDefault.Should().BeFalse();
    }

    [Fact]
    public void PopupHtml_AppliesScaleBeforeRendering()
    {
        var settings = new DictionaryDisplaySettings(
            PopupScale: 1.25,
            CustomCSS: ".custom{padding:8px}");
        var generator = new PopupHtmlGenerator();

        var shell = generator.GenerateShellHtml(settings: settings);
        var injection = generator.GenerateInjectionScript([], [], settings);

        shell.Should().Contain("--popup-scale:1.25;");
        shell.Should().Contain("calc(8px * var(--popup-scale))");
        injection.Should().Contain("popupScaleDeclarations");
        injection.Should().Contain("--popup-scale:1.25;");
        injection.Should().Contain("window.niratanStagePopupRender");
    }

    [Fact]
    public async Task DictionaryImportService_ImportsMixedDictionaryIntoEachAndroidTypeDirectory()
    {
        using var temp = new TemporaryDictionaryRoot();
        var zipPath = temp.CreateDictionaryZip("MixedDict", includeTerms: true, includeFrequency: true, includePitch: true);
        var lookupService = new RecordingLookupService();
        var importService = new DictionaryImportService(
            NullLogger<DictionaryImportService>.Instance,
            lookupService,
            temp.DictionaryRoot);

        var result = await importService.ImportAsync(zipPath);

        result.Success.Should().BeTrue(string.Join("\n", result.Errors));
        Directory.Exists(Path.Combine(temp.DictionaryRoot, "Term", "MixedDict")).Should().BeTrue();
        Directory.Exists(Path.Combine(temp.DictionaryRoot, "Frequency", "MixedDict")).Should().BeTrue();
        Directory.Exists(Path.Combine(temp.DictionaryRoot, "Pitch", "MixedDict")).Should().BeTrue();
        Directory.Exists(Path.Combine(temp.DictionaryRoot, "MixedDict")).Should().BeFalse();
        Directory.EnumerateDirectories(temp.DictionaryRoot, ".dictionary-import-*").Should().BeEmpty();
        (await importService.GetInstalledDictionariesAsync(DictionaryType.Term)).Select(d => d.Name).Should().Equal("MixedDict");
        (await importService.GetInstalledDictionariesAsync(DictionaryType.Frequency)).Select(d => d.Name).Should().Equal("MixedDict");
        (await importService.GetInstalledDictionariesAsync(DictionaryType.Pitch)).Select(d => d.Name).Should().Equal("MixedDict");
        lookupService.RebuildCount.Should().Be(1);
    }

    [Fact]
    public async Task DictionaryImportService_DoesNotExposeFrequencyOnlyDictionaryAsTerm()
    {
        using var temp = new TemporaryDictionaryRoot();
        var zipPath = temp.CreateDictionaryZip("FreqOnly", includeTerms: false, includeFrequency: true, includePitch: false);
        var importService = new DictionaryImportService(
            NullLogger<DictionaryImportService>.Instance,
            new RecordingLookupService(),
            temp.DictionaryRoot);

        var result = await importService.ImportAsync(zipPath);

        result.Success.Should().BeTrue(string.Join("\n", result.Errors));
        Directory.Exists(Path.Combine(temp.DictionaryRoot, "Term", "FreqOnly")).Should().BeFalse();
        Directory.Exists(Path.Combine(temp.DictionaryRoot, "Frequency", "FreqOnly")).Should().BeTrue();
        Directory.Exists(Path.Combine(temp.DictionaryRoot, "Pitch", "FreqOnly")).Should().BeFalse();
        (await importService.GetInstalledDictionariesAsync(DictionaryType.Term)).Should().BeEmpty();
        (await importService.GetInstalledDictionariesAsync(DictionaryType.Frequency)).Select(d => d.Name).Should().Equal("FreqOnly");
    }

    [Fact]
    public async Task DictionaryImportService_DeletesOnlySelectedDictionaryType()
    {
        using var temp = new TemporaryDictionaryRoot();
        temp.WriteNativeDictionaryDirectory(Path.Combine(temp.DictionaryRoot, "Term"), "Mixed");
        temp.WriteNativeDictionaryDirectory(Path.Combine(temp.DictionaryRoot, "Frequency"), "Mixed");
        temp.WriteNativeDictionaryDirectory(Path.Combine(temp.DictionaryRoot, "Pitch"), "Mixed");
        DictionaryConfigurationStore.Save(temp.DictionaryRoot, new DictionaryConfig(
            [new DictionaryConfigEntry("Mixed", true, 0)],
            [new DictionaryConfigEntry("Mixed", true, 0)],
            [new DictionaryConfigEntry("Mixed", true, 0)]));
        var lookupService = new RecordingLookupService();
        var importService = new DictionaryImportService(
            NullLogger<DictionaryImportService>.Instance,
            lookupService,
            temp.DictionaryRoot);

        var deleted = await importService.DeleteAsync(DictionaryType.Frequency, "Mixed");

        deleted.Should().BeTrue();
        Directory.Exists(Path.Combine(temp.DictionaryRoot, "Term", "Mixed")).Should().BeTrue();
        Directory.Exists(Path.Combine(temp.DictionaryRoot, "Frequency", "Mixed")).Should().BeFalse();
        Directory.Exists(Path.Combine(temp.DictionaryRoot, "Pitch", "Mixed")).Should().BeTrue();
        (await importService.GetInstalledDictionariesAsync(DictionaryType.Term)).Select(d => d.Name).Should().Equal("Mixed");
        (await importService.GetInstalledDictionariesAsync(DictionaryType.Frequency)).Should().BeEmpty();
        (await importService.GetInstalledDictionariesAsync(DictionaryType.Pitch)).Select(d => d.Name).Should().Equal("Mixed");
        lookupService.RebuildCount.Should().Be(1);
    }

    [Fact]
    public async Task LookupAsync_FindsDeinflectedVerbForms()
    {
        using var temp = new TemporaryDictionaryRoot();
        temp.WriteTermDictionary("TestDict",
            new object[] { "読む", "よむ", "", "v5", 10, new[] { "to read" } });

        using var service = new DictionaryLookupService(
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

        using var service = new DictionaryLookupService(
            NullLogger<DictionaryLookupService>.Instance,
            temp.DictionaryRoot);

        var results = await service.LookupAsync("読んだ");

        results.Select(r => r.Term.Expression).Should().Contain("読む");
        results.Select(r => r.Term.Expression).Should().NotContain("読み");
    }

    [Fact]
    public async Task GetMediaFileAsync_LoadsYomitanImagesOutsideMediaFolder()
    {
        using var temp = new TemporaryDictionaryRoot();
        temp.WriteTermDictionary("ImageDict",
            new object[] { "絵", "え", "", "n", 10, new object[] { new { type = "image", path = "images/pic.png" } } });
        temp.WriteFile("ImageDict", "images/pic.png", [1, 2, 3, 4]);

        using var service = new DictionaryLookupService(
            NullLogger<DictionaryLookupService>.Instance,
            temp.DictionaryRoot);
        await service.RebuildQueryAsync();

        var bytes = await service.GetMediaFileAsync("ImageDict", "images/pic.png");

        bytes.Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task LookupAsync_AttachesFrequencyAndPitchMetadataLikeHoshiDicts()
    {
        using var temp = new TemporaryDictionaryRoot();
        temp.WriteTermDictionary("TermDict",
            new object[] { "星", "ほし", "", "n", 10, new[] { "star" } });
        temp.WriteMetadataDictionary("FreqDict", "term_meta_bank_1.json",
            new object[] { "星", "freq", new { reading = "ほし", value = 42, displayValue = "42" } },
            new object[] { "星", "freq", new { reading = "ほし", frequency = new { value = 7, displayValue = "Jiten 7" } } });
        temp.WriteMetadataDictionary("PitchDict", "term_meta_bank_1.json",
            new object[] { "星", "pitch", new { reading = "ほし", pitches = new object[] { new { position = 1 } } } });

        using var service = new DictionaryLookupService(
            NullLogger<DictionaryLookupService>.Instance,
            temp.DictionaryRoot);

        var result = (await service.LookupAsync("星")).Single();

        result.Term.Frequencies.Should().ContainSingle(f => f.DictName == "FreqDict");
        result.Term.Frequencies[0].Frequencies.Select(f => f.Value).Should().Equal(42, 7);
        result.Term.Frequencies[0].Frequencies.Select(f => f.DisplayValue).Should().Equal("42", "Jiten 7");
        result.Term.Pitches.Should().ContainSingle(p => p.DictName == "PitchDict");
        result.Term.Pitches[0].PitchPositions.Should().Equal(1);
    }

    [Fact]
    public async Task LookupAsync_EnglishProfile_DeinflectsEnglishWords()
    {
        using var temp = new TemporaryDictionaryRoot();
        temp.WriteTermDictionary("EnglishDict",
            new object[] { "read", "", "", "v", 10, new[] { "to interpret written text" } });

        using var service = new DictionaryLookupService(
            NullLogger<DictionaryLookupService>.Instance,
            temp.DictionaryRoot);
        await service.SetActiveLanguageForTestsAsync("en");

        var results = await service.LookupAsync("reading");

        results.Should().Contain(result => result.Term.Expression == "read");
    }

    [Fact]
    public async Task LookupAsync_MapsEnglishIpaTranscriptions()
    {
        using var temp = new TemporaryDictionaryRoot();
        temp.WriteTermDictionary("EnglishDict",
            new object[] { "tomato", "", "", "n", 10, new[] { "a fruit" } });
        temp.WriteMetadataDictionary("IpaDict", "term_meta_bank_1.json",
            new object[]
            {
                "tomato",
                "ipa",
                new { reading = "", transcriptions = new object[] { new { ipa = "/təˈmeɪtoʊ/" } } },
            });

        using var service = new DictionaryLookupService(
            NullLogger<DictionaryLookupService>.Instance,
            temp.DictionaryRoot);
        await service.SetActiveLanguageForTestsAsync("en");

        var result = (await service.LookupAsync("tomato")).Single();

        result.Term.Pitches.Should().ContainSingle().Which.Transcriptions.Should().Contain("/təˈmeɪtoʊ/");
    }

    [Fact]
    public async Task LookupAsync_MapsCompatibilityImportSourceNameToDisplayTitle()
    {
        using var temp = new TemporaryDictionaryRoot();
        temp.WriteTermDictionary("niratan-import-abc123",
            new object[] { "学校", "がっこう", "", "n", 10, new[] { "school" } });
        var importedDir = Path.Combine(temp.DictionaryRoot, "niratan-import-abc123");
        File.WriteAllText(Path.Combine(importedDir, "index.json"), """
        {
          "title": "明鏡国語辞典 第三版",
          "format": 3,
          "revision": "test"
        }
        """);

        using var service = new DictionaryLookupService(
            NullLogger<DictionaryLookupService>.Instance,
            temp.DictionaryRoot);

        var result = (await service.LookupAsync("学校")).Single();

        result.Term.Glossaries.Select(g => g.DictName)
            .Should()
            .Contain("明鏡国語辞典 第三版")
            .And
            .NotContain("niratan-import-abc123");
    }

    [Fact]
    public async Task GetStylesAsync_CachesNativeStylesUntilRebuild()
    {
        using var temp = new TemporaryDictionaryRoot();
        temp.WriteTermDictionary("StyleDict",
            ["星", "ほし", "", "n", 10, new[] { "star" }]);
        var logger = new RecordingLogger<DictionaryLookupService>();
        using var service = new DictionaryLookupService(logger, temp.DictionaryRoot);

        await service.GetStylesAsync();
        await service.GetStylesAsync();

        logger.CountContaining("styles native+deserialize completed").Should().Be(1);

        await service.RebuildQueryAsync();
        await service.GetStylesAsync();

        logger.CountContaining("styles native+deserialize completed").Should().Be(2);
    }

    [Fact]
    public void NativeLookup_BasicImportAndLookup_ReturnsResults()
    {
        using var temp = new TemporaryDictionaryRoot();
        temp.WriteTermDictionary("TestDict",
            ["読む", "よむ", "", "v5", 10, new[] { "to read" }],
            ["星", "ほし", "", "n", 10, new[] { "star" }]);

        var dictPath = Path.Combine(temp.DictionaryRoot, "TestDict");
        File.Exists(Path.Combine(dictPath, ".hoshidicts_2")).Should().BeTrue();

        var session = IntPtr.Zero;
        try
        {
            session = HoshiDictsNative.hoshi_session_create();
            session.Should().NotBe(IntPtr.Zero);

            HoshiDictsNative.HoshiSessionRebuild(
                session,
                new[] { dictPath },
                Array.Empty<string>(),
                Array.Empty<string>());

            var jsonPtr = HoshiDictsNative.hoshi_lookup(session, "星", 16, 16);
            var json = HoshiDictsNative.ReadStringAndFree(jsonPtr);
            json.Should().NotBeNullOrEmpty();
            json.Should().NotBe("[]");
            var results = HoshiDictsNative.DeserializeLookupResults(json);
            results.Should().NotBeEmpty();
        }
        finally
        {
            if (session != IntPtr.Zero)
                HoshiDictsNative.hoshi_session_destroy(session);
        }
    }

    [Fact]
    public void NativeLookup_AsciiWordsOnly_Succeeds()
    {
        using var temp = new TemporaryDictionaryRoot();
        temp.WriteTermDictionary("TestDict",
            ["test", "test", "", "n", 10, new[] { "a test word" }],
            ["hello", "hello", "", "n", 10, new[] { "a greeting" }]);

        var dictPath = Path.Combine(temp.DictionaryRoot, "TestDict");
        var session = IntPtr.Zero;
        try
        {
            session = HoshiDictsNative.hoshi_session_create();
            HoshiDictsNative.HoshiSessionRebuild(
                session,
                new[] { dictPath },
                Array.Empty<string>(),
                Array.Empty<string>());

            var jsonPtr = HoshiDictsNative.hoshi_lookup(session, "test", 16, 16);
            var json = HoshiDictsNative.ReadStringAndFree(jsonPtr);
            var results = HoshiDictsNative.DeserializeLookupResults(json);
            results.Should().NotBeEmpty();
            results[0].Term.Expression.Should().Be("test");
        }
        finally
        {
            if (session != IntPtr.Zero)
                HoshiDictsNative.hoshi_session_destroy(session);
        }
    }

    private sealed class TemporaryDictionaryRoot : IDisposable
    {
        internal readonly string _root = Path.Combine(Path.GetTempPath(), "NiratanTests", Guid.NewGuid().ToString("N"));
        private readonly string _inputRoot;

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(
                System.Text.Unicode.UnicodeRanges.All),
        };

        public string DictionaryRoot => Path.Combine(_root, "dictionaries");
        public string InputRoot => _inputRoot;

        public TemporaryDictionaryRoot()
        {
            _inputRoot = Path.Combine(_root, "input");
            Directory.CreateDirectory(DictionaryRoot);
            Directory.CreateDirectory(_inputRoot);
        }

        public string WriteTermDictionary(string name, params object[][] terms)
        {
            var inputDir = Path.Combine(_inputRoot, name);
            Directory.CreateDirectory(inputDir);
            File.WriteAllText(Path.Combine(inputDir, "index.json"), $$"""
            {
              "title": "{{name}}",
              "format": 3,
              "revision": "test"
            }
            """);
            var termBankJson = JsonSerializer.Serialize(terms, SerializerOptions);
            File.WriteAllText(Path.Combine(inputDir, "term_bank_1.json"), termBankJson);
            return Import(name);
        }

        public string WriteFile(string dictionaryName, string relativePath, byte[] bytes)
        {
            var inputDir = Path.Combine(_inputRoot, dictionaryName);
            var filePath = Path.Combine(inputDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllBytes(filePath, bytes);
            return Import(dictionaryName);
        }

        public string WriteMetadataDictionary(string name, string bankFileName, params object[][] rows)
        {
            var inputDir = Path.Combine(_inputRoot, name);
            Directory.CreateDirectory(inputDir);
            File.WriteAllText(Path.Combine(inputDir, "index.json"), $$"""
            {
              "title": "{{name}}",
              "format": 3,
              "revision": "test"
            }
            """);
            var bankPath = Path.Combine(inputDir, bankFileName);
            File.WriteAllText(bankPath, JsonSerializer.Serialize(rows, SerializerOptions));
            var json = Import(name);
            var kind = DictionaryImportService.DetectBankKind(bankFileName, bankPath);
            var type = kind == DictionaryImportService.DictionaryBankKind.Pitch
                ? DictionaryType.Pitch
                : DictionaryType.Frequency;
            MoveImportedDictionaryToType(name, type);
            return json;
        }

        public string WriteNativeDictionaryDirectory(string parentDir, string name)
        {
            var dictDir = Path.Combine(parentDir, name);
            Directory.CreateDirectory(dictDir);
            File.WriteAllText(Path.Combine(dictDir, "index.json"), $$"""
            {
              "title": "{{name}}",
              "format": 3,
              "revision": "test"
            }
            """);
            File.WriteAllText(Path.Combine(dictDir, ".hoshidicts_1"), "");
            return dictDir;
        }

        public string CreateDictionaryZip(
            string name,
            bool includeTerms,
            bool includeFrequency,
            bool includePitch)
        {
            var inputDir = Path.Combine(_inputRoot, name);
            if (Directory.Exists(inputDir))
                Directory.Delete(inputDir, recursive: true);
            Directory.CreateDirectory(inputDir);
            File.WriteAllText(Path.Combine(inputDir, "index.json"), $$"""
            {
              "title": "{{name}}",
              "format": 3,
              "revision": "test"
            }
            """);

            if (includeTerms)
            {
                File.WriteAllText(
                    Path.Combine(inputDir, "term_bank_1.json"),
                    JsonSerializer.Serialize(new object[][]
                    {
                        ["星", "ほし", "", "n", 10, new[] { "star" }],
                    }, SerializerOptions));
            }

            var metadataRows = new List<object[]>();
            if (includeFrequency)
                metadataRows.Add(["星", "freq", new { reading = "ほし", value = 42, displayValue = "42" }]);
            if (includePitch)
                metadataRows.Add(["星", "pitch", new { reading = "ほし", pitches = new object[] { new { position = 1 } } }]);
            if (metadataRows.Count > 0)
            {
                File.WriteAllText(
                    Path.Combine(inputDir, "term_meta_bank_1.json"),
                    JsonSerializer.Serialize(metadataRows, SerializerOptions));
            }

            var zipPath = Path.Combine(_inputRoot, $"{name}.zip");
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            ZipFile.CreateFromDirectory(inputDir, zipPath, CompressionLevel.Optimal, false);
            return zipPath;
        }

        public string CreateCustomDictionaryZip(string title, IReadOnlyList<(string Name, string Content)> entries)
        {
            var inputDir = Path.Combine(_inputRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(inputDir);
            File.WriteAllText(Path.Combine(inputDir, "index.json"), $$"""
            {
              "title": "{{title}}",
              "format": 3,
              "revision": "test"
            }
            """);

            foreach (var (name, content) in entries)
            {
                var path = Path.Combine(inputDir, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }

            var zipPath = Path.Combine(_inputRoot, $"{Guid.NewGuid():N}.zip");
            ZipFile.CreateFromDirectory(inputDir, zipPath, CompressionLevel.Optimal, false);
            return zipPath;
        }

        private void MoveImportedDictionaryToType(string name, DictionaryType type)
        {
            var sourceDir = Path.Combine(DictionaryRoot, name);
            var typeDir = Path.Combine(DictionaryRoot, type.ToString());
            var targetDir = Path.Combine(typeDir, name);
            Directory.CreateDirectory(typeDir);
            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, recursive: true);
            Directory.Move(sourceDir, targetDir);
        }

        internal string Import(string dictName)
        {
            var inputDir = Path.Combine(_inputRoot, dictName);
            var zipPath = Path.Combine(_inputRoot, $"{dictName}.zip");
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(inputDir, zipPath, CompressionLevel.Optimal, false);

            var jsonPtr = HoshiDictsNative.hoshi_import(zipPath, DictionaryRoot);
            var json = HoshiDictsNative.ReadStringAndFree(jsonPtr);
            if (string.IsNullOrEmpty(json) || json.Contains("\"success\":false"))
                throw new InvalidOperationException($"Import failed for {dictName}: {json}");
            return json;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _messages.Enqueue(formatter(state, exception));
        }

        public int CountContaining(string text) =>
            _messages.Count(message => message.Contains(text, StringComparison.Ordinal));
    }

    private sealed class RecordingLookupService : IDictionaryLookupService
    {
        public int RebuildCount { get; private set; }

        public Task<List<DictionaryLookupResult>> LookupAsync(string text, int maxResults = 16, int scanLength = 16, string? traceId = null) =>
            Task.FromResult(new List<DictionaryLookupResult>());

        public Task<List<DictionaryStyle>> GetStylesAsync() =>
            Task.FromResult(new List<DictionaryStyle>());

        public Task<byte[]?> GetMediaFileAsync(string dictName, string mediaPath) =>
            Task.FromResult<byte[]?>(null);

        public Task RebuildQueryAsync()
        {
            RebuildCount++;
            return Task.CompletedTask;
        }

        public Task SetActiveLanguageAsync(string languageId) => Task.CompletedTask;
    }
}
