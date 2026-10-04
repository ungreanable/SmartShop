#!/usr/bin/env bash
# Usage: scripts/add-migration.sh <Module> <MigrationName>
# Example: scripts/add-migration.sh Ordering AddOrderChat
set -euo pipefail
module="$1"; name="$2"
project="src/Modules/SmartShop.Modules.${module}"
context="${module}DbContext"
dotnet tool restore >/dev/null
dotnet ef migrations add "$name" --project "$project" --startup-project "$project" --context "$context" --output-dir Data/Migrations
