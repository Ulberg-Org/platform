#!/usr/bin/env bash
# Tests empty_group.sh with a stand-in `az` (tests/fake-az-resources) holding a group's resources in a file.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
bin=$(mktemp -d)
trap 'rm -rf "$bin"' EXIT
ln -s "$here/tests/fake-az-resources" "$bin/az"
check() { # description, the group's resources (one per line), expected exit code
  printf '%b' "$2" >"$bin/resources"
  code=0
  PATH="$bin:$PATH" RESOURCES="$bin/resources" "$here/empty_group.sh" rg-dev >/dev/null 2>&1 || code=$?
  [ "$code" = "$3" ] || { echo "FAIL  $1: exit $code, want $3"; exit 1; }
  if [ "$3" = 0 ] && [ -s "$bin/resources" ]; then echo "FAIL  $1: left $(cat "$bin/resources")"; exit 1; fi
  echo "ok    $1"
}
check "everything goes" 'aks\nidentity\n' 0
check "a network goes once the cluster in it has" 'vnet\naks\n' 0
check "an empty group: nothing to do" '' 0
check "what never goes fails the step" 'stuck\naks\n' 1
