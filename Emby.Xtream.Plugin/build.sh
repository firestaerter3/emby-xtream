#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="$SCRIPT_DIR/out"
DLL_NAME="Emby.Xtream.Plugin.dll"

echo "=== Building Emby.Xtream.Plugin ==="
cd "$SCRIPT_DIR"
dotnet publish -c Release -o "$OUT_DIR" --no-self-contained

echo ""
echo "=== Build output ==="
ls -la "$OUT_DIR/$DLL_NAME"

echo ""
echo "DLL ready at: $OUT_DIR/$DLL_NAME"
echo ""
echo "To deploy to Emby:"
echo "  docker cp $OUT_DIR/$DLL_NAME <container>:/config/plugins/"
echo "  docker restart <container>"
