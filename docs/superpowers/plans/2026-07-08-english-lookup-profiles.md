# English Lookup Profiles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Niratan-aligned Profiles and true English dictionary lookup to Hoshi Windows.

**Architecture:** Profiles are a first-class service layer that resolves runtime contexts for Global, EPUB books, and Video. Physical dictionaries and AnkiConnect transport stay global, while dictionary enable/order/display settings, Reader appearance, and Anki mining mappings are loaded and saved per resolved Profile. Lookup passes the resolved language into WebView selection and the hoshidicts native query.

**Tech Stack:** WinUI 3, Windows App SDK, CommunityToolkit.Mvvm, C#/.NET 10, SQLite/Dapper, WebView2, hoshidicts C API, xUnit v3, FluentAssertions.

## Global Constraints

- Target platform: Windows 10+ x64.
- Build command: `dotnet build -p:Platform=x64`.
- Test command: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64`.
- Do not modify `native/hoshidicts/` source files directly.
- Dictionary logic stays in C#/native services, not WebView JavaScript.
- ViewModel must not directly access SQLite.
- Reader rendering stays WebView2 + CSS multi-column pagination.
- Bridge messages remain narrow, versioned, and validated.
- AnkiConnect transport is global; deck/model/field mappings/tags/duplicate/media/glossary mining choices are profile-owned.
- Dictionary physical storage is global; enable/order/display/collapsed state is profile-owned.

---

## File Structure

- Create `Hoshi/Models/Profiles/ContentLanguageProfile.cs`: supported language IDs (`ja`, `en`), normalization, display-unit conversion.
- Create `Hoshi/Models/Profiles/HoshiProfile.cs`: profile model, index model, context model, resolution result.
- Create `Hoshi/Services/Profiles/IProfileService.cs`: profile lifecycle, resolution, activation, profile-owned path APIs.
- Create `Hoshi/Services/Profiles/ProfileService.cs`: JSON-backed profile index/store and migration from global settings.
- Create `Hoshi/Services/Profiles/IProfileActivationService.cs`: applies resolved profile to settings, dictionaries, Anki, Reader, and lookup.
- Create `Hoshi/Services/Profiles/ProfileActivationService.cs`: runtime activation coordinator.
- Create `Hoshi/ViewModels/Pages/ProfileSettingsPageViewModel.cs`: settings page ViewModel for active profile, language defaults, create/rename/delete.
- Create `Hoshi/Views/Pages/ProfileSettingsPage.xaml` and `.xaml.cs`: WinUI page using existing settings patterns.
- Modify `Hoshi/Models/Settings/AppSettings.cs`: add profile/global activation fields and move only global Anki transport into global settings.
- Modify `Hoshi/Models/Settings/AnkiSettings.cs`: support transport/global clone and profile-owned mining clone.
- Modify `Hoshi/Services/Settings/*SettingsService.cs`: allow profile activation to replace current snapshots without bypassing change notifications.
- Modify `Hoshi/Services/Dictionary/*`: make config path/profile language explicit and preserve physical storage globally.
- Modify `native/hoshidicts_c_api/*`: add language-aware rebuild API and serialize pitch transcriptions.
- Modify `Hoshi/Services/Dictionary/HoshiDictsNative.cs`: P/Invoke new language-aware rebuild with backward-compatible wrapper.
- Modify `.gitmodules` and `native/hoshidicts` submodule pointer: point to the multilingual hoshidicts fork/revision used by Niratan, without editing submodule source files.
- Modify `Hoshi/Web/NovelReader/selection.js`: add Niratan English word/phrase boundary behavior.
- Modify `Hoshi/Views/Pages/NovelReaderPage.xaml.cs`: activate book profile context and inject content language into selection JS.
- Modify `Hoshi/Views/Video/*` and `Hoshi/ViewModels/Pages/VideoPlayerViewModel.cs`: activate remembered Video profile and pass context into lookup.
- Modify `Hoshi/Views/Pages/SettingsPage.xaml/.cs` and `Hoshi/App.xaml.cs`: enable Profiles page and register services/ViewModels.
- Modify `Hoshi/Services/Storage/Migrations/*`, `DataService.cs`, `NovelBook.cs`, `VideoItem.cs`: persist nullable profile override IDs.
- Add focused tests under `Hoshi.Tests/Services/Profiles`, `Hoshi.Tests/Services/Dictionary`, `Hoshi.Tests/Services/Storage`, and web asset tests.

---

### Task 1: Profile Domain, Storage, and Resolution

**Files:**
- Create: `Hoshi/Models/Profiles/ContentLanguageProfile.cs`
- Create: `Hoshi/Models/Profiles/HoshiProfile.cs`
- Create: `Hoshi/Services/Profiles/IProfileService.cs`
- Create: `Hoshi/Services/Profiles/ProfileService.cs`
- Test: `Hoshi.Tests/Services/Profiles/ProfileServiceTests.cs`

**Interfaces:**
- Produces: `ContentLanguageProfile.Normalize(string?)`, `DisplayUnitsFromRawCharacters(int)`, `RawCharactersFromDisplayUnits(int)`.
- Produces: `ProfileContext.Global()`, `ProfileContext.Book(string? profileId, string? bookLanguage)`, `ProfileContext.Video(string? profileId)`.
- Produces: `IProfileService.Resolve(ProfileContext context): ProfileResolution`.
- Produces: `IProfileService.GetProfileDirectory(string profileId): string`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task ResolveBook_UsesExplicitThenLanguagePrimaryThenGlobalFallback()
{
    using var temp = new TemporaryProfileRoot();
    var service = await ProfileService.CreateForTestsAsync(temp.Root);
    var english = await service.CreateProfileAsync("English EPUB", "en");
    await service.SetPrimaryProfileForLanguageAsync("en", english.Id);
    await service.SetGlobalActiveProfileAsync("default-ja-video");

    service.Resolve(ProfileContext.Book(english.Id, "ja-JP")).Profile.Id.Should().Be(english.Id);
    service.Resolve(ProfileContext.Book(null, "en-US")).Profile.Id.Should().Be(english.Id);
    service.Resolve(ProfileContext.Book(null, "fr")).Profile.Id.Should().Be("default-ja-video");
}

[Fact]
public void EnglishDisplayUnits_AreApproximateWords()
{
    ContentLanguageProfile.English.DisplayUnitsFromRawCharacters(11).Should().Be(3);
    ContentLanguageProfile.English.RawCharactersFromDisplayUnits(3).Should().Be(15);
}
```

- [ ] **Step 2: Run test to verify RED**

Run: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ProfileServiceTests"`

Expected: FAIL because `ProfileServiceTests`, `ProfileService`, and profile models do not exist.

- [ ] **Step 3: Implement minimal profile model/store**

Create immutable language IDs `ja` and `en`, bootstrap built-ins `default-ja` and `default-ja-video`, validate safe IDs, write `profiles.json` under a root passed by tests or app data, and implement the resolver precedence.

- [ ] **Step 4: Run test to verify GREEN**

Run: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ProfileServiceTests"`

Expected: PASS.

### Task 2: Profile-Owned Settings Snapshots

**Files:**
- Modify: `Hoshi/Services/Settings/ISettingsService.cs`
- Modify: `Hoshi/Services/Settings/SettingsService.cs`
- Modify: `Hoshi/Services/Settings/IReaderSettingsService.cs`
- Modify: `Hoshi/Services/Settings/ReaderSettingsService.cs`
- Modify: `Hoshi/Models/Settings/AnkiSettings.cs`
- Create: `Hoshi/Services/Profiles/ProfileSettingsStore.cs`
- Test: `Hoshi.Tests/Services/Profiles/ProfileSettingsStoreTests.cs`

**Interfaces:**
- Consumes: `IProfileService.GetProfileDirectory(profileId)`.
- Produces: `ProfileSettingsStore.ActivateAsync(profileId)` that persists the previous profile-owned snapshots and loads the target profile's dictionary display, reader settings, and Anki mining settings.
- Produces: `SettingsService.ReplaceCurrent(AppSettings settings)` and `ReaderSettingsService.ReplaceCurrent(ReaderSettings settings)` with change notifications.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task ActivateAsync_PersistsCurrentProfileAndLoadsTargetProfile()
{
    using var temp = new TemporaryProfileRoot();
    var settings = new RecordingSettingsService { Current = new AppSettings { DictionaryDisplaySettings = new DictionaryDisplaySettings(MaxResults: 7) } };
    var reader = new RecordingReaderSettingsService { Current = new ReaderSettings { FontSize = 22 } };
    var profiles = await ProfileService.CreateForTestsAsync(temp.Root);
    var english = await profiles.CreateProfileAsync("English", "en");
    var store = new ProfileSettingsStore(profiles, settings, reader);

    await store.ActivateAsync("default-ja");
    settings.Current.DictionaryDisplaySettings = new DictionaryDisplaySettings(MaxResults: 9);
    reader.Current.FontSize = 28;
    await store.ActivateAsync(english.Id);

    settings.Current.DictionaryDisplaySettings.MaxResults.Should().Be(16);
    reader.Current.FontSize.Should().Be(new ReaderSettings().FontSize);

    await store.ActivateAsync("default-ja");
    settings.Current.DictionaryDisplaySettings.MaxResults.Should().Be(9);
    reader.Current.FontSize.Should().Be(28);
}
```

- [ ] **Step 2: Run test to verify RED**

Run: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ProfileSettingsStoreTests"`

Expected: FAIL because `ProfileSettingsStore` and replace APIs do not exist.

- [ ] **Step 3: Implement snapshot store**

Save per-profile files named `dictionary-settings.json`, `reader-settings.json`, and `anki-settings.json`. Clone mutable nested collections when loading/saving. Leave `AnkiConnectUrl`, force sync, and discovered deck/note type lists in global settings.

- [ ] **Step 4: Run test to verify GREEN**

Run: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~ProfileSettingsStoreTests"`

Expected: PASS.

### Task 3: Profile-Owned Dictionary Configuration

**Files:**
- Modify: `Hoshi/Services/Dictionary/DictionaryConfigurationStore.cs`
- Modify: `Hoshi/Services/Dictionary/DictionaryImportService.cs`
- Modify: `Hoshi/Services/Dictionary/DictionaryLookupService.cs`
- Create: `Hoshi/Services/Dictionary/IDictionaryProfileContext.cs`
- Test: `Hoshi.Tests/Services/Dictionary/DictionaryProfileConfigurationTests.cs`

**Interfaces:**
- Consumes: active profile ID and language from `IProfileService`.
- Produces: per-profile dictionary config path while physical dictionaries remain under global `dictionaries/`.
- Produces: `DictionaryConfigurationStore.MergeWithInstalled(..., bool enableUnconfigured)`.
- Produces: delete cleanup across all profile configs.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public void NonDefaultProfile_DisablesUnconfiguredInstalledDictionaries()
{
    using var temp = new TemporaryDictionaryRoot();
    temp.WriteNativeDictionaryDirectory(Path.Combine(temp.DictionaryRoot, "Term"), "SharedDict");
    var config = DictionaryConfigurationStore.NormalizeForInstalled(
        DictionaryConfig.Empty,
        [DictionaryType.Term],
        type => ["SharedDict"],
        enableUnconfigured: false);

    DictionaryConfigurationStore.GetEntries(config, DictionaryType.Term)
        .Should().ContainSingle().Which.IsEnabled.Should().BeFalse();
}

[Fact]
public async Task DeleteAsync_RemovesDictionaryReferencesFromEveryProfile()
{
    using var temp = new TemporaryProfileAndDictionaryRoot();
    await temp.CreateProfileConfigAsync("default-ja", DictionaryType.Term, "SharedDict");
    await temp.CreateProfileConfigAsync("english", DictionaryType.Term, "SharedDict");

    await temp.ImportService.DeleteAsync(DictionaryType.Term, "SharedDict");

    temp.ReadProfileEntries("default-ja", DictionaryType.Term).Should().BeEmpty();
    temp.ReadProfileEntries("english", DictionaryType.Term).Should().BeEmpty();
}
```

- [ ] **Step 2: Run test to verify RED**

Run: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryProfileConfigurationTests"`

Expected: FAIL because profile-aware config APIs do not exist.

- [ ] **Step 3: Implement profile dictionary config**

Add overloads that take explicit config file paths or profile roots. Keep old global config as migration input for `default-ja`. For non-default profiles, merge installed dictionaries disabled by default.

- [ ] **Step 4: Run dictionary profile tests and existing dictionary tests**

Run: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Dictionary"`

Expected: PASS.

### Task 4: Language-Aware Native Lookup and English Results

**Files:**
- Modify: `.gitmodules`
- Update submodule pointer: `native/hoshidicts`
- Modify: `native/hoshidicts_c_api/hoshidicts_c_api.h`
- Modify: `native/hoshidicts_c_api/hoshidicts_c_api.cpp`
- Modify: `Hoshi/Services/Dictionary/HoshiDictsNative.cs`
- Modify: `Hoshi/Models/Dictionary/DictionaryLookupResult.cs`
- Test: `Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs`

**Interfaces:**
- Produces: `HoshiDictsNative.HoshiSessionRebuild(session, termPaths, freqPaths, pitchPaths, languageId)`.
- Produces: serialized `PitchEntry.Transcriptions`.
- Consumes: multilingual hoshidicts `language::get("ja"|"en")`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task LookupAsync_EnglishProfile_DeinflectsEnglishWords()
{
    using var temp = new TemporaryDictionaryRoot();
    temp.WriteTermDictionary("EnglishDict",
        new object[] { "read", "", "", "v", 10, new[] { "to interpret written text" } });
    var service = new DictionaryLookupService(
        NullLogger<DictionaryLookupService>.Instance,
        temp.DictionaryRoot);
    await service.SetActiveLanguageForTestsAsync("en");

    var results = await service.LookupAsync("reading");

    results.Should().Contain(r => r.Term.Expression == "read");
}

[Fact]
public async Task LookupAsync_MapsEnglishIpaTranscriptions()
{
    using var temp = new TemporaryDictionaryRoot();
    temp.WriteTermDictionary("EnglishDict",
        new object[] { "tomato", "", "", "n", 10, new[] { "a fruit" } });
    temp.WriteMetadataDictionary("IpaDict", "term_meta_bank_1.json",
        new object[] { "tomato", "pitch", new { reading = "", transcriptions = new[] { "/təˈmeɪtoʊ/" } } });
    var service = new DictionaryLookupService(
        NullLogger<DictionaryLookupService>.Instance,
        temp.DictionaryRoot);
    await service.SetActiveLanguageForTestsAsync("en");

    var result = (await service.LookupAsync("tomato")).Single();

    result.Term.Pitches.Should().ContainSingle().Which.Transcriptions.Should().Contain("/təˈmeɪtoʊ/");
}
```

- [ ] **Step 2: Run test to verify RED**

Run: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~LookupAsync_EnglishProfile|FullyQualifiedName~LookupAsync_MapsEnglishIpaTranscriptions"`

Expected: FAIL because current native lookup is Japanese-only and `Transcriptions` does not exist.

- [ ] **Step 3: Implement native bridge**

Point `native/hoshidicts` to `https://github.com/HuangAntimony/hoshidicts.git` at `c60de40bf5f000a28bd6d309383761cd881b196b`. Add `hoshi_session_rebuild_with_language(...)`; keep existing `hoshi_session_rebuild(...)` as a Japanese-compatible wrapper. Serialize lookup trace candidates into the existing C# trace shape using the first/sorted trace candidate, and serialize pitch transcriptions.

- [ ] **Step 4: Rebuild native and run tests**

Run: `.\build-native.ps1`

Run: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Dictionary"`

Expected: PASS.

### Task 5: Reader, Video, and Global Lookup Context Activation

**Files:**
- Modify: `Hoshi/Web/NovelReader/selection.js`
- Modify: `Hoshi/Views/Pages/NovelReaderPage.xaml.cs`
- Modify: `Hoshi/Views/Video/VideoPlayerWindow.SubtitleOverlay.cs`
- Modify: `Hoshi/ViewModels/Pages/VideoPlayerViewModel.cs`
- Modify: `Hoshi/Services/Dictionary/DictionaryPopupRequestService.cs`
- Modify: `Hoshi/Views/Dictionary/DictionaryPopupOverlay.cs`
- Test: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`
- Test: `Hoshi.Tests/Services/Video/VideoSubtitleLookupTextExtractorTests.cs`

**Interfaces:**
- Consumes: `ProfileContext` at lookup call sites.
- Produces: JS global `window.hoshiSelection.language = "ja" | "en"`.
- Produces: English hit-test behavior that starts at the beginning of the English word and treats apostrophes/hyphens as internal only between word characters.

- [ ] **Step 1: Write failing asset tests**

```csharp
[Fact]
public void SelectionJs_ContainsEnglishWordBoundaryMode()
{
    var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "NovelReader", "selection.js"));
    code.Should().Contain("window.hoshiSelection.language");
    code.Should().Contain("findEnglishWordStart");
    code.Should().Contain("EnglishWordInternalDelimiters");
}

[Fact]
public void NovelReader_InjectsResolvedContentLanguageBeforeSelectionScript()
{
    var code = File.ReadAllText(Path.Combine(ProjectRoot, "Hoshi", "Views", "Pages", "NovelReaderPage.xaml.cs"));
    code.Should().Contain("__hoshiLookupSettings");
    code.Should().Contain("hoshiSelection.language");
    code.Should().Contain("ProfileContext.Book");
}
```

- [ ] **Step 2: Run test to verify RED**

Run: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "SelectionJs_ContainsEnglishWordBoundaryMode|NovelReader_InjectsResolvedContentLanguageBeforeSelectionScript"`

Expected: FAIL because English selection and profile context injection are absent.

- [ ] **Step 3: Implement activation and selection**

Activate book context when opening a novel, inject resolved language on DOM ready, pass context through popup request creation, and activate video/global contexts before lookup. Preserve existing popup non-blocking contentReady behavior.

- [ ] **Step 4: Run reader/video/dictionary tests**

Run: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~NovelReaderWebAsset|FullyQualifiedName~VideoSubtitle|FullyQualifiedName~Dictionary"`

Expected: PASS.

### Task 6: Persistence for Book and Video Profile Overrides

**Files:**
- Modify: `Hoshi/Models/NovelBook.cs`
- Modify: `Hoshi/Models/VideoItem.cs`
- Modify: `Hoshi/Services/Storage/DataService.cs`
- Create: `Hoshi/Services/Storage/Migrations/Migration_010.cs`
- Modify: `Hoshi/Services/Storage/DatabaseMigrator.cs`
- Test: `Hoshi.Tests/Services/Storage/NovelDataServiceTests.cs`
- Test: `Hoshi.Tests/Services/Storage/VideoDataServiceTests.cs`

**Interfaces:**
- Produces: nullable `ProfileId` columns on `NovelBooks` and `VideoItems`.
- Produces: `NovelBook.ProfileId` and `VideoItem.ProfileId`.

- [ ] **Step 1: Write failing migration tests**

```csharp
[Fact]
public async Task Migration010_AddsProfileOverrideColumns()
{
    await using var connection = new SqliteConnection("Data Source=:memory:");
    await connection.OpenAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    await InvokeMigrationAsync("Migration_003", connection, transaction);
    await InvokeMigrationAsync("Migration_008", connection, transaction);
    await InvokeMigrationAsync("Migration_010", connection, transaction);
    await transaction.CommitAsync();

    var novelHasProfile = await HasColumnAsync(connection, "NovelBooks", "ProfileId");
    var videoHasProfile = await HasColumnAsync(connection, "VideoItems", "ProfileId");

    novelHasProfile.Should().BeTrue();
    videoHasProfile.Should().BeTrue();
}
```

- [ ] **Step 2: Run test to verify RED**

Run: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "Migration010_AddsProfileOverrideColumns"`

Expected: FAIL because migration 010 does not exist.

- [ ] **Step 3: Implement migration and data mappings**

Add nullable columns with idempotent `AddColumnIfMissingAsync`, update selects/inserts/upserts, and keep existing rows null.

- [ ] **Step 4: Run storage tests**

Run: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Storage"`

Expected: PASS.

### Task 7: Profiles Settings Page and DI

**Files:**
- Modify: `Hoshi/App.xaml.cs`
- Modify: `Hoshi/Views/Pages/SettingsPage.xaml`
- Create: `Hoshi/Views/Pages/ProfileSettingsPage.xaml`
- Create: `Hoshi/Views/Pages/ProfileSettingsPage.xaml.cs`
- Create: `Hoshi/ViewModels/Pages/ProfileSettingsPageViewModel.cs`
- Modify: `Hoshi/Views/Pages/AnkiSettingsPage.xaml/.cs`
- Modify: `Hoshi/ViewModels/Pages/AnkiSettingsPageViewModel.cs`
- Test: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Produces: enabled Settings > Profiles navigation item.
- Produces: active profile selector, language default selector, create/rename/delete profile commands.
- Consumes: `IProfileService` and `IProfileActivationService`.

- [ ] **Step 1: Write failing UI asset tests**

```csharp
[Fact]
public void SettingsPage_EnablesProfilesNavigation()
{
    var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Hoshi", "Views", "Pages", "SettingsPage.xaml"));
    xaml.Should().Contain("Tag=\"Hoshi.Views.Pages.ProfileSettingsPage\"");
    xaml.Should().NotContain("SettingsProfilesNavItem\"\r\n                                x:Uid=\"SettingsProfilesNavItem\"\r\n                                AutomationProperties.AutomationId=\"SettingsProfilesNavItem\"\r\n                                Content=\"Profiles\"\r\n                                IsEnabled=\"False\"");
}
```

- [ ] **Step 2: Run test to verify RED**

Run: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "SettingsPage_EnablesProfilesNavigation"`

Expected: FAIL because Profiles nav is disabled.

- [ ] **Step 3: Implement page and service registration**

Use existing settings page patterns, `ObservableCollection`, and `RelayCommand`. Do not put business logic in code-behind; code-behind only initializes the ViewModel and forwards navigation lifecycle.

- [ ] **Step 4: Build and run settings tests**

Run: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Settings|FullyQualifiedName~Profile"`

Expected: PASS.

### Task 8: Final Verification

**Files:**
- Modify docs if behavior needs architecture/verification notes:
  - `docs/ARCHITECTURE.md`
  - `docs/VERIFICATION.md`
  - `docs/CHANGELOG.md`

- [ ] **Step 1: Run full build**

Run: `dotnet build -p:Platform=x64`

Expected: PASS.

- [ ] **Step 2: Run full tests**

Run: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64`

Expected: PASS.

- [ ] **Step 3: Run app launch verification**

Run: `.\build-and-run.ps1`

Expected: Hoshi opens a responsive WinUI window. Leave the verified app instance running unless it blocks further work.

- [ ] **Step 4: Dictionary manual verification**

Run: `dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Dictionary"`

Expected: PASS. Then verify in-app Japanese lookup still works, English profile lookup uses English dictionaries, Shift hover does not block, nested popup lookup works, and light/dark popup rendering remains readable.

---

## Self-Review

- Spec coverage: full Niratan alignment is covered by profile resolution, settings isolation, dictionary config isolation, native language lookup, Reader/Video/Global activation, persistence, and Settings UI tasks.
- Placeholder scan: no `TBD`, `TODO`, or deferred implementation steps are present.
- Type consistency: profile context, profile resolution, profile store, dictionary language rebuild, and settings replacement interfaces are named consistently across tasks.
