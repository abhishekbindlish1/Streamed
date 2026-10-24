# streamed-backend

A small Go HTTP service that enriches the [Streamed TV](../src/StreamedTv/)
Tizen .NET app with team, league, and fixture data. The TV app already
shows match listings from streamed.pk; this backend adds "next match",
"current standings", and "league summary" enrichment.

## Quick start

```bash
make run            # listens on :8080 with stub data
curl http://localhost:8080/v1/health
```

Override the port via `PORT=9090 make run`.

## Endpoints

All responses are JSON and include a `source` field that identifies
where the data came from (`stub` or `openai-llm-summary`).

| Method | Path                                   | Returns                |
| ------ | -------------------------------------- | ---------------------- |
| GET    | `/v1/health`                           | `{status, uptime}`     |
| GET    | `/v1/teams/{id}/standings`             | team standings row     |
| GET    | `/v1/teams/{id}/next-match`            | next fixture           |
| GET    | `/v1/leagues/{id}/standings`           | full league table      |
| GET    | `/v1/posters`                          | manifest of cached posters + logos |
| POST   | `/v1/posters/refresh`                  | kicks the cron once    |
| GET    | `/v1/posters/{matchId}.png`            | match poster image     |
| GET    | `/v1/teams/{teamSlug}/logo.png`        | team crest image       |

### Curl recipes

```bash
curl http://localhost:8080/v1/health
curl http://localhost:8080/v1/teams/arsenal/standings
curl http://localhost:8080/v1/teams/arsenal/next-match
curl http://localhost:8080/v1/leagues/epl/standings

# Poster + logo asset pipeline (requires POSTER_CRON_ENABLED + OPENAI_API_KEY):
curl -X POST http://localhost:8080/v1/posters/refresh           # kick the cron now
curl http://localhost:8080/v1/posters                            # manifest of all cached art
curl -o poster.png http://localhost:8080/v1/posters/{matchId}.png
curl -o logo.png   http://localhost:8080/v1/teams/{teamSlug}/logo.png
```

Stub team IDs available out of the box: `arsenal`, `man-city`,
`liverpool`, `chelsea`, `spurs`. Stub league IDs: `epl`, `laliga`.

## Data sources

The service is intentionally honest about where data comes from. The
TV client should look at the `source` field and badge or warn the user
accordingly.

### 1. Stub mode (default)

When `OPENAI_API_KEY` is unset, every response is hardcoded
plausible-looking data with `"source": "stub"`. This lets the TV app
integrate against the API contract without any external dependency.

### 2. OpenAI mode (opt-in)

Set `OPENAI_API_KEY` (and optionally `OPENAI_MODEL`, default
`gpt-4o-mini`) and the service will ask an OpenAI chat-completions
model to produce a JSON payload matching each endpoint's schema.
Responses are tagged `"source": "openai-llm-summary"` and include
`"warning": "estimated"` because text LLMs do not have live sports
data and **will hallucinate**. This mode exists to demo the wiring,
not to serve real users.

```bash
export OPENAI_API_KEY=sk-...   # never commit; never log; never hardcode
make run
```

If the OpenAI call fails for any reason (network, bad JSON, rate
limit), the source transparently falls back to stub data so the API
never returns 500 due to an LLM hiccup.

### 3. Production: swap in a real sports API

The clean way to take this to production is to add a third
`data.Source` implementation backed by a live sports API. Good
options:

- **football-data.org** — free tier covers EPL, La Liga, etc. with
  standings + fixtures. Requires an API key.
- **TheSportsDB** — open data, broad sport coverage, lower freshness.
- **ESPN unofficial JSON** — undocumented but stable feeds at
  `site.api.espn.com`. No key, no SLA.

The TV client only knows the JSON shapes in
[`internal/api/types.go`](internal/api/types.go); any source that
returns those shapes is a drop-in replacement.

## Configuration

| Env var                | Purpose                                                 |
| ---------------------- | ------------------------------------------------------- |
| `PORT`                 | Listen port. Default `8080`.                            |
| `OPENAI_API_KEY`       | Enables OpenAI-backed text source. Also enables OpenAI image generation when `IMAGE_PROVIDER=openai`. Read from env, never logged. |
| `OPENAI_MODEL`         | OpenAI chat model. Default `gpt-4o-mini`.               |
| `OPENAI_IMAGE_MODEL`   | OpenAI images model used when `IMAGE_PROVIDER=openai`. Default `gpt-image-1`. |
| `IMAGE_PROVIDER`       | Image generation backend: `pollinations` (default, free, no auth) or `openai` (paid, requires `OPENAI_API_KEY`). |
| `POLLINATIONS_MODEL`   | Pollinations model. Default `flux` (best quality). `turbo` for faster generations. |
| `POSTER_CRON_ENABLED`  | Set to `true` to enable the daily poster + logo generation goroutine. Off by default. |
| `CRON_INTERVAL`        | How often the poster cron runs. Go duration string. Default `24h`. |

## Image generation providers

The poster + logo cron generates art via a pluggable `ImageClient`.
Pick the backend with `IMAGE_PROVIDER`:

### Pollinations (default)

`IMAGE_PROVIDER=pollinations` — calls
`https://image.pollinations.ai/prompt/{url-encoded-prompt}` with the
`flux` model. **Free, no API key required.** The anonymous tier rate-limits
to roughly **1 request per 15 s**, which the client honors with a small
internal spacer (the cron's concurrency=4 effectively serializes behind
the spacer). Each prompt produces a deterministic seed (FNV hash) so
re-runs yield the same image — this keeps the on-disk cache idempotent
and lets you reproduce a poster you liked. Per-image latency is typically
**3–15 s** when the model isn't cold.

```bash
IMAGE_PROVIDER=pollinations POSTER_CRON=true ./deploy.sh
```

### OpenAI Images (paid)

`IMAGE_PROVIDER=openai` — calls `https://api.openai.com/v1/images/generations`
with `gpt-image-1` (overridable via `OPENAI_IMAGE_MODEL`). Requires
`OPENAI_API_KEY` with billing enabled on the account. **About $0.04 per
image on `gpt-image-1`**, which the cron's per-run cap of 20 images
bounds to roughly **$0.80/day** at the cap.

```bash
OPENAI_API_KEY=sk-... IMAGE_PROVIDER=openai POSTER_CRON=true ./deploy.sh
```

If the configured provider is missing its credentials (e.g. `openai`
without `OPENAI_API_KEY`), the generator runs as a no-op and the cron
logs a warning — the service stays up and serves any previously cached
PNGs.

## Poster + logo pipeline

When `POSTER_CRON_ENABLED=true`, the service runs a goroutine that:

1. Fires once on startup (so you can verify wiring without waiting),
   then every `CRON_INTERVAL` after that.
2. Fetches the cricket listings from `https://streamed.pk/api/matches/cricket`.
3. For each match, generates one cinematic poster PNG and one logo PNG
   per team via the configured `IMAGE_PROVIDER` (default
   [Pollinations](https://pollinations.ai), free; or OpenAI Images if
   you set `IMAGE_PROVIDER=openai`).
4. Caches everything to disk: `data/posters/{matchId}.png` and
   `data/logos/{teamSlug}.png`. Every run is idempotent — if a file is
   already on disk it is **never** regenerated.
5. The cap is **20 NEW images per run** (combined posters + logos).
   With Pollinations that's free; with OpenAI Images at ~$0.04 each
   that bounds spend to **about $0.80/day** at the cap. Already-cached
   items don't count.
6. Concurrency is 4 in-flight image requests, simple semaphore. (The
   Pollinations client also enforces a 15 s client-side rate-limit
   spacer between requests to honor the anonymous-tier limit.)

`POST /v1/posters/refresh` kicks the loop on demand and returns
`{"status": "started"}` (or `"already-running"` if a pass is in
flight). `GET /v1/posters` returns a manifest of everything currently
cached:

```json
{
  "posters": [
    {"matchId": "abc", "url": "/v1/posters/abc.png", "generatedAt": "2026-05-08T01:23:45Z", "sizeBytes": 1310720}
  ],
  "logos": [
    {"teamSlug": "northern-xi", "teamName": "Northern XI", "url": "/v1/teams/northern-xi/logo.png", "generatedAt": "2026-05-08T01:23:48Z", "sizeBytes": 870144}
  ]
}
```

Without a working image client (e.g. `IMAGE_PROVIDER=openai` with no
`OPENAI_API_KEY`) the cron logs a warning and runs as a no-op — no
crash. Without `POSTER_CRON_ENABLED=true` the goroutine never starts,
but `/v1/posters/*` still serves anything already on disk.

## Layout

```
backend/
├── main.go                    entry point + graceful shutdown + cron wiring
├── main_test.go               smoke tests against the stub source
├── internal/
│   ├── api/types.go           response shapes (the TV contract)
│   ├── data/stub.go           hardcoded source
│   ├── data/openai.go         LLM-backed text source with stub fallback
│   ├── posters/generator.go   poster + logo generation, slugify, on-disk cache
│   ├── cron/cron.go           daily goroutine that drives the pipeline
│   └── server/server.go       router, CORS, logging, handlers (incl. asset serve)
├── data/posters/              generated PNGs (gitignored)
├── data/logos/                generated PNGs (gitignored)
├── go.mod
├── Makefile
└── .gitignore
```

## Operational details

- **Routing:** stdlib `net/http` with Go 1.22 `ServeMux` path patterns
  (`GET /v1/teams/{id}/standings`). No third-party router.
- **CORS:** `Access-Control-Allow-Origin: *`. Tighten before exposing
  publicly.
- **Logging:** stdlib `log/slog` JSON handler. Each request gets one
  access log line. The OpenAI key is never logged.
- **Graceful shutdown:** `SIGINT` / `SIGTERM` triggers a 10s drain.

## Make targets

```bash
make run        # go run .
make build      # produces bin/streamed-backend
make test       # go test ./...
make tidy       # go mod tidy
make clean      # remove bin/
```

## Deploying to Google Cloud Run

The service is a single static binary that listens on `$PORT` — exactly what
Cloud Run expects. A multi-stage `Dockerfile` builds it onto
`gcr.io/distroless/static-debian12:nonroot` (~25 MB image).

### One-time GCP setup

```bash
gcloud auth login
gcloud config set project <YOUR_PROJECT_ID>
gcloud services enable \
  run.googleapis.com \
  cloudbuild.googleapis.com \
  artifactregistry.googleapis.com
```

### Deploy

```bash
# Defaults: project from gcloud config, region us-central1, cron ON, model gpt-image-1
OPENAI_KEY=sk-... ./deploy.sh

# Override region / project / interval:
PROJECT=my-proj REGION=us-west1 CRON_INTERVAL=6h ./deploy.sh

# Without an OpenAI key (stub-only mode, no image generation):
POSTER_CRON=false ./deploy.sh
```

`deploy.sh` calls `gcloud run deploy --source .` which streams the directory
to Cloud Build, builds the container, pushes it to Artifact Registry, and
rolls out a new revision. First deploy takes ~3 min; subsequent ones ~1 min.
On success the script prints the public service URL — paste that into
`src/StreamedTv/Services/SportsDataService.cs` (the `DefaultBaseUrl`
constant) so the TV app calls Cloud Run instead of your laptop.

### Cloud Run gotchas to know about

- **Ephemeral disk.** Cloud Run wipes the container filesystem on every cold
  start. The `data/posters/` and `data/logos/` cache survives only for the
  lifetime of one container instance. With `min-instances=0` (the default,
  to stay free) you'll regenerate posters whenever the service has been idle
  long enough to scale to zero. The 20-image-per-run cap means a worst-case
  cold start costs ~$0.80 of OpenAI image credits. To make the cache
  durable: bump `--min-instances 1` (always-on, ~$5–10/mo) **or** add a GCS
  bucket layer to the generator (TODO).
- **Cold-start latency.** A scale-from-zero request can take 2–4 s while the
  binary boots and the on-startup cron kicks off in the background. The TV
  app's `SportsDataService` has a 6 s timeout — fine for most cases.
- **Concurrent requests.** `--concurrency 80` means one container handles up
  to 80 requests at once. The poster cron uses an internal mutex so only one
  generation pass runs at a time even across handlers.
- **Cost ballpark.** With cron on, OpenAI key set, and a steady catalog of
  ~10 cricket fixtures: roughly **$1–3 / day** in OpenAI image fees plus
  Cloud Run free tier covering the HTTP serving. Without the cron: free
  tier covers everything.
