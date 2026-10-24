#!/usr/bin/env bash
# Deploy streamed-backend to Google Cloud Run.
#
# Prereqs (one-time):
#   gcloud auth login
#   gcloud config set project <PROJECT_ID>
#   gcloud services enable run.googleapis.com cloudbuild.googleapis.com artifactregistry.googleapis.com
#
# Usage:
#   ./deploy.sh                           # uses defaults below
#   PROJECT=my-proj REGION=us-west1 ./deploy.sh
#   OPENAI_KEY=sk-... POSTER_CRON=true ./deploy.sh
#
# What it does:
#   - Builds the container from ./Dockerfile using `gcloud run deploy --source .`
#     (Cloud Build picks it up automatically; no Artifact Registry setup needed
#     beyond the gcloud services enable above)
#   - Deploys as a public, unauthenticated service
#   - Pipes secrets in as env vars (OPENAI_API_KEY, POSTER_CRON_ENABLED, etc.)
#   - Prints the resulting service URL on success

set -euo pipefail

SERVICE="${SERVICE:-streamed-backend}"
REGION="${REGION:-us-central1}"
# Default GCP project ID for the Streamed personal account. Override by passing
# PROJECT=<other-id> on the command line, or by `gcloud config set project <id>`
# (we fall back to whatever gcloud has set if PROJECT is unset).
DEFAULT_PROJECT="streamed-495716"
PROJECT="${PROJECT:-$(gcloud config get-value project 2>/dev/null)}"
PROJECT="${PROJECT:-$DEFAULT_PROJECT}"

if [[ -z "$PROJECT" ]]; then
  echo "ERROR: no GCP project set. Run 'gcloud config set project <PROJECT_ID>' or pass PROJECT=<id>." >&2
  exit 1
fi

OPENAI_KEY="${OPENAI_KEY:-${OPENAI_API_KEY:-}}"
# Test-account fallback: hardcoded so deploys "just work" without remembering
# to source an env var. SAFE TO COMMIT only because this key belongs to a
# disposable OpenAI account that will be deleted post-testing. Replace with
# Secret Manager (`--update-secrets OPENAI_API_KEY=openai-api-key:latest`) the
# moment this graduates to a real account.
if [[ -z "$OPENAI_KEY" ]]; then
  OPENAI_KEY="REDACTED_CONFIGURE_BEFORE_USE"
fi
# Same disposable-test-account caveat as OPENAI_KEY: hardcoded so deploys
# don't need an env var. Move to Secret Manager when it goes prod.
FOOTBALL_DATA_KEY="${FOOTBALL_DATA_KEY:-REDACTED_CONFIGURE_BEFORE_USE}"
# CricAPI key: same disposable-test-account caveat as the others. Free tier
# is 100 req/day, so the cricapi source caches aggressively.
CRIC_API_KEY="${CRIC_API_KEY:-REDACTED_CONFIGURE_BEFORE_USE}"
CRIC_ENABLED="true"
# RapidAPI livescore disabled — monthly quota exhausted. Key retained for reference only.
POSTER_CRON="${POSTER_CRON:-true}"
CRON_INTERVAL="${CRON_INTERVAL:-24h}"
OPENAI_MODEL="${OPENAI_MODEL:-gpt-4o-mini}"
# OPENAI_BASE_URL points at any OpenAI-compatible /chat/completions
# endpoint. Empty = real OpenAI. Set to "https://api.groq.com/openai/v1"
# to route through Groq (free tier; pair with a Groq-supported
# OPENAI_MODEL like "llama-3.3-70b-versatile" or "llama-3.1-8b-instant").
OPENAI_BASE_URL="${OPENAI_BASE_URL:-}"
OPENAI_IMAGE_MODEL="${OPENAI_IMAGE_MODEL:-gpt-image-1}"
# Groq API key for the 2-step standings pipeline:
#   compound-beta-mini (web search → prose) → llama-3.3-70b-versatile (→ JSON).
# Free tier ~8000 TPM. Models overridable via GROQ_SEARCH_MODEL / GROQ_JSON_MODEL.
GROQ_API_KEY="${GROQ_API_KEY:-REDACTED_CONFIGURE_BEFORE_USE}"
GROQ_SEARCH_MODEL="${GROQ_SEARCH_MODEL:-compound-beta-mini}"
GROQ_JSON_MODEL="${GROQ_JSON_MODEL:-llama-3.3-70b-versatile}"
# Image generation provider. "pollinations" = free, no auth, ~1 req/15s
# (default since the test OpenAI account has $0 spend allowance and 400s
# every image request). Set to "openai" to use OpenAI Images instead
# (requires a working OPENAI_API_KEY with billing enabled).
IMAGE_PROVIDER="${IMAGE_PROVIDER:-pollinations}"
POLLINATIONS_MODEL="${POLLINATIONS_MODEL:-flux}"
# GCS bucket used for poster + logo persistence. Without this, the
# generator falls back to ./data on the container disk (wiped on every
# cold start). The bucket must already exist and the runtime SA needs
# roles/storage.objectAdmin on it.
STORAGE_BUCKET="${STORAGE_BUCKET:-streamed-backend-assets-1057053643049}"

# ---- Auth (TV pairing + JWT) ----
# Three vars wire the auth service. All three must be present or the
# /v1/auth/* + /v1/me routes serve 503 (and the rest of the API still
# works fine — useful for partial degradation).
#
# FIRESTORE_PROJECT — GCP project that hosts the Firestore database
#                     (we created it as the (default) DB in us-central1).
# JWT_SIGNING_KEY   — HS256 signing key, >= 32 bytes. The hardcoded value
#                     here is from `openssl rand -base64 48` — same
#                     "disposable test account" caveat as the API keys
#                     above. Move to Secret Manager when going prod.
# GOOGLE_CLIENT_ID  — OAuth Client ID for the web pairing page. MUST be
#                     created via GCP Console → APIs & Services →
#                     Credentials → Create OAuth Client ID (type: Web
#                     application). gcloud doesn't support creating
#                     these. Paste the value here once you have it.
FIRESTORE_PROJECT="${FIRESTORE_PROJECT:-streamed-495716}"
JWT_SIGNING_KEY="${JWT_SIGNING_KEY:-REDACTED_CONFIGURE_BEFORE_USE}"
GOOGLE_CLIENT_ID="${GOOGLE_CLIENT_ID:-1057053643049-s9n6b46p4eu8fq887b7f724282t3broa.apps.googleusercontent.com}"

ENV_VARS="POSTER_CRON_ENABLED=${POSTER_CRON},CRON_INTERVAL=${CRON_INTERVAL},OPENAI_MODEL=${OPENAI_MODEL},OPENAI_IMAGE_MODEL=${OPENAI_IMAGE_MODEL},IMAGE_PROVIDER=${IMAGE_PROVIDER},POLLINATIONS_MODEL=${POLLINATIONS_MODEL},STORAGE_BUCKET=${STORAGE_BUCKET},GROQ_SEARCH_MODEL=${GROQ_SEARCH_MODEL},GROQ_JSON_MODEL=${GROQ_JSON_MODEL}"
if [[ -n "$GROQ_API_KEY" ]]; then
  ENV_VARS="${ENV_VARS},GROQ_API_KEY=${GROQ_API_KEY}"
else
  echo "INFO: GROQ_API_KEY not set — Groq 2-step pipeline disabled; standings fall through to football-data/openai." >&2
fi
if [[ -n "$OPENAI_BASE_URL" ]]; then
  ENV_VARS="${ENV_VARS},OPENAI_BASE_URL=${OPENAI_BASE_URL}"
fi
if [[ -n "$OPENAI_KEY" ]]; then
  ENV_VARS="${ENV_VARS},OPENAI_API_KEY=${OPENAI_KEY}"
else
  echo "WARN: OPENAI_API_KEY not set — service will run with stub data only and the cron will skip generation." >&2
fi
if [[ -n "$FOOTBALL_DATA_KEY" ]]; then
  ENV_VARS="${ENV_VARS},FOOTBALL_DATA_KEY=${FOOTBALL_DATA_KEY}"
else
  echo "INFO: FOOTBALL_DATA_KEY not set — football queries fall through to stub/openai." >&2
fi
if [[ -n "$CRIC_API_KEY" ]]; then
  ENV_VARS="${ENV_VARS},CRIC_API_KEY=${CRIC_API_KEY},CRIC_ENABLED=${CRIC_ENABLED}"
else
  echo "INFO: CRIC_API_KEY not set — cricket queries fall through to stub/openai." >&2
fi
# RAPIDAPI_KEY intentionally omitted — livescore disabled (quota exhausted).

# Auth env. Only attached when GOOGLE_CLIENT_ID is set to a real value
# (not the placeholder). Without all three the auth routes 503 — which
# is the correct degraded behaviour, and the deploy still succeeds so
# the rest of the API is reachable while the user finishes the OAuth
# Console UI step.
if [[ "$GOOGLE_CLIENT_ID" == PASTE_* || -z "$GOOGLE_CLIENT_ID" ]]; then
  echo "WARN: GOOGLE_CLIENT_ID is unset/placeholder — /v1/auth/* + /v1/me will 503." >&2
  echo "      Create one at https://console.cloud.google.com/apis/credentials?project=${PROJECT}" >&2
  echo "      then re-run with GOOGLE_CLIENT_ID=<value>.apps.googleusercontent.com ./deploy.sh" >&2
else
  ENV_VARS="${ENV_VARS},FIRESTORE_PROJECT=${FIRESTORE_PROJECT},JWT_SIGNING_KEY=${JWT_SIGNING_KEY},GOOGLE_CLIENT_ID=${GOOGLE_CLIENT_ID}"
fi

echo "Deploying ${SERVICE} to ${REGION} in project ${PROJECT}..."

# --memory 512Mi: image generation buffers can spike; 256Mi was OOMing
# during base64 decode on dev runs.
# --cpu 1, --concurrency 80: defaults are fine for our QPS.
# --timeout 300: the on-startup cron pass can take a while when fully cold.
# --min-instances 0: scales to zero between requests (free when idle).
gcloud run deploy "$SERVICE" \
  --source . \
  --project "$PROJECT" \
  --region "$REGION" \
  --platform managed \
  --allow-unauthenticated \
  --memory 512Mi \
  --cpu 1 \
  --concurrency 80 \
  --timeout 300 \
  --min-instances 0 \
  --max-instances 4 \
  --set-env-vars "$ENV_VARS" \
  --quiet

URL=$(gcloud run services describe "$SERVICE" --project "$PROJECT" --region "$REGION" --format='value(status.url)')
echo
echo "Deployed:"
echo "  $URL"
echo
echo "Smoke test:"
echo "  curl $URL/v1/health"
echo "  curl $URL/v1/teams/lookup?name=Manchester+United"
echo "  curl $URL/v1/posters"
echo
echo "Wire the TV app:"
echo "  Edit src/StreamedTv/Services/SportsDataService.cs and set DefaultBaseUrl to:"
echo "    \"$URL\""
