# Streamed

A Samsung/Tizen TV web app for browsing sports, following teams, managing viewer
profiles, and playing streams. The frontend uses plain HTML, CSS, and JavaScript;
the optional local Team Hub backend uses only Node.js built-in modules.

## Quick start

Use Node.js 24 LTS (`nvm use` if you use nvm). The app has no production npm
dependencies and no frontend build step. Install development tools for tests:

```sh
git clone https://github.com/abhishekbindlish1/Streamed.git
cd Streamed
cp data/config.example.js data/config.local.js
npm ci
npm start
```

Visit `http://localhost:3000` in a browser. Choose a viewer profile, then select
a sport to browse matches. The For You tab fills as you follow teams. Use the
arrow keys and Enter for TV-style navigation, or a mouse.
`PORT=8080 npm start` selects another port; `npm run dev` restarts the server
when its source changes. Do not overwrite an existing local configuration.

The app first requests the match API configured in `data/config.local.js`
(default: `https://streamed.pk`). If those requests fail or return no matches, it
loads `data/live-demo.json`. The demo contains sample events and public test
videos, not current match broadcasts. Only use streams you own or are authorized
to access.

## Project layout

```text
Streamed/
├── index.html                  # Browser and Tizen entry point
├── src/
│   ├── app.js                  # UI, profiles, TV navigation, and playback
│   ├── api.js                  # Match and Team Hub API clients
│   ├── compat.js               # Flex layout detection and Samsung remote keys
│   ├── compat.css              # Older browser layout fallbacks
│   └── styles.css
├── assets/profiles/            # Bundled profile avatars
├── data/
│   ├── config.example.js       # Copy to ignored config.local.js
│   ├── live-demo.json          # Fallback catalog and test streams
│   ├── sources.sample.json     # Example authorized stream catalog
│   └── team-db.js              # Bundled team metadata
├── server.js                   # Local HTTP server and Team Hub endpoints
├── config.xml                  # Tizen widget manifest
├── tizen_web_project.yaml      # Tizen project and packaging exclusions
├── icon.png
├── tests/                      # Unit, Playwright, and real Chromium 69 checks
├── .github/workflows/ci.yml     # Push and pull request checks
└── docs/
    ├── development.md          # Configuration, backend, and validation
    └── tizen.md                # Samsung TV installation
```

Run `npm run check` to check JavaScript syntax. See
[development notes](docs/development.md) for functional smoke checks and optional
Team Hub enrichment, and [Tizen setup](docs/tizen.md) for device installation.

Local settings, credentials, generated caches, and TV build/signing outputs are
ignored by Git. The included server is a development helper that serves the
project directory; keep it on a trusted development network.

GitHub Actions runs syntax checks, Node unit tests, actual Chromium 69 regression
checks, and Playwright browser tests for desktop and 720p/1080p TV layouts. See
[testing](docs/development.md#tests-and-ci) for commands and failure reports.

The older TV compatibility target is the 2020 Samsung TU series, including
UE55TU8505, running Tizen 5.5 / Chromium 69. Native playback and signed widget
installation still need verification on the TV; see [device checks](docs/tizen.md).
