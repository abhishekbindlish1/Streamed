# TV platforms and emulator coverage

LG smart TVs run webOS. Samsung smart TVs run Tizen. Android/Google TV runs on
Android TV devices and attached boxes; the brand of the connected display does
not change an Android box into webOS or Tizen.

| Platform | App format | Test environment |
| --- | --- | --- |
| Android / Google TV | APK | Official Google Android TV API 36 x64 image, hardware-accelerated GitHub runner |
| Older Android compatibility | APK | API 28 generic Android image in portrait, separate CI job |
| LG webOS | IPK web app | LG webOS TV Simulator; older SDKs also provided a VirtualBox emulator |
| Samsung Tizen web | WGT | Samsung TV extensions in Tizen Studio; choose an image matching the TV OS |
| Samsung Tizen .NET | TPK | A compatible .NET/NUI runtime and API level, or a physical compatible TV |

The repository has Android, Tizen web, Tizen .NET and an experimental LG webOS
client. The LG app shares `apps/web`, adds its own manifest and produces an IPK
with `npm run build:webos`. An APK or TPK cannot be installed into LG's Simulator.
The LG preview includes owned sample media and works without a backend account.

## Android checks

`native.yml` builds the APKs once, verifies their signatures, then runs the same
functional checks on two independent emulators. The TV job uses an actual
Android TV image, not just a phone with a landscape screen.

The UI tests use D-pad and media key events to select matches, open local HLS and
MP4 test videos, observe native video dimensions and advancing playback time,
pause/resume, and return to the catalog. A separate debug-only application ID
exercises pairing failure/retry, the Back authentication gate, completion and
session persistence. Only this fixture package's data is reset.

Media fixtures are generated test patterns, with no external accounts or streams.
Screenshots, UI XML and diagnostic logs are retained as CI artifacts. Assertions
remain enabled on failure. Native codec/device/DRM differences still need real
hardware acceptance tests; these fixtures do not validate production providers.

For manual setup, see [the sandbox](sandbox.md). `scripts/setup-android-sdk.sh`
installs named SDK packages using the official manager and accepts required SDK
licences without losing its exit status. The emulator launcher refuses an occupied
port and stops only processes that it started.

## LG workstation setup

Use LG's [webOS TV Simulator documentation](https://webostv.developer.lge.com/develop/tools/simulator-introduction)
and the official [webOS CLI](https://github.com/webos-tools/cli). Select the `tv`
profile, prepare a webOS application with `appinfo.json`, and launch it with the
simulator's supported workflow. This tests the webOS app, not the Android APK.
GUI dependencies and the simulator distribution must be installed on that host.

The official LG TV 26 Simulator 1.5.0 has run successfully in CI and in this cloud.
The checks open the staged app using the actual LG platform API, navigate with
remote keys, decode the local H.264/AAC video, check frames and advancing time,
exercise Play/Pause, and return to the catalog with Back. Screenshots are retained
as `lg-simulator-results`. These are Simulator checks, not physical LG TV tests.

`scripts/setup-tv-sdks.py` pins the Linux distribution checksum, checks its archive,
and prepares its AppImage. The LG download flow needs `webostv.developer.lge.com`,
`developer.lge.com` and `gfts.lge.com`. The Simulator itself queries `ip-api.com` at
startup; in this proxy-only cloud that produced an SDK DNS error dialog. Dismissing
that vendor dialog allowed the app to run. CI on Ubuntu 24 avoids this local
proxy/DNS limitation. SDK startup and app playback are separate readiness checks.

The managed cloud Chromium also blocks `file://` navigation. Do not change its
policy to run package tests. The ordinary HTTP browser checks run locally; the
packaged-file test runs in CI, and LG's own Simulator exercises its own runtime.

## Samsung workstation setup

Install Tizen Studio and Samsung TV extensions, then create the matching TV image
in [Samsung's TV Emulator](https://developer.samsung.com/smarttv/develop/getting-started/using-sdk/tv-emulator.html).
The SDK's [Emulator Manager](https://github.com/Samsung/tizen-docs/blob/master/docs/sdk-tools/baseline-sdk/common-tools/emulator-manager.md)
can create and manage instances; its `em-cli` binary is under
`tools/emulator/bin/` in the SDK installation. Use that installed version's help
and available profiles instead of assuming a profile name.

For this repository, the web widget is the Tizen 5.5 path for the pictured 2020 TV.
The native .NET project targets API 9. A generic or older TV emulator does not
establish that it contains the required .NET/NUI runtime. Verify that runtime and
profile before attempting the TPK. Samsung signing and device authorization still
apply on physical TVs; SDK development signing is not universal TV installation.

The official TV 10.0.6 x64 image and Tizen Studio 6.1 CLI are installed in this
cloud. Every Samsung component was checked against the vendor's SHA-256 records;
the download helper also pins the outer distribution. The image advertises .NET 8
and TizenFX TV API 12.12.0. This does not by itself validate this app's NUI calls.

This cloud has no `/dev/kvm`. The current emulator explicitly rejected startup
without hardware virtualization, including when software mode was requested.
The `samsung_emulator` CI job therefore uses an accelerated Ubuntu runner. Its
first run did not connect to SDB within the boot deadline; that is an unresolved
VM startup check, not a passed native-app test. The job captures launch logs and a
desktop screenshot, installs the unchanged development TPK, and checks startup
only after the VM connects. Native playback and physical device acceptance remain
separate. Chromium 69 checks cover browser compatibility, not the entire Tizen OS.
