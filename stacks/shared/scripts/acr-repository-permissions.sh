#!/usr/bin/env bash
# Switches the registry to repository permissions (ABAC), so a role can be limited to some repositories
# (each app's CI may push only into `<app>/`, D54). The azure-native provider can't set this mode yet, so
# Program.fs runs this as a Pulumi Command: on apply only, as the apply identity. Running it again is harmless.
# Env: REGISTRY.
set -euo pipefail

mode() { az acr show --name "$REGISTRY" --query roleAssignmentMode -o tsv; }
case "$(mode)" in
  AbacRepositoryPermissions | rbac-abac) ;;
  *) az acr update --name "$REGISTRY" --role-assignment-mode rbac-abac -o none ;;
esac

case "$(mode)" in
  AbacRepositoryPermissions | rbac-abac) echo "Registry $REGISTRY uses repository permissions" ;;
  *) echo "Registry $REGISTRY did not switch to repository permissions: $(mode)" >&2; exit 1 ;;
esac
