# syntax=docker/dockerfile:1
# QoffeeAutomat hub container image (PLAN.md S20, D-024, task T918).
#
# Build from the repository root (the context must contain src/ and the Directory.* files):
#
#   docker build -f packaging/hub/Dockerfile -t qoffeeautomat-hub .
#   docker buildx build --platform linux/amd64,linux/arm64 -f packaging/hub/Dockerfile .
#
# The release workflow (.github/workflows/release.yml, job hub-image) builds this file for linux/amd64 and
# linux/arm64 and publishes the result to GHCR with a provenance attestation.
#
# * Multi-stage: the SDK stage always runs on the build machine (--platform=$BUILDPLATFORM) and publishes for the
#   target architecture, so an arm64 image is not compiled under emulation. Only the runtime stage runs per target.
# * Runs as the non-root user "app" (APP_UID of the .NET images, 1654). The data directory is /data, a volume.
# * No secret is baked in: the hub creates its keys, its TLS certificate and the one-time setup token in /data on the
#   first start. Configuration comes from QOFFEE_HUB_* environment variables (see docs/user/hub/docker.md).
# * The health check asks /healthz on the loopback address (packaging/hub/healthcheck.sh).

ARG DOTNET_VERSION=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
# Empty: the version of Directory.Build.props is used. The release workflow passes the version of the tag.
ARG VERSION=
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src ./src
# A runtime identifier (not "self-contained") makes publish copy the native SQLite library of the target architecture.
RUN set -eu; \
    case "$TARGETARCH" in \
      amd64) rid=linux-x64 ;; \
      arm64) rid=linux-arm64 ;; \
      *) echo "Unsupported architecture '$TARGETARCH' (linux/amd64 and linux/arm64 are supported)." >&2; exit 1 ;; \
    esac; \
    version_arg=""; \
    if [ -n "$VERSION" ]; then version_arg="-p:Version=$VERSION"; fi; \
    dotnet publish src/QoffeeAutomat.Hub/QoffeeAutomat.Hub.csproj \
      --configuration Release --runtime "$rid" --self-contained false \
      -p:UseAppHost=false $version_arg \
      --output /out

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
ARG VERSION=
ARG REVISION=
LABEL org.opencontainers.image.title="QoffeeAutomat hub" \
      org.opencontainers.image.description="Central hub of QoffeeAutomat: configuration, metrics and the fleet link for Home Assistant." \
      org.opencontainers.image.source="https://github.com/toeck98/QoffeeAutomat" \
      org.opencontainers.image.documentation="https://toeck98.github.io/QoffeeAutomat/user/hub/" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}"

# curl is only used by the health check.
RUN apt-get update \
    && apt-get install --no-install-recommends --yes curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /out /app
COPY --chmod=0755 packaging/hub/healthcheck.sh /usr/local/bin/qoffeeautomat-healthcheck

# The directory is created and owned by the application user here, so that a new named volume inherits that ownership.
# The hub tightens it to mode 0700 itself on start (S09).
RUN mkdir /data && chown "$APP_UID:$APP_UID" /data && chmod 0700 /data

ENV QOFFEE_HUB_DATA_DIR=/data \
    QOFFEE_HUB_PORT=8490 \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
VOLUME ["/data"]
EXPOSE 8490
USER $APP_UID

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["/usr/local/bin/qoffeeautomat-healthcheck"]

ENTRYPOINT ["dotnet", "/app/QoffeeAutomat.Hub.dll"]
