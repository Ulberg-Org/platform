#!/bin/sh
# Applies an app's database roles in Azure (D85), run by the environment's bootstrap Job as the server's Entra
# admin, through workload identity: swaps the pod's projected token for an Entra token for Postgres, then runs
# /job/roles.sql in the app's database (and pgaudit's extension there, D93) and /job/azure.sql in `postgres`, where
# Azure's Entra functions (pgaadauth_*) live; roles are server-wide. Each is one transaction, so a failure leaves nothing half-done and a
# retry starts clean. Retries the swap for a few minutes: a new federation takes a while to reach Entra.
set -eu
scope=https%3A%2F%2Fossrdbms-aad.database.windows.net%2F.default
assertion_type=urn%3Aietf%3Aparams%3Aoauth%3Aclient-assertion-type%3Ajwt-bearer
token=""
for attempt in 1 2 3 4 5 6 7 8 9 10 11 12; do
  token=$(wget -qO- --post-data \
    "client_id=$AZURE_CLIENT_ID&grant_type=client_credentials&scope=$scope&client_assertion_type=$assertion_type&client_assertion=$(cat "$AZURE_FEDERATED_TOKEN_FILE")" \
    "${AZURE_AUTHORITY_HOST}${AZURE_TENANT_ID}/oauth2/v2.0/token" 2>/dev/null |
    sed -n 's/.*"access_token":"\([^"]*\)".*/\1/p') || true
  [ -n "$token" ] && break
  echo "no Entra token yet (attempt $attempt), retrying in 15 s"
  sleep 15
done
[ -n "$token" ] || { echo "no Entra token for Postgres" >&2; exit 1; }
export PGPASSWORD=$token
psql -v ON_ERROR_STOP=1 -q --single-transaction -f /job/roles.sql
psql -v ON_ERROR_STOP=1 -q -c 'CREATE EXTENSION IF NOT EXISTS pgaudit'
psql -v ON_ERROR_STOP=1 -q --single-transaction -d postgres -f /job/azure.sql
echo "roles applied to $PGDATABASE"
