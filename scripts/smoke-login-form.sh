#!/usr/bin/env bash
set -euo pipefail

base_url="${1:-http://127.0.0.1:9080}"
login_url="${base_url%/}/auth/login"

check_submission() {
  local label="$1"
  shift

  local headers
  headers="$(curl --silent --show-error --output /dev/null --dump-header - \
    --request POST \
    --header 'Content-Type: application/x-www-form-urlencoded' \
    --data-urlencode 'Email=login-smoke-invalid@example.test' \
    --data-urlencode 'Password=IntentionallyInvalidPassword1' \
    "$@" \
    "$login_url")"

  local status
  status="$(awk 'toupper($1) ~ /^HTTP\// { code=$2 } END { print code }' <<<"$headers")"
  local location
  location="$(awk 'BEGIN { IGNORECASE=1 } /^Location:/ { sub(/\r$/, "", $2); print $2 }' <<<"$headers")"

  if [[ "$status" != "302" || "$location" != /login\?error=* ]]; then
    echo "$label login form smoke failed: status=${status:-missing} location=${location:-missing}" >&2
    return 1
  fi

  echo "$label login form smoke passed"
}

check_submission unchecked
check_submission checked --data-urlencode 'RememberMe=true'
