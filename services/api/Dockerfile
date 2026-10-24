# Multi-stage build for streamed-backend.
# Stage 1: build a static binary on golang:alpine.
# Stage 2: ship it on a tiny distroless base. Total image ~25 MB.

FROM golang:1.25-alpine AS build
WORKDIR /src

# Copy go.mod + go.sum first so the module-download layer caches even
# when only source files change.
COPY go.mod go.sum ./
RUN go mod download

COPY . .

# CGO disabled so the resulting binary is fully static and runs on
# distroless/static (no libc).
ENV CGO_ENABLED=0 GOOS=linux
RUN go build -ldflags="-s -w" -o /out/streamed-backend .

# Stage 2: runtime image. distroless/static includes ca-certificates
# (needed for HTTPS calls to streamed.pk and api.openai.com) and a
# nonroot user (uid 65532).
FROM gcr.io/distroless/static-debian12:nonroot

WORKDIR /app
COPY --from=build /out/streamed-backend /app/streamed-backend

# Cloud Run sets PORT; main.go reads it. The EXPOSE is informational.
EXPOSE 8080

# Cloud Run wipes the rootfs on cold start anyway, so the on-disk
# poster cache under /app/data is a best-effort warm cache for the
# lifetime of one container.
USER nonroot:nonroot
ENTRYPOINT ["/app/streamed-backend"]
