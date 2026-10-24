# Streamed Tizen .NET

This is the imported NUI TV implementation, targeting `tizen90` / Tizen 9. It is
separate from the Tizen 5.5-compatible [web app](../web). The project, manifest,
and tooling metadata now agree on API 9.0.

```sh
dotnet build apps/tizen-dotnet/StreamedTv.csproj -c Debug
```

Run from the repository root with the SDK pinned in `global.json`. A successful
build produces a development `.tpk` under `bin/Debug/tizen90`. The SDK's default
test certificate does not make this package publicly installable on Samsung TVs.
Use your Samsung device/certificate profile for actual installation. Cloud tests
compile the app and test core logic; they do not emulate NUI or TV playback.

The native app requires its configured backend for authentication and data. Use
the [sandbox](../../docs/sandbox.md) for credential-free web/Android fixture tests.
Client-side extractor token minting from the sanitized import is disabled: a
shared signing secret must not be shipped inside a client application. Provider
credentials, Google sign-in, Firestore, and production extraction are separate
backend configuration tasks. HTTPS uses the platform trust store.

Historical documents are preserved under `docs/imported-native`; their old local
paths and scaffold notes are not the current build instructions.
