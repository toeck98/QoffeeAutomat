#!/usr/bin/env bash
# Entry point of the end-to-end agent container (PLAN.md S14, task T508).
#
# First start: copies the seed configuration (settings, one PowerShell command with variables,
# one button command, one custom sensor) into the data directory. Then it runs the agent with the
# simulated platform, sets the password through the first-run setup (the setup token never leaves
# the container) and reports readiness by creating /tmp/ready, which the container health check
# waits for. Restarting the container keeps the data directory, so the pairing survives.
# E2E_AGENT_NAME (optional, first start only) replaces the agent name of the seed.

set -euo pipefail

DATA_DIR="${QOFFEEAUTOMAT_DATA_DIR:-/data}"
API="http://127.0.0.1:8480"

rm -f /tmp/ready

if [ ! -e "$DATA_DIR/config/settings.json" ]; then
  mkdir -p "$DATA_DIR/config"
  cp -r /seed/. "$DATA_DIR/config/"
  # The hub scenario (tests/e2e/hub, T919) runs two agents and names them through E2E_AGENT_NAME.
  if [ -n "${E2E_AGENT_NAME:-}" ]; then
    case "$E2E_AGENT_NAME" in
      *[!A-Za-z0-9._-]*)
        echo "e2e: E2E_AGENT_NAME may contain only letters, digits, '.', '_' and '-'" >&2
        exit 1
        ;;
      *) ;;
    esac
    sed -i "s/\"name\": \"E2E-PC\"/\"name\": \"$E2E_AGENT_NAME\"/" "$DATA_DIR/config/settings.json"
  fi
fi

# E2E_AGENT_PLATFORM=linux runs the real Linux platform (T804); the default is the simulated platform.
dotnet /app/QoffeeAutomat.Agent.dll \
  --platform "${E2E_AGENT_PLATFORM:-simulated}" --data-dir "$DATA_DIR" --ui-port 8480 --link-port 8481 &
agent_pid=$!
trap 'kill -TERM "$agent_pid" 2>/dev/null || true' TERM INT

for _ in $(seq 1 240); do
  if curl -fsS "$API/healthz" >/dev/null 2>&1 && [ -s "$DATA_DIR/secrets/setup-token.txt" ]; then
    break
  fi
  sleep 0.5
done

# The setup endpoint answers 404 once a password exists (a restart of the container).
if curl -fsS "$API/api/v1/auth/setup" >/dev/null 2>&1; then
  token="$(tr -d '\r\n' < "$DATA_DIR/secrets/setup-token.txt")"
  body="$(printf '{"setupToken":"%s","password":"%s"}' "$token" "$E2E_AGENT_PASSWORD")"
  curl -fsS -X POST "$API/api/v1/auth/setup" \
    -H 'Content-Type: application/json' -H 'X-QoffeeAutomat-Csrf: 1' \
    --data "$body" >/dev/null
  echo "e2e: password configured"
fi

touch /tmp/ready
echo "e2e: agent ready"

status=0
while kill -0 "$agent_pid" 2>/dev/null; do
  wait "$agent_pid" || status=$?
done
exit "$status"
