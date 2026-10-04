#!/usr/bin/env bash
# Builds the api, worker and web container images into the local Docker engine (no registry, no Dockerfile).
# Usage: scripts/build-images.sh [prefix] [tag]      default: smartshop-local latest
# Then in deploy/.env: IMAGE_PREFIX=smartshop-local  TAG=latest
set -euo pipefail
prefix="${1:-smartshop-local}"
tag="${2:-latest}"
cd "$(dirname "$0")/.."

for project in src/Hosts/SmartShop.Api src/Hosts/SmartShop.Worker src/Clients/SmartShop.Web; do
  name="smartshop-$(basename "$project" | sed 's/^SmartShop\.//' | tr '[:upper:]' '[:lower:]')"
  echo "==> $prefix/$name:$tag"
  dotnet publish "$project" -c Release -t:PublishContainer \
    -p:ContainerRepository="$prefix/$name" -p:ContainerImageTag="$tag"
done

docker image ls --filter "reference=$prefix/*"
