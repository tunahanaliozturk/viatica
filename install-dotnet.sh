#!/usr/bin/env bash
# Installs the SDK pinned in global.json into .dotnet/, leaving any system-wide .NET untouched.
set -euo pipefail
cd "$(dirname "$0")"
VERSION=$(sed -n 's/.*"version": "\(.*\)".*/\1/p' global.json | head -1)
SCRIPT="$(mktemp)"
trap 'rm -f "$SCRIPT"' EXIT
curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$SCRIPT"
bash "$SCRIPT" --version "$VERSION" --install-dir .dotnet
