#!/usr/bin/env bash
# Runs the shared engine tests plus the headless Avalonia UI test; renders preview-linux.png.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
dotnet run --project "$here/Tests/Linux/MediaFlattener.Tests.Linux.csproj" -c Release -- "$here/preview-linux.png"
