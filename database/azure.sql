-- In Azure (D69, D75, D85), run in the `postgres` database (Azure's pgaadauth functions are there; roles are
-- server-wide). Entra identities and groups sign in, each holding one role: the backend's workload
-- identity and the platform leads (platform-write, through PIM) read and write; developers, infra developers and
-- every developer's Claude agent (platform-agents, app-agents: D91) read. A member signs in with the group's name and their own Entra token. Role settings
-- don't pass to members, so the read-only ones get them too.
SELECT * FROM pgaadauth_create_principal('@backend@', false, false);
GRANT @db@_app TO "@backend@";
SELECT * FROM pgaadauth_create_principal('platform-write', false, false);
GRANT @db@_app TO "platform-write";
SELECT * FROM pgaadauth_create_principal('platform-read', false, false);
SELECT * FROM pgaadauth_create_principal('platform-agents', false, false);
SELECT * FROM pgaadauth_create_principal('app-developers', false, false);
SELECT * FROM pgaadauth_create_principal('app-agents', false, false);
GRANT @db@_readonly TO "platform-read", "platform-agents", "app-developers", "app-agents";
ALTER ROLE "platform-read" SET default_transaction_read_only = on;
ALTER ROLE "platform-read" SET statement_timeout = '30s';
ALTER ROLE "platform-agents" SET default_transaction_read_only = on;
ALTER ROLE "platform-agents" SET statement_timeout = '30s';
ALTER ROLE "app-developers" SET default_transaction_read_only = on;
ALTER ROLE "app-developers" SET statement_timeout = '30s';
ALTER ROLE "app-agents" SET default_transaction_read_only = on;
ALTER ROLE "app-agents" SET statement_timeout = '30s';
