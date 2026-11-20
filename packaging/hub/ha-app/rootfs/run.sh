#!/usr/bin/env bash
# Entry point of the QoffeeAutomat hub as a Home Assistant app (PLAN.md S20, task T918).
#
# Runs as root only for the first steps: it reads the options of the app (/data/options.json, written by the
# Supervisor), translates them into QOFFEE_HUB_* settings, hands /data to the application user and then replaces
# itself with the hub running as that user. Nothing in here is a secret; certificate files are read from /ssl by
# the hub itself.

set -euo pipefail

readonly OPTIONS_FILE="/data/options.json"
readonly DATA_DIR="/data"
readonly SSL_DIR="/ssl"
readonly APP_USER_ID="${APP_UID:-1654}"

log() {
  printf '[qoffeeautomat-hub-app] %s\n' "$*"
}

die() {
  printf '[qoffeeautomat-hub-app] ERROR: %s\n' "$*" >&2
  exit 1
}

# option NAME: prints the value of an option, or nothing when it is not set.
option() {
  jq --raw-output --arg name "$1" '.[$name] // empty' "$OPTIONS_FILE"
}

[[ -r "$OPTIONS_FILE" ]] || die "$OPTIONS_FILE is missing; start this image through the Home Assistant Supervisor."

hub_name="$(option hub_name)"
ingress_role="$(option ingress_role)"
certfile="$(option certfile)"
keyfile="$(option keyfile)"

case "${ingress_role:-admin}" in
  admin | operator | viewer) ingress_role="${ingress_role:-admin}" ;;
  *) die "ingress_role must be admin, operator or viewer (got '$ingress_role')." ;;
esac

export QOFFEE_HUB_DATA_DIR="$DATA_DIR"
export QOFFEE_HUB_PORT="8490"
export QOFFEE_HUB_BIND="0.0.0.0"
if [[ -n "$hub_name" ]]; then
  export QOFFEE_HUB_NAME="$hub_name"
fi

# Ingress (S20, "Listeners"): a second listener on 8099 that serves only the Supervisor (172.30.32.2). The hub trusts
# the Home Assistant user headers on that listener and nowhere else, and maps the user to this role.
export QOFFEE_HUB_INGRESS_PORT="8099"
export QOFFEE_HUB_INGRESS_ALLOWED_ADDRESS="172.30.32.2"
export QOFFEE_HUB_INGRESS_ROLE="$ingress_role"

# TLS: the self-signed certificate of the hub unless the owner names a certificate in /ssl.
if [[ -n "$certfile" || -n "$keyfile" ]]; then
  [[ -n "$certfile" && -n "$keyfile" ]] || die "Set both certfile and keyfile, or neither."
  [[ "$certfile" != */* && "$keyfile" != */* ]] || die "certfile and keyfile are file names in /ssl, without a path."
  [[ -r "$SSL_DIR/$certfile" ]] || die "$SSL_DIR/$certfile does not exist or cannot be read."
  [[ -r "$SSL_DIR/$keyfile" ]] || die "$SSL_DIR/$keyfile does not exist or cannot be read."
  export QOFFEE_HUB_TLS="certificate"
  export QOFFEE_HUB_TLS_CERT="$SSL_DIR/$certfile"
  export QOFFEE_HUB_TLS_KEY="$SSL_DIR/$keyfile"
  log "TLS: certificate $certfile from /ssl."
else
  export QOFFEE_HUB_TLS="selfsigned"
  log "TLS: self-signed certificate of the hub."
fi

# The hub keeps its state private to the application user (S09): hand the volume over before dropping privileges.
mkdir -p "$DATA_DIR"
chown -R "$APP_USER_ID:$APP_USER_ID" "$DATA_DIR"
chmod 0700 "$DATA_DIR"

log "Starting the hub; Ingress on port 8099 (role ${ingress_role}), agents on port 8490."
exec setpriv --reuid="$APP_USER_ID" --regid="$APP_USER_ID" --clear-groups \
  dotnet /app/QoffeeAutomat.Hub.dll
