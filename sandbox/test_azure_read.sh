#!/usr/bin/env bash
# Tests sandbox/azure-read with a stand-in curl (tests/fake-curl) and tokens in a temporary home.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
home=$(mktemp -d)
trap 'rm -rf "$home"' EXIT
mkdir -p "$home/bin" "$home/.config/azure-read"
ln -s "$here/tests/fake-curl" "$home/bin/curl"
read_() { HOME=$home PATH="$home/bin:$PATH" "$here/azure-read" "$@" 2>&1; }
expect() { # description, got, want
  [ "$2" = "$3" ] && echo "ok    $1" || { printf 'FAIL  %s\n got: %s\nwant: %s\n' "$1" "$2" "$3"; exit 1; }
}

expect "without tokens it says why" "$(read_ /subscriptions || true)" \
  "No Azure token: only the platform profile has one (sandbox/tokens renews it every 45 minutes)"

echo management-token >"$home/.config/azure-read/management"
echo logs-token >"$home/.config/azure-read/logs"
expect "a path is a GET on Azure Resource Manager with its token" \
  "$(read_ '/subscriptions?api-version=2022-12-01' | head -2)" \
  "GET https://management.azure.com/subscriptions?api-version=2022-12-01
Authorization: Bearer management-token"
expect "logs is a Log Analytics query with its token" \
  "$(read_ logs ws-1 'AzureActivity | take 1')" \
  'POST https://api.loganalytics.io/v1/workspaces/ws-1/query
Authorization: Bearer logs-token
{"query":"AzureActivity | take 1"}'
expect "anything else is refused" "$(read_ https://example.com || true)" \
  "usage: azure-read <ARM path, starting with /> | azure-read logs <workspace id> <KQL query>"
