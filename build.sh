#!/usr/bin/env bash
# Build the plugin and stage it as dist/MovieCurator_<version>/ ready to copy into Jellyfin.
#   ./build.sh            uses `dotnet` (.NET 9 SDK) if installed
#   sudo ./build.sh       without a local SDK, builds inside the official .NET SDK docker image
set -euo pipefail
cd "$(dirname "$0")"
VERSION=$(sed -n 's:.*<AssemblyVersion>\(.*\)</AssemblyVersion>.*:\1:p' Jellyfin.Plugin.MovieCurator.csproj)
OUT="dist/MovieCurator_${VERSION}"

DOTNET="${DOTNET:-$(command -v dotnet || true)}"
[ -z "$DOTNET" ] && [ -x "$HOME/.dotnet/dotnet" ] && DOTNET="$HOME/.dotnet/dotnet"

if [ -n "$DOTNET" ]; then
  "$DOTNET" publish -c Release -o bin/publish
else
  echo "No dotnet SDK found - building in docker (mcr.microsoft.com/dotnet/sdk:9.0)"
  docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:9.0 \
    dotnet publish -c Release -o bin/publish
  [ -n "${SUDO_UID:-}" ] && chown -R "$SUDO_UID:$SUDO_GID" bin obj
fi

rm -rf dist && mkdir -p "$OUT"
cp bin/publish/Jellyfin.Plugin.MovieCurator.dll "$OUT/"
sed "s/\"version\": \".*\"/\"version\": \"${VERSION}\"/; s/__TIMESTAMP__/$(date -u +%Y-%m-%dT%H:%M:%SZ)/" meta.json > "$OUT/meta.json"
[ -n "${SUDO_UID:-}" ] && chown -R "$SUDO_UID:$SUDO_GID" dist

cat <<EOF

Built $OUT
Install: copy it into Jellyfin's plugins folder and restart Jellyfin, e.g.
  JF_CONFIG=/path/to/jellyfin/config      # the folder mounted as /config in docker
  sudo rm -rf "\$JF_CONFIG"/plugins/MovieCurator_*
  sudo cp -r $OUT "\$JF_CONFIG"/plugins/
  sudo docker restart jellyfin            # or: sudo systemctl restart jellyfin
Then open Dashboard -> Movie Curator.
EOF
