#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
pages_dir="$repo_dir/src/MailWinnow.Web/Components/Pages"

if rg -q 'Hello, world!|Development Mode|Weather forecast' "$pages_dir"; then
  echo "UI contract failed: template or diagnostic copy is present." >&2
  exit 1
fi

rg -q 'Private mail, thoughtfully filtered' "$pages_dir/Home.razor"
rg -q 'Something went wrong' "$pages_dir/Error.razor"
rg -q '@media\(max-width:700px\)' "$repo_dir/src/MailWinnow.Web/wwwroot/app.css"
rg -q 'prefers-reduced-motion' "$repo_dir/src/MailWinnow.Web/wwwroot/accessibility.css"

echo "UI contract passed."
