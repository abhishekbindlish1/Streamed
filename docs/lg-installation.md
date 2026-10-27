# Install the LG webOS preview

Use the `.ipk` package on the LG TV's built-in webOS. The two `.apk` downloads
are Android builds: ARM64 for Android devices and x64 for the emulator sandbox.
An Android box connected to an LG screen still needs an Android app, rather
than the webOS app. Renaming an APK to IPK does not convert it.

The LG preview targets webOS TV 6.0 and newer. Its sample catalog and generated
video passed in LG's official TV 26 Simulator; installation and playback on your
particular TV still need to be checked. This preview uses sample media and does
not provide production account login or provider streams.

## Prepare the TV once

1. Install LG's **Developer Mode** app from the TV's app store and sign in with
   an LG Developer account. Follow [LG's Developer Mode
   instructions](https://webostv.developer.lge.com/develop/getting-started/developer-mode-app).
2. Turn **Dev Mode Status** on and restart the TV when prompted.
3. Reopen Developer Mode, turn **Key Server** on, and note the TV's IP address
   and the displayed passphrase.
4. Connect a computer to the same local network as the TV. You do not need an
   emulator or the full LG Simulator to install the package.

The package does not install through Android's package installer or by simply
opening a download in the TV browser. Developer Mode authorizes sideloading.
Keep its session active; expiry can remove development apps.

## Install from a computer

Install Node.js and the pinned webOS CLI on your computer:

```sh
npm install --global @webos-tools/cli@3.2.6
ares-config --profile tv
```

Add the TV, replacing `192.168.1.50` with the address shown by Developer Mode:

```sh
ares-setup-device --add streamed-lg --info "host=192.168.1.50" --info "port=9922" --info "username=prisoner"
ares-novacom --device streamed-lg --getkey
```

Enter the passphrase displayed on the TV in the CLI prompt. Keep that passphrase
and the retrieved connection key on your computer.

Unzip the LG build download, then run these commands in the folder containing
the `.ipk` file:

```sh
ares-install --device streamed-lg streamed-webos-development-v1.1.0-preview.6.ipk
ares-launch --device streamed-lg com.streamed.tv.webos.preview
```

Use the exact downloaded IPK filename if its version differs. The installed app
is **Streamed Preview**. If setup fails, share the TV model, webOS version, and
the exact CLI error, without the passphrase or private connection key.

## If an Android box is connected to the LG screen

Use the ARM64 Android APK on a compatible Android system, not the LG IPK. The
x64 sandbox APK is intended for an emulator. Our Android app requires API 26
(Android 8.0) or newer and the device APK currently requires a 64-bit ARM Android
OS. A processor's advertised capability alone does not establish the OS ABI.

With ADB connected, these read-only commands report the relevant device details:

```sh
adb shell getprop ro.product.cpu.abilist
adb shell getprop ro.build.version.sdk
```

An Android "App not installed" message needs the actual installer error and
device details to distinguish an ABI mismatch, an older OS, or an existing app
signed with a different development key. The TV/box model and installation
method determine which path applies.
