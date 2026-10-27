# Streamed Android — experimental

The .NET MAUI app targets .NET 10 / Android API 26+, on phones and Android TV.
The device preview APK includes both ARM32 (`armeabi-v7a`) and ARM64 (`arm64-v8a`);
the sandbox APK includes x86 and x64 for emulators. LG webOS and
Samsung Tizen require their own app formats; see [TV testing](../../docs/tv-testing.md).
Android TV 10 is API 29 and meets the minimum OS requirement. Some TVs have a
64-bit processor with a 32-bit Android system; the previous ARM64-only download
could not install on those systems. Use the combined ARM APK and follow
[Android installation](../../docs/android-installation.md) for device diagnostics.

The Android client includes focusable match buttons with a visible remote focus
state, retryable catalog loading, a pairing root that Back cannot bypass, and a
native Media3 player through CommunityToolkit MediaElement for MP4/HLS/DASH.
Embedded HTML player pages use a WebView with DOM storage and autoplay enabled.
Playback stops when the player closes. Production login/providers and physical
codec/DRM combinations remain separate acceptance tests.

MAUI 10.0.120's batched Android initialization can convert unset minimum sizes
to `Int32.MaxValue` on a 32-bit runtime, inflating scroll contents and crashing
the focused button's ripple bitmap. The Android mapper applies MAUI's normal
minimum-size converters after initialization. Keep the API 29 x86 layout and
playback checks when updating MAUI; a successful x64 run cannot catch this case.

Prerequisites: the SDK in `global.json`, a full JDK 21, Android SDK platform 36 /
build-tools 36.0.0, and the `maui-android` workload.

```sh
dotnet workload install maui-android --skip-manifest-update
bash scripts/verify-dotnet-workloads.sh
dotnet build apps/android/StreamedTv.Android.csproj -c Debug \
  -p:SandboxMode=true \
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

Without `RuntimeIdentifier`, the project builds one ARM32/ARM64 device APK, or an
x86/x64 APK when `SandboxMode=true`. Outputs are directly under
`bin/Debug/net10.0-android`. An explicit single `RuntimeIdentifier` is still
supported for local development and uses its RID subdirectory.

CI builds normal ARM, sandbox x86/x64 and pairing-fixture x86/x64 APKs, then runs
remote navigation, native HLS/MP4 playback and pairing checks on API 28, Android TV
10/API 29 (32-bit x86), and Android TV API 36. These emulator ABIs do not establish
physical ARM TV acceptance. The release collector inspects actual APK metadata
and rejects device packages missing either ARM ABI. See [sandbox commands](../../docs/sandbox.md) and
[release downloads](../../docs/releases.md#native-downloads). Packages remain debug
signed development previews, so a later signer may require uninstalling first.
