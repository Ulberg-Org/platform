#!/usr/bin/env bash
# Tests check_teardown_use.sh against small workflow folders: the honest case, and each way around it.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

destroy='on: { schedule: [ { cron: "*/15 * * * *" } ] }
jobs: { destroy: { runs-on: ubuntu-latest, environment: teardown, steps: [ { run: "true" } ] } }'
start='on: { workflow_dispatch: {} }
jobs: { start: { runs-on: ubuntu-latest, environment: dev, steps: [ { run: "true" } ] } }'

check() { # description, want (pass|fail), case folder
  if "$here/check_teardown_use.sh" "$work/$3" >/dev/null 2>&1; then got=pass; else got=fail; fi
  [ "$got" = "$2" ] && echo "ok    $1" || { echo "FAIL  $1: $got, want $2"; exit 1; }
}

mkdir "$work/honest" "$work/other" "$work/map" "$work/expression" "$work/callable"
echo "$destroy" >"$work/honest/destroy.yml"
echo "$start" >"$work/honest/start.yml"
check "destroy.yml uses teardown, start.yml uses dev" pass honest

echo "$destroy" >"$work/other/destroy.yml"
echo 'on: push
jobs: { x: { runs-on: ubuntu-latest, environment: teardown, steps: [ { run: "true" } ] } }' >"$work/other/sneaky.yml"
check "another workflow names teardown" fail other

echo "$destroy" >"$work/map/destroy.yml"
echo 'on: push
jobs: { x: { runs-on: ubuntu-latest, environment: { name: teardown, url: "https://x" }, steps: [ { run: "true" } ] } }' >"$work/map/sneaky.yaml"
check "another workflow names it as a map" fail map

echo "$destroy" >"$work/expression/destroy.yml"
echo 'on: push
jobs: { x: { runs-on: ubuntu-latest, environment: "${{ inputs.where }}", steps: [ { run: "true" } ] } }' >"$work/expression/sneaky.yml"
check "an environment given by an expression" fail expression

echo 'on: { workflow_call: {}, schedule: [ { cron: "*/15 * * * *" } ] }
jobs: { destroy: { runs-on: ubuntu-latest, environment: teardown, steps: [ { run: "true" } ] } }' >"$work/callable/destroy.yml"
check "destroy.yml callable by other workflows" fail callable

mkdir "$work/broken"
echo "$destroy" >"$work/broken/destroy.yml"
printf 'on: push\njobs: { x: [ unclosed\n' >"$work/broken/sneaky.yml"
check "a file the check can't read" fail broken
