# Streamed web / Tizen widget

The working browser app and older Tizen web implementation live here. Run the
following from the repository root with Node.js 24:

```sh
npm ci
cp apps/web/data/config.example.js apps/web/data/config.local.js
npm start
```

Preserve existing local configuration. `npm run sandbox` starts a fixture-only
demo without credentials; `npm run build` packages the static browser release.
The app has no production npm dependencies. Development dependencies are pinned
in the repository's workspace lockfile.

See [development](../../docs/development.md), [releases](../../docs/releases.md),
and [web TV installation](../../docs/tizen.md). Tizen 5.5 / Chromium 69 is the older
web compatibility target. The separate .NET app targets Tizen 9.
