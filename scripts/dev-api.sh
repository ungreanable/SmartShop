#!/usr/bin/env bash
# Tiny helper for poking the local API as a dev-login user (development only).
# Bodies are passed as Python dict literals so non-ASCII (Thai) text survives every shell.
# Usage: source scripts/dev-api.sh; token=$(dev_login key "Display Name"); api POST /api/plants/join "$token" "{'joinCode':'ABC'}"
BASE="${SMARTSHOP_URL:-http://localhost:5100}"
_json() { PYTHONIOENCODING=utf-8 python -c "import json,sys;sys.stdout.write(json.dumps(eval(sys.argv[1]),ensure_ascii=True))" "$1"; }
dev_login() { _json "{'key':'$1','displayName':'$2'}" | curl -s -X POST -H "Content-Type: application/json" --data-binary @- "$BASE/api/auth/dev" \
  | python -c "import sys,json;print(json.load(sys.stdin)['accessToken'])"; }
api() { local method=$1 path=$2 token=$3 body=${4:-}
  if [ -n "$body" ]; then
    _json "$body" | curl -s -X "$method" -H "Authorization: Bearer $token" -H "Content-Type: application/json" ${PLANT:+-H "X-Plant-Id: $PLANT"} ${IDEMPOTENCY:+-H "Idempotency-Key: $IDEMPOTENCY"} --data-binary @- "$BASE$path"
  else
    curl -s -X "$method" -H "Authorization: Bearer $token" ${PLANT:+-H "X-Plant-Id: $PLANT"} "$BASE$path"
  fi; }
