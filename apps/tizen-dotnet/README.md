# Streamed Tizen .NET

This is the imported NUI TV implementation, targeting `tizen90` / Tizen 9. It is
separate from the Tizen 5.5-compatible [web app](../web). The project, manifest,
and tooling metadata now agree on API 9.0.

```sh
python3 scripts/setup-tv-sdks.py tizen-cli --root /tmp/streamed-tv-sdk --cache /tmp/streamed-tv-downloads
export TIZEN_SDK_DIRECTORY=/tmp/streamed-tv-sdk/tizen-studio
python3 scripts/build-tizen-development.py
```

Run from the repository root with the SDK pinned in `global.json`. A successful
build produces a development `.tpk` under `bin/Release/tizen90`. The helper uses
a disposable author and the current Tizen Studio public distributor. Direct
`dotnet build` uses the old NuGet SDK's expired default certificates and must not
be used to publish an installer. No generated private key is kept in the checkout
or release. Different development authors require uninstalling the previous app.
The SDK developer CA expires January 1, 2027; the release collector rejects expired
certificates and records the earliest expiry in `BUILD-INFO.json`.

For an existing Samsung profile, set all four GitHub Actions repository secrets:
`TIZEN_AUTHOR_P12_BASE64`, `TIZEN_AUTHOR_PASSWORD`,
`TIZEN_DISTRIBUTOR_P12_BASE64`, and `TIZEN_DISTRIBUTOR_PASSWORD`.
The P12 secrets are base64-encoded file contents. Supply a profile authorized for
the target TV/emulator, including its device authorization. The build decodes keys
only into a private temporary directory, removes them on exit, and redacts signing
values from logs. Never commit profiles or paste passwords into an issue/chat.
The same environment variables work locally. A partial configuration fails instead
of silently falling back to SDK signing.

Use your Samsung device/certificate profile for physical TV installation. The
accelerated `samsung_emulator` job boots the official TV image and checks native
TPK installation and startup. See [TV testing](../../docs/tv-testing.md) for the
verified coverage; production authentication and TV playback are separate checks.

The native app requires its configured backend for authentication and data. Use
the [sandbox](../../docs/sandbox.md) for credential-free web/Android fixture tests.
Client-side extractor token minting from the sanitized import is disabled: a
shared signing secret must not be shipped inside a client application. Provider
credentials, Google sign-in, Firestore, and production extraction are separate
backend configuration tasks. HTTPS uses the platform trust store.

Historical documents are preserved under `docs/imported-native`; their old local
paths and scaffold notes are not the current build instructions.

Development TPKs are published alongside browser/APK downloads on the repository’s
[Releases page](https://github.com/abhishekbindlish1/Streamed/releases), starting
with the all-platform preview. Check INSTALL.md before installing on a TV.
