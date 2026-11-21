#!/usr/bin/env bash
# Prints, as a JSON list, the environments `destroy` should remove (D45): the one asked for by hand, or every
# environment whose resource group was started 4 or more hours ago (its `started` tag). Environments are the
# stacks in stacks/environment (Pulumi.<name>.yaml).
set -euo pipefail
asked=${1:-}
max=$((4 * 3600))
here=$(cd "$(dirname "$0")/../.." && pwd)

due=()
if [ -n "$asked" ]; then
  due=("$asked")
else
  for config in "$here"/stacks/environment/Pulumi.*.yaml; do
    environment=$(basename "$config" .yaml | sed 's/^Pulumi\.//')
    started=$(az group show --name "rg-$environment" --query tags.started -o tsv 2>/dev/null) || continue
    [ -n "$started" ] || continue
    age=$(($(date -u +%s) - $(date -u -d "$started" +%s)))
    [ "$age" -lt "$max" ] || due+=("$environment")
  done
fi
printf '%s\n' "${due[@]}" | jq -Rsc 'split("\n") | map(select(. != ""))'
