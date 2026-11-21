#!/usr/bin/env bash
# Removes the lock a cancelled `start` or `destroy` left on an environment's stack (D45). Safe because the two
# share a concurrency group per environment: when one runs, no other is, so a lock it finds is stale.
#
#   release_stale_lock.sh <stack>     (run in stacks/environment, logged in to the state backend)
set -euo pipefail
stack=${1:?usage: release_stale_lock.sh <stack>}

if out=$(pulumi cancel --yes --stack "$stack" 2>&1); then
  echo "released a stale lock on $stack"
elif grep -qiE "not locked|no (update|lock)|nothing to cancel" <<<"$out"; then
  echo "$stack isn't locked"
else
  echo "$out" >&2
  exit 1
fi
