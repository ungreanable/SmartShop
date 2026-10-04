#!/usr/bin/env bash
# Fails when a NuGet dependency (direct or transitive) uses a copyleft or non-OSI license.
# SmartShop is MIT: linked libraries must be permissive (MIT, Apache-2.0, BSD, MPL-2.0 unmodified ...).
set -euo pipefail

dotnet restore SmartShop.slnx >/dev/null
packages_dir="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
denied='GPL-2.0|GPL-3.0|AGPL|LGPL-2|LGPL-3|SSPL|BUSL|Commercial|Elastic-2.0'
failed=0

while read -r name version; do
  nuspec=$(find "$packages_dir/${name,,}/$version" -maxdepth 1 -name '*.nuspec' 2>/dev/null | head -1)
  [[ -z "$nuspec" ]] && continue
  license=$(grep -oP '(?<=<license type="expression">)[^<]+' "$nuspec" || true)
  if [[ -n "$license" && "$license" =~ $denied ]]; then
    echo "::error::$name $version is licensed under $license"
    failed=1
  fi
done < <(dotnet list SmartShop.slnx package --include-transitive --format json \
  | python3 -c 'import json,sys
d=json.load(sys.stdin)
seen=set()
for p in d.get("projects",[]):
  for f in p.get("frameworks",[]):
    for k in ("topLevelPackages","transitivePackages"):
      for pkg in f.get(k,[]):
        key=(pkg["id"],pkg["resolvedVersion"])
        if key not in seen:
          seen.add(key); print(*key)')

if [[ $failed -eq 0 ]]; then echo "All NuGet licenses are permissive."; fi
exit $failed
