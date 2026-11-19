#!/usr/bin/env bash
# Fails if anyone holds privileged access *standing* that should only be reachable through PIM (D35, D36):
#   - Global Admin: only the emergency accounts in access/people.yaml;
#   - Privileged Role Administrator (can grant Global Admin): nobody;
#   - Owner or User Access Administrator on the subscription, including inherited from above: only the
#     emergency accounts;
#   - nobody but the apply identity created a group or app: Entra makes whoever creates one its owner, so
#     any other owner means it was made outside Pulumi (D42 detection, independent of enforcement);
#   - tenant defaults (D42): members can't create groups (security or Microsoft 365), apps or tenants,
#     consent to apps, invite guests, join by email domain or sign up for trials.
# PIM activations are not standing access and are ignored. Runs in the pulumi workflow as the read-only
# preview identity, on every PR, on main and every night, so a role added outside PIM fails a check.
set -euo pipefail

graph=https://graph.microsoft.com/v1.0
sub=/subscriptions/${AZURE_SUBSCRIPTION_ID:?}
emergency=$(yq '.["emergency-access"][]' access/people.yaml)
[ -n "$emergency" ] || { echo "No emergency-access accounts in access/people.yaml" >&2; exit 1; }

# Prints every principal in $1 (one per line) that isn't allowed by $2 (one per line).
not_allowed() { grep -vxF -f <(printf '%s\n' "$2") <<<"$1" || true; }

standing_directory_role() { # role-template-id
  az rest --method GET \
    --url "$graph/roleManagement/directory/roleAssignmentSchedules?\$filter=roleDefinitionId%20eq%20'$1'" \
    --query "value[?assignmentType=='Assigned'].principalId" -o tsv
}

problems=()
report() { # label principals
  local p
  for p in $2; do problems+=("$1: $p"); done
}

report "standing Global Admin" "$(not_allowed "$(standing_directory_role 62e90394-69f5-4237-9190-012177145e10)" "$emergency")"
report "standing Privileged Role Administrator" "$(standing_directory_role e8611ab8-c189-46e8-94e1-60213ab1f814)"
for role in Owner "User Access Administrator"; do
  holders=$(az role assignment list --scope "$sub" --include-inherited --role "$role" --query "[].principalId" -o tsv)
  report "$role on the subscription" "$(not_allowed "$holders" "$emergency")"
done

# Groups and apps with any owner other than the apply identity (names, then the other owners).
apply_identity=$(az ad sp list --display-name id-platform-apply --query "[0].id" -o tsv)
[ -n "$apply_identity" ] || { echo "Service principal id-platform-apply not found" >&2; exit 1; }
for kind in groups applications; do
  while IFS= read -r line; do
    [ -n "$line" ] && problems+=("$kind made outside Pulumi: $line")
  done < <(az rest --method GET --url "$graph/$kind?\$select=displayName&\$expand=owners(\$select=id)&\$top=999" -o json |
    jq -r --arg ok "$apply_identity" \
      '.value[] | [.owners[].id | select(. != $ok)] as $other | select($other | length > 0)
       | "\(.displayName) (owners: \($other | join(", ")))"')
done

# The same values as Desired.TenantDefaults.locked. az prints booleans in lowercase.
defaults=$(az rest --method GET --url "$graph/policies/authorizationPolicy" --query "join(' ', [
  to_string(defaultUserRolePermissions.allowedToCreateSecurityGroups),
  to_string(defaultUserRolePermissions.allowedToCreateApps),
  to_string(defaultUserRolePermissions.allowedToCreateTenants),
  to_string(length(defaultUserRolePermissions.permissionGrantPoliciesAssigned)),
  allowInvitesFrom,
  to_string(allowEmailVerifiedUsersToJoinOrganization),
  to_string(allowedToSignUpEmailBasedSubscriptions)])" -o tsv)
[ "$defaults" = "false false false 0 adminsAndGuestInviters false false" ] ||
  problems+=("tenant authorization policy: members may create, consent or invite ($defaults)")
m365=$(az rest --method GET --url "$graph/groupSettings" \
  --query "value[?templateId=='62375ab9-6b52-47ed-826b-58e47e0e304b'].values[] | [?name=='EnableGroupCreation'].value | [0]" -o tsv)
[ "$m365" = "false" ] || problems+=("Microsoft 365 group creation: members may create groups (${m365:-not set})")

if [ ${#problems[@]} -gt 0 ]; then
  echo "Standing privileged access outside PIM, groups or apps made outside Pulumi, or tenant defaults that allow members to create:" >&2
  printf '  %s\n' "${problems[@]}" >&2
  echo "Remove it, or make it PIM-eligible through access/people.yaml." >&2
  exit 1
fi
echo "Standing access ok: Global Admin, Owner and User Access Administrator are held only by the emergency accounts, no group or app was made outside Pulumi, and members can't create anything on their own"
