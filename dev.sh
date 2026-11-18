#!/usr/bin/env bash
# Fast local test loop (no GitHub): build -> install as the "Movie Curator_<version>" plugin -> fresh Jellyfin process.
#   sudo ./dev.sh      (sudo: Jellyfin's plugins folder belongs to root; the build itself runs as you)
#   ./dev.sh           works too when the plugin folder already exists and is yours
# Version: the installed one if any, else the latest git tag (v1.1.0-exp.18 -> 1.1.0.18); override with DEV_VERSION=...
# Restart: a real process restart (an in-process restart can't swap a loaded plugin DLL):
#   API key in $KEY_FILE -> POST /System/Shutdown (docker's restart policy starts it again), else (as root) docker restart.
set -euo pipefail
cd "$(dirname "$0")"

ME="${SUDO_USER:-$USER}"
ME_HOME=$(getent passwd "$ME" | cut -d: -f6)
JF_CONFIG="${JF_CONFIG:-$ME_HOME/Servers/docker-jellyfin/config}"
JF_URL="${JF_URL:-http://localhost:7001}"
JF_CONTAINER="${JF_CONTAINER:-jellyfin}"
KEY_FILE="${JF_KEY_FILE:-$ME_HOME/Servers/docker-jellyfin/.jellyfin_api_key}"
DOTNET="${DOTNET:-$ME_HOME/.dotnet/dotnet}"
[ -x "$DOTNET" ] || DOTNET=$(command -v dotnet || true)
[ -n "$DOTNET" ] || { echo "No .NET 9 SDK (~/.dotnet/dotnet or dotnet on PATH)."; exit 1; }
as_me() { if [ "$(id -u)" = 0 ] && [ "$ME" != root ]; then sudo -u "$ME" -H "$@"; else "$@"; fi; }

PLUGINS="$JF_CONFIG/plugins"
dir=$(ls -d "$PLUGINS"/"Movie Curator_"* 2>/dev/null | sort -V | tail -1 || true)
if [ -n "${DEV_VERSION:-}" ]; then ver="$DEV_VERSION"
elif [ -n "$dir" ]; then ver="${dir##*_}"
else
  tag=$(as_me git describe --tags --abbrev=0 2>/dev/null || echo v1.0.0)
  ver=$(echo "${tag#v}" | sed -E 's/-exp\.([0-9]+)$/.\1/; s/^([0-9]+\.[0-9]+\.[0-9]+)$/\1.0/')
fi
target="$PLUGINS/Movie Curator_$ver"

echo "Building v$ver ..."
as_me env DOTNET_CLI_TELEMETRY_OPTOUT=1 "$DOTNET" publish -c Release -o bin/publish --nologo -v q \
  -p:Version="$ver" -p:AssemblyVersion="$ver" -p:FileVersion="$ver"

# install: other Movie Curator versions out of the way, then the DLL + a fresh meta.json (status Active)
if [ ! -w "$PLUGINS" ] && [ ! -w "$target" ]; then
  echo "Can't write to $PLUGINS: run it with sudo."; exit 1
fi
for old in "$PLUGINS"/"Movie Curator_"*; do
  [ -e "$old" ] && [ "$old" != "$target" ] && rm -rf "$old" && echo "Removed $(basename "$old")"
done
mkdir -p "$target"
cp bin/publish/Jellyfin.Plugin.MovieCurator.dll "$target/"
sed "s/\"version\": \".*\"/\"version\": \"$ver\"/; s/__TIMESTAMP__/$(date -u +%Y-%m-%dT%H:%M:%SZ)/; s/\"changelog\": \".*\"/\"changelog\": \"local dev build\"/" \
  meta.json > "$target/meta.json"
[ "$(id -u)" = 0 ] && [ "$ME" != root ] && chown -R "$ME" "$target"
echo "Installed: $target"

echo "Restarting Jellyfin (new process) ..."
since=$(date -u '+%Y-%m-%d %H:%M:%S')
if [ -s "$KEY_FILE" ] && curl -fsS -X POST -H "X-Emby-Token: $(cat "$KEY_FILE")" "$JF_URL/System/Shutdown" >/dev/null 2>&1; then
  :
elif [ "$(id -u)" = 0 ]; then
  docker restart "$JF_CONTAINER" >/dev/null
else
  echo "No API key in $KEY_FILE and not root: restart Jellyfin yourself (or run with sudo)."; exit 1
fi
sleep 8
for _ in $(seq 1 90); do curl -fsS "$JF_URL/System/Info/Public" >/dev/null 2>&1 && break; sleep 2; done

log=$(ls -t "$JF_CONFIG"/log/log_*.log | head -1)
recent=$(awk -v s="[$since" '$0 >= s' "$log")
if echo "$recent" | grep -q 'Failed to load assembly.*MovieCurator'; then
  echo "!! Jellyfin could not load the plugin:"; echo "$recent" | grep -A2 'Failed to load assembly.*MovieCurator' | cut -c1-240; exit 1
fi
loaded=$(echo "$recent" | grep -h 'Loaded plugin: "Movie Curator"' | tail -1 | grep -o '"1[^"]*"$' || true)
echo "Jellyfin is back. Movie Curator loaded: ${loaded:-not yet (check Dashboard -> Plugins)}"
echo "Hard-refresh the curator page (Ctrl+Shift+R)."
