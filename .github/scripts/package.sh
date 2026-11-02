#!/usr/bin/env bash
# Builds the plugin and zips it with a meta.json stamped for this release.
# Usage: package.sh <version> <owner> [changelog] [timestamp]
set -euo pipefail

version="$1"
owner="$2"
changelog="${3:-Version $version}"
timestamp="${4:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}"

root="$(cd "$(dirname "$0")/../.." && pwd)"
dist="$root/dist"
stage="$dist/bpm-tagger_$version"
zip_path="$stage.zip"

rm -rf "$stage" "$zip_path"
mkdir -p "$stage"

dotnet publish "$root/Jellyfin.Plugin.BpmTagger/Jellyfin.Plugin.BpmTagger.csproj" -c Release -o "$dist/publish" \
  -p:Version="$version" -p:AssemblyVersion="$version" -p:FileVersion="$version"

cp "$dist/publish/Jellyfin.Plugin.BpmTagger.dll" "$stage/"
jq --arg version "$version" --arg owner "$owner" --arg changelog "$changelog" --arg timestamp "$timestamp" \
  '.version = $version | .owner = $owner | .changelog = $changelog | .timestamp = $timestamp' \
  "$root/meta.json" > "$stage/meta.json"

# Jellyfin unpacks the zip straight into the plugin's folder, so the files sit at its root.
(cd "$stage" && zip -q "$zip_path" Jellyfin.Plugin.BpmTagger.dll meta.json)
echo "Packaged $zip_path"
