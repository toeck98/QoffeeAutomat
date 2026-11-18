#!/usr/bin/env bash
# QoffeeAutomat - uninstall the tarball installation (PLAN.md S13, T804).
#
#   sudo <prefix>/lib/qoffeeautomat/uninstall.sh [--purge-data]
#
# Stops and removes the system service and the session helper unit and deletes the program files. The configuration,
# secrets, history and logs (/etc, /var/lib and /var/log below "qoffeeautomat") stay unless you give --purge-data.
# The prefix is taken from the location of this script.
#
# Options:
#   --purge-data   also delete /etc/qoffeeautomat, /var/lib/qoffeeautomat and /var/log/qoffeeautomat
#   -h, --help     show this help

set -euo pipefail

SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PURGE=0

usage() { sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }
die() { echo "uninstall.sh: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --purge-data) PURGE=1; shift ;;
    -h | --help) usage; exit 0 ;;
    *) die "unknown option '$1' (try --help)" ;;
  esac
done
[ "$(id -u)" -eq 0 ] || die "run it as root, for example: sudo $0"
[ "$(basename "$SELF_DIR")" = "qoffeeautomat" ] || die "run the copy that install.sh placed in <prefix>/lib/qoffeeautomat"

app="$SELF_DIR"
prefix="$(dirname "$(dirname "$app")")"

if [ -d /run/systemd/system ]; then
  systemctl disable --now qoffeeautomat.service > /dev/null 2>&1 || true
  systemctl --global disable qoffeeautomat-session.service > /dev/null 2>&1 || true
fi
rm -f /etc/systemd/system/qoffeeautomat.service /etc/systemd/user/qoffeeautomat-session.service
rm -rf /etc/systemd/system/qoffeeautomat.service.d
if [ -d /run/systemd/system ]; then
  systemctl daemon-reload || true
fi

if [ -L "$prefix/bin/qoffeeautomat" ]; then
  rm -f "$prefix/bin/qoffeeautomat"
fi
# The folder holds this script: it is deleted last.
cd /
rm -rf "$app"

if [ "$PURGE" -eq 1 ]; then
  rm -rf /etc/qoffeeautomat /var/lib/qoffeeautomat /var/log/qoffeeautomat
  echo "QoffeeAutomat is uninstalled; configuration, data and logs are deleted."
else
  echo "QoffeeAutomat is uninstalled. Configuration, data and logs are kept (--purge-data deletes them)."
fi
