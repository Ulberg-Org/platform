#!/usr/bin/env bash
# The `teardown` GitHub environment gives the apply identity without approval (D45), so only destroy.yml may
# use it. Fails if any other workflow in <folder> names it, if any workflow names an environment by an
# expression (which could hide it), or if destroy.yml can be called from another workflow. Reads the files
# as data only; the guard workflow runs main's copy of this script on a pull request's files.
#
#   check_teardown_use.sh <workflows folder>
set -euo pipefail
folder=${1:?usage: check_teardown_use.sh <workflows folder>}
problems=()

for file in "$folder"/*.yml "$folder"/*.yaml; do
  [ -e "$file" ] || continue
  name=$(basename "$file")
  # Each job's environment, written as a name or as { name: ... }. A file yq can't read fails the check.
  environments=$(yq '.jobs[] | .environment | select(.) | (.name // .)' "$file")
  while IFS= read -r environment; do
    case "$environment" in
      *'${{'*) problems+=("$name: an environment named by an expression ($environment)") ;;
      teardown) [ "$name" = destroy.yml ] || problems+=("$name: uses the teardown environment (only destroy.yml may)") ;;
    esac
  done <<<"$environments"
done

[ ! -e "$folder/destroy.yml" ] || [ "$(yq '.on | has("workflow_call")' "$folder/destroy.yml")" = false ] ||
  problems+=("destroy.yml: can be called by other workflows (workflow_call)")

[ ${#problems[@]} -eq 0 ] || { printf '%s\n' "${problems[@]}" >&2; exit 1; }
echo "teardown: only destroy.yml uses it"
