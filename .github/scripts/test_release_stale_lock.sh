#!/usr/bin/env bash
# Tests release_stale_lock.sh with a stand-in `pulumi` (tests/fake-pulumi): a lock released, no lock, an error.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
bin=$(mktemp -d)
trap 'rm -rf "$bin"' EXIT
ln -s "$here/tests/fake-pulumi" "$bin/pulumi"
check() { # description, case, want (pass|fail)
  if PATH="$bin:$PATH" CASE="$2" "$here/release_stale_lock.sh" dev >/dev/null 2>&1; then got=pass; else got=fail; fi
  [ "$got" = "$3" ] && echo "ok    $1" || { echo "FAIL  $1: $got, want $3"; exit 1; }
}
check "a stale lock is released" locked pass
check "no lock is fine" free pass
check "any other error stops the run" broken fail
