#!/usr/bin/env bash
# Tests empty_group.sh with a stand-in `az` (tests/fake-az-resources) holding a group's resources in a file.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
bin=$(mktemp -d)
trap 'rm -rf "$bin"' EXIT
ln -s "$here/tests/fake-az-resources" "$bin/az"
check() { # description, the group's resources (one per line), expected exit code, their diagnostic settings
  printf '%b' "$2" >"$bin/resources"
  printf '%b' "${4:-}" >"$bin/settings"
  code=0
  PATH="$bin:$PATH" RESOURCES="$bin/resources" SETTINGS="$bin/settings" "$here/empty_group.sh" rg-dev >/dev/null 2>&1 ||
    code=$?
  [ "$code" = "$3" ] || { echo "FAIL  $1: exit $code, want $3"; exit 1; }
  if [ "$3" = 0 ] && [ -s "$bin/resources" ]; then echo "FAIL  $1: left $(cat "$bin/resources")"; exit 1; fi
  if [ -s "$bin/settings" ]; then echo "FAIL  $1: left the diagnostic settings $(cat "$bin/settings")"; exit 1; fi
  echo "ok    $1"
}
check "everything goes" 'aks\nidentity\n' 0
check "a network goes once the cluster in it has" 'vnet\naks\n' 0
check "an empty group: nothing to do" '' 0
check "what never goes fails the step" 'stuck\naks\n' 1
# They'd outlive it and reattach to a new resource of the same name (Microsoft's advice: delete them first, D98).
check "a resource's diagnostic settings go before it" 'aks\nserver\n' 0 'server telemetry\n'
