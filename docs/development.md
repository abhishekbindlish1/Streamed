# Development

Run all commands from the repository root. Use the existing checkout in cloud
tasks; a separate Git worktree is unnecessary.

## Runtime and configuration

Node.js 24 LTS provides the built-in HTTP server and `fetch` API. There are no
third-party Node dependencies, generated bundles, or required installation
commands. The browser loads HLS.js from jsDelivr for HLS playback; Samsung AVPlay
is available only on supported TVs.

Copy `data/config.example.js` to `data/config.local.js` once, then adjust its
non-secret URLs. The local file is ignored by Git. The app also works without
it, but its request in `index.html` will return 404 and Team Hub backend access
will be disabled. Configuration keys:

| Key | Purpose |
| --- | --- |
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
`npm run check` checks syntax only; the project does not yet include an automated
test suite. External stream playback and Samsung AVPlay require separate checks
on the intended browser/TV and network.

For a frontend-only session, `python3 -m http.server 8080` also serves the app,
but it does not implement the local Team Hub endpoints. Use HTTP rather than
opening `index.html` as a file because the catalog is loaded with `fetch`.

## Optional Team Hub enrichment

Team search uses `data/team-db.js`, the loaded catalog, and browser-local cached
teams. No API key is needed for this base workflow. The Node server exposes:

| Endpoint | Behavior |
| --- | --- |
| `GET /api/team-hub/teams` | Bundled teams merged with generated enrichment caches. |
| `GET /api/ai/team-standings?team=Arsenal&teamKey=arsenal&sport=football` | Cached standings or an OpenAI lookup; returns 503 when no key is configured. |
| `GET /api/team-hub/backfill` | Fetch Wikipedia logo metadata and write the local team cache. |
| `GET /api/team-hub/backfill?standings=1` | Also request standings when a server-side key is configured. |

Backfill is manual: standings requests can use paid API calls. It writes ignored
`data/team-backfill.json` and `data/team-standings-cache.json`. Do not call it as
part of routine startup. To enable UI standings, configure the optional backend
URL and supply its server-side credential separately.

External services depend on the selected features: the match API, jsDelivr for
HLS.js, configured media/image hosts, Wikipedia for logo backfill, and OpenAI for
optional standings. Demo event dates and statuses are fixed fixtures.
