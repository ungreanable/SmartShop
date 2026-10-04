#!/usr/bin/env bash
# Publishes one host as a multi-arch (linux/amd64 + linux/arm64) image to a registry.
# Each architecture is built on its own (building both in one publish races on shared obj/ files), then
# the tags are created as a multi-arch index with `docker buildx imagetools`.
#
# Usage: scripts/publish-multiarch.sh <project.csproj> <registry> <repository> "<tag1;tag2;...>"
# Example (CI):   scripts/publish-multiarch.sh src/Hosts/SmartShop.Api/SmartShop.Api.csproj ghcr.io ungreanable/smartshop-api "edge;sha-abc1234"
# Example (test): docker run -d -p 5005:5000 registry:2 && scripts/publish-multiarch.sh ... localhost:5005 test/smartshop-api edge
set -euo pipefail
project="$1"; registry="$2"; repository="$3"; tags="$4"
build_tag="build-$(date +%s)"

for arch in x64 arm64; do
  echo "==> $registry/$repository:$build_tag-$arch"
  dotnet publish "$project" -c Release -t:PublishContainer \
    -p:ContainerRegistry="$registry" -p:ContainerRepository="$repository" \
    -p:ContainerRuntimeIdentifier="linux-$arch" -p:ContainerImageTag="$build_tag-$arch"
done

IFS=';' read -ra tag_list <<< "$tags"
for tag in "${tag_list[@]}"; do
  echo "==> $registry/$repository:$tag (linux/amd64, linux/arm64)"
  docker buildx imagetools create -t "$registry/$repository:$tag" \
    "$registry/$repository:$build_tag-x64" "$registry/$repository:$build_tag-arm64"
done
