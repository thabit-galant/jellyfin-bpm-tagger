# .NET 9 SDK (matches Jellyfin 10.11) plus ffmpeg for running the BPM detector locally.
FROM mcr.microsoft.com/devcontainers/dotnet:1-9.0-bookworm
 
# The base image includes a Yarn apt source whose signing key has expired, which makes
# `apt-get update` fail. Yarn isn't needed here, so drop that source before updating.
RUN grep -rl 'dl.yarnpkg.com' /etc/apt/sources.list.d/ 2>/dev/null | xargs -r rm -f \
    && apt-get update \
    && export DEBIAN_FRONTEND=noninteractive \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
