#!/usr/bin/env bash
# Tests check_teardown_use.sh against the workflow folders in tests/teardown: the honest case, and each way
# around it.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
check() { # description, want (pass|fail), case folder
  if "$here/check_teardown_use.sh" "$here/tests/teardown/$3" >/dev/null 2>&1; then got=pass; else got=fail; fi
  [ "$got" = "$2" ] && echo "ok    $1" || { echo "FAIL  $1: $got, want $2"; exit 1; }
}
check "destroy.yml uses teardown, start.yml uses dev" pass honest
check "another workflow names teardown" fail other
check "another workflow names it as a map" fail map
check "an environment given by an expression" fail expression
check "destroy.yml callable by other workflows" fail callable
check "a file the check can't read" fail broken
