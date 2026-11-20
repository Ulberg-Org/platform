#!/usr/bin/env bash
# CI check for D65: every platform component's Helm chart is still the package its digest pins. A chart
# re-published under the same version (or a typo in component.yaml) fails here, before Argo CD would deploy it.
set -euo pipefail
dir=$(mktemp -d)
failed=0
for file in components/*/component.yaml; do
  repo=$(awk '/^  repo:/ {print $2}' "$file")
  [ -n "$repo" ] || continue
  name=$(awk '/^  name:/ {print $2}' "$file")
  version=$(awk '/^  version:/ {print $2}' "$file")
  want=$(awk '/^  digest:/ {print $2}' "$file")
  rm -f "$dir"/*.tgz
  if [ -n "$name" ]; then helm pull "$name" --repo "$repo" --version "$version" -d "$dir" >/dev/null
  else helm pull "$repo" --version "$version" -d "$dir" >/dev/null; fi
  got="sha256:$(sha256sum "$dir"/*.tgz | cut -d' ' -f1)"
  if [ "$got" = "$want" ]; then echo "ok    $file ($version)"
  else echo "FAIL  $file ($version): the chart is $got, component.yaml pins $want"; failed=1; fi
done
exit $failed
