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

The repository currently has Android, Tizen web and Tizen .NET clients. It does
not have a packaged LG webOS client. An APK or TPK cannot be installed into LG's
webOS Simulator. The browser client can be the basis of a webOS port, but that
still needs its webOS manifest, remote/media integration and acceptance tests.

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

The LG simulator has not been run in this cloud. Its official download/documentation
hosts were blocked by the environment proxy; the needed domains are saved in the
environment draft for review. Installing the CLI alone is not a simulator test.

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

No Samsung TV emulator has been run here. The cloud has no `/dev/kvm` or
VirtualBox device, and vendor SDK downloads are currently blocked by its proxy.
Older generic Tizen documentation describes software emulation, but this does not
prove support for a current Samsung TV image or this app's .NET runtime. Use a
suitable workstation/runner or a compatible TV for that validation. The existing
Chromium 69 web checks cover browser compatibility, not the entire Tizen OS.
