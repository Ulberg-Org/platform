#!/usr/bin/env bash
# Sets the GitHub organization's defaults (Desired.OrgDefaults): what members may do on their own. The
# provider's organization-settings resource requires a billing email, which this public repo shouldn't
# hold, so GitHub.fs runs this as a Pulumi Command instead: on apply only, with the admin app's token
# (GITHUB_TOKEN, Organization administration: write). A PATCH, so running it again is harmless.
# Env: ORG, SETTINGS (the fields as JSON), GITHUB_TOKEN.
set -euo pipefail

api() { # curl arguments
  curl -sSf "https://api.github.com/orgs/$ORG" -H "Authorization: Bearer $GITHUB_TOKEN" \
    -H "Accept: application/vnd.github+json" -H "X-GitHub-Api-Version: 2022-11-28" "$@"
}

api -X PATCH --data "$SETTINGS" >/dev/null

# Read it back: every field we sent must be what GitHub now has.
actual=$(api)
jq -e --argjson want "$SETTINGS" '. as $got | $want | to_entries | all(.value == $got[.key])' <<<"$actual" >/dev/null || {
  echo "GitHub organization settings did not take effect:" >&2
  jq --argjson want "$SETTINGS" '. as $got | $want | with_entries(.value = $got[.key])' <<<"$actual" >&2
  exit 1
}
echo "GitHub organization settings set and verified"
