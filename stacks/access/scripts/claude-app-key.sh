#!/usr/bin/env bash
# Imports the Claude GitHub App's private key (D52) into Key Vault as a key that may only sign. Key Vault
# never returns a key's private part (there is no release policy), so after this the key exists only
# there. Entra.fs-style Pulumi Command: on apply only, as the apply identity (Key Vault Crypto Officer on
# the vault, granted by the stack just before). Runs once: if the key exists it only checks it.
# Env: VAULT, KEY_NAME, and CLAUDE_APP_PRIVATE_KEY (the dev environment secret, only needed for the import;
# it is passed to the apply step by the workflow and never becomes a Pulumi input, so it isn't in state).
set -euo pipefail

if ! az keyvault key show --vault-name "$VAULT" --name "$KEY_NAME" -o none 2>/dev/null; then
  [ -n "${CLAUDE_APP_PRIVATE_KEY:-}" ] || {
    echo "Key $KEY_NAME isn't in $VAULT, and CLAUDE_APP_PRIVATE_KEY isn't set to import it" >&2
    exit 1
  }
  # The role assignment just made can take a few minutes to reach Key Vault.
  for attempt in $(seq 1 18); do
    az keyvault key import --vault-name "$VAULT" --name "$KEY_NAME" --pem-string "$CLAUDE_APP_PRIVATE_KEY" \
      --ops sign --protection software -o none 2>/dev/null && break
    [ "$attempt" -lt 18 ] || { echo "Could not import $KEY_NAME into $VAULT" >&2; exit 1; }
    sleep 10
  done
fi

ops=$(az keyvault key show --vault-name "$VAULT" --name "$KEY_NAME" --query "join(',', key.keyOps)" -o tsv)
[ "$ops" = "sign" ] || { echo "$KEY_NAME may do '$ops', expected only 'sign'" >&2; exit 1; }
echo "Claude app key $KEY_NAME is in $VAULT and may only sign"
