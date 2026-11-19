#!/usr/bin/env bash
# Fails if GitHub access drifts from what the code and PLAN say (D43, D52):
#   - Claude's app (ulberg-claude) may only do contents, pull requests and workflows, and only on the repos
#     it was installed on (not every repo in the org);
#   - the organization requires 2FA, gives members no base access and doesn't let them create repos.
# Runs in the pulumi workflow with the read-only app's token (organization administration: read): on
# every PR, on main and every night.
set -euo pipefail

org=$(yq '.config["github:owner"]' stacks/access/Pulumi.main.yaml)
problems=()

claude=$(gh api "orgs/$org/installations" --jq '.installations[] | select(.app_slug == "ulberg-claude")')
claude=${claude:-"{}"}
perms=$(jq -r '(.permissions // {}) | to_entries | map("\(.key)=\(.value)") | sort | join(",")' <<<"$claude")
[ "$perms" = "contents=write,metadata=read,pull_requests=write,workflows=write" ] ||
  problems+=("ulberg-claude may do: ${perms:-nothing (not installed)}")
[ "$(jq -r .repository_selection <<<"$claude")" = "selected" ] ||
  problems+=("ulberg-claude is installed on every repo in the org, not only the selected ones")

settings=$(gh api "orgs/$org" --jq '[.two_factor_requirement_enabled, .default_repository_permission,
  .members_can_create_repositories] | map(tostring) | join(" ")')
[ "$settings" = "true none false" ] ||
  problems+=("org settings (2FA required, base permission, members create repos): $settings, expected true none false")

if [ ${#problems[@]} -gt 0 ]; then
  echo "GitHub access differs from D43/D52:" >&2
  printf '  %s\n' "${problems[@]}" >&2
  exit 1
fi
echo "GitHub access ok: Claude's app has contents, pull requests and workflows on selected repos; the org requires 2FA and gives members nothing by default"
