#!/usr/bin/env bash
# Tests due_environments.sh with a stand-in `az` that reports each group's `started` tag.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
fake=$(mktemp -d); trap 'rm -rf "$fake"' EXIT
cat >"$fake/az" <<'AZ'
#!/usr/bin/env bash
# az group show --name rg-<env> --query tags.started -o tsv
case "$4" in
  rg-dev) echo "$DEV_STARTED" ;;
  *) echo "ERROR: (ResourceGroupNotFound)" >&2; exit 3 ;;
esac
AZ
chmod +x "$fake/az"
check() { # description, expected, asked, dev's start (empty: no group)
  got=$(PATH="$fake:$PATH" DEV_STARTED="$4" "$here/due_environments.sh" "$3")
  [ "$got" = "$2" ] && echo "ok    $1" || { echo "FAIL  $1: got $got, want $2"; exit 1; }
}
ago() { date -u -d "$1 ago" +%Y-%m-%dT%H:%M:%SZ; }
check "started 5 hours ago: due" '["dev"]' "" "$(ago '5 hours')"
check "started 1 hour ago: not yet" '[]' "" "$(ago '1 hour')"
check "not running: nothing to do" '[]' "" ""
check "asked by hand: at once" '["dev"]' "dev" "$(ago '1 hour')"
