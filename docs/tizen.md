# Samsung TV / Tizen

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
