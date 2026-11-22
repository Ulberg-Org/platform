#!/usr/bin/env bash
# Tests check_leftovers.sh with stand-ins for az and pulumi (tests/fake-*-leftovers) reading the cases in
# tests/leftovers/: what Azure has next to what the stack's state knows.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
bin=$(mktemp -d)
trap 'rm -rf "$bin"' EXIT
ln -s "$here/tests/fake-az-leftovers" "$bin/az"
ln -s "$here/tests/fake-pulumi-leftovers" "$bin/pulumi"
check() { # description, case, expected exit code, text the output must contain
  code=0
  output=$(PATH="$bin:$PATH" FIXTURE="$here/tests/leftovers/$2" "$here/check_leftovers.sh" dev 2>&1) || code=$?
  [ "$code" = "$3" ] || { echo "FAIL  $1: exit $code, want $3"; echo "$output"; exit 1; }
  grep -qF -- "$4" <<<"$output" || { echo "FAIL  $1: output lacks '$4'"; echo "$output"; exit 1; }
  echo "ok    $1"
}
check "everything in Azure is in the state (ids compared ignoring case)" clean 0 "No leftovers"
check "a diagnostic setting the state doesn't know fails" orphan-setting 1 "diagnosticsettings/telemetry"
check "a resource the state doesn't know fails" orphan-resource 1 "namespaces/evhns"
check "the subscription's setting the state doesn't know fails" orphan-subscription-setting 1 "telemetry-dev"
check "no group: nothing can clash" no-group 0 "doesn't exist"
