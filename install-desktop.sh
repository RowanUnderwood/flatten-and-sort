#!/usr/bin/env bash
# Adds a Media Flattener launcher for the current user pointing at App-linux/ (run build.sh first).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
exe="$here/App-linux/media-flattener"
[[ -x "$exe" ]] || { echo "Build first: ./build.sh" >&2; exit 1; }
target="${XDG_DATA_HOME:-$HOME/.local/share}/applications/media-flattener.desktop"
mkdir -p "$(dirname "$target")"
cat > "$target" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Media Flattener
Comment=Flatten and sort media files into image, video and other folders
Exec="$exe"
Terminal=false
Categories=Utility;FileTools;
DESKTOP
echo "Installed: $target"
