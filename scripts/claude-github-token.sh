#!/usr/bin/env bash
# Gives Claude a 1 h GitHub token as the `ulberg-claude` app (D52) and stores it in Claude's own `gh` profile.
# Claude runs it itself, with the agent's `az` login: Key Vault signs the app's JWT with a key that never
# leaves Key Vault and that only platform-agents may sign with, and GitHub exchanges the JWT for an
# installation token (contents + pull requests on the installed repos; never workflows, settings,
# approvals or merges). Run it again when the token expires; every signing is in Key Vault's log.
#
# Claude Code must run with GH_CONFIG_DIR pointing at Claude's profile (see AGENTS.md); the script refuses
# to overwrite any other `gh` login.
set -euo pipefail

app_id=5835801 # ulberg-claude
org=Ulberg-Org
vault=kv-platform-21784576
key=claude-github-app
profile=$(cd ~ && pwd)/.config/gh-claude

[ "${GH_CONFIG_DIR:-}" = "$profile" ] || {
  echo "GH_CONFIG_DIR must be $profile (Claude's profile), not '${GH_CONFIG_DIR:-}'. See AGENTS.md." >&2
  exit 1
}

b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }
api() { # path, extra curl arguments
  curl -sSf "https://api.github.com$1" -H "Accept: application/vnd.github+json" \
    -H "X-GitHub-Api-Version: 2022-11-28" "${@:2}"
}

# The app's JWT: valid for 9 minutes (GitHub allows 10), backdated a minute for clock drift.
now=$(date +%s)
header=$(printf '{"alg":"RS256","typ":"JWT"}' | b64url)
payload=$(printf '{"iat":%d,"exp":%d,"iss":"%s"}' $((now - 60)) $((now + 540)) "$app_id" | b64url)
digest=$(printf '%s.%s' "$header" "$payload" | openssl dgst -sha256 -binary | openssl base64 -A)
signature=$(az keyvault key sign --vault-name "$vault" --name "$key" --algorithm RS256 --digest "$digest" \
  --query signature -o tsv | tr '+/' '-_' | tr -d '=')
jwt="$header.$payload.$signature"

installation=$(api "/orgs/$org/installation" -H "Authorization: Bearer $jwt" | jq -r .id)
token=$(api "/app/installations/$installation/access_tokens" -X POST -H "Authorization: Bearer $jwt" |
  jq -r '.token + " " + .expires_at')

# Written directly: `gh auth login` checks a token against /user, which an installation token can't read.
mkdir -p "$profile" && chmod 700 "$profile"
(
  umask 077
  printf 'github.com:\n    oauth_token: %s\n    git_protocol: https\n    user: ulberg-claude[bot]\n' "${token%% *}" \
    >"$profile/hosts.yml"
)
echo "Claude's GitHub token is valid until ${token##* } (gh profile $profile)"
