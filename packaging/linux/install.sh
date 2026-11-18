#!/usr/bin/env bash
# QoffeeAutomat - install the agent from the tarball (PLAN.md S13, T804).
#
# Run it as root from the unpacked tarball, next to the folder "app":
#
#   sudo ./install.sh [--prefix DIR] [--ui-port PORT] [--link-port PORT] [--no-start]
#                     [--no-service] [--no-session-service]
#
# It
#   * copies the files to <prefix>/lib/qoffeeautomat (owned by root, not writable by others: the service
#     runs as root and starts every file in that folder) and links <prefix>/bin/qoffeeautomat to the program,
#   * registers, enables and starts the systemd system service "qoffeeautomat" with
#     "qoffeeautomat service install", which also creates the data directories with their permissions
#     (/etc/qoffeeautomat, /var/lib/qoffeeautomat, /var/log/qoffeeautomat; all 0700, owned by root),
#   * installs the systemd user unit "qoffeeautomat-session" for the session helper into /etc/systemd/user and
#     enables it for every user (it starts with the next desktop login).
#
# Options:
#   --prefix DIR           install below DIR (default: /usr/local)
#   --ui-port PORT         port of the loopback UI and API listener (default: from the settings, 8480)
#   --link-port PORT       port of the QoffeeLink listener (default: from the settings, 8481)
#   --no-start             enable the service but do not start it
#   --no-service           copy the files only; do not touch systemd (for example in a container)
#   --no-session-service   do not install the user unit of the session helper
#   -h, --help             show this help
#
# Running it again over an installed version upgrades it: the service is stopped, the files are replaced and the
# service is started again. Configuration, secrets, history and logs are not touched. The system unit is left as it
# is unless you give a port option. Uninstall with uninstall.sh (it is copied to <prefix>/lib/qoffeeautomat).

set -euo pipefail

SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PREFIX="/usr/local"
UI_PORT=""
LINK_PORT=""
START=1
SERVICE=1
SESSION=1

UNIT_NAME="qoffeeautomat.service"
SESSION_UNIT_NAME="qoffeeautomat-session.service"
SYSTEM_UNIT="/etc/systemd/system/$UNIT_NAME"
USER_UNIT_DIR="/etc/systemd/user"
SECRETS_DIR="/var/lib/qoffeeautomat/secrets"

usage() { sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }
die() { echo "install.sh: $*" >&2; exit 1; }
say() { echo "==> $*"; }

check_port() {
  [[ "$2" =~ ^[0-9]+$ ]] && [ "$2" -ge 1 ] && [ "$2" -le 65535 ] || die "$1 must be a number between 1 and 65535"
}

# The directory must belong to root and nobody else may write to it.
check_protected() {
  local dir="$1" owner mode
  owner="$(stat -c '%u' "$dir")"
  mode="$(stat -c '%a' "$dir")"
  if [ "$owner" != "0" ] || [ $(( 8#$mode & 8#022 )) -ne 0 ]; then
    die "$dir must be owned by root and not writable by group or others (it is $mode, owner $owner)"
  fi
}

main() {
  while [ $# -gt 0 ]; do
    case "$1" in
      --prefix) [ $# -ge 2 ] || die "--prefix needs a directory"; PREFIX="$2"; shift 2 ;;
      --ui-port) [ $# -ge 2 ] || die "--ui-port needs a port"; check_port --ui-port "$2"; UI_PORT="$2"; shift 2 ;;
      --link-port) [ $# -ge 2 ] || die "--link-port needs a port"; check_port --link-port "$2"; LINK_PORT="$2"; shift 2 ;;
      --no-start) START=0; shift ;;
      --no-service) SERVICE=0; shift ;;
      --no-session-service) SESSION=0; shift ;;
      -h | --help) usage; exit 0 ;;
      *) die "unknown option '$1' (try --help)" ;;
    esac
  done

  [ "$(id -u)" -eq 0 ] || die "run it as root, for example: sudo ./install.sh"
  [ -x "$SELF_DIR/app/qoffeeautomat" ] || die "$SELF_DIR/app/qoffeeautomat not found: run the script from the unpacked tarball"
  # The prefix ends up in unit files: only plain path characters are accepted.
  [[ "$PREFIX" =~ ^/[A-Za-z0-9._/+-]*$ ]] || die "--prefix must be an absolute path of letters, digits and . _ / + -"
  PREFIX="${PREFIX%/}"
  [ -n "$PREFIX" ] || PREFIX="/usr"
  local app="$PREFIX/lib/qoffeeautomat"

  if command -v dpkg-query > /dev/null 2>&1 && dpkg-query -W -f='${Status}' qoffeeautomat 2> /dev/null | grep -q "ok installed"; then
    die "QoffeeAutomat is installed as a .deb package. Remove it first (apt remove qoffeeautomat) or upgrade it with apt."
  fi

  if [ "$SERVICE" -eq 1 ] && [ ! -d /run/systemd/system ]; then
    echo "install.sh: systemd is not running on this computer; the files are copied, but no service is registered." >&2
    SERVICE=0
    SESSION=0
  fi

  install -d -m 0755 -o root -g root "$PREFIX" "$PREFIX/lib" "$PREFIX/bin"
  check_protected "$PREFIX/lib"

  local was_active=0
  if [ "$SERVICE" -eq 1 ] && systemctl is-active --quiet "$UNIT_NAME"; then
    was_active=1
    say "Stop the running service"
    systemctl stop "$UNIT_NAME"
  fi

  say "Copy the files to $app"
  rm -rf "$app.new"
  cp -a "$SELF_DIR/app" "$app.new"
  install -m 0755 "$SELF_DIR/uninstall.sh" "$app.new/uninstall.sh"
  chown -R root:root "$app.new"
  chmod -R go-w "$app.new"
  if [ -e "$app" ]; then
    rm -rf "$app.old"
    mv "$app" "$app.old"
  fi
  mv "$app.new" "$app"
  rm -rf "$app.old"
  ln -sfn "$app/qoffeeautomat" "$PREFIX/bin/qoffeeautomat"

  if [ "$SESSION" -eq 1 ]; then
    say "Install the user unit of the session helper"
    local fresh_session=1
    [ -e "$USER_UNIT_DIR/$SESSION_UNIT_NAME" ] && fresh_session=0
    install -d -m 0755 -o root -g root "$USER_UNIT_DIR"
    sed "s|/usr/lib/qoffeeautomat/|$app/|g" "$SELF_DIR/systemd/$SESSION_UNIT_NAME" > "$USER_UNIT_DIR/$SESSION_UNIT_NAME.new"
    chmod 0644 "$USER_UNIT_DIR/$SESSION_UNIT_NAME.new"
    mv "$USER_UNIT_DIR/$SESSION_UNIT_NAME.new" "$USER_UNIT_DIR/$SESSION_UNIT_NAME"
    # Enabled once, on the first installation; a user or administrator who disabled it keeps that choice on upgrades.
    if [ "$fresh_session" -eq 1 ]; then
      systemctl --global enable "$SESSION_UNIT_NAME" > /dev/null
    fi
  fi

  if [ "$SERVICE" -eq 1 ]; then
    local register=0
    if [ ! -e "$SYSTEM_UNIT" ] || [ -n "$UI_PORT" ] || [ -n "$LINK_PORT" ]; then
      register=1
    elif ! grep -q "^ExecStart=$app/qoffeeautomat " "$SYSTEM_UNIT"; then
      echo "install.sh: $SYSTEM_UNIT starts another program; it is registered again (ports from the unit are reset)." >&2
      register=1
    fi

    if [ "$register" -eq 1 ]; then
      say "Register the system service"
      local args=(service install)
      [ -z "$UI_PORT" ] || args+=(--ui-port "$UI_PORT")
      [ -z "$LINK_PORT" ] || args+=(--link-port "$LINK_PORT")
      [ "$START" -eq 1 ] || args+=(--no-start)
      "$app/qoffeeautomat" "${args[@]}"
    else
      systemctl daemon-reload
      systemctl enable "$UNIT_NAME" > /dev/null 2>&1 || true
      if [ "$START" -eq 1 ] || [ "$was_active" -eq 1 ]; then
        say "Start the service"
        systemctl start "$UNIT_NAME"
      fi
    fi
  fi

  echo
  echo "QoffeeAutomat is installed in $app."
  if [ "$SERVICE" -eq 1 ]; then
    echo "  Service:   systemctl status qoffeeautomat    (logs: journalctl -u qoffeeautomat)"
  else
    echo "  Run it:    $PREFIX/bin/qoffeeautomat run     (or register the service: sudo $PREFIX/bin/qoffeeautomat service install)"
  fi
  echo "  Web UI:    http://127.0.0.1:${UI_PORT:-8480}/   (on this computer only)"
  echo "  Setup token for the first-run setup:  sudo cat $SECRETS_DIR/setup-token.txt"
  if [ "$SESSION" -eq 1 ]; then
    echo "  Session helper: starts with your next desktop login; start it now with: systemctl --user start qoffeeautomat-session"
  fi
  echo "  Documentation: https://toeck98.github.io/QoffeeAutomat/user/install-linux/"
}

main "$@"
exit $?
