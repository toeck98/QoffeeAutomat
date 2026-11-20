#!/usr/bin/env bash
# Health check of the hub container (HEALTHCHECK of packaging/hub/Dockerfile, task T918).
#
# Asks GET /healthz, the unauthenticated liveness endpoint of the hub (PLAN.md S20), on the loopback address:
# first over HTTPS (the default, with the self-signed certificate of the hub, which is why the certificate is not
# verified: the request never leaves the container), then over plain HTTP (QOFFEE_HUB_TLS=none, behind a proxy).
# Exit status 0 means healthy.

set -euo pipefail

port="${QOFFEE_HUB_PORT:-8490}"
host="${QOFFEE_HUB_BIND:-127.0.0.1}"
case "$host" in
  "" | "0.0.0.0" | "::") host="127.0.0.1" ;;
  *:*) host="[${host}]" ;;
  *) ;;
esac

probe() {
  curl --fail --silent --max-time 4 --output /dev/null "$@"
}

probe --insecure "https://${host}:${port}/healthz" || probe "http://${host}:${port}/healthz"
