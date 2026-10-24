# Streamed Android — experimental

The .NET MAUI app targets .NET 10 / Android API 26+, on phones and Android TV.
The device preview APK is ARM64; the x64 APK is for emulators. LG webOS and
Samsung Tizen require their own app formats; see [TV testing](../../docs/tv-testing.md).

The Android client includes focusable match buttons with a visible remote focus
state, retryable catalog loading, a pairing root that Back cannot bypass, and a
native Media3 player through CommunityToolkit MediaElement for MP4/HLS/DASH.
Embedded HTML player pages use a WebView with DOM storage and autoplay enabled.
Playback stops when the player closes. Production login/providers and physical
codec/DRM combinations remain separate acceptance tests.

Prerequisites: the SDK in `global.json`, a full JDK 21, Android SDK platform 36 /
build-tools 36.0.0, and the `maui-android` workload.

```sh
dotnet workload install maui-android --skip-manifest-update
bash scripts/verify-dotnet-workloads.sh
dotnet build apps/android/StreamedTv.Android.csproj -c Debug \
  -p:SandboxMode=true -p:RuntimeIdentifier=android-x64 \
  -p:EmbedAssembliesIntoApk=true -p:AndroidPackageFormats=apk
```

Start `npm run sandbox`. This explicit sandbox APK connects to `10.0.2.2:8080`,
uses generated local videos, visibly labels sample content and skips pairing.
It has a separate application ID. Release builds reject sandbox mode. HTTPS
certificate validation remains enabled; the debug network policy permits HTTP
only to the local emulator host.

To exercise the real pairing screen with a local test server, additionally build
with `-p:SandboxPairing=true` and run the server with
`SANDBOX_PAIRING_FIXTURE=true npm run sandbox`. This creates another application
ID, `com.streamed.tv.android.sandbox.pairing`, and uses an explicitly fake fixture
session. This flag requires both Debug configuration and SandboxMode. It never
creates a production account. Normal builds retain real authentication.

CI builds normal ARM64, sandbox x64 and pairing-fixture x64 APKs, then runs actual
remote navigation, native HLS/MP4 playback and pairing checks on API 28 and Android
TV API 36. See [sandbox commands](../../docs/sandbox.md) and
[release downloads](../../docs/releases.md#native-downloads). Packages remain debug
signed development previews, so a later signer may require uninstalling first.
