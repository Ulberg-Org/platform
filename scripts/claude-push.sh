#!/usr/bin/env bash
# Publishes the current branch as one verified commit by Claude's GitHub app (D52), instead of `git push`.
# Both repos require verified signatures on main, and the app can't sign git commits; commits it creates
# through GitHub's API are signed by GitHub. So this script uploads what changed between the branch's
# published tip (or origin/<base> for a new branch) and HEAD, makes one commit of it with the local commit
# messages, and moves the remote branch there. The developer the work is for is credited with a
# Co-authored-by trailer (the local commits' author). Run it from the repo; the work tree must be clean.
#
# Usage: claude-push.sh [base]   (default base: main)
set -euo pipefail

base=${1:-main}
branch=$(git branch --show-current)
[ -n "$branch" ] && [ "$branch" != "$base" ] || { echo "Switch to a feature branch first" >&2; exit 1; }
[ -z "$(git status --porcelain)" ] || { echo "Commit or stash your changes first" >&2; exit 1; }
repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner)

git fetch -q origin "$base"
# `gh api` prints the error body to stdout on a 404, so a failure must clear the value.
published=$(gh api "repos/$repo/git/ref/heads/$branch" --jq .object.sha 2>/dev/null) || published=""
parent=${published:-$(git rev-parse "origin/$base")}
git fetch -q origin "$parent"
# When origin/<base> was merged in locally, the published commit records it as a second parent, so GitHub sees
# the branch as up to date with <base> rather than as changing the same lines.
merged=()
git merge-base --is-ancestor "origin/$base" HEAD && ! git merge-base --is-ancestor "origin/$base" "$parent" &&
  merged=("$(git rev-parse "origin/$base")")
[ -n "$(git diff --no-renames --name-only "$parent" HEAD)" ] || [ ${#merged[@]} -gt 0 ] ||
  { echo "Nothing to publish" >&2; exit 1; }

# One tree entry per changed file: new content as a blob, or null to delete it.
entries=$(git diff --no-renames --name-status "$parent" HEAD | while IFS=$'\t' read -r status path; do
  if [ "$status" = D ]; then
    jq -nc --arg p "$path" '{path: $p, mode: "100644", type: "blob", sha: null}'
  else
    mode=$(git ls-tree HEAD -- "$path" | cut -d' ' -f1)
    blob=$(git show "HEAD:$path" | base64 | tr -d '\n' | jq -Rs '{content: ., encoding: "base64"}' |
      gh api "repos/$repo/git/blobs" --input - --jq .sha)
    jq -nc --arg p "$path" --arg m "$mode" --arg s "$blob" '{path: $p, mode: $m, type: "blob", sha: $s}'
  fi
done | jq -s .)

base_tree=$(gh api "repos/$repo/git/commits/$parent" --jq .tree.sha)
tree=$(jq -n --arg b "$base_tree" --argjson e "$entries" '{base_tree: $b, tree: $e}' |
  gh api "repos/$repo/git/trees" --input - --jq .sha)

# The local commit messages, oldest first, then credit for the developer.
author=$(git log -1 --format='%an <%ae>' HEAD)
message="$(git log --reverse --format=%B "$parent..HEAD" | grep -v '^Co-authored-by: ' | cat -s)
Co-authored-by: $author"
commit=$(jq -n --arg m "$message" --arg t "$tree" --args '{message: $m, tree: $t, parents: $ARGS.positional}' \
    "$parent" "${merged[@]}" |
  gh api "repos/$repo/git/commits" --input - --jq .sha)

if [ -n "$published" ]; then
  gh api -X PATCH "repos/$repo/git/refs/heads/$branch" -f sha="$commit" --silent
else
  gh api "repos/$repo/git/refs" -f ref="refs/heads/$branch" -f sha="$commit" --silent
fi

# The local branch now matches what was published (same tree, one verified commit).
git fetch -q origin "$branch"
git reset -q --keep "origin/$branch"
git branch -q --set-upstream-to "origin/$branch"
verified=$(gh api "repos/$repo/commits/$commit" --jq .commit.verification.verified)
echo "Published $branch as ${commit:0:7} (verified: $verified)"
