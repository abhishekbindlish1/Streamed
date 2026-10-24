# Multi-stage build: build a tiny Go binary, then plant it on a
# Debian image that has chromium pre-installed. chromium is what
# adds ~250 MB to the image — Cloud Run ships layers on demand
# so cold start cost is the dominant variable, not GiB-stored.

FROM golang:1.24-bookworm AS builder
WORKDIR /src
COPY go.mod go.sum* ./
RUN go mod download
COPY . .
RUN CGO_ENABLED=0 GOOS=linux go build -ldflags="-s -w" -o /out/streamed-extractor .

FROM debian:bookworm-slim
# chromium + minimum fonts so HTML5 video probes don't crash on
# missing-font assertions. ca-certificates so HTTPS works against
# the wrapper hosts.
RUN apt-get update && apt-get install -y --no-install-recommends \
    chromium \
    ca-certificates \
    fonts-liberation \
    fonts-noto-color-emoji \
    && rm -rf /var/lib/apt/lists/*

# chromedp probes PATH for the browser binary; debian's chromium
# package installs it as /usr/bin/chromium. Tell chromedp explicitly
# in case PATH ordering changes in some future container base.
ENV CHROMEDP_BROWSER_BIN=/usr/bin/chromium

# Run as non-root for general hygiene + so chromium doesn't need
# the --no-sandbox flag for the wrong reason. (We still pass
# --no-sandbox in the chromedp opts because Cloud Run gen-2
# doesn't expose the kernel namespaces chromium uses for its
# sandbox — that's a Cloud Run constraint, not a security choice.)
RUN useradd -m -u 1000 extractor
USER extractor
WORKDIR /home/extractor

COPY --from=builder --chown=extractor:extractor /out/streamed-extractor .

ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["./streamed-extractor"]
