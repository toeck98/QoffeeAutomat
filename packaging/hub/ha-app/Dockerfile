# Home Assistant app image of the QoffeeAutomat hub (PLAN.md S20, D-024, task T918).
#
# A thin layer on the hub container image (packaging/hub/Dockerfile): the same binaries, plus the entry point
# run.sh, which turns the options of the app (/data/options.json) into QOFFEE_HUB_* settings and starts the hub.
# config.yaml names a prebuilt image, so the Supervisor pulls it from GHCR and does not build this file; the release
# workflow (job hub-app-image) builds it for amd64 and arm64 from the hub image of the same release.
#
# Local build (from the repository root, after building the hub image as "qoffeeautomat-hub"):
#
#   docker build -f packaging/hub/ha-app/Dockerfile --build-arg BUILD_FROM=qoffeeautomat-hub \
#     -t qoffeeautomat-hub-app packaging/hub/ha-app

ARG BUILD_FROM=ghcr.io/toeck98/qoffeeautomat-hub:latest
FROM ${BUILD_FROM}

LABEL org.opencontainers.image.title="QoffeeAutomat Hub (Home Assistant app)" \
      org.opencontainers.image.source="https://github.com/toeck98/QoffeeAutomat" \
      org.opencontainers.image.licenses="MIT"

# The Supervisor mounts /data owned by root. run.sh starts as root only to hand /data to the application user and
# then drops to it (setpriv, part of util-linux), so the hub itself never runs as root. jq reads options.json.
USER root
RUN apt-get update \
    && apt-get install --no-install-recommends --yes jq \
    && rm -rf /var/lib/apt/lists/*

COPY --chmod=0755 rootfs/ /

# The hub image defines a HEALTHCHECK; the Supervisor uses `watchdog` from config.yaml instead.
HEALTHCHECK NONE
ENTRYPOINT ["/run.sh"]
