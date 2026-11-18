#!/usr/bin/env bash
# QoffeeAutomat - build the Linux tarball (PLAN.md S13, T804).
#
# Packs the staging directory of publish.sh together with install.sh, uninstall.sh and the systemd user unit into
# QoffeeAutomat-<version>-linux-<arch>.tar.gz (one top-level folder of the same name; the program is in app/).
#
# Usage:
#   bash packaging/linux/build-tarball.sh --arch x64|arm64 [--version SEMVER] [--output DIR]
#
# Options:
#   --arch ARCH       x64 or arm64 (required); run publish.sh for it first
#   --version SEMVER  version in the file name (default: <Version> of Directory.Build.props)
#   --output DIR      packaging directory of publish.sh (default: artifacts/packaging below the repository root)
#   -h, --help        show this help

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HERE="$ROOT/packaging/linux"
ARCH=""
VERSION=""
OUTPUT=""

usage() { sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }
die() { echo "build-tarball.sh: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --arch) [ $# -ge 2 ] || die "--arch needs a value"; ARCH="$2"; shift 2 ;;
    --version) [ $# -ge 2 ] || die "--version needs a value"; VERSION="$2"; shift 2 ;;
    --output) [ $# -ge 2 ] || die "--output needs a value"; OUTPUT="$2"; shift 2 ;;
    -h | --help) usage; exit 0 ;;
    *) die "unknown option '$1' (try --help)" ;;
  esac
done

case "$ARCH" in
  x64 | arm64) ;;
  *) die "--arch must be x64 or arm64" ;;
esac
if [ -z "$VERSION" ]; then
  VERSION="$(sed -n 's|.*<Version>[[:space:]]*\([^<[:space:]]*\)[[:space:]]*</Version>.*|\1|p' "$ROOT/Directory.Build.props" | head -n 1)"
  [ -n "$VERSION" ] || die "Directory.Build.props has no <Version>"
fi
OUTPUT="${OUTPUT:-$ROOT/artifacts/packaging}"
STAGE="$OUTPUT/stage/linux-$ARCH"
[ -x "$STAGE/qoffeeautomat" ] || die "$STAGE is not staged: run publish.sh --arch $ARCH first"

NAME="QoffeeAutomat-$VERSION-linux-$ARCH"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
TOP="$WORK/$NAME"
mkdir -p "$TOP/app" "$TOP/systemd"
cp -a "$STAGE/." "$TOP/app/"
install -m 0755 "$HERE/install.sh" "$TOP/install.sh"
install -m 0755 "$HERE/uninstall.sh" "$TOP/uninstall.sh"
install -m 0644 "$HERE/qoffeeautomat-session.service" "$TOP/systemd/qoffeeautomat-session.service"
install -m 0644 "$ROOT/LICENSE" "$TOP/LICENSE"
cat > "$TOP/README.txt" <<TXT
QoffeeAutomat $VERSION for Linux ($ARCH)

Install:    sudo ./install.sh        (see ./install.sh --help)
Uninstall:  sudo /usr/local/lib/qoffeeautomat/uninstall.sh
Docs:       https://toeck98.github.io/QoffeeAutomat/user/install-linux/
TXT

mkdir -p "$OUTPUT"
OUT="$OUTPUT/$NAME.tar.gz"
rm -f "$OUT"
# Owner root, fixed order: the same input gives the same file list.
tar --owner=0 --group=0 --numeric-owner --sort=name -C "$WORK" -cf - "$NAME" | gzip -9n > "$OUT"
tar -tzf "$OUT" > /dev/null
echo "Tarball: $OUT"
