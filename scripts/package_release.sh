#!/bin/bash
# Build the Forsk release for Rhino 7 on Mac: the Package Manager (yak) file
# and a .macrhi, both from one staged folder.
#
#   ./scripts/package_release.sh            # writes dist/release/
#   OUT=/some/folder ./scripts/package_release.sh
#
# The package carries the plugin build plus Forsk's own files in a forsk/
# folder beside it (prompts, the AI detection tools, the daylight tracer),
# so it needs no checkout on the user's Mac. FORSK_DIR is the forsk checkout
# (default: ../forsk). On a Mac with Rhino 7, yak builds the .yak itself;
# elsewhere the .yak is zipped in yak's layout (manifest.yml at the root).
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FORSK_DIR="${FORSK_DIR:-$REPO/../forsk}"
OUT="${OUT:-$REPO/dist/release}"
YAK="${YAK:-/Applications/Rhino 7.app/Contents/Resources/bin/yak}"

[[ -f "$FORSK_DIR/prompts/system.md" ]] || { echo "error: no forsk checkout at $FORSK_DIR (set FORSK_DIR)" >&2; exit 1; }
VERSION="$(sed -n 's/^version: *//p' "$REPO/plugin/manifest.yml")"
NAME="$(sed -n 's/^name: *//p' "$REPO/plugin/manifest.yml")"

# The WindowsDesktop targets are missing from Linux SDKs; on net48 they only add references the csproj has.
EXTRA=()
[[ "$(uname)" == "Darwin" ]] || EXTRA=(-p:UseWindowsForms=false)
echo "==> building $NAME $VERSION (Release, net48)"
# macOS bash 3.2 treats an empty array as unbound under set -u, hence the ${...+...} form.
dotnet build "$REPO/plugin/rhinomcp.csproj" -c Release --nologo -v q ${EXTRA[@]+"${EXTRA[@]}"} >/dev/null
BUILD="$REPO/plugin/bin/Release/net48"
[[ -f "$BUILD/rhinomcp.rhp" ]] || { echo "error: no rhinomcp.rhp in $BUILD" >&2; exit 1; }

STAGE="$(mktemp -d)/rhinomcp.rhp"
mkdir -p "$STAGE"
rsync -a --exclude '*.pdb' "$BUILD/" "$STAGE/"
cp "$REPO/plugin/manifest.yml" "$STAGE/manifest.yml"
cp "$FORSK_DIR/brand/forsk_logo.png" "$STAGE/icon.png"
cp "$REPO/LICENSE" "$STAGE/LICENSE-rhinomcp.txt"

BUNDLE="$STAGE/forsk"
mkdir -p "$BUNDLE/tools" "$BUNDLE/daylight"
rsync -a "$FORSK_DIR/prompts/" "$BUNDLE/prompts/"
# uv.lock stays: the tools run with uv --frozen, which needs it.
for tool in pdf_vector cubicasa; do
  rsync -a --exclude tests --exclude .venv --exclude __pycache__ --exclude '*.egg-info' --exclude .pytest_cache \
    "$FORSK_DIR/tools/$tool/" "$BUNDLE/tools/$tool/"
done
cp "$REPO/server/src/forsk_daylight.py" "$BUNDLE/daylight/"
printf '%s\n' "Forsk $VERSION, built $(date -u +%Y-%m-%d) from rhino-7-mcp $(git -C "$REPO" rev-parse --short HEAD) and forsk $(git -C "$FORSK_DIR" rev-parse --short HEAD)." > "$BUNDLE/VERSION.txt"

mkdir -p "$OUT"
YAKFILE="$OUT/$NAME-$VERSION-rh7_0-mac.yak"
rm -f "$YAKFILE" "$OUT/Forsk-$VERSION.macrhi"
if [[ -x "$YAK" ]]; then
  echo "==> yak build"
  (cd "$STAGE" && "$YAK" build --platform mac >/dev/null && mv ./*.yak "$YAKFILE")
else
  echo "==> zipping the .yak (no yak here)"
  (cd "$STAGE" && zip -qr -X "$YAKFILE" .)
fi
# A .macrhi is the zipped plugin package folder; double-click or drop on the Rhino 7 Dock icon.
(cd "$(dirname "$STAGE")" && zip -qr -X "$OUT/Forsk-$VERSION.macrhi" rhinomcp.rhp -x 'rhinomcp.rhp/manifest.yml' 'rhinomcp.rhp/icon.png')
rm -rf "$(dirname "$STAGE")"

echo "done:"
ls -1 "$OUT"/*"$VERSION"*
