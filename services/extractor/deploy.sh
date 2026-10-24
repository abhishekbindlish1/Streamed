#!/usr/bin/env bash
# Deploy streamed-extractor to Cloud Run as a SEPARATE service.
# Mirrors the existing backend's deploy.sh pattern.
#
# Usage:
#   ./deploy.sh
#   PROJECT=my-proj REGION=us-west1 ./deploy.sh

set -euo pipefail

SERVICE="${SERVICE:-streamed-extractor}"
REGION="${REGION:-us-central1}"
DEFAULT_PROJECT="streamed-495716"
PROJECT="${PROJECT:-$(gcloud config get-value project 2>/dev/null)}"
PROJECT="${PROJECT:-$DEFAULT_PROJECT}"

if [[ -z "$PROJECT" ]]; then
  echo "ERROR: no GCP project set. Run 'gcloud config set project <PROJECT_ID>' or pass PROJECT=<id>." >&2
  exit 1
fi

# Same JWT signing key as the main backend — TV uses its existing
# pairing JWT to auth against the extractor too.
JWT_SIGNING_KEY="${JWT_SIGNING_KEY:-REDACTED_CONFIGURE_BEFORE_USE}"

# PUBLIC_BASE_URL is the URL the TV will use to reach this service.
# Cloud Run gives us this URL after the first deploy, so for new
# deployments we hardcode the known stable hostname rather than
# bootstrap-loop. Override if you're deploying to a different host.
PUBLIC_BASE_URL="${PUBLIC_BASE_URL:-https://streamed-extractor-1057053643049.us-central1.run.app}"

ENV_VARS="JWT_SIGNING_KEY=${JWT_SIGNING_KEY},PUBLIC_BASE_URL=${PUBLIC_BASE_URL}"

echo "Deploying ${SERVICE} to ${REGION} in project ${PROJECT}..."

# Notable resource flags vs. the main backend:
#   --memory 2Gi  : chromium plus a single tab fits comfortably in
#                   1Gi, but headroom prevents the OOM-killer
#                   firing on a slow page that loads a heavy bundle.
#   --cpu 1       : chromedp single-tab work is single-threaded
#                   in the hot path; more vCPU doesn't help much.
#   --max-instances 5 : personal-use cap. Each instance is one
#                   chromium process — keeps the total bill bounded.
#   --concurrency 1 : ONE extraction per instance at a time.
#                   chromium tabs share state; concurrent tabs in
#                   the same chromium process can leak between
#                   each other and trigger random races.
#   --timeout 60s : chromedp gives up at 20s anyway; this is the
#                   request-level cap.
gcloud run deploy "${SERVICE}" \
  --project "${PROJECT}" \
  --region "${REGION}" \
  --source . \
  --allow-unauthenticated \
  --port 8080 \
  --memory 2Gi \
  --cpu 1 \
  --concurrency 1 \
  --max-instances 5 \
  --timeout 60s \
  --set-env-vars "${ENV_VARS}" \
  --quiet

URL=$(gcloud run services describe "${SERVICE}" \
  --project "${PROJECT}" \
  --region "${REGION}" \
  --format='value(status.url)')

echo ""
echo "Deployed:"
echo "  ${URL}"
echo ""
echo "Smoke test:"
echo "  curl ${URL}/v1/health"
echo "  curl -X POST '${URL}/v1/extract?url=https://embedsports.top/embed/admin/admin-willow-cricket/3' \\"
echo "    -H 'Authorization: Bearer <YOUR_JWT>'"
