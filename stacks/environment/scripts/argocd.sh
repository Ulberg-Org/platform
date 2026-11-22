#!/usr/bin/env bash
# Argo CD's bootstrap in Azure (D67, D100), the same as the platform CLI's locally: the chart components/argocd pins,
# its digest checked, installed with values.yaml and values-<environment>.yaml, then the root application. From
# then on Argo CD manages itself and everything else from the repo; the environment stack runs this once per
# cluster (a Pulumi Command), so the two never fight over it.
#
# Environment: KUBECONFIG_DATA (the cluster's, signing in with kubelogin as CI's az session), ENVIRONMENT, REPO.
set -euo pipefail
root=$(cd "$(dirname "$0")/../../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
printf '%s' "$KUBECONFIG_DATA" >"$work/kubeconfig"
export KUBECONFIG="$work/kubeconfig"

component="$root/components/argocd/component.yaml"
namespace=$(yq '.namespace' "$component")
digest=$(yq '.chart.digest' "$component")
helm pull "$(yq '.chart.name' "$component")" --repo "$(yq '.chart.repo' "$component")" \
  --version "$(yq '.chart.version' "$component")" -d "$work/charts"
chart=$(ls "$work"/charts/*.tgz)
[ "sha256:$(sha256sum "$chart" | cut -d' ' -f1)" = "$digest" ] ||
  { echo "Argo CD's chart isn't the package components/argocd pins ($digest)" >&2; exit 1; }

helm upgrade --install argocd "$chart" -n "$namespace" --create-namespace \
  -f "$root/components/argocd/values.yaml" -f "$root/components/argocd/values-$ENVIRONMENT.yaml" \
  --wait --timeout 10m
sed -e "s|@repo@|$REPO|g" -e "s|@environment@|$ENVIRONMENT|g" "$root/cli/templates/root-application.yaml" |
  kubectl apply -f -
echo "Argo CD $(yq '.chart.version' "$component") and its root application ($REPO, $ENVIRONMENT)"
