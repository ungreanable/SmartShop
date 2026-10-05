#!/usr/bin/env bash
# Builds the api, worker and web container images into the local Docker engine (no registry, no Dockerfile).
# Usage: scripts/build-images.sh [prefix] [tag] [runtime]
#   prefix   default smartshop-local
#   tag      default latest
#   runtime  linux-x64 (default, Intel/AMD servers) or linux-arm64 (ARM servers, e.g. Hetzner CAX)
# Then in deploy/.env: IMAGE_PREFIX=<prefix>  TAG=<tag>
# Copy to a server: docker save <prefix>/smartshop-api:<tag> <prefix>/smartshop-worker:<tag> <prefix>/smartshop-web:<tag> | gzip > smartshop-images.tar.gz
set -euo pipefail
prefix="${1:-smartshop-local}"
tag="${2:-latest}"
runtime="${3:-linux-x64}"
cd "$(dirname "$0")/.."

for project in src/Hosts/SmartShop.Api src/Hosts/SmartShop.Worker src/Clients/SmartShop.Web; do
  name="smartshop-$(basename "$project" | sed 's/^SmartShop\.//' | tr '[:upper:]' '[:lower:]')"
  echo "==> $prefix/$name:$tag ($runtime)"
  bash scripts/codegen.sh "$project"
  dotnet publish "$project" -c Release -t:PublishContainer \
    -p:ContainerRuntimeIdentifier="$runtime" \
    -p:ContainerRepository="$prefix/$name" -p:ContainerImageTag="$tag"
done

docker image ls --filter "reference=$prefix/*"
