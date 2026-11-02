#!/usr/bin/env bash
# Adds a packaged zip to the Jellyfin repository manifest, replacing any entry for the same version.
# Usage: update-manifest.sh <manifest.json> <plugin.zip> <download url>
set -euo pipefail

manifest="$1"
zip_path="$2"
source_url="$3"

meta="$(unzip -p "$zip_path" meta.json)"
# Jellyfin checks the download against this MD5 before installing it.
checksum="$(md5sum "$zip_path" | cut -d' ' -f1)"

updated="$(jq --argjson meta "$meta" --arg checksum "$checksum" --arg url "$source_url" '
  ($meta | {version, changelog, targetAbi, timestamp, checksum: $checksum, sourceUrl: $url}) as $entry
  | (map(select(.guid == $meta.guid))[0].versions // []) as $existing
  | map(select(.guid != $meta.guid)) + [
      ($meta | {guid, name, description, overview, owner, category}) + {
        versions: ($existing | map(select(.version != $entry.version)) + [$entry]
          | sort_by(.version | split(".") | map(tonumber)) | reverse)
      }
    ]
' "$manifest")"

printf '%s\n' "$updated" > "$manifest"
echo "Added version $(jq -r .version <<< "$meta") to $manifest"
