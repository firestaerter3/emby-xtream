#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="$SCRIPT_DIR/out"
DLL_NAME="Emby.Xtream.Plugin.dll"

# Derive version from git tags automatically:
#   On tag v1.2.0        -> 1.2.0
#   3 commits after tag  -> 1.2.0.3  (always higher than last release)
#   No tags              -> 0.0.1
GIT_DESC=$(git -C "$SCRIPT_DIR" describe --tags 2>/dev/null || echo "")
if [ -z "$GIT_DESC" ]; then
    VERSION="0.0.1"
elif echo "$GIT_DESC" | grep -q '-'; then
    # v1.2.0-3-gabcdef -> base=1.2.0, commits=3 -> 1.2.0.3
    BASE=$(echo "$GIT_DESC" | sed 's/^v//' | cut -d'-' -f1)
    COMMITS=$(echo "$GIT_DESC" | cut -d'-' -f2)
    VERSION="${BASE}.${COMMITS}"
else
    # Exactly on tag: v1.2.0 -> 1.2.0
    VERSION="${GIT_DESC#v}"
fi

echo "=== Version: $VERSION (from git: $GIT_DESC) ==="

echo ""
echo "=== Running Tests ==="
dotnet test "$SCRIPT_DIR/../Emby.Xtream.Plugin.Tests/" --no-restore -v minimal

echo ""
echo "=== Building Emby.Xtream.Plugin ==="
cd "$SCRIPT_DIR"

# Stamp config.html's data-controller with an MD5 hash of config.js so the browser
# always fetches a fresh JS file after each build.  The file is restored afterwards
# so the working tree stays clean.
JS_FILE="$SCRIPT_DIR/Configuration/Web/config.js"
HTML_FILE="$SCRIPT_DIR/Configuration/Web/config.html"
if command -v md5 >/dev/null 2>&1; then
    JS_HASH=$(md5 -q "$JS_FILE" | cut -c1-8)
else
    JS_HASH=$(md5sum "$JS_FILE" | awk '{print substr($1,1,8)}')
fi
JS_NAME="xtreamconfigjs${JS_HASH}"
cp "$HTML_FILE" "${HTML_FILE}.bak"
trap 'mv "${HTML_FILE}.bak" "$HTML_FILE" 2>/dev/null || true' EXIT
sed -i '' "s|data-controller=\"__plugin/xtreamconfigjs[^\"]*\"|data-controller=\"__plugin/${JS_NAME}\"|" "$HTML_FILE"

dotnet publish -c Release -o "$OUT_DIR" --no-self-contained -p:Version="$VERSION"

mv "${HTML_FILE}.bak" "$HTML_FILE"
trap - EXIT

echo ""
echo "=== Build output ==="
ls -la "$OUT_DIR/$DLL_NAME"

echo ""
echo "DLL ready at: $OUT_DIR/$DLL_NAME (v$VERSION)"
echo ""
echo "To deploy to Emby:"
echo "  docker cp $OUT_DIR/$DLL_NAME <container>:/config/plugins/"
echo "  docker restart <container>"
