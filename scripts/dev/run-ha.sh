#!/usr/bin/env bash
# QoffeeAutomat - start a Home Assistant development container (PLAN.md S12, task T011).
#
# Starts the official Home Assistant container with custom_components/qoffeeautomat mounted
# read-only, so edits in the repository are picked up after a restart of Home Assistant. The
# configuration of the instance lives in .ha-dev/config (ignored by git) and survives
# restarts; delete .ha-dev to start from scratch.
#
# Usage:
#   bash scripts/dev/run-ha.sh [--version VERSION] [--host-network] [--port PORT]
#
# Options:
#   --version VERSION   Home Assistant image tag (default: 2026.11.1, or $HA_VERSION)
#   --host-network      use host networking so that mDNS discovery of the agent works
#                       (Linux only; the port option is ignored)
#   --port PORT         host port for the web interface (default: 8123)
#   -h, --help          show this help
#
# The web interface is then available at http://localhost:8123. The integration needs an
# agent: start one with scripts/dev/run-agent.sh. Inside the container the agent is
# reachable at host.docker.internal (or localhost with --host-network).

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
VERSION="${HA_VERSION:-2026.11.1}"
PORT=8123
HOST_NETWORK=0
CONTAINER_NAME="qoffeeautomat-ha-dev"
CONFIG_DIR="$ROOT/.ha-dev/config"

usage() { sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

while [ $# -gt 0 ]; do
  case "$1" in
    --version) VERSION="${2:?--version needs a value}"; shift 2 ;;
    --host-network) HOST_NETWORK=1; shift ;;
    --port) PORT="${2:?--port needs a value}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

command -v docker >/dev/null 2>&1 || { echo "Docker is required." >&2; exit 1; }

mkdir -p "$CONFIG_DIR"
if [ ! -f "$CONFIG_DIR/configuration.yaml" ]; then
  cat > "$CONFIG_DIR/configuration.yaml" <<'YAML'
# Development instance of Home Assistant for QoffeeAutomat (created by scripts/dev/run-ha.sh).
default_config:

logger:
  default: info
  logs:
    custom_components.qoffeeautomat: debug
YAML
fi

NETWORK_ARGS=(-p "$PORT:8123" --add-host host.docker.internal:host-gateway)
if [ "$HOST_NETWORK" -eq 1 ]; then
  NETWORK_ARGS=(--network host)
fi

echo "Starting Home Assistant $VERSION (container $CONTAINER_NAME)"
exec docker run --rm --name "$CONTAINER_NAME" \
  "${NETWORK_ARGS[@]}" \
  -e TZ="${TZ:-UTC}" \
  -v "$CONFIG_DIR:/config" \
  -v "$ROOT/custom_components/qoffeeautomat:/config/custom_components/qoffeeautomat:ro" \
  "ghcr.io/home-assistant/home-assistant:$VERSION"
