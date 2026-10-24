# Development

The repository is a monorepo; see [architecture](architecture.md). Run npm commands
from the repository root. Web source/configuration is under `apps/web`, native
apps under `apps/android` and `apps/tizen-dotnet`, and Go services under `services`.
The root npm commands forward to the web workspace.

For native development use the SDK in `global.json`, Go from `.go-version`, a full
JDK 21, and Android platform 36/build-tools 36.0.0. This prepared cloud machine
retains those tools under `/workspace/.tools`; activate them with
`. /workspace/.tools/activate.sh`. Other machines should install those supported
toolchains normally. The Android SDK workload archives were explicitly signature
verified before use. Build-generated signing keys are development keys only.

```sh
dotnet test tests/Streamed.Core.Tests/Streamed.Core.Tests.csproj
dotnet build apps/tizen-dotnet/StreamedTv.csproj -c Debug
```

Run `go test ./...` separately in `services/api` and `services/extractor`.
See [sandbox](sandbox.md) for fixture-only Android startup and screenshot capture.

Run all commands from the repository root. Use the existing checkout in cloud
tasks; a separate Git worktree is unnecessary.

## Runtime and configuration

Node.js 24 LTS provides the built-in HTTP server and `fetch` API. The app has no
third-party production Node dependencies or generated bundles. Run `npm ci` to
install the pinned development dependencies (Playwright and Acorn). The browser
loads a pinned, integrity-checked HLS.js build from jsDelivr for HLS playback;
Samsung AVPlay is available only on supported TVs.

Copy `apps/web/data/config.example.js` to `apps/web/data/config.local.js` once, then adjust its
non-secret URLs. The local file is ignored by Git. The app also works without
it, but its request in `index.html` will return 404 and Team Hub backend access
will be disabled. Configuration keys:

| Key | Purpose |
| --- | --- |
| `DEMO_MODE` | `true` uses only the bundled sample catalog; public browser builds enable this. Source development defaults to `false`. |
| `INTERNAL_API_BASE_URL` | Match API origin; defaults to `https://streamed.pk`. |
| `TEAM_HUB_API_BASE_URL` | Team metadata backend. The example uses the local HTTP origin. |
| `TEAM_STANDINGS_API_BASE_URL` | Optional standings backend; empty disables lookups. |

The Node server implements Team Hub endpoints, not the match provider API. It
loads `.env.local` if present, preserving already-set process variables. Optional
server variables are `PORT` (default `3000`), `OPENAI_MODEL` (default
`gpt-4.1-mini`), and `OPENAI_API_KEY` for standings. Store credentials securely
on the server. Local environment files must stay out of Git and TV packages.

The development server serves files from the project directory, so use a trusted
development network and do not deploy it as a public production server.

## Start and validate

```sh
npm run check
npm start
```

From another terminal:

```sh
curl --fail http://localhost:3000/
curl --fail http://localhost:3000/data/live-demo.json
curl --fail http://localhost:3000/api/team-hub/teams
```

Confirm the page loads, the demo response has a nonempty `matches` array, and
the Team Hub response has a nonempty `teams` array. In a browser, create/select
a profile, select a sport to browse matches, open search, and open a match's
stream picker. A new profile's For You tab has no followed matches yet.
`npm run check` checks syntax only; use the test commands below for automated
regression checks. External stream playback and Samsung AVPlay require separate
checks on the intended browser/TV and network.

For a frontend-only session, `python3 -m http.server 8080` also serves the app,
but it does not implement the local Team Hub endpoints. Use HTTP rather than
opening `index.html` as a file because the catalog is loaded with `fetch`.

## Optional Team Hub enrichment

Team search uses `apps/web/data/team-db.js`, the loaded catalog, and browser-local cached
teams. No API key is needed for this base workflow. The Node server exposes:

| Endpoint | Behavior |
| --- | --- |
| `GET /api/team-hub/teams` | Bundled teams merged with generated enrichment caches. |
| `GET /api/ai/team-standings?team=Arsenal&teamKey=arsenal&sport=football` | Cached standings or an OpenAI lookup; returns 503 when no key is configured. |
| `GET /api/team-hub/backfill` | Fetch Wikipedia logo metadata and write the local team cache. |
| `GET /api/team-hub/backfill?standings=1` | Also request standings when a server-side key is configured. |

Backfill is manual: standings requests can use paid API calls. It writes ignored
`apps/web/data/team-backfill.json` and `apps/web/data/team-standings-cache.json`. Do not call it as
part of routine startup. To enable UI standings, configure the optional backend
URL and supply its server-side credential separately.

External services depend on the selected features: the match API, jsDelivr for
HLS.js, configured media/image hosts, Wikipedia for logo backfill, and OpenAI for
optional standings. Demo event dates and statuses are fixed fixtures.

## Tests and CI

```sh
npm ci
npm run check
npm test
npx playwright install chromium
npm run test:browser
npm run test:legacy
```

The framework uses Node's built-in test runner for unit/compatibility checks and
Playwright Test for browser workflows. Acorn guards the frontend's ES2018 syntax
baseline. Playwright runs desktop, Tizen-user-agent 1920x1080, and Tizen-user-agent
1280x720 projects, including an explicit test of missing Flexbox gap. The Samsung
AVPlay contract is mocked in browser tests: that verifies integration, not TV
decoding. External providers are blocked; fixtures keep tests deterministic and
avoid paid API calls. Browser test servers use port 3100 and are stopped by the
runner. The intentionally skipped desktop AVPlay case is TV-specific.

Browser tests also serve the built browser distribution under `/Streamed/` on
port 3102 and verify it runs without backend API requests or missing local assets.
`npm test` checks that ZIPs exclude private configuration, reject symlink inputs,
include the frontend assets, match their checksum, and reproduce byte for byte.
Building needs the system `zip` command; package tests also use `unzip`.

On cloud machines with Chromium already installed, use:

```sh
PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH=/usr/bin/chromium npm run test:browser
```

`npm run test:legacy` requires Linux x64 and Node.js 24 LTS. It downloads the
pinned Chromium 69 binary packaged in `chrome-aws-lambda@1.5.0` from npm, verifies
its SHA-512, and extracts it into a temporary directory. A direct DevTools harness
runs the actual engine at 720p and 1080p because current Playwright cannot launch
Chromium 69. It checks full-screen overlays, width constraints, poster aspect
ratios, flex spacing, profile persistence, search, stream selection, and numeric
Return events, and verifies that the pinned HLS.js build loads in that engine.
Browser page requests to external HTTPS providers are blocked. The fixture
server uses port 3101; the harness removes the temporary browser and stops its
own processes. Tests do not install Tizen Studio or sign a widget.

`.github/workflows/ci.yml` runs these commands on pushes and pull requests with
read-only repository permissions. It also supports manual runs. The hosted runner
installs Playwright's Chromium and Linux dependencies. Failed browser tests save
screenshots and Playwright traces; the workflow uploads `playwright-report/` and
`test-results/` for seven days. Inspect the HTML report with
`npx playwright show-report`. Mark the `test` check as required in GitHub branch
protection if merges should be blocked on failures.

After tests, the workflow retains a browser build artifact. Version tags publish
the ZIP and checksum to GitHub Releases; `main` can deploy its tested static site
to GitHub Pages. See [releases](releases.md) for the one-time Pages setting and
versioning procedure.
