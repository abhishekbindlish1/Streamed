# LG webOS TV preview

This port shares the frontend in `apps/web`; it has its own webOS manifest,
packaging and runtime checks. The initial compatibility target is webOS TV 6.0
and newer. The official Simulator job uses webOS TV 26; older hardware still needs
acceptance testing. This is a development preview, not an LG Content Store release.

From the repository root, with Node.js 24:

```sh
npm ci
npm run build:webos
```

The generated app is in `apps/webos/dist/app` and the installable `.ipk` is under
`apps/webos/dist/packages`. The package ID is `com.streamed.tv.webos.preview`.
The preview uses sample events and an owned H.264/AAC test video, with all code,
media and catalog assets local. It does not require a backend account or network
connection for its sample catalog and MP4 playback.

Enable Developer Mode on a compatible LG TV and configure it using LG's official
CLI. Then use `ares-install --device DEVICE PACKAGE.ipk` and
`ares-launch --device DEVICE com.streamed.tv.webos.preview`. Developer Mode and
TV authorization are prerequisites; this package does not enable them remotely.
Avoid putting credentials into the generated client config.

`scripts/setup-tv-sdks.py webos --root SDK_DIR --cache DOWNLOAD_DIR` prepares the
official Linux Simulator with pinned archive verification. It prints
`WEBOS_SIMULATOR_APPDIR`; export that value before running
`xvfb-run -a node scripts/webos-simulator-smoke.js`. SDK downloads remain outside
the checkout and are not redistributed as app assets. LG's JavaScript library is
bundled under its Apache 2.0 license; see `vendor/README.md`.

Magic Remote Back (461) closes overlays and playback before handing root Back to
LG's platform API. Media Play/Pause are explicit commands; delayed startup retries
must not undo Pause. Backgrounding the app stops active playback. The package
preloads its demo catalog so `file://` execution does not depend on local Fetch.

Browser contract tests, actual LG Simulator tests, and real TV acceptance are
different checks. The Simulator does not reproduce TV codecs, DRM or every Luna
API. Production provider streams and physical LG hardware are not validated by
the sample-media tests. See [TV testing](../../docs/tv-testing.md).
