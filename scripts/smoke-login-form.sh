#!/usr/bin/env bash
set -euo pipefail

base_url="${1:-http://127.0.0.1:9080}"
login_url="${base_url%/}/auth/login"

check_submission() {
  local label="$1"
  shift

  local work_dir
  work_dir="$(mktemp -d)"
  local cookie_jar="$work_dir/cookies"
  local login_page="$work_dir/login.html"
  trap 'rm -rf -- "$work_dir"' RETURN

  curl --silent --show-error \
    --cookie-jar "$cookie_jar" \
    --output "$login_page" \
    "${base_url%/}/login"

  local antiforgery_token
  antiforgery_token="$(sed -n 's/.*name="__RequestVerificationToken" value="\([^"]*\)".*/\1/p' "$login_page" | head -n 1)"
  if [[ -z "$antiforgery_token" ]]; then
    echo "$label login form smoke failed: anti-forgery token missing" >&2
    return 1
  fi

  local headers
  headers="$(curl --silent --show-error --output /dev/null --dump-header - \
    --request POST \
    --cookie "$cookie_jar" \
    --header 'Content-Type: application/x-www-form-urlencoded' \
    --data-urlencode "__RequestVerificationToken=$antiforgery_token" \
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
