# Streamed Android — experimental

The imported .NET MAUI port targets .NET 10 / Android API 26 and newer. It is a
separate app, not a completed feature-equivalent port of the TV UI. The application
bootstrap is included, package versions are pinned, and removed MAUI Frame usage
has been migrated to Border. HTTPS certificate validation is enabled.

Prerequisites: the SDK in `global.json`, a full JDK 21 (not just a JRE), Android SDK
platform 36 / build-tools 36.0.0, and the `maui-android` workload.

```sh
dotnet workload install maui-android --skip-manifest-update
bash scripts/verify-dotnet-workloads.sh
dotnet build apps/android/StreamedTv.Android.csproj -c Debug \
  -p:SandboxMode=true -p:RuntimeIdentifier=android-x64 \
  -p:EmbedAssembliesIntoApk=true -p:AndroidPackageFormats=apk
```

The fixture-only sandbox build uses a separate application ID and skips pairing
only when explicitly compiled with `SandboxMode=true`. Release configuration
rejects this flag. It connects to the host fixture server at `10.0.2.2:8080` from
an Android emulator. Run `npm run sandbox` first. This is a debug APK, not a
Play Store release or a production account login implementation.

Without that build flag, the pairing/authentication gate stays enabled. Production
signing, account-backed flows, TV remote ergonomics, and real stream playback need
separate acceptance testing. See [sandbox](../../docs/sandbox.md).

The APK has been built, installed, and startup-checked in a real API 28 software
emulator against the local fixture API. Its capture is in `docs/screenshots`.
This verifies sandbox catalog rendering, not production login, polished card
sizing, TV remote parity, or external media playback.
