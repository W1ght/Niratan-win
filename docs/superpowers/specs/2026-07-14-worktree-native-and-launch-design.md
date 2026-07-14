# Worktree Native DLL and Launch Design

## Goal

Make a fresh linked worktree build/test successfully without manually copying `hoshidicts_c_api.dll`, and make `build-and-run.ps1` always build and launch the Niratan executable belonging to the script's own worktree.

## Root cause

- `native/out/` is intentionally ignored, so a new linked worktree does not contain `native/out/hoshidicts_c_api.dll`.
- both C# projects currently copy the DLL only when that worktree-local file already exists; a normal build therefore succeeds but native dictionary tests fail at runtime.
- `build-and-run.ps1` uses `./` paths and invokes `dotnet build` from the caller's current directory. Calling a worktree's script while the shell remains in the main checkout builds and launches the main checkout, creating the old-instance symptom.

## Native DLL preparation

Add `scripts/Ensure-NativeHoshidicts.ps1` with this deterministic order:

1. Resolve the requested repository root to an absolute path.
2. Return immediately when its worktree-local `native/out/hoshidicts_c_api.dll` exists.
3. Ask Git for the absolute common Git directory. For a linked worktree, derive the primary checkout root from the common `.git` directory and copy its `native/out/hoshidicts_c_api.dll` into the current worktree.
4. If no shared DLL exists, invoke the current worktree's existing `build-native.ps1`.
5. Fail if the local DLL still does not exist.

Both `Niratan.csproj` and `Niratan.Tests.csproj` run this preparation before copying the DLL on x64 builds. ARM64 behavior is unchanged. No file under `native/hoshidicts/` is modified.

## Exact-worktree launch

`build-and-run.ps1` must:

- derive every path from `$PSScriptRoot`, independent of the caller's current directory;
- run the native preparation script for that root;
- build the explicit `Niratan/Niratan.csproj` x64 Debug target;
- require the exact worktree output executable to exist;
- start that absolute executable with its output folder as the working directory and retain the process object;
- wait for a non-zero main-window handle or fail if the process exits/never creates a window;
- print the launched PID and absolute executable path.

The existing behavior of closing running Niratan processes before the build is retained so the user sees only the newly verified instance.

## Verification

- An automated asset test must fail against the old scripts and assert root-anchored build/launch plus MSBuild native preparation wiring.
- Delete the feature worktree's local `native/out` and output copies, then run a normal x64 test/build to prove automatic recovery from the primary checkout copy.
- Invoke the worktree script from the primary checkout directory and verify the live process path equals the feature worktree executable path and has a responsive window.
