#!/usr/bin/env bash
# Deletes everything in an environment's resource group, but neither the group nor the role assignments on it:
# they're the access stack's (D95). `destroy` falls back to it when `pulumi destroy` fails. Some resources can
# only go once others have (a network once the cluster in it), so it repeats until the group is empty. A resource's
# diagnostic settings go first: they'd outlive it and reattach to the next one with its name (D98).
set -euo pipefail
group=$1
for _ in 1 2 3 4 5; do
  ids=$(az resource list --resource-group "$group" --query "[].id" -o tsv)
  [ -n "$ids" ] || exit 0
  for id in $ids; do
    for setting in $(az monitor diagnostic-settings list --resource "$id" --query "[].name" -o tsv 2>/dev/null); do
      az monitor diagnostic-settings delete --resource "$id" --name "$setting"
    done
  done
  # shellcheck disable=SC2086 # one argument per id
  az resource delete --ids $ids || true
done
echo "$group still holds:" >&2
az resource list --resource-group "$group" --query "[].id" -o tsv >&2
exit 1
