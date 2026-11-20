#!/usr/bin/env bash
# QoffeeAutomat - smoke test of the hub container image (PLAN.md S20, task T918).
#
# Starts the image and checks what the acceptance criteria of T918 ask for:
#   * the process does not run as root,
#   * /data is a volume owned by the application user,
#   * the HEALTHCHECK of the image (GET /healthz) turns healthy,
#   * the unauthenticated /healthz answers over HTTPS with the self-signed certificate.
# It runs against the architecture of the Docker host, so CI calls it on an amd64 and on an arm64 runner.
#
# Usage:
#   bash packaging/hub/test-image.sh IMAGE [TIMEOUT_SECONDS]
#
# IMAGE is a local image name or a registry reference; TIMEOUT_SECONDS (default 120) is how long the health check
# may take to turn healthy. Needs the Docker CLI. Exit status: 0 when the image is fine, 1 otherwise.

set -euo pipefail

usage() { sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }
die() { echo "test-image.sh: $*" >&2; exit 1; }

if [ $# -lt 1 ]; then
  usage >&2
  exit 1
fi
case "$1" in
  -h | --help) usage; exit 0 ;;
  *) ;;
esac

image="$1"
timeout="${2:-120}"
[[ "$timeout" =~ ^[0-9]+$ ]] || die "TIMEOUT_SECONDS must be a number"
name="qoffeeautomat-hub-test-$$"

cleanup() {
  docker rm --force --volumes "$name" > /dev/null 2>&1 || true
}
trap cleanup EXIT

echo "Starting $image as $name"
docker run --detach --name "$name" --env QOFFEE_HUB_LOG_FORMAT=text "$image" > /dev/null

fail() {
  echo "test-image.sh: $*" >&2
  echo "--- container log ---" >&2
  docker logs "$name" >&2 || true
  exit 1
}

# 1. Not root.
uid="$(docker exec "$name" id -u)" || fail "the container is not running"
[ "$uid" != "0" ] || fail "the hub runs as root (uid 0)"
echo "OK: runs as uid $uid"

# 2. /data is a volume and belongs to the application user.
docker inspect --format '{{range .Mounts}}{{.Destination}}{{"\n"}}{{end}}' "$name" | grep -qx '/data' \
  || fail "/data is not a volume"
owner="$(docker exec "$name" stat --format '%u' /data)"
[ "$owner" = "$uid" ] || fail "/data belongs to uid $owner, not to uid $uid"
echo "OK: /data is a volume owned by uid $owner"

# 3. The health check of the image becomes healthy.
deadline=$((SECONDS + timeout))
status="starting"
while [ "$SECONDS" -lt "$deadline" ]; do
  status="$(docker inspect --format '{{if .State.Running}}{{.State.Health.Status}}{{else}}exited{{end}}' "$name")"
  [ "$status" = "healthy" ] && break
  [ "$status" != "exited" ] || fail "the container stopped"
  sleep 3
done
[ "$status" = "healthy" ] || fail "the health check is '$status' after ${timeout}s"
echo "OK: the health check is healthy"

# 4. /healthz over HTTPS, from inside the container (the self-signed certificate is not verified).
docker exec "$name" curl --fail --silent --insecure --max-time 5 "https://127.0.0.1:8490/healthz" > /dev/null \
  || fail "GET /healthz over HTTPS failed"
echo "OK: GET /healthz answers"
echo "The image $image passed."
