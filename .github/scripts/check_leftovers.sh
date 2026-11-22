#!/usr/bin/env bash
# The leftovers check (D98), in the PR's environment preview: what exists in Azure for an environment but not in
# its stack's state would make the next `start` fail with "already exists". That's the resources in its group,
# their diagnostic settings (which outlive their resource and reattach to a new one of the same name) and the
# subscription's telemetry-<env> setting. Children Azure creates itself (Azure Monitor's hubs) aren't listed by
# `az resource list`, so they never count. Read-only; run from the repo, signed in to az and Pulumi.
#
#   check_leftovers.sh <environment>
set -euo pipefail
environment=${1:?usage: check_leftovers.sh <environment>}
group=rg-$environment
here=$(cd "$(dirname "$0")/../.." && pwd)

[ "$(az group exists --name "$group")" = true ] || { echo "$group doesn't exist: nothing can clash"; exit 0; }

# Azure resource ids are case-insensitive, and az and Pulumi write some parts differently.
lower() { tr '[:upper:]' '[:lower:]'; }
known=$(cd "$here/stacks/environment" && pulumi stack export --stack "$environment" |
  jq -r '.deployment.resources[].id // empty' | lower | sort -u)
resources=$(az resource list --resource-group "$group" --query "[].id" -o tsv)
in_azure=$({
  echo "$resources"
  # Some types have no diagnostic settings (identities, DNS links: ResourceTypeNotSupported). A sign-in problem
  # would already have failed `az resource list` above.
  for id in $resources; do
    az monitor diagnostic-settings list --resource "$id" --query "[].id" -o tsv 2>/dev/null || true
  done
  az monitor diagnostic-settings subscription list --query "value[?name=='telemetry-$environment'].id" -o tsv
} | grep -v '^$' | lower | sort -u)

leftovers=$(comm -23 <(echo "$in_azure") <(echo "$known"))
if [ -n "$leftovers" ]; then
  echo "In Azure but not in $environment's state, so the next start would fail on them (remove them, or import):" >&2
  echo "$leftovers" >&2
  exit 1
fi
echo "No leftovers for $environment: everything in $group is in its state"
