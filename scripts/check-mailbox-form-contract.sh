#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
page="$repo_root/src/MailWinnow.Web/Components/Pages/Mailboxes.razor"
endpoints="$repo_root/src/MailWinnow.Web/Security/MailboxEndpoints.cs"

grep -Fq 'action="/mailboxes/source/save"' "$page"
grep -Fq 'name="UseSsl" type="checkbox"' "$page"

new_source_form="$(awk '/<h3>Add source account<\/h3>/{capture=1} capture{print} /<\/form>/{if(capture) exit}' "$page")"
if grep -Fq 'name="Id"' <<<"$new_source_form"; then
  echo 'mailbox form contract failed: new source form must omit Id' >&2
  exit 1
fi

grep -Fq 'public sealed class SourceRequest' "$endpoints"
grep -Fq 'public Guid? Id { get; init; }' "$endpoints"
grep -Fq 'public bool UseSsl { get; init; }' "$endpoints"
grep -Fq 'public bool Enabled { get; init; }' "$endpoints"

echo 'mailbox form contract passed'
