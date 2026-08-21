# Yukari galgame hook helper

This directory is the isolated native helper copied from the local Fushi/Hibiki
`develop` checkout at commit `b140f90c32689d7104c44c7b8e9ec2d09d248984`.

The helper remains a separate injector/DLL boundary. The WinUI host must not link
these sources into `Niratan.exe`; it only stages `voice_hook/<arch>/`, starts
`fushi_voice_injector.exe`, and reads the versioned shared-memory contract.

Generated `build/`, `dist/`, and Unity build caches are intentionally excluded.
The current app bundle contains the x86/x64 base helper runtime; Unity resource
audio extraction is not bundled in this M0 port and therefore is not claimed as
available. A real game process is required before any engine support can be
promoted beyond `implemented_unverified`.
