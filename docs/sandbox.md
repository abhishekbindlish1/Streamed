# Sandbox

The local fixture environment supports the browser app and experimental Android
client. It requires no credentials and does not call the real backend or mint
account tokens. Sample events and public test videos are not match broadcasts.

## Browser

With Node.js 24 and the `zip` command installed, run from the repository root:

```sh
npm ci
npm run sandbox
```

The server binds to loopback on port 8080. Open its root path in a browser, choose
a profile, and select a sport. HLS.js and video playback need Internet access;
catalog browsing and profiles do not need a backend. Profiles stay in the browser.

```sh
curl --fail http://127.0.0.1:8080/health
curl --fail http://127.0.0.1:8080/api/sports
curl --fail http://127.0.0.1:8080/api/matches/football
```

`SANDBOX_PORT` selects another port. Loopback is the default; use `SANDBOX_HOST`
only if your own development network requires it. This fixture server has no
production authentication and must not be deployed as a real backend.

## Android emulator

Prerequisites: a full JDK 21, the SDK pinned in `global.json`, Android platform 36
and build-tools 36.0.0, and the MAUI Android workload. The app supports API 26 and
newer; the combined emulator APK supports both 32-bit x86 and x64 Android images.

```sh
dotnet workload install maui-android --skip-manifest-update
bash scripts/verify-dotnet-workloads.sh
dotnet build apps/android/StreamedTv.Android.csproj -c Debug \
  -p:SandboxMode=true \
  -p:EmbedAssembliesIntoApk=true -p:AndroidPackageFormats=apk
adb install -r apps/android/bin/Debug/net10.0-android/com.streamed.tv.android.sandbox-Signed.apk
```

Keep `npm run sandbox` running on the emulator host. Android reaches it through
`10.0.2.2:8080`. This debug APK uses a separate application ID, allows local HTTP,
skips pairing, and visibly labels sample content. Normal builds keep their auth
gate. Release builds reject `SandboxMode=true`. Native fixture APIs serve a local
generated H.264/AAC MP4 and HLS playlist with byte-range support; native playback
tests do not depend on an external media provider.

With the APK installed and an emulator running:

```sh
bash scripts/android-smoke.sh
```

The script launches the app, selects matches using remote keys, checks advancing
native HLS and MP4 playback, pause/resume and Back through UI Automator, and captures
the real Android screen. Set `ADB` to another ADB binary
or `SCREENSHOT_DIR` to change the output folder. CI uses an accelerated emulator
and uploads screenshots. This cloud machine uses slow software emulation, which
can show Android system ANRs during startup; those are distinct from app errors
or a successful rendering check.
For slow software emulation, `ANDROID_COMMAND_TIMEOUT` can raise the individual
ADB command deadline; CI retains its 40-second default. Playback and layout
assertions remain the same.

CI builds the APKs once, then runs separate API 28 portrait, Android TV 10/API 29
32-bit x86, and Android TV API 36 x64 landscape images. Each job creates a 4 GB AVD and runs
`bash scripts/android-ci-smoke.sh StreamedCi`. This starts a disposable read-only
emulator, checks boot within a deadline, and runs playback and pairing assertions.
It starts and stops its own fixture server and emulator; startup logs, UI XML and
screenshots are retained as workflow artifacts on failure.
For an inactive prepared AVD, pass its name instead. Set
`ANDROID_FIXTURE_SERVER_RUNNING=true` to reuse a running fixture server and
`ANDROID_EMULATOR_PORT` to select an unused emulator port. The script refuses
to replace a running emulator on that port.

The CI launcher also needs the isolated pairing APK. Build it with the same
combined-emulator command plus `-p:SandboxPairing=true`; its package ID ends in `.sandbox.pairing`.
For manual pairing checks, start the server with
`SANDBOX_PAIRING_FIXTURE=true npm run sandbox`, install that APK, and run
`bash scripts/android-smoke.sh --pairing`. The server deliberately fails one
pairing request, waits for an explicit test completion, and issues an obviously
fake fixture token. This debug flag is rejected outside an explicit Debug sandbox
build. It does not configure production authentication or create real accounts.

The cloud's prepared `StreamedAndroidTV` API 36 AVD was checksum-verified but its
software-only boot did not finish usefully here. Use the hardware-accelerated CI
matrix for TV checks. See [vendor emulator distinctions](tv-testing.md).

## Go API and native Tizen

The production-shaped Go API is separate from the fixture server. Its stub mode
requires no provider credentials. With Go 1.27.3:

```sh
cd services/api
go test ./...
PORT=8090 POSTER_CRON_ENABLED=false go run .
```

Smoke-check `/v1/health`, `/v1/config`, and `/v1/teams/lookup?name=arsenal` locally.
Pairing, cloud-backed profiles, Firestore, live scores, and provider enrichment
need separate server-side configuration; no fake production accounts are created.
The imported deployment recipes contain redacted placeholders, which are not
working credentials.

The native .NET TV app targets Tizen 9 and needs a compatible Tizen runtime for
its NUI UI and media engine. Use a TV and Samsung signing profile for installation
and playback tests. The web widget remains the older-OS path.

## Screenshots

`npm run screenshots` captures the web sandbox into `docs/screenshots`. In this
cloud instance, prefix it with
`PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH=/usr/bin/chromium`. Browser images and real
Android captures are labeled separately. Native Tizen screenshots require running
that UI on Tizen; the Linux browser/Android sandbox does not render it.
