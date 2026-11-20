#!/usr/bin/env bash
# QoffeeAutomat - smoke test of the Home Assistant app image of the hub (PLAN.md S20, task T918).
#
# Starts the app image the way the Supervisor does (options in /data/options.json, an empty /data folder owned by
# root) and checks that run.sh hands /data to the application user, drops privileges (the hub, which is process 1
# after run.sh has replaced itself, must not run as root) and that the hub answers GET /healthz.
# The ingress listener (port 8099) is not part of this test; it belongs to the hub tests.
#
# Usage:
#   bash packaging/hub/test-app-image.sh IMAGE [TIMEOUT_SECONDS]
#
# IMAGE is the app image (built from packaging/hub/ha-app/Dockerfile); TIMEOUT_SECONDS defaults to 120.
# Needs the Docker CLI. Exit status: 0 when the image is fine, 1 otherwise.

set -euo pipefail

usage() { sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }
die() { echo "test-app-image.sh: $*" >&2; exit 1; }

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
name="qoffeeautomat-hub-app-test-$$"
data_dir="$(mktemp -d)"

cleanup() {
  docker rm --force "$name" > /dev/null 2>&1 || true
  rm -rf "$data_dir"
}
trap cleanup EXIT

printf '{"hub_name": "App test hub", "ingress_role": "admin"}\n' > "$data_dir/options.json"
chmod 0644 "$data_dir/options.json"

echo "Starting $image as $name"
docker run --detach --name "$name" --volume "$data_dir:/data" "$image" > /dev/null

fail() {
  echo "test-app-image.sh: $*" >&2
  echo "--- container log ---" >&2
  docker logs "$name" >&2 || true
  exit 1
}

deadline=$((SECONDS + timeout))
until docker exec "$name" curl --fail --silent --insecure --max-time 5 "https://127.0.0.1:8490/healthz" > /dev/null 2>&1; do
  [ "$(docker inspect --format '{{.State.Running}}' "$name")" = "true" ] || fail "the container stopped"
  [ "$SECONDS" -lt "$deadline" ] || fail "GET /healthz did not answer within ${timeout}s"
  sleep 3
done
echo "OK: GET /healthz answers"

uid="$(docker exec "$name" awk '/^Uid:/ { print $2 }' /proc/1/status)"
[ -n "$uid" ] && [ "$uid" != "0" ] || fail "the hub runs as root (uid '$uid')"
echo "OK: the hub runs as uid $uid"

owner="$(docker exec "$name" stat --format '%u' /data)"
[ "$owner" = "$uid" ] || fail "/data belongs to uid $owner, not to uid $uid"
echo "OK: /data belongs to uid $owner"
echo "The image $image passed."
