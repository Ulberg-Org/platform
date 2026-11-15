#!/usr/bin/env bash
# Sets the tenant's Group.Unified directory setting: who may create Microsoft 365 groups
# (Desired.TenantDefaults). The azuread provider has no resource for it, so Entra.fs runs this as a Pulumi
# Command: on apply only, as the apply identity (needs Directory.ReadWrite.All). Graph replaces the whole
# list of values, so the script sends every value: ours, plus the current ones (or the template's defaults
# when the setting doesn't exist yet). Running it again is harmless.
# Env: VALUES (a JSON object, setting name → value).
set -euo pipefail

graph=https://graph.microsoft.com/v1.0
template=62375ab9-6b52-47ed-826b-58e47e0e304b # Group.Unified

setting_id() {
  az rest --method GET --url "$graph/groupSettings" --query "value[?templateId=='$template'].id | [0]" -o tsv
}

# name → value pairs as Graph's list, with VALUES applied on top of $1 (a Graph `values` list).
merged() {
  jq -c --argjson want "$VALUES" 'map({name, value: ($want[.name] // .value)})' <<<"$1"
}

id=$(setting_id)
if [ -n "$id" ]; then
  current=$(az rest --method GET --url "$graph/groupSettings/$id" --query values -o json)
  az rest --method PATCH --url "$graph/groupSettings/$id" \
    --body "$(jq -nc --argjson v "$(merged "$current")" '{values: $v}')" -o none
else
  defaults=$(az rest --method GET --url "$graph/groupSettingTemplates/$template" \
    --query "values[].{name: name, value: defaultValue}" -o json)
  az rest --method POST --url "$graph/groupSettings" \
    --body "$(jq -nc --arg t "$template" --argjson v "$(merged "$defaults")" '{templateId: $t, values: $v}')" -o none
fi

# Read it back: every value we set must be what Entra now has. Retries for a minute.
for attempt in 1 2 3 4 5 6 7; do
  id=$(setting_id)
  actual=$([ -n "$id" ] && az rest --method GET --url "$graph/groupSettings/$id" --query values -o json || echo '[]')
  jq -e --argjson want "$VALUES" 'from_entries as $got | $want | to_entries | all(.value == $got[.key])' \
    <<<"$actual" >/dev/null && break
  [ "$attempt" -lt 7 ] || { echo "Group settings did not take effect: $actual" >&2; exit 1; }
  sleep 10
done
echo "Group settings set and verified"
