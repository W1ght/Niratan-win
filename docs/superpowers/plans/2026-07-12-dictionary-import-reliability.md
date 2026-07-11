# Dictionary Import Reliability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Hoshi reliably replace same-title dictionaries, retry observed Windows path-conversion failures, and give large valid Yomitan archives a safe size-aware native import timeout.

**Architecture:** Keep the existing C# staging flow and `hoshidicts` backend. Add a focused filesystem transaction helper for prepared copies, stable title-based replacement, backup, and rollback; extend the C API wrapper with a timeout-aware export whose worker state is heap-owned; keep the existing lookup-safe compatibility ZIP behavior.

**Tech Stack:** C#/.NET 10, WinUI 3, xUnit v3, FluentAssertions, C++23, CMake/Ninja, existing `hoshidicts_c_api`.

## Global Constraints

- Do not modify any file under `native/hoshidicts/`.
- Build and test x64 only; do not build ARM64 by default.
- Dictionary IO and native work stay off the UI thread and behind the existing import lock.
- Compatibility ZIPs keep only `index.json`, `styles.css`, numbered `term_bank_*`, `term_meta_bank_*`, and `tag_bank_*` files.
- Native import counts remain the only authority for Term/Frequency/Pitch classification.
- Existing profile order and enabled references remain stable when a same-title dictionary is replaced.
- Do not change the settings UI, lookup semantics, or WebView2 bridge.

---

### Task 1: Specify managed timeout and retry classification

**Files:**
- Modify: `Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs`
- Modify: `Hoshi/Services/Dictionary/DictionaryImportService.cs`

**Interfaces:**
- Produces: `internal static int GetNativeImportTimeoutSeconds(long zipSizeBytes)`
- Produces: `internal static bool IsCompatibilityRetryCandidate(NativeImportResultJson result)`
- Consumes: `NativeImportResultJson`

- [ ] **Step 1: Add failing retry-classification tests**

Add tests that construct failed `NativeImportResultJson` values directly:

```csharp
[Theory]
[InlineData("C++ exception: filesystem error: in __char_to_wide: Illegal byte sequence")]
[InlineData("filesystem error: character conversion failed")]
public void DictionaryImportService_RetriesObservedWindowsCharacterConversionFailures(string error)
{
    DictionaryImportService.IsCompatibilityRetryCandidate(
            new NativeImportResultJson(false, Errors: [error]))
        .Should().BeTrue();
}

[Theory]
[InlineData("could not find index.json")]
[InlineData("failed to parse index.json")]
[InlineData("empty dictionary")]
public void DictionaryImportService_DoesNotRetryInvalidDictionaryFormats(string error)
{
    DictionaryImportService.IsCompatibilityRetryCandidate(
            new NativeImportResultJson(false, Errors: [error]))
        .Should().BeFalse();
}
```

- [ ] **Step 2: Add failing timeout-boundary tests**

```csharp
[Theory]
[InlineData(32L * 1024 * 1024, 5 * 60)]
[InlineData(64L * 1024 * 1024, 5 * 60)]
[InlineData(65L * 1024 * 1024, 10 * 60)]
[InlineData(177L * 1024 * 1024, 10 * 60)]
[InlineData(379L * 1024 * 1024, 15 * 60)]
[InlineData(1024L * 1024 * 1024, 25 * 60)]
[InlineData(4096L * 1024 * 1024, 30 * 60)]
public void DictionaryImportService_ComputesSizeAwareNativeTimeout(long bytes, int seconds)
{
    DictionaryImportService.GetNativeImportTimeoutSeconds(bytes).Should().Be(seconds);
}
```

- [ ] **Step 3: Run the new tests and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryImportService_RetriesObservedWindowsCharacterConversionFailures|FullyQualifiedName~DictionaryImportService_DoesNotRetryInvalidDictionaryFormats|FullyQualifiedName~DictionaryImportService_ComputesSizeAwareNativeTimeout"
```

Expected: compilation fails because the retry classifier is private and `GetNativeImportTimeoutSeconds` does not exist.

- [ ] **Step 4: Implement the minimal managed helpers**

Make the classifier `internal` and add the observed error signals without treating a generic filesystem error as sufficient by itself:

```csharp
internal static bool IsCompatibilityRetryCandidate(NativeImportResultJson result)
{
    if (!OperatingSystem.IsWindows() || result.Success)
        return false;

    var errors = result.Errors ?? [];
    if (errors.Count == 0)
        return true;

    return errors.Any(error =>
        error.Contains("SEHException", StringComparison.OrdinalIgnoreCase)
        || error.Contains("External component has thrown an exception", StringComparison.OrdinalIgnoreCase)
        || error.Contains("Unicode character", StringComparison.OrdinalIgnoreCase)
        || error.Contains("multi-byte code page", StringComparison.OrdinalIgnoreCase)
        || error.Contains("code page", StringComparison.OrdinalIgnoreCase)
        || error.Contains("Illegal byte sequence", StringComparison.OrdinalIgnoreCase)
        || error.Contains("__char_to_wide", StringComparison.OrdinalIgnoreCase)
        || (error.Contains("filesystem error", StringComparison.OrdinalIgnoreCase)
            && error.Contains("character conversion", StringComparison.OrdinalIgnoreCase)));
}
```

Implement the exact timeout formula from the approved spec:

```csharp
internal static int GetNativeImportTimeoutSeconds(long zipSizeBytes)
{
    const long mib = 1024L * 1024L;
    if (zipSizeBytes <= 64L * mib)
        return 5 * 60;

    var additionalBuckets = (zipSizeBytes - 64L * mib + 256L * mib - 1) / (256L * mib);
    return (int)Math.Min(30 * 60, (5 + additionalBuckets * 5) * 60);
}
```

- [ ] **Step 5: Run the focused tests and verify GREEN**

Run the Step 3 command again. Expected: all new tests pass.

- [ ] **Step 6: Commit managed classification and timeout policy**

```powershell
git add -- Hoshi/Services/Dictionary/DictionaryImportService.cs Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs
git commit -m "fix(dictionary): classify import retries and timeouts"
```

---

### Task 2: Add the timeout-aware native import API safely

**Files:**
- Modify: `native/hoshidicts_c_api/hoshidicts_c_api.h`
- Modify: `native/hoshidicts_c_api/hoshidicts_c_api.cpp`
- Modify: `Hoshi/Services/Dictionary/HoshiDictsNative.cs`
- Modify: `Hoshi/Services/Dictionary/DictionaryImportService.cs`
- Modify: `Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs`

**Interfaces:**
- Produces: `hoshi_import_with_timeout(const char*, const char*, int)`
- Produces: `HoshiDictsNative.hoshi_import_with_timeout(string, string, int)`
- Consumes: `DictionaryImportService.GetNativeImportTimeoutSeconds(long)`
- Preserves: existing `hoshi_import(const char*, const char*)`

- [ ] **Step 1: Add a failing native-wrapper contract test**

In `NovelReaderWebAssetTests`, load the wrapper header, wrapper source, managed P/Invoke file, and import service source. Assert the exact cross-layer contract:

```csharp
private static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
         directory != null;
         directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Hoshi.slnx")))
            return directory.FullName;
    }

    throw new DirectoryNotFoundException("Could not locate the Hoshi repository root.");
}

[Fact]
public void DictionaryNativeImport_UsesTimeoutAwareHeapOwnedWorkerContract()
{
    var root = FindRepositoryRoot();
    var header = File.ReadAllText(Path.Combine(root, "native", "hoshidicts_c_api", "hoshidicts_c_api.h"));
    var source = File.ReadAllText(Path.Combine(root, "native", "hoshidicts_c_api", "hoshidicts_c_api.cpp"));
    var pinvoke = File.ReadAllText(Path.Combine(root, "Hoshi", "Services", "Dictionary", "HoshiDictsNative.cs"));
    var service = File.ReadAllText(Path.Combine(root, "Hoshi", "Services", "Dictionary", "DictionaryImportService.cs"));

    header.Should().Contain("hoshi_import_with_timeout");
    source.Should().Contain("struct ImportThreadState");
    source.Should().Contain("std::make_shared<ImportThreadState>");
    source.Should().Contain("g_import_poisoned");
    source.Should().NotContain("std::thread import_thread([&]()");
    pinvoke.Should().Contain("hoshi_import_with_timeout");
    service.Should().Contain("GetNativeImportTimeoutSeconds");
}
```

- [ ] **Step 2: Run the contract test and verify RED**

Run:

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryNativeImport_UsesTimeoutAwareHeapOwnedWorkerContract"
```

Expected: assertions fail because the new export and heap-owned state do not exist.

- [ ] **Step 3: Declare the new export while preserving the old export**

Add to the C header:

```cpp
HOSHI_API char* hoshi_import_with_timeout(
    const char* zip_path,
    const char* output_dir,
    int timeout_seconds);
```

Keep `hoshi_import` declared unchanged.

- [ ] **Step 4: Replace stack-captured worker state with heap-owned state**

In the wrapper implementation, introduce Windows-only state:

```cpp
struct ImportThreadState {
  std::mutex mutex;
  std::condition_variable done_cv;
  ImportResult result;
  bool done = false;
};

std::atomic<bool> g_import_poisoned{false};
```

Move the existing import body to `hoshi_import_with_timeout`. Capture the shared
state and path strings by value:

```cpp
auto state = std::make_shared<ImportThreadState>();
std::thread import_thread([state, zip_path_str, output_dir_str]() {
  try {
    state->result = dictionary_importer::import(zip_path_str, output_dir_str, true);
  } catch (const std::exception& e) {
    state->result.success = false;
    state->result.errors.push_back(std::string("C++ exception: ") + e.what());
  } catch (...) {
    state->result.success = false;
    state->result.errors.push_back("Unknown C++ exception during import");
  }
  {
    std::lock_guard<std::mutex> lock(state->mutex);
    state->done = true;
  }
  state->done_cv.notify_one();
});
```

Wait with a clamped positive timeout. On timeout, set `g_import_poisoned`, detach,
and serialize the existing timed-out JSON shape with a restart instruction. On
success, join and move/copy `state->result` into the response. If poisoned before
starting, return a failure immediately. Implement the compatibility wrapper:

```cpp
HOSHI_API char* hoshi_import(const char* zip_path, const char* output_dir) {
  return hoshi_import_with_timeout(zip_path, output_dir, 3 * 60);
}
```

- [ ] **Step 5: Add the managed P/Invoke and use it from the service**

```csharp
[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
internal static extern IntPtr hoshi_import_with_timeout(
    [MarshalAs(UnmanagedType.LPUTF8Str)] string zip_path,
    [MarshalAs(UnmanagedType.LPUTF8Str)] string output_dir,
    int timeout_seconds);
```

Change `RunNativeImport` to accept `timeoutSeconds` and call the new API. In
`ImportAsync`, compute the value from `new FileInfo(localZip).Length` and reuse it
for the compatibility retry.

- [ ] **Step 6: Build the native wrapper**

Run:

```powershell
cmake --build native/hoshidicts_c_api/build --config Release
Copy-Item -LiteralPath native/hoshidicts_c_api/build/hoshidicts_c_api.dll -Destination native/out/hoshidicts_c_api.dll -Force
```

Expected: C++ build succeeds and `native/out/hoshidicts_c_api.dll` has a fresh timestamp.

- [ ] **Step 7: Run the contract and managed dictionary-import tests**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryNativeImport_UsesTimeoutAwareHeapOwnedWorkerContract|FullyQualifiedName~DictionaryImportService"
```

Expected: all selected tests pass.

- [ ] **Step 8: Commit the timeout-aware native bridge**

```powershell
git add -- native/hoshidicts_c_api/hoshidicts_c_api.h native/hoshidicts_c_api/hoshidicts_c_api.cpp Hoshi/Services/Dictionary/HoshiDictsNative.cs Hoshi/Services/Dictionary/DictionaryImportService.cs Hoshi.Tests/Services/Novels/NovelReaderWebAssetTests.cs
git commit -m "fix(dictionary): harden native import timeout"
```

---

### Task 3: Implement transactional same-title replacement

**Files:**
- Create: `Hoshi/Services/Dictionary/DictionaryImportCommitter.cs`
- Modify: `Hoshi/Services/Dictionary/DictionaryImportService.cs`
- Modify: `Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs`

**Interfaces:**
- Produces: `DictionaryImportCommitter.Commit(string dictionaryRoot, string importedDirectory, string displayTitle, IReadOnlyList<DictionaryType> types, string transactionId)`
- Produces: `DictionaryImportCommitResult` containing committed `(DictionaryType Type, string FileName)` entries
- Produces: `DictionaryImportCommitter.RecoverAbandonedTransactions(string dictionaryRoot)`
- Consumes: `DictionaryType`, staged native directory, native import type counts

- [ ] **Step 1: Extend the ZIP test helper with a revision parameter**

Change `CreateDictionaryZip` to accept `string revision = "test"` and serialize it
into `index.json`. Existing callers keep the default.

- [ ] **Step 2: Add a failing same-title replacement integration test**

```csharp
[Fact]
public async Task DictionaryImportService_ReplacesSameTitleDictionaryPayload()
{
    using var temp = new TemporaryDictionaryRoot();
    var first = temp.CreateDictionaryZip("SameTitle", true, false, false, revision: "v1");
    var service = new DictionaryImportService(
        NullLogger<DictionaryImportService>.Instance,
        new RecordingLookupService(),
        temp.DictionaryRoot);

    (await service.ImportAsync(first)).Success.Should().BeTrue();
    var second = temp.CreateDictionaryZip("SameTitle", true, false, false, revision: "v2");
    (await service.ImportAsync(second)).Success.Should().BeTrue();

    var directories = Directory.GetDirectories(Path.Combine(temp.DictionaryRoot, "Term"));
    directories.Should().ContainSingle();
    using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(directories[0], "index.json")));
    index.RootElement.GetProperty("revision").GetString().Should().Be("v2");
}
```

- [ ] **Step 3: Add a failing compatibility-directory identity test**

Seed `Term/hoshi-import-existing` with an index title of `SameTitle`, then import a
new `SameTitle` ZIP. Assert `hoshi-import-existing` remains the only directory and
its revision changes to `v2`.

- [ ] **Step 4: Add a failing rollback integration test**

Seed an old Term dictionary named `RollbackDict`, create a regular file at
`Frequency/RollbackDict`, and import a mixed Term/Frequency ZIP with revision
`v2`. Assert import failure, the old Term index still reports `v1`, and lookup
rebuild was not called.

- [ ] **Step 5: Run the three replacement tests and verify RED**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryImportService_ReplacesSameTitleDictionaryPayload|FullyQualifiedName~DictionaryImportService_ReusesCompatibilityDirectoryForSameTitle|FullyQualifiedName~DictionaryImportService_RollsBackReplacementWhenLaterTypeCommitFails"
```

Expected: replacement retains v1 or creates a second directory, compatibility
identity is not reused, and rollback behavior is absent.

- [ ] **Step 6: Implement `DictionaryImportCommitter`**

The helper must:

1. Validate `importedDirectory/index.json` exists.
2. For each type, find an installed directory whose parsed index title equals
   `displayTitle`; reuse its directory name when found.
3. Create `.dictionary-prepare-<transactionId>/<storageName>` and copy the staged
   payload there.
4. After all prepares succeed, move existing targets to
   `.dictionary-backup-<transactionId>/<storageName>` and move prepared payloads
   into place.
5. If any commit fails, remove newly committed targets and restore all backups in
   reverse order before rethrowing.
6. Return one `(Type, FileName)` entry per committed type.

Use explicit record types rather than `Dictionary<string, object>`:

```csharp
internal sealed record DictionaryImportCommittedEntry(DictionaryType Type, string FileName);
internal sealed record DictionaryImportCommitResult(IReadOnlyList<DictionaryImportCommittedEntry> Entries);
```

- [ ] **Step 7: Integrate transactional commit into `ImportAsync`**

Replace the `if (!Directory.Exists(targetDir)) CopyDirectory(...)` loop with one
committer call. Update active config only after a successful commit by iterating
the returned entries. Catch commit exceptions inside the background import body,
log the transaction ID and exception, and return a failed
`DictionaryImportResult`; do not rebuild lookup on this path.

- [ ] **Step 8: Implement abandoned transaction recovery**

During `NormalizeConfig`, call `RecoverAbandonedTransactions` before enumerating
installed dictionaries. Delete abandoned prepare roots. For each backup child,
restore it when its target is missing; otherwise delete the stale backup. Never
delete a backup whose target is missing.

- [ ] **Step 9: Run replacement and existing import tests and verify GREEN**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DictionaryImportService"
```

Expected: all new and existing import tests pass.

- [ ] **Step 10: Commit transactional replacement**

```powershell
git add -- Hoshi/Services/Dictionary/DictionaryImportCommitter.cs Hoshi/Services/Dictionary/DictionaryImportService.cs Hoshi.Tests/Services/Dictionary/DictionaryLookupServiceTests.cs
git commit -m "fix(dictionary): replace same-title imports atomically"
```

---

### Task 4: Verify behavior, document the root-cause fix, and launch Hoshi

**Files:**
- Modify: `docs/CHANGELOG.md`
- Verify: all files changed by Tasks 1-3

**Interfaces:**
- Consumes: completed managed and native import changes
- Produces: documented root cause, passing x64 build/tests, running verified app

- [ ] **Step 1: Add a concise changelog entry**

Document only root cause and solution:

```markdown
- Dictionary re-import previously skipped an existing same-title directory while
  reporting success; imports now stage and transactionally replace the payload
  while preserving profile references.
- Windows character-conversion failures now enter the lookup-safe ASCII retry,
  and large imports use a size-aware timeout with heap-owned native worker state.
```

- [ ] **Step 2: Verify no forbidden submodule edits**

```powershell
git status --short
git diff --submodule=short -- native/hoshidicts
```

Expected: no file or submodule pointer change under `native/hoshidicts/`.

- [ ] **Step 3: Run dictionary tests**

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Dictionary"
```

Expected: zero failures.

- [ ] **Step 4: Run the required x64 build and full test suite**

```powershell
dotnet build -p:Platform=x64
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64
```

Expected: build succeeds and the full test suite has zero failures. Record the
existing `SQLitePCLRaw.lib.e_sqlite3` NU1903 warning separately if it remains.

- [ ] **Step 5: Build, launch, and verify the WinUI app**

```powershell
.\build-and-run.ps1
```

Confirm a responsive Hoshi top-level window appears. Import two small test ZIPs
with the same title and different revisions, then verify the dictionary list has
one entry and lookup uses the second payload. Leave the verified app instance
running.

- [ ] **Step 6: Review the final diff**

```powershell
git diff --check
git diff --stat
git status --short
```

Expected: only the planned C#, C++ wrapper, tests, and changelog files are
changed; the ignored locally rebuilt native DLL is present under `native/out/`,
and the user's pre-existing unrelated changes remain untouched.

- [ ] **Step 7: Commit the changelog after verification**

```powershell
git add -- docs/CHANGELOG.md
git commit -m "docs: record dictionary import reliability fixes"
```
