# Dictionary Import Reliability Design

## Context

Niratan Windows and Niratan use the same `hoshidicts` revision, but Niratan adds a
Windows C API wrapper and a C# staging/commit layer. Three reliability gaps in
those Windows-specific layers explain dictionary imports that fail or appear to
have no effect:

- Niratan skips the copy when a target directory already exists, while still
  returning a successful import result.
- The Windows compatibility retry does not recognize the observed
  `__char_to_wide: Illegal byte sequence` filesystem error.
- The native wrapper applies one fixed three-minute timeout and detaches a
  thread that captures stack-owned state by reference.

The local `D:\smb\yomitan` audit found 77 structurally valid Yomitan v3 ZIPs,
including six pairs that share a dictionary title but contain different data,
and several image-heavy or highly compressed archives.

## Goals

- Re-importing a dictionary with the same display title replaces the installed
  payload instead of reporting a false success.
- Replacement preserves the stable storage directory name so dictionary order,
  enabled state, and profile references remain valid.
- Windows code-page failures use the existing lookup-safe compatibility retry.
- Large valid archives receive enough time to import without leaving dangling
  native thread state after a timeout.
- A failed commit leaves the previously installed dictionary usable.
- Existing callers of `hoshi_import` remain binary-compatible.

## Non-Goals

- Do not modify any file under `native/hoshidicts/`.
- Do not add a second dictionary backend or change lookup semantics.
- Do not introduce an external helper executable in this change.
- Do not broaden the compatibility ZIP beyond the lookup-safe files required by
  the repository rules.
- Do not change dictionary ordering, profile ownership, or the settings UI.

## Selected Approach

Use a scoped hardening of the existing in-process importer:

1. Stage the native output as today.
2. Resolve replacement identity by the restored display title from
   `index.json`, not only by the native directory name.
3. Prepare all destination copies before changing installed directories.
4. Commit each type directory through same-volume renames with backup and
   rollback.
5. Expand the Windows compatibility classification to the concrete filesystem
   conversion errors already observed in logs.
6. Add a timeout-aware C API entry point. C# computes the timeout from the local
   ZIP size, while the native wrapper stores worker completion state on the heap
   and rejects later imports after a timeout until the process restarts.

This is smaller than a helper-process architecture and fixes the reported
failure modes without changing release packaging. A helper process remains the
preferred future step if native importer crashes continue after these fixes.

## Import Data Flow

### Source staging and native import

The selected ZIP is copied to the existing ASCII-only staging directory. Niratan
computes a timeout from the copied ZIP size:

- Up to 64 MiB: 5 minutes.
- For each started 256 MiB above 64 MiB: add 5 minutes.
- Cap the timeout at 30 minutes.

Examples: a 32 MiB package receives 5 minutes, a 177 MiB package receives 10
minutes, a 379 MiB package receives 15 minutes, and a 1 GiB package receives 25
minutes.

C# calls a new narrow native API:

```c
char* hoshi_import_with_timeout(
    const char* zip_path,
    const char* output_dir,
    int timeout_seconds);
```

The existing `hoshi_import(zip_path, output_dir)` remains exported and delegates
to the new function with its current three-minute default for compatibility with
existing tests and callers.

### Native worker lifetime

The Windows wrapper moves `ImportResult`, mutex, condition variable, and the
completion flag into a `shared_ptr`-owned state object captured by value by the
worker thread. A timeout may still detach the blocked worker, but no detached
code references the returned function's stack.

After a timeout, the wrapper marks the importer process as poisoned. Subsequent
imports return a typed JSON failure telling the user to restart Niratan instead of
starting another native import beside an abandoned worker. Normal successful or
ordinary failed imports do not poison the process.

### Compatibility retry

The retry classifier remains Windows-only and excludes genuine format errors
such as missing `index.json`, invalid JSON, or an empty dictionary. It recognizes
the existing error families plus these observed filesystem conversion signals:

- `Illegal byte sequence`
- `__char_to_wide`
- `filesystem error` when accompanied by a character conversion failure

The compatibility ZIP continues to contain only:

- `index.json`
- `styles.css`
- `term_bank_*.json`
- `term_meta_bank_*.json`
- `tag_bank_*.json`

Its temporary title remains ASCII, and the installed `index.json` title is
restored after native import.

## Same-Title Replacement

### Identity

For each imported dictionary type, Niratan searches installed type directories for
an `index.json` whose `title` equals the imported display title using ordinal
comparison. This handles both normal title-named directories and existing
`niratan-import-*` compatibility directories.

When a title match exists, the replacement reuses that directory's file name.
This keeps all profile configuration references stable. When no title match
exists, Niratan uses the native imported directory name as today.

### Prepare, commit, and rollback

For every type reported by native counts:

1. Copy the staged native directory to a hidden prepared sibling inside the
   destination type directory.
2. Do not modify configuration yet.
3. After every prepared copy succeeds, rename any existing target to a hidden
   backup sibling.
4. Rename the prepared sibling to the stable target name.
5. If any rename fails, remove newly committed targets and restore every backup.
6. After all types commit, delete backups and update the active profile config.

Prepared and backup names contain only a GUID and ASCII punctuation. Startup
normalization removes abandoned prepared/backup directories left by a process
termination.

Replacement changes only the types present in the new native result. It does not
delete an older Frequency or Pitch installation when a newly imported package
does not contain that type, matching Niratan's current behavior.

## Error Handling

- Source copy failures retain the existing user-facing error and include the
  underlying filesystem message.
- Native format failures return the native error without compatibility retry.
- Compatibility ZIP construction failures return a managed import error and
  clean the non-timed-out staging directory.
- Commit failures return failure, restore the old payload, and do not rebuild the
  query.
- Native timeout failures retain staging because a detached worker can still be
  using it, poison further native imports, and instruct the user to restart.
- A successful replacement rebuilds the lookup query exactly once.

## Testing

Add regression coverage in `DictionaryLookupServiceTests` for:

- Two different ZIPs with the same `index.json.title` replace the installed
  `index.json.revision` and keep one installed directory.
- Replacement reuses a pre-existing `niratan-import-*` directory whose index title
  matches the imported title.
- Commit rollback restores the old directory when a later type commit fails,
  using an internal filesystem commit helper with deterministic failure
  injection rather than relying on platform file locks.
- `Illegal byte sequence` and `__char_to_wide` are compatibility retry
  candidates.
- Missing index, invalid JSON, and empty dictionary errors are not retry
  candidates.
- Timeout calculation covers boundary sizes and the 30-minute cap.
- Existing mixed Term/Frequency/Pitch imports still populate every type and
  rebuild once.

Add a native wrapper source contract test for:

- The timeout-aware export remains declared in the header and implemented in the
  C++ wrapper.
- Worker state is heap-owned and the timeout path does not capture stack state by
  reference.

Verification after implementation:

```powershell
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Dictionary"
dotnet build -p:Platform=x64
dotnet test Niratan.Tests/Niratan.Tests.csproj -c Debug -p:Platform=x64
```

Then build and launch Niratan, import a small same-title replacement, and confirm
the dictionary list contains one updated entry and lookup uses the new payload.

## Security and Constraints

- Compatibility archive entry validation continues to reject absolute paths,
  drive-qualified paths, NULs, and `.`/`..` traversal segments.
- Native output is committed only from a staging directory containing
  `index.json`.
- No broad native API is exposed to WebView2 or JavaScript.
- All filesystem and native import work remains off the UI thread and serialized
  by the existing import lock.
