#!/usr/bin/env bash
# Tests claude-push.sh against a local bare repo standing in for GitHub (tests/fake-gh): it publishes exactly the
# local tree, and a failed upload or a published tree that differs stops it with the local branch kept (#68).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
mkdir "$work/bin"
ln -s "$here/tests/fake-gh" "$work/bin/gh"
export PATH="$work/bin:$PATH" ORIGIN="$work/origin"
export GIT_AUTHOR_NAME=dev GIT_AUTHOR_EMAIL=dev@example.com GIT_COMMITTER_NAME=dev GIT_COMMITTER_EMAIL=dev@example.com
fail() { echo "FAIL  $1"; exit 1; }

# GitHub has main with a.txt and b.txt; the local branch `work` changes a.txt. Then claude-push.sh runs with the
# given fault and must exit with the given code.
push() { # fault (NAME=1 for the fake, or empty), expected exit code
  rm -rf "$work/origin" "$work/clone"
  git init -q --bare -b main "$work/origin"
  git clone -q "$work/origin" "$work/clone" 2>/dev/null
  cd "$work/clone"
  echo a >a.txt && echo b >b.txt && git add . && git commit -qm init && git push -q origin main
  git switch -qc work && echo changed >a.txt && git commit -qam "change a"
  code=0
  env ${1:+"$1"} "$here/claude-push.sh" >/dev/null 2>&1 || code=$?
  [ "$code" = "$2" ] || fail "with ${1:-no fault}: exit $code, want $2"
}

push "" 0
[ "$(git -C "$ORIGIN" rev-parse 'work^{tree}')" = "$(git rev-parse 'HEAD^{tree}')" ] || fail "published tree differs"
echo "ok    publishes exactly the local tree"

push UPLOAD_FAILS=1 1
! git -C "$ORIGIN" rev-parse -q --verify refs/heads/work >/dev/null || fail "published after a failed upload"
echo "ok    a failed upload publishes nothing"

push LOSE=a.txt 1
[ "$(git log -1 --format=%s)" = "change a" ] && [ "$(cat a.txt)" = changed ] || fail "local branch replaced"
echo "ok    a published tree that differs stops it, with the local branch kept"
