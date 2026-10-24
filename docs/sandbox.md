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
newer; startup checks use an API 28 x64 emulator.

```sh
dotnet workload install maui-android --skip-manifest-update
bash scripts/verify-dotnet-workloads.sh
dotnet build apps/android/StreamedTv.Android.csproj -c Debug \
  -p:SandboxMode=true -p:RuntimeIdentifier=android-x64 \
  -p:EmbedAssembliesIntoApk=true -p:AndroidPackageFormats=apk
adb install -r apps/android/bin/Debug/net10.0-android/android-x64/com.streamed.tv.android.sandbox-Signed.apk
```

Keep `npm run sandbox` running on the emulator host. Android reaches it through
`10.0.2.2:8080`. This debug APK uses a separate application ID, allows local HTTP,
skips pairing, and visibly labels sample content. Normal builds keep their auth
gate. Release builds reject `SandboxMode=true`.

With the APK installed and an emulator running:

```sh
bash scripts/android-smoke.sh
```

The script launches the app, checks its rendered fixture catalog through UI
Automator, and captures the real Android screen. Set `ADB` to another ADB binary
or `SCREENSHOT_DIR` to change the output folder. CI uses an accelerated emulator
and uploads screenshots. This cloud machine uses slow software emulation, which
can show Android system ANRs during startup; those are distinct from app errors
or a successful rendering check.

CI creates a 1280×720 API 28 AVD with the official SDK tools, then runs
`bash scripts/android-ci-smoke.sh StreamedCi`. This starts a disposable read-only
emulator, checks boot within a deadline, installs the sandbox APK, and runs the
rendering assertion. It starts and stops its own fixture server and emulator;
startup logs and screenshots are retained as workflow artifacts on failure.
For an existing prepared AVD, pass its name instead. Set
`ANDROID_FIXTURE_SERVER_RUNNING=true` to reuse a running fixture server and
`ANDROID_EMULATOR_PORT` to select an unused emulator port. The script refuses
to replace a running emulator on that port.

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
