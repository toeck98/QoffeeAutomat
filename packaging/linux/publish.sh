#!/usr/bin/env bash
# QoffeeAutomat - publish and stage the agent for Linux (PLAN.md S13, T804).
#
# Publishes src/QoffeeAutomat.Agent for linux-x64 or linux-arm64 (net10.0 build: it contains the Linux
# platform and the systemd integration; self-contained, single file, ReadyToRun, version stamped, no
# symbols) and copies the result into a staging directory. build-tarball.sh and build-deb.sh package that
# directory. The executable is renamed to "qoffeeautomat": that is the name that the systemd units and the
# command line use. The session helper is a verb of the same executable (qoffeeautomat session-host), so
# there is no second program.
#
# Usage:
#   bash packaging/linux/publish.sh --arch x64|arm64 [--version SEMVER] [--output DIR]
#
# Options:
#   --arch ARCH       x64 or arm64 (required)
#   --version SEMVER  version to stamp into the executable (default: <Version> of Directory.Build.props)
#   --output DIR      where the staging directory goes (default: artifacts/packaging below the repository root)
#   -h, --help        show this help
#
# Output: DIR/stage/linux-<arch>/ with qoffeeautomat, wwwroot/, templates/, the native libraries and LICENSE.
# Needs the .NET SDK of global.json. Cross-compiling for the other architecture works.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ARCH=""
VERSION=""
OUTPUT=""

usage() { sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }
die() { echo "publish.sh: $*" >&2; exit 1; }

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
  x64) RID="linux-x64" ;;
  arm64) RID="linux-arm64" ;;
  *) die "--arch must be x64 or arm64" ;;
esac

if [ -z "$VERSION" ]; then
  VERSION="$(sed -n 's|.*<Version>[[:space:]]*\([^<[:space:]]*\)[[:space:]]*</Version>.*|\1|p' "$ROOT/Directory.Build.props" | head -n 1)"
  [ -n "$VERSION" ] || die "Directory.Build.props has no <Version>"
fi
if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  die "'$VERSION' is not a SemVer version (MAJOR.MINOR.PATCH[-prerelease])"
fi
OUTPUT="${OUTPUT:-$ROOT/artifacts/packaging}"
mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"

PUBLISH_DIR="$ROOT/artifacts/publish/agent/$RID"
STAGE="$OUTPUT/stage/$RID"

rm -rf "$PUBLISH_DIR" "$STAGE"

echo "==> Publish the agent ($RID, $VERSION)"
# -f net10.0 selects the build with the Linux platform (the project is multi-targeted). Native libraries (SQLite) stay
# next to the executable instead of being extracted into a temporary directory at start-up: the service runs as root and
# must not load code from a directory that it unpacked itself.
dotnet publish "$ROOT/src/QoffeeAutomat.Agent/QoffeeAutomat.Agent.csproj" \
  --configuration Release --framework net10.0 --runtime "$RID" --self-contained true \
  --output "$PUBLISH_DIR" --nologo \
  -p:PublishSingleFile=true \
  -p:PublishReadyToRun=true \
  -p:PublishTrimmed=false \
  -p:IncludeNativeLibrariesForSelfExtract=false \
  -p:EnableCompressionInSingleFile=false \
  -p:DebugType=none \
  -p:DebugSymbols=false \
  -p:SatelliteResourceLanguages=en \
  -p:IsTransformWebConfigDisabled=true \
  -p:Version="$VERSION"

for required in QoffeeAutomat.Agent wwwroot/index.html templates/commands/set_volume.json templates/sensors/disk_free_percent.json; do
  [ -e "$PUBLISH_DIR/$required" ] || die "the publish output lacks $required"
done

echo "==> Stage into $STAGE"
mkdir -p "$STAGE"
cp -a "$PUBLISH_DIR/." "$STAGE/"
mv "$STAGE/QoffeeAutomat.Agent" "$STAGE/qoffeeautomat"
rm -f "$STAGE/web.config"
cp "$ROOT/LICENSE" "$STAGE/LICENSE"

# Modes of the files in the package: nobody but root may change what the service starts as root (PLAN.md S09).
find "$STAGE" -type d -exec chmod 0755 {} +
find "$STAGE" -type f -exec chmod 0644 {} +
chmod 0755 "$STAGE/qoffeeautomat"

# A smoke test of the staged executable, when it can run on this computer.
host_arch="$(uname -m)"
if { [ "$ARCH" = "x64" ] && [ "$host_arch" = "x86_64" ]; } || { [ "$ARCH" = "arm64" ] && [ "$host_arch" = "aarch64" ]; }; then
  echo "==> Check the version of the executable"
  reported="$("$STAGE/qoffeeautomat" version)"
  echo "$reported"
  numeric="${VERSION%%-*}"
  case "$reported" in
    *"$numeric"*) ;;
    *) die "the executable reports '$reported', expected $numeric" ;;
  esac
fi

echo "Staged: $STAGE"
