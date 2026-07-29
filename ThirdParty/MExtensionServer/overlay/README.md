# Niratan M-Extension-Server overlay

This directory contains Niratan's source-selection protocol overlay for
M-Extension-Server 1.0.4.

The upstream `/dalvik` handler always executes the first source returned by an
APK. Niratan adds a string-valued `sourceId` field to each request. The overlay
selects the matching Mihon manga or anime source before delegating to the
unchanged upstream invoker.

The overlay loader also repairs narrow invalid constructor patterns emitted
when dex2jar 2.4 converts APKs processed by recent R8 versions. It restores
uniquely identifiable stateless lambdas/interceptors and singleton allocations
that incorrectly instantiate their superclass. This keeps current extension
companion objects loadable on the JVM without modifying the APK or executing
DEX in the Niratan process.

`NiratanMExtensionOverlay.jar` is built from the adjacent `src` directory
against the pinned upstream server JAR. It is loaded before the upstream JAR on
the private sidecar class path. Both this overlay and the upstream component are
licensed under the Mozilla Public License 2.0.
