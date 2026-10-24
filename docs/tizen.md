# Samsung TV / Tizen

## Compatibility target

The 2020 Samsung TU series, including UE55TU8505 (firmware family T-NKLDEUC), is
the older target: Tizen 5.5 with a Chromium 69 web engine. A firmware build number
such as 2743.0 is not the Tizen major version. See Samsung's
[model groups](https://developer.samsung.com/smarttv/develop/specifications/tv-model-groups.html)
and [web engine specifications](https://developer.samsung.com/smarttv/develop/specifications/web-engine-specifications.html).
Earlier Tizen versions are not covered by this compatibility pass, even though
the existing widget manifest has a lower install-time `required_version`.

The frontend uses physical CSS offsets instead of `inset`, plus feature-based
fallbacks for CSS `min()`, poster aspect ratios, and Flexbox gap. These work
without identifying a particular firmware string. Modern browsers keep their
native layout. Remote handling accepts legacy arrow names and numeric Samsung
Return/media/color keys even when `event.key` is vendor-specific. The widget
declares the `tv.inputdevice` privilege for media-key registration.

On TVs, HLS source ranking includes AVPlay support, so a missing CDN library does
not demote native HLS playback. HTML video remains the fallback if AVPlay fails.
The browser HLS.js dependency is pinned with an integrity hash and its bundle is
checked in the legacy engine.

## Install

The frontend lives at the project root so `config.xml` can continue to load
`index.html` directly. No JavaScript bundling or build step is required for the
browser app. Packaging and signing a TV widget require Tizen Studio, Samsung TV
extension packages, a certificate profile, and a compatible TV.

1. Install Tizen Studio and its Samsung TV extensions on your development machine.
2. On the TV, enable Apps developer mode, set your computer's IP, and restart.
3. Connect with `sdb connect TV_IP_ADDRESS` and confirm with `sdb devices`.
4. Import this repository as a Tizen Web Project.
5. Copy `data/config.example.js` to `data/config.local.js` if needed. For Team Hub,
   use your backend's network-reachable URL, not the TV's local widget origin or
   your computer's `localhost`. Keep all credentials on the backend.
6. Select a Samsung certificate profile and build a signed `.wgt` widget. The
   YAML project names its output `app`; use the actual generated filename.
7. Install it with `tizen install -n app.wgt -t TV_DEVICE_NAME`.

`tizen_web_project.yaml` lists frontend assets and excludes local environment
files, backend code, development metadata, and signing/build outputs. Before
installing, inspect the generated package to confirm it contains only intended
frontend assets. Never bundle API keys or certificate private keys. A local
browser check does not verify TV signing, installation, codecs, or AVPlay.

The app can use native video playback, HLS.js in compatible browsers, or Samsung
AVPlay on supported TVs. Provider CORS rules, authentication, DRM, iframe policy,
and codec support can affect playback independently of the app. Use only
authorized stream sources.

## On-device acceptance checks

Cloud tests cover a real Chromium 69 engine and mock the Samsung AVPlay API. They
do not verify the TV firmware, codecs, network, certificates, or native decoder.
After signing and sideloading the widget on the target TV:

1. Confirm the profile picker fills the display and a profile can be selected
   with arrows and OK. Add a profile and verify it survives relaunching the app.
2. Select a sport. Check poster proportions, focus visibility, navigation spacing,
   and scrolling. Open search and confirm Return closes it.
3. Open a match. Check that its stream picker fills the display and Return closes
   it. Test the Team Hub overlay too.
4. Play an authorized H.264/AAC HLS or MP4 source supported by the TV. Verify
   picture, sound, play/pause, next/previous stream, and Return to the listing.
5. Repeat a second playback after closing the first to check AVPlay cleanup. Test
   one unavailable stream to check fallback and a newer browser for regressions.
