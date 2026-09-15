#!/bin/bash
# Build the rhinomcp plugin and install it as a Rhino-for-Mac plugin *package*.
#
# Rhino 7 on macOS does not load a raw .rhp dropped on the viewport (that
# Open/Import dialog is Rhino treating it as a document). It scans:
#
#   ~/Library/Application Support/McNeel/Rhinoceros/MacPlugIns/
#
# for folders named *.rhp (Finder packages) and loads the assembly of the
# same name inside. See:
# https://developer.rhino3d.com/guides/rhinocommon/plugin-installers-mac/
#
# Usage:
#   ./install.sh                          # Debug build
#   CONFIG=Release ./install.sh           # Release build
#   RHINO_PLUGIN_DIR=/path ./install.sh   # override the package folder
#
# Do not use the Package Manager `rhinomcp` yak; that package is Rhino 8.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SLN="$SCRIPT_DIR/rhinomcp.sln"
CONFIG="${CONFIG:-Debug}"
MACPLUGINS="${MACPLUGINS:-$HOME/Library/Application Support/McNeel/Rhinoceros/MacPlugIns}"
RHINO_PLUGIN_DIR="${RHINO_PLUGIN_DIR:-$MACPLUGINS/rhinomcp.rhp}"

if [[ "$(uname)" != "Darwin" ]]; then
  echo "error: this script targets macOS. On Windows, use the csproj directly." >&2
  exit 1
fi

if [[ ! -f "$SLN" ]]; then
  echo "error: solution not found at $SLN" >&2
  exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
  if [[ -x "$HOME/.dotnet/dotnet" ]]; then
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$PATH"
  else
    echo "error: dotnet CLI not found. Install the .NET 8 SDK:" >&2
    echo "  curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 8.0" >&2
    exit 1
  fi
fi

if pgrep -qi "Rhinoceros|Rhino 7"; then
  echo "warning: Rhino 7 is running. The files can still be copied, but you"
  echo "         must Quit and reopen Rhino before mcpstart will exist."
  echo
fi

echo "==> building rhinomcp ($CONFIG, net48)"
dotnet build "$SLN" --configuration "$CONFIG" --nologo --verbosity minimal

BUILD_OUT="$SCRIPT_DIR/bin/$CONFIG/net48"
if [[ ! -f "$BUILD_OUT/rhinomcp.rhp" ]]; then
  echo "error: build succeeded but rhinomcp.rhp is not at $BUILD_OUT" >&2
  exit 1
fi

echo "==> installing Mac plugin package to $RHINO_PLUGIN_DIR"
mkdir -p "$RHINO_PLUGIN_DIR"
# rsync --delete so stale dlls from a previous build / NuGet upgrade don't
# linger and shadow the freshly-built ones at load time.
rsync -a --delete "$BUILD_OUT/" "$RHINO_PLUGIN_DIR/"

MACRHI="$SCRIPT_DIR/bin/$CONFIG/rhinomcp.macrhi"
echo "==> writing $MACRHI (drag this onto the Rhino 7 Dock icon if you prefer)"
rm -f "$MACRHI"
(
  cd "$(dirname "$RHINO_PLUGIN_DIR")"
  ditto -c -k --keepParent "$(basename "$RHINO_PLUGIN_DIR")" "$MACRHI"
)

echo
echo "done. Mac plugin package:"
echo "  $RHINO_PLUGIN_DIR/rhinomcp.rhp"
echo "macrhi (optional, Dock-drop):"
echo "  $MACRHI"
echo
cat <<EOF
Next:
  1. Quit Rhino 7 completely (Cmd+Q), then open it again.
  2. Type mcpstart. You want: RhinoMCP server started on 127.0.0.1:1999

Do not drag the raw .rhp onto the viewport. That Open/Import dialog means
Rhino thought it was a model file, not a plugin.

If mcpstart is still unknown after a restart, open Rhinoceros > Settings >
Plug-ins and look for rhinomcp, or check the command history for a load error.
EOF
