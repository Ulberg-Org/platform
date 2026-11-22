#!/usr/bin/env bash
# Regression test for Claude's agent accounts (D41, D91), against the real tenant. Every developer in
# access/people.yaml has one, holding exactly its developer's read roles:
#   - an infra developer's (platform-agents): Reader, Monitoring Reader, Log Analytics Reader, Cost Management
#     Reader on the subscription; on each environment's resource group only, AKS Cluster User and RBAC Reader
#     (D84, D88, D95: read, no Secrets); Entra's Security Reader, active;
#   - an app developer's (app-agents): nothing in Azure or Entra (the database read-only is a Postgres role);
#   - both: on the Claude GitHub App key only, Key Vault Crypto User (D52: sign); no eligible Entra roles; no PIM
#     for Groups, neither for the account nor for its group; that group only (so it inherits nothing).
# Runs in the pulumi workflow as the read-only preview identity: every PR, main and nightly.
set -euo pipefail

graph=https://graph.microsoft.com/v1.0
domain=$(yq '.config["access:tenantDomain"]' stacks/access/Pulumi.main.yaml)
infra=$(yq '.groups.infra-developers[]' access/people.yaml)
app_only=$(yq '.groups.app-developers[]' access/people.yaml | grep -vxF -f <(echo "$infra") || true)
sub=/subscriptions/${AZURE_SUBSCRIPTION_ID:?}
azure_roles=$(printf '%s @ '"$sub"'\n' "Cost Management Reader" "Log Analytics Reader" "Monitoring Reader" "Reader")
# D52: may sign with the Claude GitHub App key, and with nothing else in the vault.
vault=$(yq '.config["access:keyVault"]' stacks/access/Pulumi.main.yaml)
vault_rg=$(yq '.config["access:ciIdentityResourceGroup"]' stacks/access/Pulumi.main.yaml)
app_key_signer="Key Vault Crypto User @ $sub/resourceGroups/$vault_rg/providers/Microsoft.KeyVault/vaults/$vault/keys/claude-github-app"
security_reader=5d6b6bb7-de71-4623-b4af-96380a352509
# D88: reads on the clusters in each environment's resource group (stacks/environment/Pulumi.<env>.yaml), granted
# on the group, which outlives the environment (D95).
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

# Compared ignoring case: Azure resource ids are case-insensitive, and az writes some parts in lowercase.
lower() { tr '[:upper:]' '[:lower:]'; }

check_agent() { # developer, group, its Azure roles ("role @ scope" lines), its active Entra role ids
  local upn="$1-agent@$domain" id
  id=$(az ad user show --id "$upn" --query id -o tsv 2>/dev/null || true)
  if [ -z "$id" ]; then
    echo "::notice::$upn doesn't exist yet; the next apply creates it"
    return
  fi
  expect "$upn: Azure roles" \
    "$(az role assignment list --all --assignee "$id" --include-groups --include-inherited \
      --query "[].join(' @ ', [roleDefinitionName, scope])" -o tsv | lower)" "$(lower <<<"$3")"
  expect "$upn: active Entra roles" \
    "$(az rest --method GET \
      --url "$graph/roleManagement/directory/roleAssignmentSchedules?\$filter=principalId%20eq%20'$id'" \
      --query "value[].roleDefinitionId" -o tsv)" "$4"
  expect "$upn: eligible Entra roles" \
    "$(az rest --method GET \
      --url "$graph/roleManagement/directory/roleEligibilitySchedules?\$filter=principalId%20eq%20'$id'" \
      --query "value[].roleDefinitionId" -o tsv)" ""
  expect "$upn: PIM-for-Groups eligibility" "$(pim_group_eligibility "$id")" ""
  expect "$upn: groups" \
    "$(az rest --method GET --url "$graph/users/$id/transitiveMemberOf/microsoft.graph.group?\$select=displayName" \
      --query "value[].displayName" -o tsv)" "$2"
}

for group in platform-agents app-agents; do
  group_id=$(az ad group show --group "$group" --query id -o tsv 2>/dev/null || true)
  [ -z "$group_id" ] || expect "$group: PIM-for-Groups eligibility" "$(pim_group_eligibility "$group_id")" ""
done
for key in $infra; do
  check_agent "$key" platform-agents "$azure_roles
$app_key_signer
$cluster_reads" "$security_reader"
done
for key in $app_only; do
  check_agent "$key" app-agents "$app_key_signer" ""
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
