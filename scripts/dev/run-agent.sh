#!/usr/bin/env bash
# QoffeeAutomat - start the agent for development (PLAN.md S12, task T011).
#
# Runs the agent with Platform.Simulated (deterministic fake metrics and commands, works on
# any OS) and a temporary data directory that is deleted when the agent stops.
#
# Usage:
#   bash scripts/dev/run-agent.sh [--keep-data] [--data-dir DIR] [-- AGENT ARGS...]
#
# Options:
#   --keep-data      do not delete the temporary data directory on exit
#   --data-dir DIR   use DIR instead of a new temporary directory (never deleted)
#   -h, --help       show this help
#
# The agent listens on http://127.0.0.1:8480 (UI and API) and https://0.0.0.0:8481
# (QoffeeLink) once the host is implemented (task T201). Arguments after "--" are passed on
# to the agent.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DATA_DIR="${QOFFEEAUTOMAT_DATA_DIR:-}"
KEEP_DATA=0
CREATED_DATA_DIR=0
AGENT_ARGS=()

usage() { sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

while [ $# -gt 0 ]; do
  case "$1" in
    --keep-data) KEEP_DATA=1; shift ;;
    --data-dir) DATA_DIR="${2:?--data-dir needs a value}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    --) shift; AGENT_ARGS=("$@"); break ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

command -v dotnet >/dev/null 2>&1 || { echo "The .NET SDK (see global.json) is required." >&2; exit 1; }

if [ -z "$DATA_DIR" ]; then
  DATA_DIR="$(mktemp -d "${TMPDIR:-/tmp}/qoffeeautomat-dev.XXXXXX")"
  CREATED_DATA_DIR=1
else
  mkdir -p "$DATA_DIR"
fi

cleanup() {
  if [ "$CREATED_DATA_DIR" -eq 1 ] && [ "$KEEP_DATA" -eq 0 ]; then
    rm -rf "$DATA_DIR"
  elif [ "$CREATED_DATA_DIR" -eq 1 ]; then
    echo "Data directory kept: $DATA_DIR"
  fi
}
trap cleanup EXIT

export QOFFEEAUTOMAT_DATA_DIR="$DATA_DIR"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export DOTNET_NOLOGO=true

echo "Data directory: $DATA_DIR"
echo "Platform:       simulated"

dotnet run --project "$ROOT/src/QoffeeAutomat.Agent" -- \
  --platform simulated --data-dir "$DATA_DIR" ${AGENT_ARGS[@]+"${AGENT_ARGS[@]}"}
