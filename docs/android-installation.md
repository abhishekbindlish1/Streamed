# Install on Android and Android TV

Use `streamed-android-arm-development-vVERSION.apk` on ARM Android devices and
TVs. It includes `armeabi-v7a` and `arm64-v8a` in one installer and requires
Android 8.0/API 26 or newer. Android TV 10 is API 29 and meets that requirement.
The screenshot of BRAVIA 4K VH21 identifies a Sony Android TV, rather than LG
webOS. It does not show whether its Android userspace is 32-bit or 64-bit.

Some TVs have a 64-bit processor but a 32-bit Android OS. Earlier Streamed device
downloads included only ARM64, so those systems had no compatible native code.
The new combined ARM build addresses that gap. The x86/x64 sandbox APK is for
emulators and connects to their local fixture server. The LG IPK and Samsung
TPK are for different operating systems.

## Install the device preview

Transfer the ARM device APK to the TV and use an installer that is allowed to
install unknown apps in the TV's security settings. Select the device APK, not
the emulator sandbox APK. This development preview retains the real pairing
flow; production account/provider playback and physical TV behavior still need
acceptance testing.

The new component version is 1.1.1, version code 3. Native minimum dimensions are
normalized so unset values cannot inflate native scroll content on
32-bit runtimes. Page bounds and initial focus are also checked before Android
draws focused buttons, which can otherwise allocate oversized ripple bitmaps.
Development builds are debug signed. A different signer from an already-installed build can prevent an update
even when the architecture and Android version are supported.

## Get the actual installation error

If the TV still reports only "App not installed", enable developer/ADB access
using the TV's supported settings and connect from a computer on the same local
network. Confirm the connection on the TV. These read-only commands report the
OS's supported ABIs and API level:

```sh
adb shell getprop ro.product.cpu.abilist
adb shell getprop ro.build.version.sdk
```

With the new device APK on that computer, try:

```sh
adb install -r streamed-android-arm-development-vVERSION.apk
```

Use the actual filename if the version differs. Share the exact `Failure [...]`
message and the two property outputs. Common causes are distinct:

| Installer error | Meaning |
| --- | --- |
| `INSTALL_FAILED_NO_MATCHING_ABIS` | The installed OS has no ABI supported by that APK; use the combined ARM package on ARM TVs. |
| `INSTALL_FAILED_OLDER_SDK` | The installed Android version is below the APK's minimum. |
| `INSTALL_FAILED_UPDATE_INCOMPATIBLE` | An existing app with the same ID uses a different signing certificate. |
| `INSTALL_FAILED_VERSION_DOWNGRADE` | The installed app has a higher version code. |

Uninstalling an existing app can resolve an identity conflict but also removes
its local data. Identify the error before taking that step. No signing private
keys or account credentials are needed to report an installer failure.

The screenshot alone does not establish which installer error occurred. The new
build's metadata and signatures can be checked automatically; installation on
the actual TV remains the final device acceptance check.
