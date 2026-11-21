#!/usr/bin/env bash
# Tests due_environments.sh with a stand-in `az` (tests/fake-az) that reports dev's `started` tag.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
bin=$(mktemp -d)
trap 'rm -rf "$bin"' EXIT
ln -s "$here/tests/fake-az" "$bin/az"
ago() { date -u -d "$1 ago" +%Y-%m-%dT%H:%M:%SZ; }
check() { # description, expected, asked, dev's start (empty: no group)
  got=$(PATH="$bin:$PATH" DEV_STARTED="$4" "$here/due_environments.sh" "$3")
  [ "$got" = "$2" ] && echo "ok    $1" || { echo "FAIL  $1: got $got, want $2"; exit 1; }
}
check "started 5 hours ago: due" '["dev"]' "" "$(ago '5 hours')"
check "started 1 hour ago: not yet" '[]' "" "$(ago '1 hour')"
check "not running: nothing to do" '[]' "" ""
check "asked by hand: at once" '["dev"]' "dev" "$(ago '1 hour')"
