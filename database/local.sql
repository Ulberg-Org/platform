-- Locally (D61) the roles sign in with local-only passwords, and per-query statistics (D64) come from
-- pg_stat_statements, read by a monitor role for the collector and by the read-only role (Claude's MCP).
ALTER ROLE @db@_migrator LOGIN PASSWORD 'local-only-migrator';
ALTER ROLE @db@_app LOGIN PASSWORD 'local-only-app';
ALTER ROLE @db@_readonly LOGIN PASSWORD 'local-only-readonly';
CREATE EXTENSION pg_stat_statements;
CREATE ROLE @db@_monitor LOGIN PASSWORD 'local-only-monitor' IN ROLE pg_monitor;
GRANT pg_read_all_stats TO @db@_readonly;
