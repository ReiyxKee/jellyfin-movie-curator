#!/usr/bin/env bash
# Fast local test loop (no GitHub): build -> drop the DLL into the installed plugin folder -> restart Jellyfin via its API.
#   ./dev.sh
# The build is stamped with the installed version, so a real release later still installs normally over it.
#
# One-time setup:
#   - .NET 9 SDK: ~/.dotnet (or `dotnet` on PATH)
#   - write access to the installed plugin folder:   sudo chown -R "$USER" "<config>/plugins/Movie Curator_<version>"
#     (again after installing a release from the catalog: Jellyfin creates that folder as root)
#   - an API key: Jellyfin Dashboard -> API Keys -> +, saved in $KEY_FILE (chmod 600)
set -euo pipefail
cd "$(dirname "$0")"

JF_CONFIG="${JF_CONFIG:-$HOME/Servers/docker-jellyfin/config}"
JF_URL="${JF_URL:-http://localhost:7001}"
KEY_FILE="${JF_KEY_FILE:-$HOME/Servers/docker-jellyfin/.jellyfin_api_key}"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"

dir=$(ls -d "$JF_CONFIG"/plugins/"Movie Curator_"* 2>/dev/null | sort -V | tail -1 || true)
[ -n "$dir" ] || { echo "No installed 'Movie Curator_*' folder in $JF_CONFIG/plugins: install a release once from the catalog."; exit 1; }
ver="${dir##*_}"
[ -w "$dir" ] || { echo "Can't write to '$dir'. Once:  sudo chown -R $USER \"$dir\""; exit 1; }
[ -s "$KEY_FILE" ] || { echo "No API key in $KEY_FILE (Jellyfin: Dashboard -> API Keys -> +)."; exit 1; }
[ -x "$DOTNET" ] || { echo "No .NET SDK (looked for dotnet on PATH and ~/.dotnet/dotnet)."; exit 1; }

echo "Building as v$ver ..."
DOTNET_CLI_TELEMETRY_OPTOUT=1 "$DOTNET" publish -c Release -o bin/publish --nologo -v q \
  -p:Version="$ver" -p:AssemblyVersion="$ver" -p:FileVersion="$ver"
cp bin/publish/Jellyfin.Plugin.MovieCurator.dll "$dir/"
echo "Copied into: $dir"

echo "Restarting Jellyfin ..."
since=$(date -u +%Y-%m-%dT%H:%M:%S)
curl -fsS -X POST -H "X-Emby-Token: $(cat "$KEY_FILE")" "$JF_URL/System/Restart" >/dev/null
sleep 5
for _ in $(seq 1 90); do
  curl -fsS "$JF_URL/System/Info/Public" >/dev/null 2>&1 && break
  sleep 2
done

log=$(ls -t "$JF_CONFIG"/log/log_*.log | head -1)
loaded=$(grep -h 'Loaded plugin: "Movie Curator"' "$log" | tail -1 | grep -o '"1[^"]*"$' || true)
echo "Jellyfin is back (restart requested $since UTC). Movie Curator loaded: ${loaded:-?}"
echo "Hard-refresh the curator page (Ctrl+Shift+R)."
