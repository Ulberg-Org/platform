#!/usr/bin/env bash
# Regression test for Claude's agent accounts (D41), against the real tenant. Each agent in
# access/people.yaml must hold exactly its read roles:
#   - Azure: Reader, Monitoring Reader, Log Analytics Reader, Cost Management Reader on the subscription
#     (via platform-agents), plus, on the Claude GitHub App key only, Key Vault Crypto User (D52: sign), and on
#     each environment's resource group only, AKS Cluster User and RBAC Reader (D84, D88, D95: read, no Secrets);
#   - Entra roles: Security Reader, active; nothing eligible;
#   - PIM for Groups: no eligibility, neither for the account nor for platform-agents;
#   - groups: platform-agents only (so it inherits nothing).
# Runs in the pulumi workflow as the read-only preview identity: every PR, main and nightly.
set -euo pipefail

graph=https://graph.microsoft.com/v1.0
domain=$(yq '.config["access:tenantDomain"]' stacks/access/Pulumi.main.yaml)
agents=$(yq '.agents // [] | .[]' access/people.yaml)
sub=/subscriptions/${AZURE_SUBSCRIPTION_ID:?}
azure_roles=$(printf '%s @ '"$sub"'\n' "Cost Management Reader" "Log Analytics Reader" "Monitoring Reader" "Reader")
# D52: may sign with the Claude GitHub App key, and with nothing else in the vault.
vault=$(yq '.config["access:keyVault"]' stacks/access/Pulumi.main.yaml)
vault_rg=$(yq '.config["access:ciIdentityResourceGroup"]' stacks/access/Pulumi.main.yaml)
app_key_signer="Key Vault Crypto User @ $sub/resourceGroups/$vault_rg/providers/Microsoft.KeyVault/vaults/$vault/keys/claude-github-app"
security_reader=5d6b6bb7-de71-4623-b4af-96380a352509
# D88: reads on the clusters in each environment's resource group (stacks/environment/Pulumi.<env>.yaml), granted
# on the group, which outlives the environment (D95). Compared ignoring case: Azure resource ids are
# case-insensitive, and az writes some parts in lowercase.
cluster_reads=$(for config in stacks/environment/Pulumi.*.yaml; do
  env=$(basename "$config" .yaml | sed 's/^Pulumi\.//')
  for role in "Azure Kubernetes Service Cluster User Role" "Azure Kubernetes Service RBAC Reader"; do
    echo "$role @ $sub/resourceGroups/rg-$env"
  done
done)

problems=()
# Compares two newline-separated lists (order doesn't matter) and records any difference.
expect() { # label actual expected
  local actual expected
  actual=$(grep -v '^$' <<<"$2" | sort -u || true)
  expected=$(grep -v '^$' <<<"$3" | sort -u || true)
  [ "$actual" = "$expected" ] || problems+=("$1: expected [${expected//$'\n'/, }], found [${actual//$'\n'/, }]")
}

pim_group_eligibility() { # principal-id
  az rest --method GET \
    --url "$graph/identityGovernance/privilegedAccess/group/eligibilitySchedules?\$filter=principalId%20eq%20'$1'" \
    --query "value[].groupId" -o tsv
}

group_id=$(az ad group show --group platform-agents --query id -o tsv)
expect "platform-agents: PIM-for-Groups eligibility" "$(pim_group_eligibility "$group_id")" ""

for key in $agents; do
  upn="$key-agent@$domain"
  id=$(az ad user show --id "$upn" --query id -o tsv 2>/dev/null || true)
  if [ -z "$id" ]; then
    echo "::notice::$upn doesn't exist yet; the next apply creates it"
    continue
  fi

  expect "$upn: Azure roles" \
    "$(az role assignment list --all --assignee "$id" --include-groups --include-inherited \
      --query "[].join(' @ ', [roleDefinitionName, scope])" -o tsv |
      grep -vixF -e "$app_key_signer" -f <(echo "$cluster_reads") || true)" \
    "$azure_roles"
  expect "$upn: active Entra roles" \
    "$(az rest --method GET \
      --url "$graph/roleManagement/directory/roleAssignmentSchedules?\$filter=principalId%20eq%20'$id'" \
      --query "value[].roleDefinitionId" -o tsv)" "$security_reader"
  expect "$upn: eligible Entra roles" \
    "$(az rest --method GET \
      --url "$graph/roleManagement/directory/roleEligibilitySchedules?\$filter=principalId%20eq%20'$id'" \
      --query "value[].roleDefinitionId" -o tsv)" ""
  expect "$upn: PIM-for-Groups eligibility" "$(pim_group_eligibility "$id")" ""
  expect "$upn: groups" \
    "$(az rest --method GET --url "$graph/users/$id/transitiveMemberOf/microsoft.graph.group?\$select=displayName" \
      --query "value[].displayName" -o tsv)" "platform-agents"
done

# D52: the Claude app's key may only sign (never decrypt, wrap or export).
expect "Key Vault key claude-github-app: permitted operations" \
  "$(az keyvault key show --vault-name "$vault" --name claude-github-app --query "key.keyOps" -o tsv)" "sign"

if [ ${#problems[@]} -gt 0 ]; then
  echo "Agent accounts hold more (or other) access than D41 allows:" >&2
  printf '  %s\n' "${problems[@]}" >&2
  exit 1
fi
echo "Agent access ok: read roles only, no PIM, platform-agents only"
