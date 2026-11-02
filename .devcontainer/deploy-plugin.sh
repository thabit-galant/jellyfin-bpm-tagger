#!/usr/bin/env bash
# Builds the plugin, installs it into the dev Jellyfin server and restarts that server.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
container=bpm-tagger-jellyfin
version="$(grep -oP '"version":\s*"\K[^"]+' "$root/meta.json")"
target="$root/.dev/jellyfin/config/plugins/BPM Tagger_${version}"
have_docker=false
if command -v docker >/dev/null 2>&1 && docker inspect "$container" >/dev/null 2>&1; then
  have_docker=true
fi

dotnet publish "$root/Jellyfin.Plugin.BpmTagger/Jellyfin.Plugin.BpmTagger.csproj" -c Release -o "$root/publish"

# Files created while Jellyfin ran as root can't be written from here. Hand them back to this user,
# using a throwaway root container that mounts the same volumes (works even if Jellyfin is stopped).
if [ -d "$root/.dev" ] && [ -n "$(find "$root/.dev" ! -user "$(id -u)" -print -quit)" ]; then
  if $have_docker; then
    echo "Fixing ownership of .dev/ (files were created by root)..."
    image="$(docker inspect --format '{{.Config.Image}}' "$container")"
    docker run --rm --user 0 --entrypoint chown --volumes-from "$container" "$image" \
      -R "$(id -u):$(id -g)" /config /cache /music
  else
    echo "Some files in .dev/ belong to root. On the host, run: sudo chown -R \$USER: .dev" >&2
    exit 1
  fi
fi

mkdir -p "$target"
cp "$root/publish/Jellyfin.Plugin.BpmTagger.dll" "$root/meta.json" "$target/"
echo "Installed to .dev/jellyfin/config/plugins/BPM Tagger_${version}"

if $have_docker; then
  docker restart "$container" >/dev/null
  echo "Restarted Jellyfin. It will be back at http://localhost:8096 in a few seconds."
else
  echo "Restart the Jellyfin container to load the new build."
fi