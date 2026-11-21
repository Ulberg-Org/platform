#!/usr/bin/env bash
# Tests release_stale_lock.sh with a stand-in `pulumi`: a lock released, no lock, and a real error.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
fake=$(mktemp -d)
trap 'rm -rf "$fake"' EXIT
cat >"$fake/pulumi" <<'P'
#!/usr/bin/env bash
case "$CASE" in
  locked) echo "The currently running update for 'dev' has been canceled!" ;;
  free) echo "error: no lock files found for stack 'dev'" >&2; exit 255 ;;
  broken) echo "error: failed to load checkpoint: access denied" >&2; exit 255 ;;
esac
P
chmod +x "$fake/pulumi"
check() { # description, case, want (pass|fail)
  if PATH="$fake:$PATH" CASE="$2" "$here/release_stale_lock.sh" dev >/dev/null 2>&1; then got=pass; else got=fail; fi
  [ "$got" = "$3" ] && echo "ok    $1" || { echo "FAIL  $1: $got, want $3"; exit 1; }
}
check "a stale lock is released" locked pass
check "no lock is fine" free pass
check "any other error stops the run" broken fail
