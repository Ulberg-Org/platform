#!/usr/bin/env bash
# The pulumi workflow previews and applies the stacks in the order of stacks/order.yaml (D53), so setup and
# teardown follow one source of truth. Fails if a job's pulumi steps name other stacks or another order.
set -euo pipefail

want=$(yq '.[]' stacks/order.yaml | sed 's#^#stacks/#')
for job in preview apply; do
  got=$(yq ".jobs.$job.steps[] | select(.uses == \"pulumi/actions*\") | .with.\"work-dir\"" .github/workflows/pulumi.yml)
  [ "$got" = "$want" ] || {
    printf 'pulumi.yml job %s runs\n%s\nbut stacks/order.yaml says\n%s\n' "$job" "$got" "$want" >&2
    exit 1
  }
done
echo "Stack order ok: $(echo $want)"
