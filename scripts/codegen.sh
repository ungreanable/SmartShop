#!/usr/bin/env bash
# Pre-generates Wolverine handler code for a host into <host>/Internal/Generated (git-ignored), so the image does not
# compile handlers with Roslyn at runtime (~1 GB of memory, never released). Run right before publishing an image.
# No database or broker is needed; the settings below only satisfy start-up validation.
# Usage: scripts/codegen.sh src/Hosts/SmartShop.Api/SmartShop.Api.csproj
set -euo pipefail
project="$1"
case "$project" in
  *SmartShop.Api*|*SmartShop.Worker*) ;;
  *) echo "codegen: $project has no message handlers, skipping"; exit 0 ;;
esac

if [ -d "$project" ]; then dir="$project"; else dir="$(dirname "$project")"; fi
rm -rf "$dir/Internal/Generated"
dotnet build "$project" -c Release -v q -nologo

ASPNETCORE_ENVIRONMENT=Production \
Messaging__Role=Standalone \
ConnectionStrings__smartshop="Host=localhost;Database=codegen;Username=codegen;Password=codegen" \
ConnectionStrings__cache="localhost:6379" \
Auth__Jwt__SigningKey="codegen-placeholder-signing-key-0123456789" \
Media__SigningKey="codegen" \
  timeout 600 dotnet run --no-build -c Release --project "$project" -- codegen write > /dev/null

count=$(find "$dir/Internal/Generated" -name '*.cs' | wc -l)
echo "codegen: $count files in $dir/Internal/Generated"
[ "$count" -gt 0 ]
