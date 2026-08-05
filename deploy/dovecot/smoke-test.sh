#!/bin/sh
# Builds this template, starts a temporary IMAPS service, then removes all test state.
set -eu

command -v docker >/dev/null || { echo 'docker is required' >&2; exit 1; }
docker compose version >/dev/null
root_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
temp_dir=$(mktemp -d)
project="dovecot-smoke-$$"
cleanup() {
  docker compose --project-directory "$root_dir" -p "$project" down --volumes --remove-orphans >/dev/null 2>&1 || true
  # TLS creation runs as container root, so clear the disposable bind mount from a
  # container before removing its now-empty host directory.
  DOVECOT_CONFIG_DIR="$temp_dir" docker compose --project-directory "$root_dir" -p "$project" run --rm --no-deps --entrypoint sh dovecot -c 'rm -rf /etc/dovecot/local/* /etc/dovecot/local/.[!.]*' >/dev/null 2>&1 || true
  rmdir "$temp_dir" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

hash=$(docker compose --project-directory "$root_dir" run --rm --no-deps --entrypoint doveadm dovecot pw -s SHA512-CRYPT -p smoke-test-password)
printf 'smoke@example.test:%s\n' "$hash" > "$temp_dir/users"
DOVECOT_CONFIG_DIR="$temp_dir" DOVECOT_IMAPS_PORT=19993 \
  docker compose --project-directory "$root_dir" -p "$project" up --build -d

attempt=0
until printf 'a logout\r\n' | openssl s_client -quiet -connect 127.0.0.1:19993 2>/dev/null | grep -q '^\* OK'; do
  attempt=$((attempt + 1))
  [ "$attempt" -lt 20 ] || { docker compose --project-directory "$root_dir" -p "$project" logs >&2; exit 1; }
  sleep 2
done
echo 'Dovecot IMAPS smoke test passed.'
