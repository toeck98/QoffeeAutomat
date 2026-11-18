#!/usr/bin/env bash
# QoffeeAutomat - build the .deb package (PLAN.md S13, T804).
#
# Packs the staging directory of publish.sh with dpkg-deb into qoffeeautomat_<version>_<amd64|arm64>.deb:
#   /usr/lib/qoffeeautomat/                         program files (only root can write)
#   /usr/bin/qoffeeautomat                          link to the program
#   /usr/lib/systemd/system/qoffeeautomat.service   system service (runs as root)
#   /usr/lib/systemd/user/qoffeeautomat-session.service   session helper (systemd user unit)
# The maintainer scripts in deb/ create /etc/qoffeeautomat, /var/lib/qoffeeautomat and /var/log/qoffeeautomat
# (0700, root), enable and start the service, and stop and remove everything on uninstall (purge deletes the data).
#
# Usage:
#   bash packaging/linux/build-deb.sh --arch x64|arm64 [--version SEMVER] [--output DIR]
#
# Options:
#   --arch ARCH       x64 (amd64) or arm64 (required); run publish.sh for it first
#   --version SEMVER  version (default: <Version> of Directory.Build.props); the "-" of a prerelease becomes "~"
#   --output DIR      packaging directory of publish.sh (default: artifacts/packaging below the repository root)
#   -h, --help        show this help

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HERE="$ROOT/packaging/linux"
ARCH=""
VERSION=""
OUTPUT=""

usage() { sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }
die() { echo "build-deb.sh: $*" >&2; exit 1; }

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
  x64) DEB_ARCH="amd64" ;;
  arm64) DEB_ARCH="arm64" ;;
  *) die "--arch must be x64 or arm64" ;;
esac
command -v dpkg-deb > /dev/null || die "dpkg-deb is required"
if [ -z "$VERSION" ]; then
  VERSION="$(sed -n 's|.*<Version>[[:space:]]*\([^<[:space:]]*\)[[:space:]]*</Version>.*|\1|p' "$ROOT/Directory.Build.props" | head -n 1)"
  [ -n "$VERSION" ] || die "Directory.Build.props has no <Version>"
fi
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || die "'$VERSION' is not a SemVer version"
DEB_VERSION="$(printf '%s' "$VERSION" | sed 's/-/~/')"
OUTPUT="${OUTPUT:-$ROOT/artifacts/packaging}"
STAGE="$OUTPUT/stage/linux-$ARCH"
[ -x "$STAGE/qoffeeautomat" ] || die "$STAGE is not staged: run publish.sh --arch $ARCH first"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
PKG="$WORK/pkg"
LIB="$PKG/usr/lib/qoffeeautomat"
install -d -m 0755 "$LIB" "$PKG/usr/bin" "$PKG/usr/lib/systemd/system" "$PKG/usr/lib/systemd/user" \
  "$PKG/usr/share/doc/qoffeeautomat" "$PKG/DEBIAN"
cp -a "$STAGE/." "$LIB/"
rm -f "$LIB/LICENSE"
ln -s /usr/lib/qoffeeautomat/qoffeeautomat "$PKG/usr/bin/qoffeeautomat"
install -m 0644 "$HERE/qoffeeautomat.service" "$PKG/usr/lib/systemd/system/qoffeeautomat.service"
install -m 0644 "$HERE/qoffeeautomat-session.service" "$PKG/usr/lib/systemd/user/qoffeeautomat-session.service"
install -m 0644 "$ROOT/LICENSE" "$PKG/usr/share/doc/qoffeeautomat/copyright"
for script in postinst prerm postrm; do
  install -m 0755 "$HERE/deb/$script" "$PKG/DEBIAN/$script"
done

size_kb="$(du -sk --exclude=DEBIAN "$PKG" | cut -f1)"
# The program is self-contained; it needs the C and C++ runtimes, OpenSSL, zlib and ICU (globalization).
cat > "$PKG/DEBIAN/control" <<CONTROL
Package: qoffeeautomat
Version: $DEB_VERSION
Architecture: $DEB_ARCH
Maintainer: QoffeeAutomat maintainers <noreply@users.noreply.github.com>
Installed-Size: $size_kb
Section: admin
Priority: optional
Homepage: https://github.com/toeck98/QoffeeAutomat
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libssl3t64 | libssl3, libicu78 | libicu76 | libicu74 | libicu72 | libicu70 | libicu67
Recommends: systemd
Description: Home Assistant agent for this computer (QoffeeAutomat)
 Lets Home Assistant monitor this computer and run commands on it over
 QoffeeLink. Installs the system service "qoffeeautomat" and the systemd user
 unit "qoffeeautomat-session" for the session helper. The web interface listens
 on 127.0.0.1:8480 only.
CONTROL

mkdir -p "$OUTPUT"
OUT="$OUTPUT/qoffeeautomat_${DEB_VERSION}_${DEB_ARCH}.deb"
rm -f "$OUT"
dpkg-deb --root-owner-group -Zxz --build "$PKG" "$OUT" > /dev/null
dpkg-deb --info "$OUT" | sed -n '1,12p'
echo "Package: $OUT"
