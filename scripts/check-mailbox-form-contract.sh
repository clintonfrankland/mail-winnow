#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
page="$repo_root/src/MailWinnow.Web/Components/Pages/Mailboxes.razor"
endpoints="$repo_root/src/MailWinnow.Web/Security/MailboxEndpoints.cs"

grep -Fq 'action="/mailboxes/source/save"' "$page"
grep -Fq 'name="UseSsl" type="checkbox"' "$page"

new_source_form="$(awk '/<dialog id="add-source-dialog"/{capture=1} capture{print} /<\/form>/{if(capture) exit}' "$page")"
if grep -Fq 'name="Id"' <<<"$new_source_form"; then
  echo 'mailbox form contract failed: new source form must omit Id' >&2
  exit 1
fi

grep -Fq 'public sealed class SourceRequest' "$endpoints"
grep -Fq 'public Guid? Id { get; init; }' "$endpoints"
grep -Fq 'public bool UseSsl { get; init; }' "$endpoints"
grep -Fq 'public bool Enabled { get; init; }' "$endpoints"

grep -Fq '>Source mailboxes</h2>' "$page"
grep -Fq '>Add source</button>' "$page"
grep -Fq '>Destination mailbox</h2>' "$page"
grep -Fq 'Credential stored' "$page"
grep -Fq 'Password</dt><dd>••••••••</dd>' "$page"
if grep -Fq 'ProtectedCredential' "$page"; then
  echo 'mailbox form contract failed: protected credentials must not be rendered' >&2
  exit 1
fi

echo 'mailbox form contract passed'
