#!/usr/bin/env bash
# QoffeeAutomat - test the .deb and the tarball in a container that runs systemd (PLAN.md S13, T802, T804).
#
# Starts a privileged container with systemd as PID 1 from a Debian or Ubuntu image, then
#   1. installs the .deb with apt, waits for /healthz, checks the unit, the directory modes and the session unit,
#   2. removes it (the service is stopped, the data stays), installs it again and purges it (the data is gone),
#   3. installs the tarball with install.sh, waits for /healthz and runs uninstall.sh --purge-data.
#
# Usage:
#   bash packaging/linux/test-packages.sh --image IMAGE --deb FILE [--tarball FILE]
#
# Options:
#   --image IMAGE    base image, for example ubuntu:latest or debian:stable (required)
#   --deb FILE       the .deb to test (required)
#   --tarball FILE   the tarball to test with install.sh (optional)
#   -h, --help       show this help
#
# Needs Docker and a runner that allows privileged containers with the host cgroup namespace (GitHub-hosted
# Ubuntu runners do). The container is removed at the end.

set -euo pipefail

IMAGE=""
DEB=""
TARBALL=""

usage() { sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }
die() { echo "test-packages.sh: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --image) [ $# -ge 2 ] || die "--image needs a value"; IMAGE="$2"; shift 2 ;;
    --deb) [ $# -ge 2 ] || die "--deb needs a value"; DEB="$2"; shift 2 ;;
    --tarball) [ $# -ge 2 ] || die "--tarball needs a value"; TARBALL="$2"; shift 2 ;;
    -h | --help) usage; exit 0 ;;
    *) die "unknown option '$1' (try --help)" ;;
  esac
done
[ -n "$IMAGE" ] || die "--image is required"
[ -f "$DEB" ] || die "--deb must name an existing file"
[ -z "$TARBALL" ] || [ -f "$TARBALL" ] || die "--tarball must name an existing file"

TAG="qoffeeautomat-systemd-test:$(printf '%s' "$IMAGE" | tr -c 'A-Za-z0-9\n' '-')"
CONTAINER="qoffeeautomat-test-$$"

cleanup() {
  docker rm -f "$CONTAINER" > /dev/null 2>&1 || true
}
trap cleanup EXIT

in_box() { docker exec "$CONTAINER" bash -c "$1"; }
step() { echo "::group::$*"; }
end_step() { echo "::endgroup::"; }
fail() {
  echo "::error::$*"
  docker exec "$CONTAINER" journalctl -u qoffeeautomat --no-pager -n 80 || true
  exit 1
}

wait_healthy() {
  for _ in $(seq 1 60); do
    if in_box "curl -fsS http://127.0.0.1:8480/healthz > /dev/null"; then
      echo "/healthz answered"
      return 0
    fi
    sleep 2
  done
  fail "/healthz did not answer within 120 seconds"
}

check_mode() {
  local path="$1" expected="$2" actual
  actual="$(in_box "stat -c '%a %U' '$path'")"
  [ "$actual" = "$expected root" ] || fail "$path is '$actual', expected '$expected root'"
}

step "Build the systemd image from $IMAGE"
docker build --quiet -t "$TAG" - << DOCKERFILE
FROM $IMAGE
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
    && apt-get install --no-install-recommends --yes systemd systemd-sysv dbus curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
STOPSIGNAL SIGRTMIN+3
CMD ["/sbin/init"]
DOCKERFILE
end_step

step "Start the container"
docker run -d --name "$CONTAINER" --privileged --cgroupns=host \
  --tmpfs /run --tmpfs /run/lock -v /sys/fs/cgroup:/sys/fs/cgroup:rw "$TAG" > /dev/null
state=""
for _ in $(seq 1 60); do
  state="$(docker exec "$CONTAINER" systemctl is-system-running 2> /dev/null || true)"
  case "$state" in running | degraded) break ;; esac
  sleep 1
done
case "$state" in running | degraded) ;; *) fail "systemd did not start (state '$state')" ;; esac
end_step

step "Install the .deb"
docker cp "$DEB" "$CONTAINER:/tmp/qoffeeautomat.deb"
in_box "apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install --yes /tmp/qoffeeautomat.deb"
in_box "systemctl is-active --quiet qoffeeautomat" || fail "the service is not active after the installation"
in_box "systemctl is-enabled --quiet qoffeeautomat" || fail "the service is not enabled"
wait_healthy
in_box "qoffeeautomat version"
for dir in /etc/qoffeeautomat /var/lib/qoffeeautomat /var/log/qoffeeautomat; do
  check_mode "$dir" 700
done
in_box "systemctl --global is-enabled qoffeeautomat-session.service" > /dev/null || fail "the session unit is not enabled for all users"
in_box "systemd-analyze verify --user /usr/lib/systemd/user/qoffeeautomat-session.service" || echo "::warning::systemd-analyze verify of the session unit reported problems"
in_box "find /etc/qoffeeautomat /var/lib/qoffeeautomat -type f -perm /077 | grep -q ." && fail "a data file is accessible by group or others"
end_step

step "Remove and purge the .deb"
in_box "apt-get remove --yes qoffeeautomat"
in_box "systemctl is-active --quiet qoffeeautomat" && fail "the service still runs after the removal"
[ -z "$(in_box 'ls /usr/lib/qoffeeautomat 2> /dev/null || true')" ] || fail "program files remain after the removal"
in_box "test -d /var/lib/qoffeeautomat" || fail "the data was deleted by remove (only purge may do that)"
in_box "DEBIAN_FRONTEND=noninteractive apt-get install --yes /tmp/qoffeeautomat.deb"
wait_healthy
in_box "apt-get purge --yes qoffeeautomat"
in_box "test ! -e /etc/qoffeeautomat && test ! -e /var/lib/qoffeeautomat && test ! -e /var/log/qoffeeautomat" || fail "purge left data behind"
in_box "test ! -e /usr/lib/systemd/system/qoffeeautomat.service" || fail "the unit remains after the purge"
end_step

if [ -n "$TARBALL" ]; then
  step "Install the tarball"
  docker cp "$TARBALL" "$CONTAINER:/tmp/qoffeeautomat.tar.gz"
  in_box "mkdir /tmp/tarball && tar -xzf /tmp/qoffeeautomat.tar.gz -C /tmp/tarball && cd /tmp/tarball/* && ./install.sh"
  in_box "systemctl is-active --quiet qoffeeautomat" || fail "the service is not active after install.sh"
  wait_healthy
  in_box "/usr/local/bin/qoffeeautomat version"
  check_mode /etc/qoffeeautomat 700
  in_box "/usr/local/lib/qoffeeautomat/uninstall.sh --purge-data"
  in_box "systemctl is-active --quiet qoffeeautomat" && fail "the service still runs after uninstall.sh"
  in_box "test ! -e /usr/local/lib/qoffeeautomat && test ! -e /etc/qoffeeautomat" || fail "uninstall.sh left files behind"
  end_step
fi

echo "All package tests passed on $IMAGE."
