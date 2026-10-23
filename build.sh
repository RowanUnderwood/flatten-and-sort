#!/usr/bin/env bash
# Publishes a self-contained Linux x64 build to App-linux/.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
dotnet publish "$here/Linux/MediaFlattener.Linux.csproj" -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$here/App-linux" --nologo
echo "Ready: App-linux/media-flattener"
